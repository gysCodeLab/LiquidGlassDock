using System.Runtime.InteropServices;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 从 Windows Shell 拿高质量应用图标（256px 级别），
/// 再转成 GPU 可用的预乘 BGRA 位图。
/// </summary>
internal static class IconLoader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
        CropToSquare = 0x20,
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object shellItem);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

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

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
        [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, uint step, IntPtr brush, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    // 跨窗口共享的图标缓存：Dock 和启动台请求同一张图标时不重复走 Shell。
    // 启动台第一次打开要画 30+ 个桌面图标，冷缓存下逐个 GetImage 能把 UI 线程卡住好几秒。
    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string Path, int Size), byte[]> Cache = new();

    /// <summary>后台预热：只负责把像素放进缓存，绘制代码下次 Load 直接命中。</summary>
    public static void Warm(string path, int size) => Load(path, size);

    /// <summary>返回预乘 alpha 的 BGRA 像素，尺寸为 size×size。</summary>
    public static byte[]? Load(string path, int size)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue((path, size), out byte[]? hit)) return hit;
        }

        byte[]? pixels = LoadUncached(path, size);
        if (pixels is not null)
        {
            lock (CacheLock)
            {
                // 图标总量有限（桌面 + 固定项），超过这个数说明路径很杂，直接重来
                if (Cache.Count > 1024) Cache.Clear();
                Cache[(path, size)] = pixels;
            }
        }
        return pixels;
    }

    private static byte[]? LoadUncached(string path, int size)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out object item);
            var factory = (IShellItemImageFactory)item;
            int hr = factory.GetImage(new SIZE(size, size), SIIGBF.BiggerSizeOk | SIIGBF.IconOnly, out hbm);
            if (hr < 0 || hbm == IntPtr.Zero)
            {
                hbm = IntPtr.Zero;
                return LoadLegacy(path, size);
            }

            return FromHBitmap(hbm, size);
        }
        catch
        {
            return LoadLegacy(path, size);
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
        }
    }

    private static byte[]? FromHBitmap(IntPtr hbm, int size)
    {
        var bmp = new BITMAP();
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bmp) == 0 || bmp.bmWidth <= 0) return null;
        int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
            bmiColors = new uint[3],
        };

        byte[] pixels = new byte[w * h * 4];
        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref bmi, 0) == 0) return null;
        }
        finally
        {
            DeleteDC(hdc);
        }

        return PostProcess(pixels, w, h, size);
    }

    private static byte[]? LoadLegacy(string path, int size)
    {
        if (ExtractIconEx(path, 0, out IntPtr large, out IntPtr small, 1) == 0 || large == IntPtr.Zero)
        {
            if (small != IntPtr.Zero) DestroyIcon(small);
            return null;
        }

        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = size,
                biHeight = -size,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
            bmiColors = new uint[3],
        };

        IntPtr hdc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            IntPtr dib = CreateDIBSection(hdc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) return null;

            IntPtr old = SelectObject(hdc, dib);
            DrawIconEx(hdc, 0, 0, large, size, size, 0, IntPtr.Zero, 0x0003 /* DI_NORMAL */);
            SelectObject(hdc, old);

            byte[] pixels = new byte[size * size * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            DeleteObject(dib);
            DestroyIcon(large);
            if (small != IntPtr.Zero) DestroyIcon(small);

            return PostProcess(pixels, size, size, size);
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    /// <summary>缩放到目标尺寸 + 预乘 alpha + 修正老式图标的全零 alpha。</summary>
    private static byte[] PostProcess(byte[] src, int sw, int sh, int target)
    {
        bool anyAlpha = false;
        for (int i = 3; i < src.Length; i += 4)
        {
            if (src[i] != 0) { anyAlpha = true; break; }
        }

        byte[] dst = new byte[target * target * 4];
        for (int y = 0; y < target; y++)
        {
            int sy = sh == target ? y : (int)((long)y * sh / target);
            for (int x = 0; x < target; x++)
            {
                int sx = sw == target ? x : (int)((long)x * sw / target);
                int si = (sy * sw + sx) * 4;
                int di = (y * target + x) * 4;

                byte b = src[si], g = src[si + 1], r = src[si + 2];
                byte a = anyAlpha ? src[si + 3] : (byte)((r | g | b) == 0 && (r + g + b) == 0 ? 0 : 255);

                // 预乘
                dst[di] = (byte)(b * a / 255);
                dst[di + 1] = (byte)(g * a / 255);
                dst[di + 2] = (byte)(r * a / 255);
                dst[di + 3] = a;
            }
        }

        return MaskIfSolidTile(dst, target);
    }

    /// <summary>
    /// 不少 Windows 旧图标是一张完全不透明的方块底（calc.exe 就是），直接贴到
    /// 玻璃 Dock 上会变成一块突兀的方砖、并留下两条硬边。按 macOS 处理非原生
    /// 图标的做法给它套一层圆角遮罩，变成规整的 App 图块。带透明通道的图标不动。
    /// </summary>
    private static byte[] MaskIfSolidTile(byte[] px, int size)
    {
        int n = size * size;
        int transparent = 0;
        for (int i = 3; i < px.Length; i += 4)
            if (px[i] < 8) transparent++;

        if (transparent * 100.0 / n > 1.0) return px;   // 本身有透明通道，保留原样

        float half = size * 0.5f;
        float r = size * 0.22f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float dx = MathF.Abs(x + 0.5f - half) - (half - r);
            float dy = MathF.Abs(y + 0.5f - half) - (half - r);
            float ox = MathF.Max(dx, 0f), oy = MathF.Max(dy, 0f);
            float d = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(dx, dy), 0f) - r;
            float cov = Math.Clamp(0.5f - d, 0f, 1f);

            int i = (y * size + x) * 4;
            int a = px[i + 3];
            int na = (int)(a * cov);
            if (na == a) continue;
            if (a > 0)
            {
                px[i] = (byte)(px[i] * na / a);
                px[i + 1] = (byte)(px[i + 1] * na / a);
                px[i + 2] = (byte)(px[i + 2] * na / a);
            }
            px[i + 3] = (byte)na;
        }

        return px;
    }
}
