using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Gfx = System.Drawing.Graphics;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 抓一张"某个窗口现在长什么样"的图，给 Dock 的悬停预览用。
///
/// 为什么用 PrintWindow 而不是抓屏：
/// - 抓屏（Desktop Duplication）只能拿到**屏幕上的**像素，窗口被别的窗口盖住时
///   就只能拍到盖住它的那个；预览图必须是"那个窗口自己的内容"。
/// - PrintWindow 让目标窗口把自己的客户区画到我们的 DC 上，被遮挡、在后台都成立。
///
/// 三个已知边界，全部按"给不出图就明说"处理，不糊一张黑图出去：
/// 1. 受保护内容（Netflix 一类 DRM、部分银行客户端）会被系统抹黑；
/// 2. 极少数硬件加速窗口（老的 Qt / 某些 Electron）画不出来；
/// 3. 最小化的窗口有些程序不响应 WM_PRINT，拿到的是空帧。
/// 这三种情况返回 null，卡片改画"图标 + 说明"。
///
/// 注意：这个类是**兜底**。正常路径是 DWM 缩略图（PreviewThumbnailHost），
/// 它对最小化窗口同样能画出画面；只有 DWM 拒绝注册时才会走到这里。
/// </summary>
internal static class WindowThumbnail
{
    /// <summary>PW_RENDERFULLCONTENT：让 DirectComposition / D3D 内容也一起画进来。</summary>
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    /// <summary>窗口标题。空标题（有些无边框窗口）返回空串，由调用方决定显示什么。</summary>
    public static string TitleOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return "";
        try
        {
            int len = GetWindowTextLengthW(hwnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(Math.Min(len + 1, 512));
            int n = GetWindowTextW(hwnd, sb, sb.Capacity);
            return n > 0 ? sb.ToString(0, n) : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 目标窗口"真正的"尺寸（含 32~8192 的合理性检查：全屏 4K 也才 3840,
    /// 超出这个范围说明是窗口初始化阶段的异常值）。
    ///
    /// 最小化窗口不能信 GetWindowRect —— 它报的是任务栏图标那一小块（新版系统上是
    /// -32000 哨兵值），拿它算宽高比会把预览压成一根扁条。只有 GetWindowPlacement
    /// 里的 rcNormalPosition 还记得这个窗口本来多大。
    /// </summary>
    public static bool TargetSize(IntPtr hwnd, out int w, out int h)
    {
        w = h = 0;

        if (Native.IsIconic(hwnd))
        {
            var wp = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
            if (Native.GetWindowPlacement(hwnd, ref wp))
            {
                w = wp.rcNormalPosition.Right - wp.rcNormalPosition.Left;
                h = wp.rcNormalPosition.Bottom - wp.rcNormalPosition.Top;
                if (w is > 120 and < 8192 && h is > 120 and < 8192) return true;
            }
        }

        if (!Native.GetWindowRect(hwnd, out Native.RECT r)) return false;
        w = r.Right - r.Left;
        h = r.Bottom - r.Top;
        return w is > 32 and < 8192 && h is > 32 and < 8192;
    }

    /// <summary>
    /// 抓取并缩放到不超过 maxWidth×maxHeight（保持比例）。
    /// 返回 null 表示这个窗口给不出可用画面。
    /// </summary>
    public static Bitmap? Capture(IntPtr hwnd, int maxWidth, int maxHeight)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return null;
        if (maxWidth < 16 || maxHeight < 16) return null;

        if (!TargetSize(hwnd, out int w, out int h)) return null;

        // PrintWindow 是按窗口原始尺寸渲染的，4K 全屏窗口直接打要几十毫秒。
        // 只为了 280px 宽的预览图，完全没必要按原尺寸来 —— 先按目标比例缩一遍源尺寸，
        // 让窗口用 WM_PRINT 画到更小的 DC 上（多数程序会自适应），拿不到再退回原尺寸。
        int srcW = w, srcH = h;
        double fit = Math.Min(maxWidth / (double)w, maxHeight / (double)h);
        if (fit < 1.0)
        {
            srcW = Math.Max(64, (int)Math.Round(w * fit));
            srcH = Math.Max(64, (int)Math.Round(h * fit));
        }

        Bitmap? shot = Render(hwnd, srcW, srcH);
        if (shot is null && (srcW != w || srcH != h))
        {
            // 缩小后画不出东西（有些程序对非原生尺寸的 WM_PRINT 直接返回空），退回原尺寸
            shot = Render(hwnd, w, h);
        }

        if (shot is null) return null;

        Bitmap result = Fit(shot, maxWidth, maxHeight);
        if (!ReferenceEquals(result, shot)) shot.Dispose();
        return result;
    }

    private static Bitmap? Render(IntPtr hwnd, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using (Gfx g = Gfx.FromImage(bmp))
            {
                g.Clear(Color.Black);
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)) return null;
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }

            // 全黑 = 对方没画（受保护 / 不响应 WM_PRINT）。这种"预览"比没有更糟。
            return IsBlank(bmp) ? null : bmp;
        }
        catch
        {
            bmp.Dispose();
            return null;
        }
    }

    private static bool IsBlank(Bitmap bmp)
    {
        Rectangle rect = new(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            byte[] row = new byte[stride];
            int minL = 255, maxL = 0;

            // 每 8 行抽一行、每 8 像素抽一点：判断"是不是一整块纯色"用不着全量扫
            for (int y = 0; y < bmp.Height; y += 8)
            {
                Marshal.Copy(data.Scan0 + y * stride, row, 0, stride);
                for (int x = 0; x < bmp.Width; x += 8)
                {
                    int i = x * 4;
                    int l = (row[i] * 30 + row[i + 1] * 59 + row[i + 2] * 11) / 100;
                    if (l < minL) minL = l;
                    if (l > maxL) maxL = l;
                    if (maxL - minL > 10) return false;
                }
            }

            return true;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static Bitmap Fit(Bitmap src, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(maxWidth / (double)src.Width, maxHeight / (double)src.Height);
        if (scale >= 1.0) return src;

        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (Gfx g = Gfx.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new Rectangle(0, 0, w, h));
        }

        return dst;
    }
}
