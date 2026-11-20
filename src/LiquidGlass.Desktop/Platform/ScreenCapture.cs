using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 屏幕截图（GDI BitBlt）。用于"真实运行效果"验证。
/// 注意：Dock 窗口平时带 WDA_EXCLUDEFROMCAPTURE，截图前必须临时关掉，
/// 否则拍出来是一块透明的洞。
/// </summary>
internal static class ScreenCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public uint[] bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int sx, int sy, uint rop);

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    /// <summary>
    /// 抓取屏幕矩形，返回 32 位位图（BGRX，忽略 alpha）。
    /// 卡片背景的"玻璃底"就是拿它磨出来的 —— 只抓一次、抓的是一小块，
    /// 比全程跑 DXGI 桌面复制便宜三个数量级。
    /// </summary>
    public static Bitmap? Grab(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        byte[]? pixels = GrabPixels(x, y, w, h);
        if (pixels is null) return null;

        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            int rowBytes = w * 4;
            for (int row = 0; row < h; row++)
                Marshal.Copy(pixels, row * rowBytes, data.Scan0 + row * data.Stride, rowBytes);
        }
        catch
        {
            bmp.Dispose();
            return null;
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return bmp;
    }

    /// <summary>抓取屏幕矩形并写成 32 位 BMP（自底向上，BGRX）。</summary>
    public static bool SaveBmp(int x, int y, int w, int h, string path)
    {
        if (w <= 0 || h <= 0) return false;

        byte[]? grabbed = GrabPixels(x, y, w, h);
        if (grabbed is null) return false;
        byte[] pixels = grabbed;

        // 写 BMP：自底向上，所以把行倒过来
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        int rowSize = w * 4;
        int imageSize = rowSize * h;
        bw.Write((ushort)0x4D42);
        bw.Write(14 + 40 + imageSize);
        bw.Write(0);
        bw.Write(14 + 40);
        bw.Write(40);
        bw.Write(w);
        bw.Write(h);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(imageSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);

        for (int row = h - 1; row >= 0; row--)
            bw.Write(pixels, row * rowSize, rowSize);

        return true;
    }

    /// <summary>真正的 GDI 抓取：返回自下而上、BGRX 的裸像素。</summary>
    private static byte[]? GrabPixels(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr bits = IntPtr.Zero;

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,   // 负数 = 自上而下，方便直接写 BMP 时翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
            bmiColors = new uint[3],
        };

        IntPtr dib = CreateDIBSection(memDc, ref bmi, 0, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        IntPtr old = SelectObject(memDc, dib);

        // 负坐标（副屏在左边）时 BitBlt 会失败，这里夹取到虚拟桌面范围内
        int vx = GetSystemMetrics(76);  // SM_XVIRTUALSCREEN
        int vy = GetSystemMetrics(77);  // SM_YVIRTUALSCREEN
        int vw = GetSystemMetrics(78);  // SM_CXVIRTUALSCREEN
        int vh = GetSystemMetrics(79);  // SM_CYVIRTUALSCREEN

        int sx = Math.Clamp(x, vx, vx + vw);
        int sy = Math.Clamp(y, vy, vy + vh);

        BitBlt(memDc, 0, 0, w, h, screenDc, sx, sy, SRCCOPY | CAPTUREBLT);

        byte[] pixels = new byte[w * h * 4];
        Marshal.Copy(bits, pixels, 0, pixels.Length);

        SelectObject(memDc, old);
        DeleteObject(dib);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        return pixels;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
