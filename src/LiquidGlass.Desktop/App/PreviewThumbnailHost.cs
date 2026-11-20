using System.Runtime.InteropServices;
using LiquidGlass.Desktop.Platform;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 预览卡片里那块"活画面"的宿主窗口。
///
/// 为什么不画在卡片自己的位图里：卡片是逐像素透明的分层窗口，内容由我们用
/// GDI+ 画好再交给 DWM；而"目标窗口的实时画面"只有 DWM 能给（DwmRegisterThumbnail）。
/// DWM 把缩略图画进**一个真实窗口**的重定向表面，画不进我们那张位图。
/// 所以这里单独开一个不带 alpha 的普通窗口，盖在卡片缩略图那块位置上，
/// 用圆角区域把四个角切掉，视觉上和卡片是一体的。
///
/// 三个必须的样式：
/// - WS_EX_TRANSPARENT：点击穿过它，落到下面的卡片上（点缩略图 = 切到前台）；
/// - WS_EX_NOACTIVATE：它是"贴在屏幕上的画"，绝不能抢焦点；
/// - WS_EX_TOOLWINDOW：不进 Alt+Tab，不出任务栏。
/// </summary>
internal sealed class PreviewThumbnailHost : IDisposable
{
    private const string ClassName = "LiquidGlassPreviewThumb";

    private static readonly Dictionary<IntPtr, PreviewThumbnailHost> Instances = new();
    private static readonly List<Native.WndProc> Procs = new();   // 防委托被 GC
    private static bool _classRegistered;

    private readonly Native.WndProc _proc;
    private IntPtr _hwnd;
    private IntPtr _thumb;
    private IntPtr _thumbTarget;
    private int _width, _height, _radius;

    /// <summary>上一次设给 DWM 的目标矩形与取景框，用来判断"要不要重设属性"。</summary>
    private int _destW, _destH;
    private Native.RECT _srcRect;

    public PreviewThumbnailHost()
    {
        _proc = WndProcEntry;
        Procs.Add(_proc);
    }

    public bool Visible => _hwnd != IntPtr.Zero && Native.IsWindowVisible(_hwnd);

    /// <summary>
    /// 把 target 的实时画面贴到 (x,y,w,h) 这块屏幕区域里。返回 false = 系统不给注册，
    /// 调用方应退回静态抓图或直接显示占位说明。
    ///
    /// 取景规则：**铺满这块矩形**，比例绝不硬拉。窗口比取景框宽就左右各裁一点（居中），
    /// 比取景框窄（竖窗口）就上下裁 —— 偏上取，因为标题栏和正文开头才是
    /// "这窗口里是什么"的关键，居中裁会把两者各切掉一半。
    /// 以前是"等比缩到框里、四周留白"，竖窗口在横向卡片里就变成中间一根窄条。
    /// </summary>
    public bool ShowFor(IntPtr target, int x, int y, int w, int h, int radius)
    {
        if (target == IntPtr.Zero || !Native.IsWindow(target)) return false;
        if (w < 8 || h < 8) return false;

        int dw = w, dh = h;
        bool haveSource = false;
        Native.RECT src = default;

        // 最小化窗口要用还原尺寸算比例，否则画面会被压扁
        if (WindowThumbnail.TargetSize(target, out int sw, out int sh) && sw > 0 && sh > 0)
        {
            haveSource = true;
            src = new Native.RECT { Left = 0, Top = 0, Right = sw, Bottom = sh };

            double boxAspect = w / (double)h;
            double srcAspect = sw / (double)sh;

            if (srcAspect > boxAspect + 0.01)
            {
                int cut = Math.Max(8, (int)Math.Round(sh * boxAspect));
                int left = (sw - cut) / 2;
                src.Left = left;
                src.Right = Math.Min(sw, left + cut);
            }
            else if (srcAspect < boxAspect - 0.01)
            {
                int cut = Math.Max(8, (int)Math.Round(sw / boxAspect));
                int top = (int)Math.Round((sh - cut) * 0.32);
                src.Top = top;
                src.Bottom = Math.Min(sh, top + cut);
            }
        }

        int dx = x, dy = y;

        EnsureWindow(dw, dh, radius);
        if (_hwnd == IntPtr.Zero) return false;

        if (_thumb != IntPtr.Zero && _thumbTarget == target)
        {
            // 同一目标：只更新窗口位置。取景框变了（换了比例不同的槽位）才重设属性，
            // 重注册一次要重新接合成链，能省就省。
            if (dw != _destW || dh != _destH || !SameRect(src, _srcRect))
                ApplyThumbProps(dw, dh, src, haveSource);

            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, dx, dy, dw, dh,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            return true;
        }

        UnregisterThumb();

        int hr = Native.DwmRegisterThumbnail(_hwnd, target, out IntPtr thumb);
        if (hr != 0 || thumb == IntPtr.Zero)
        {
            Logger.Warn($"[preview] DwmRegisterThumbnail 失败 hr=0x{hr:X8}");
            return false;
        }

        _thumb = thumb;
        _thumbTarget = target;

        if (!ApplyThumbProps(dw, dh, src, haveSource)) return false;

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, dx, dy, dw, dh,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Native.ShowWindow(_hwnd, Native.SW_SHOWNA);
        return true;
    }

    private bool ApplyThumbProps(int dw, int dh, Native.RECT src, bool haveSource)
    {
        if (_thumb == IntPtr.Zero) return false;

        var props = new Native.DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY
                      | (haveSource ? Native.DWM_TNP_RECTSOURCE : 0),
            rcDestination = new Native.RECT { Left = 0, Top = 0, Right = dw, Bottom = dh },
            rcSource = src,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = false,
        };

        int hr = Native.DwmUpdateThumbnailProperties(_thumb, ref props);
        if (hr != 0)
        {
            Logger.Warn($"[preview] DwmUpdateThumbnailProperties 失败 hr=0x{hr:X8}");
            UnregisterThumb();
            return false;
        }

        _destW = dw;
        _destH = dh;
        _srcRect = src;
        return true;
    }

    private static bool SameRect(Native.RECT a, Native.RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public void Hide()
    {
        // 先撤缩略图再藏窗口：只藏窗口的话 DWM 那边还挂着一条合成链，
        // 目标窗口每次重绘都要多合成一次，看不见但白花 GPU。
        UnregisterThumb();
        if (_hwnd != IntPtr.Zero) Native.ShowWindow(_hwnd, Native.SW_HIDE);
    }

    public void Dispose()
    {
        UnregisterThumb();
        if (_hwnd != IntPtr.Zero)
        {
            Instances.Remove(_hwnd);
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- 内部

    private void UnregisterThumb()
    {
        if (_thumb == IntPtr.Zero) return;
        Native.DwmUnregisterThumbnail(_thumb);
        _thumb = IntPtr.Zero;
        _thumbTarget = IntPtr.Zero;
    }

    private void EnsureWindow(int w, int h, int radius)
    {
        if (_hwnd != IntPtr.Zero)
        {
            if (w == _width && h == _height && radius == _radius) return;

            _width = w; _height = h; _radius = radius;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, w, h,
                Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
            ApplyRegion(w, h, radius);
            return;
        }

        _width = w; _height = h; _radius = radius;
        RegisterClassOnce();

        int exStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST |
                      Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE;

        _hwnd = Native.CreateWindowEx(exStyle, ClassName, "LiquidGlassPreviewThumb",
            Native.WS_POPUP, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero,
            Native.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Logger.Warn("预览宿主窗口创建失败");
            return;
        }

        Instances[_hwnd] = this;
        ApplyRegion(w, h, radius);
    }

    private void ApplyRegion(int w, int h, int radius)
    {
        // 这块区域里没有我们画的像素，唯一能控制角落的就是让窗口本身不是矩形。
        // 卡片现在只有下方是圆角（画面贴着卡片下沿），上方是贴着标题带的直边，
        // 所以先取一个四角全圆的区域，再用一块方角矩形把上面两个角"填平"。
        int r = Math.Max(0, radius);
        if (r <= 0)
        {
            IntPtr plain = Native.CreateRectRgn(0, 0, w + 1, h + 1);
            if (plain != IntPtr.Zero && Native.SetWindowRgn(_hwnd, plain, true) == 0)
                Native.DeleteObject(plain);
            return;
        }

        IntPtr region = Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2);
        if (region == IntPtr.Zero) return;

        IntPtr top = Native.CreateRectRgn(0, 0, w + 1, Math.Max(1, h + 1 - r));
        if (top != IntPtr.Zero)
        {
            Native.CombineRgn(region, region, top, Native.RGN_OR);
            Native.DeleteObject(top);
        }

        if (Native.SetWindowRgn(_hwnd, region, true) == 0)
            Native.DeleteObject(region);
    }

    private void RegisterClassOnce()
    {
        if (_classRegistered) return;
        _classRegistered = true;

        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandle(null),
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = ClassName,
        };

        if (Native.RegisterClassEx(ref wc) == 0)
            Logger.Warn("预览宿主窗口类注册失败");
    }

    private IntPtr WndProcEntry(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hwnd, out PreviewThumbnailHost? self))
            return self.WndProc(hwnd, msg, wParam, lParam);

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // 宿主自己不画任何东西，缩略图由 DWM 合成进来。
            // 返回 1 = 别擦背景，否则会先刷一块白底再把画面盖上去（闪一下）。
            case Native.WM_ERASEBKGND:
                return 1;

            case 0x000F: // WM_PAINT
                Native.BeginPaint(hwnd, out Native.PAINTSTRUCT ps);
                Native.EndPaint(hwnd, ref ps);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
