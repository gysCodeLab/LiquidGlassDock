using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Gfx = System.Drawing.Graphics;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 系统壁纸渲染器：把当前壁纸按「填充」模式铺成整屏像素图。
///
/// 为什么启动台必须用它、不能抓屏：
/// Dock 贴在屏幕最底部，它背后永远是壁纸，所以 BitBlt 抓到的就是壁纸 ——
/// 这就是 Dock 玻璃看着干净的全部原因。启动台是整屏覆盖的，同一份 BitBlt
/// 抓到的是「壁纸 + 桌面上那 80 个图标」，玻璃再把它原样显示一遍，
/// 面板里就会浮出一层桌面图标的残影（左上角那张「夏夜的烟火」文件夹就是这么来的），
/// 整体发脏发灰，跟离线预览里那块干净的玻璃完全是两个东西。
///
/// 换成壁纸本身以后：内容干净、不抓到自己、每次打开都是同一张底，
/// 观感和离屏渲染（--lab）终于一致了。
/// </summary>
internal static class WallpaperSource
{
    private const int SPI_GETDESKWALLPAPER = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(int action, int capacity, StringBuilder buffer, int winIni);

    private static Bitmap? _cached;
    private static string? _cachedKey;

    /// <summary>
    /// 整屏壁纸（显示器像素尺寸，填充模式）。换壁纸后自动失效重算。
    /// 返回的对象由本类持有，调用方**不要 Dispose**。
    /// </summary>
    public static Bitmap? Monitor(int width, int height)
    {
        if (width < 8 || height < 8) return null;

        string? path = CurrentPath();
        if (path is null) return _cached;

        string key = path + "|" + Stamp(path) + "|" + width + "x" + height;
        if (_cached is not null && _cachedKey == key) return _cached;

        Bitmap? rendered = Render(path, width, height);
        // 解码失败就继续用上一张：宁可显示旧壁纸，也不要把玻璃底换成纯色
        if (rendered is null) return _cached;

        _cached?.Dispose();
        _cached = rendered;
        _cachedKey = key;
        App.Logger.Info($"[wallpaper] 壁纸底图就绪 {width}x{height} ← {Path.GetFileName(path)}");
        return _cached;
    }

    private static long Stamp(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch { return 0; }
    }

    private static string? CurrentPath()
    {
        // SPI 给的是「正在生效」的那张，幻灯片模式下也是当前这张
        var sb = new StringBuilder(520);
        if (SystemParametersInfo(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0))
        {
            string p = sb.ToString();
            if (p.Length > 2 && File.Exists(p)) return p;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key?.GetValue("WallPaper") is string reg && reg.Length > 2 && File.Exists(reg)) return reg;
        }
        catch
        {
            // 注册表读不到不算错，下面还有转码副本兜底
        }

        // 第三顺位：Windows 自己转码出来的副本（纯色桌面时它也不存在）
        string transcoded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        return File.Exists(transcoded) ? transcoded : null;
    }

    private static Bitmap? Render(string path, int width, int height)
    {
        try
        {
            // 先读进内存再解码：Image.FromFile 会把文件锁住，
            // 用户正在换壁纸时这边一锁，资源管理器那头就删不掉旧图。
            byte[] bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            using Image src = Image.FromStream(stream, false, false);

            var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using (Gfx g = Gfx.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                // Windows 的「填充」：等比放大到铺满整屏，多出来的居中裁掉。
                // 这里必须用 double 算 —— 2560x1600 上 0.4px 的舍入就会让画面整体偏一条边，
                // 玻璃折射把边缘拉进来以后，那一条边会被放大成一道亮缝。
                double k = Math.Max(width / (double)src.Width, height / (double)src.Height);
                double dw = src.Width * k;
                double dh = src.Height * k;
                g.DrawImage(src,
                    (float)((width - dw) * 0.5), (float)((height - dh) * 0.5),
                    (float)dw, (float)dh);
            }

            return bmp;
        }
        catch (Exception ex)
        {
            App.Logger.Warn("[wallpaper] 壁纸解码失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 从整屏壁纸上裁一块（源坐标以显示器左上角为原点）。越界部分留黑，
    /// 边缘那一圈黑只在折射带上出现，正常布局下面板四周有 48px 余量，碰不到。
    /// </summary>
    public static Bitmap? Crop(Bitmap src, int x, int y, int width, int height)
    {
        if (width < 8 || height < 8) return null;

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using Gfx g = Gfx.FromImage(bmp);
            g.Clear(Color.Black);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            int sx = Math.Max(0, x);
            int sy = Math.Max(0, y);
            int dx = sx - x;
            int dy = sy - y;
            int w = Math.Min(width - dx, src.Width - sx);
            int h = Math.Min(height - dy, src.Height - sy);
            if (w <= 0 || h <= 0) return bmp;

            g.DrawImage(src,
                new Rectangle(dx, dy, w, h),
                new Rectangle(sx, sy, w, h),
                GraphicsUnit.Pixel);
            return bmp;
        }
        catch (Exception ex)
        {
            App.Logger.Warn("[wallpaper] 裁剪失败: " + ex.Message);
            bmp.Dispose();
            return null;
        }
    }
}
