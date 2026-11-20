using System.Drawing;
using Gfx = System.Drawing.Graphics;
using System.Drawing.Imaging;
using LiquidGlass.Desktop.Platform;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 逐像素透明的自绘窗口。
///
/// 为什么不用普通窗口 + WM_PAINT：
/// 设置面板和启动台都有圆角、外阴影、半透明底。普通窗口的透明区域会被系统
/// 填成黑色（Dock 早期"开机黑屏"就是这个机制），而 GDI 的圆形在这里只能靠
/// 逐点抠像，边缘全是锯齿。分层窗口把"已经画好的整张位图 + 每个像素的 alpha"
/// 一起交给 DWM 合成，圆角和阴影才是真正平滑的。
///
/// 消息直接走 Dock 自己那条循环（同一线程），不额外起线程、不额外吸 CPU：
/// 只有内容变化时才重画一次位图，其余时间窗口完全不产生绘制请求。
/// </summary>
internal abstract class LayeredWindow : IDisposable
{
    private static readonly Dictionary<IntPtr, LayeredWindow> Instances = new();
    private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);
    private static readonly List<Native.WndProc> Procs = new();   // 防止委托被 GC

    private readonly string _className;
    private Native.WndProc? _proc;
    private bool _trackingLeave;

    protected IntPtr Hwnd { get; private set; }
    protected int PixelWidth { get; private set; }
    protected int PixelHeight { get; private set; }

    /// <summary>DPI 缩放：布局一律用逻辑像素写，这里换算成设备像素。</summary>
    protected float Scale { get; private set; }

    protected LayeredWindow(string className, float scale)
    {
        _className = className;
        Scale = scale <= 0.1f ? 1f : scale;
    }

    /// <summary>
    /// true = 这个窗口的内容不走 GDI，而是由 D3D 合成交换链直接交给 DWM。
    ///
    /// GDI 的 UpdateLayeredWindow 只能做到"半透明贴图"，DWM 不会给它做背景模糊 ——
    /// 想要跟 Dock 本体一模一样的液态玻璃，唯一的路就是同一套 D3D 管线：
    /// WS_EX_NOREDIRECTIONBITMAP 的窗口自己什么也不画，像素全部来自
    /// composition swapchain（预乘 alpha），由 DWM 直接合成。
    /// </summary>
    protected virtual bool UsesGpuBackdrop => false;

    /// <summary>GPU 模式下窗口创建好之后回调，子类在这里建合成目标。</summary>
    protected virtual void OnWindowCreated() { }

    /// <summary>
    /// true = 这个窗口永远不接受激活（WS_EX_NOACTIVATE）。
    ///
    /// 悬浮类窗口必须为 true：设置面板、悬停预览卡片一激活就会把用户正在用的
    /// 窗口踩下去。但**启动台不能** —— 它里面有个搜索框，键盘消息只会发给
    /// 前台窗口，带 NOACTIVATE 的窗口永远拿不到 WM_CHAR，表现就是
    /// "搜索框点不动、怎么打都没字"。启动台因此覆写成 false。
    /// </summary>
    protected virtual bool NoActivate => true;

    /// <summary>GPU 模式下的"画一帧"。子类负责渲染 + Present。</summary>
    protected virtual void OnGpuFrame() { }

    public bool IsVisible => Hwnd != IntPtr.Zero && Native.IsWindowVisible(Hwnd);

    /// <summary>窗口句柄。父窗口做截图排除 / 层级调整时要用。</summary>
    public IntPtr Handle => Hwnd;

    /// <summary>逻辑像素 → 设备像素。至少 1，否则细线会消失。</summary>
    protected int S(float logical) => Math.Max(1, (int)MathF.Round(logical * Scale));

    protected float SF(float logical) => logical * Scale;

    // ---------------------------------------------------------------- 生命周期

    protected void Create(float logicalWidth, float logicalHeight)
    {
        PixelWidth = S(logicalWidth);
        PixelHeight = S(logicalHeight);

        if (Hwnd != IntPtr.Zero)
        {
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, PixelWidth, PixelHeight,
                Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
            return;
        }

        RegisterClass();

        // GPU 模式不能带 WS_EX_LAYERED：那是 GDI 的逐像素透明通道，与合成交换链
        // 是两条互斥的路。带了它，DWM 会把窗口当普通分层窗口合成，swapchain 的
        // 内容根本不会出现在屏幕上。
        int exStyle = UsesGpuBackdrop
            ? Native.WS_EX_NOREDIRECTIONBITMAP | Native.WS_EX_TOOLWINDOW |
              Native.WS_EX_TOPMOST | (NoActivate ? Native.WS_EX_NOACTIVATE : 0)
            : Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;

        Hwnd = Native.CreateWindowEx(exStyle, _className, "LiquidGlass",
            Native.WS_POPUP, 0, 0, PixelWidth, PixelHeight,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

        if (Hwnd == IntPtr.Zero)
        {
            Logger.Warn("分层窗口创建失败: " + _className);
            return;
        }

        Instances[Hwnd] = this;
        OnWindowCreated();
    }

    private void RegisterClass()
    {
        if (!Registered.Add(_className)) return;

        _proc = WndProcEntry;
        Procs.Add(_proc);

        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = Native.GetModuleHandle(null),
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = _className,
        };

        ushort atom = Native.RegisterClassEx(ref wc);
        if (atom == 0) Logger.Warn("窗口类注册失败: " + _className);
    }

    /// <summary>显示在指定位置（左上角，设备像素）。不抢焦点。</summary>
    protected void ShowAt(int x, int y, bool activate = false)
    {
        if (Hwnd == IntPtr.Zero) return;

        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, PixelWidth, PixelHeight,
            Native.SWP_SHOWWINDOW | (activate ? 0u : Native.SWP_NOACTIVATE));

        if (activate)
        {
            Native.ShowWindow(Hwnd, Native.SW_SHOW);
            // 直接用 SetForegroundWindow 会被前台锁挡掉（Dock 是 NOACTIVATE 窗口，
            // 打开面板时前台往往还是别的程序）。走三级降级才能真正把焦点拿过来。
            WindowActivator.Activate(Hwnd);
        }

        Render();
    }

    /// <summary>显示器 DPI 变了（换屏 / 改缩放）时重建像素尺寸。</summary>
    protected void ApplyScale(float scale, float logicalWidth, float logicalHeight)
    {
        if (scale > 0.1f) Scale = scale;
        PixelWidth = S(logicalWidth);
        PixelHeight = S(logicalHeight);
        if (Hwnd != IntPtr.Zero)
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, PixelWidth, PixelHeight,
                Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
    }

    protected void HideWindow()
    {
        if (Hwnd != IntPtr.Zero) Native.ShowWindow(Hwnd, Native.SW_HIDE);
    }

    public virtual void Dispose()
    {
        if (Hwnd != IntPtr.Zero)
        {
            Instances.Remove(Hwnd);
            Native.DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- 绘制

    /// <summary>画一帧并提交给 DWM。只有内容变了才需要调用。</summary>
    protected virtual void Render()
    {
        if (Hwnd == IntPtr.Zero || PixelWidth <= 0 || PixelHeight <= 0) return;

        if (UsesGpuBackdrop)
        {
            OnGpuFrame();
            return;
        }

        using var bmp = new Bitmap(PixelWidth, PixelHeight, PixelFormat.Format32bppPArgb);
        using (Gfx g = Gfx.FromImage(bmp))
        {
            UiTheme.Prepare(g);
            g.Clear(Color.Transparent);
            OnPaint(g, PixelWidth, PixelHeight);
        }

        IntPtr hbmp = IntPtr.Zero;
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        try
        {
            hbmp = bmp.GetHbitmap(Color.FromArgb(0));
            screenDc = Native.GetDC(IntPtr.Zero);
            memDc = Native.CreateCompatibleDC(screenDc);
            old = Native.SelectObject(memDc, hbmp);

            // 位置必须按窗口当前真实坐标传：拖动标题栏后窗口位置是系统改的，
            // 这里若传 0,0 会把窗口瞬间拽回屏幕左上角。
            Native.GetWindowRect(Hwnd, out Native.RECT rect);
            var dst = new Native.POINT { X = rect.Left, Y = rect.Top };
            var size = new Native.SIZE(PixelWidth, PixelHeight);
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };

            bool ok = Native.UpdateLayeredWindow(Hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend,
                Native.ULW_ALPHA);
            if (!ok)
                Logger.Warn($"UpdateLayeredWindow 失败 err={System.Runtime.InteropServices.Marshal.GetLastWin32Error()} " +
                            $"hwnd=0x{Hwnd:X} size={PixelWidth}x{PixelHeight} dst=({dst.X},{dst.Y})");
        }
        catch (Exception ex)
        {
            Logger.Warn("分层窗口绘制失败: " + ex.Message);
        }
        finally
        {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
            if (memDc != IntPtr.Zero) Native.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>把整块内容画到给定画布上。宽高是设备像素。</summary>
    protected abstract void OnPaint(Gfx g, int width, int height);

    /// <summary>只重画，不改变窗口状态。</summary>
    public void Invalidate() => Render();

    /// <summary>面板要用系统指标定位，这里透出去，免得子类各写一份 P/Invoke。</summary>
    protected static int SystemMetric(int index) => Native.GetSystemMetrics(index);

    // ---------------------------------------------------------------- 消息

    private IntPtr WndProcEntry(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Instances.TryGetValue(hwnd, out LayeredWindow? self))
            return self.WndProc(hwnd, msg, wParam, lParam);

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        int x = unchecked((short)((long)lParam & 0xFFFF));
        int y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));

        switch (msg)
        {
            case Native.WM_MOUSEMOVE:
                if (!_trackingLeave)
                {
                    var tme = new Native.TRACKMOUSEEVENT
                    {
                        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.TRACKMOUSEEVENT>(),
                        dwFlags = Native.TME_LEAVE,
                        hwndTrack = hwnd,
                    };
                    _trackingLeave = Native.TrackMouseEvent(ref tme);
                }

                if (OnMouseMove(x, y)) Render();
                return IntPtr.Zero;

            case Native.WM_MOUSELEAVE:
                _trackingLeave = false;
                if (OnMouseLeave()) Render();
                return IntPtr.Zero;

            case Native.WM_LBUTTONDOWN:
                Native.SetCapture(hwnd);
                if (OnLeftDown(x, y)) Render();
                return IntPtr.Zero;

            case Native.WM_MBUTTONDOWN:
                if (OnMiddleDown(x, y)) Render();
                return IntPtr.Zero;

            case Native.WM_XBUTTONDOWN:
            {
                int button = unchecked((short)(((long)wParam >> 16) & 0xFFFF));
                if (OnXButtonDown(x, y, button)) Render();
                return IntPtr.Zero;
            }

            // 鼠标钩子在"录制快捷键"期间把按键投回来。钩子回调里不能直接改状态，
            // 必须等它返回后再处理。
            case Native.WM_LIQUIDGLASS_CAPTURE_MOUSE:
                OnCaptureMouseShortcut((int)(long)wParam);
                return IntPtr.Zero;

            // 键盘钩子投回来的按键（wParam 已还原成真实 vk，不经过输入法）。
            case Native.WM_LIQUIDGLASS_CAPTURE_KEY:
                OnCaptureKeyboardShortcut((int)(long)wParam, (int)(long)lParam);
                return IntPtr.Zero;

            // 录制期间点在面板外面：放弃录制。
            case Native.WM_LIQUIDGLASS_CANCEL_CAPTURE:
                OnCancelShortcutCapture();
                return IntPtr.Zero;

            case Native.WM_LBUTTONUP:
                Native.ReleaseCapture();
                if (OnLeftUp(x, y)) Render();
                return IntPtr.Zero;

            case Native.WM_MOUSEWHEEL:
            {
                int delta = (short)(((long)wParam >> 16) & 0xFFFF);
                var pt = new Native.POINT { X = x, Y = y };
                Native.ScreenToClient(hwnd, ref pt);
                if (OnWheel(delta, pt.X, pt.Y)) Render();
                return IntPtr.Zero;
            }

            case Native.WM_NCHITTEST:
                return OnHitTest(x, y);

            case Native.WM_KEYDOWN:
            {
                byte vk = (byte)((long)wParam & 0xFF);

                // 输入法吃掉字母键时，wParam 只剩 VK_PROCESSKEY，真实键值要从
                // lParam 的扫描码反查。不还原的话，中文输入法下按 M 会被当成
                // "这个键"，组合键也永远录不进去。
                if (vk == Native.VK_PROCESSKEY)
                {
                    uint scan = (uint)(((long)lParam >> 16) & 0xFF);
                    uint mapped = Native.MapVirtualKeyW(scan, Native.MAPVK_VSC_TO_VK_EX);
                    if (mapped != 0) vk = (byte)mapped;
                }

                if (OnKeyDown(vk)) Render();
                return IntPtr.Zero;
            }

            case Native.WM_CHAR:
                if (OnChar((char)((long)wParam & 0xFFFF))) Render();
                return IntPtr.Zero;

            // 中文输入法：拼音阶段系统不给我们字符，只有组合出结果的那一次
            // WM_IME_COMPOSITION(GCS_RESULTSTR) 里才有真正的汉字。
            // 取到就自己消费掉，不再下传 DefWindowProc —— 否则它会再投一个
            // WM_IME_CHAR，同一个字会被加进搜索串两次。
            case Native.WM_IME_COMPOSITION:
                return HandleImeComposition(hwnd, msg, wParam, lParam);

            // 单字节输入法（或没有输入上下文时的兜底）走这里。
            case Native.WM_IME_CHAR:
                if (OnChar((char)((long)wParam & 0xFFFF))) Render();
                return IntPtr.Zero;

            // 组合被取消（Esc / 点别处）：把预览擦掉，否则拼音会一直挂在搜索框里。
            case Native.WM_IME_ENDCOMPOSITION:
                if (OnImeComposing("")) Render();
                return IntPtr.Zero;

            case Native.WM_MOUSEACTIVATE:
                // 面板类窗口默认抢焦点（打开设置就该像打开一个普通窗口）。
                // 悬停预览卡片相反：它只是浮在那儿，绝不能把用户正在用的窗口踩下去。
                return (IntPtr)(AllowActivate ? Native.MA_ACTIVATE : Native.MA_NOACTIVATE);

            case Native.WM_SETCURSOR:
                Native.SetCursor(Native.LoadCursorW(IntPtr.Zero, (IntPtr)_cursor));
                return (IntPtr)1;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private int _cursor = Native.IDC_ARROW;

    /// <summary>切换鼠标指针形状（箭头 / 手）—— 可点的图标、按钮用。</summary>
    protected void SetCursorKind(bool hand)
        => SetCursorShape(hand ? Native.IDC_HAND : Native.IDC_ARROW);

    /// <summary>"I" 形光标 —— 搜索框上必须用它，用户才知道这里是能打字的。</summary>
    protected void SetCursorText() => SetCursorShape(Native.IDC_IBEAM);

    private void SetCursorShape(int idc)
    {
        if (idc == _cursor) return;
        _cursor = idc;
        if (Hwnd != IntPtr.Zero) Native.SetCursor(Native.LoadCursorW(IntPtr.Zero, (IntPtr)idc));
    }

    /// <summary>
    /// 输入法组合。两条路要分开处理：
    /// - GCS_RESULTSTR：这一次组合已经落字（"桌面"），追加进真正的输入内容；
    /// - GCS_COMPSTR：还在敲拼音（"zhuomian"），只作为**预览**显示，不进输入内容。
    ///
    /// 预览这一条不能省。不画的话，中文用户敲拼音时搜索框一动不动，
    /// 看上去就是"键盘没反应"——和"点击没反应"是同一种坏体验。
    /// </summary>
    private IntPtr HandleImeComposition(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        long flags = (long)lParam;

        if ((flags & Native.GCS_RESULTSTR) != 0)
        {
            bool changed = false;
            string done = ReadImeString(hwnd, Native.GCS_RESULTSTR);
            if (done.Length > 0) changed = OnImeText(done);

            // 落字的同时组合串必然清空，通知子类擦掉预览
            if (OnImeComposing("")) changed = true;
            if (changed) Render();
            return IntPtr.Zero;
        }

        if ((flags & Native.GCS_COMPSTR) != 0)
        {
            string composing = ReadImeString(hwnd, Native.GCS_COMPSTR);
            if (OnImeComposing(composing)) Render();
            return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>从输入上下文里取指定类型的字符串（结果串 / 组合串）。</summary>
    private static string ReadImeString(IntPtr hwnd, int index)
    {
        IntPtr imc = Native.ImmGetContext(hwnd);
        if (imc == IntPtr.Zero) return "";

        try
        {
            int bytes = Native.ImmGetCompositionStringW(imc, index, IntPtr.Zero, 0);
            if (bytes <= 0) return "";

            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
            try
            {
                Native.ImmGetCompositionStringW(imc, index, buf, bytes);
                return System.Runtime.InteropServices.Marshal.PtrToStringUni(buf, bytes / 2) ?? "";
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
            }
        }
        finally
        {
            Native.ImmReleaseContext(hwnd, imc);
        }
    }

    protected virtual bool OnMouseMove(int x, int y) => false;
    protected virtual bool OnMouseLeave() => false;
    protected virtual bool OnLeftDown(int x, int y) => false;
    protected virtual bool OnLeftUp(int x, int y) => false;
    protected virtual bool OnMiddleDown(int x, int y) => false;
    /// <summary>鼠标侧键：button = 1（后退）或 2（前进）。</summary>
    protected virtual bool OnXButtonDown(int x, int y, int button) => false;
    /// <summary>录制快捷键时，鼠标钩子投回来的鼠标键：1=中键 2=侧键后退 3=侧键前进。</summary>
    protected virtual void OnCaptureMouseShortcut(int button) { }
    /// <summary>录制快捷键时，键盘钩子投回来的按键（modifiers：1=Ctrl 2=Alt 4=Shift 8=Win）。</summary>
    protected virtual void OnCaptureKeyboardShortcut(int vk, int modifiers) { }
    /// <summary>录制期间点了面板外面，放弃录制。</summary>
    protected virtual void OnCancelShortcutCapture() { }
    protected virtual bool OnWheel(int delta, int x, int y) => false;
    protected virtual bool OnKeyDown(byte vk) => false;
    protected virtual bool OnChar(char c) => false;

    /// <summary>输入法给出的一段已确定文本（一个或多个汉字）。默认当逐字输入处理。</summary>
    protected virtual bool OnImeText(string text)
    {
        bool any = false;
        foreach (char c in text)
        {
            if (OnChar(c)) any = true;
        }

        return any;
    }

    /// <summary>输入法正在组合的串（拼音）。空串 = 组合结束。返回 true 表示需要重画。</summary>
    protected virtual bool OnImeComposing(string text) => false;
    protected virtual IntPtr OnHitTest(int x, int y) => (IntPtr)Native.HTCLIENT;

    /// <summary>被点到时是否允许抢焦点。悬停预览卡片覆写成 false。</summary>
    protected virtual bool AllowActivate => true;

    /// <summary>窗口当前屏幕矩形（设备像素）。父窗口判断"鼠标是不是还压在卡片上"要用。</summary>
    public bool TryGetScreenBounds(out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        if (Hwnd == IntPtr.Zero) return false;
        if (!Native.GetWindowRect(Hwnd, out Native.RECT rect)) return false;
        x = rect.Left;
        y = rect.Top;
        w = rect.Right - rect.Left;
        h = rect.Bottom - rect.Top;
        return w > 0 && h > 0;
    }
}
