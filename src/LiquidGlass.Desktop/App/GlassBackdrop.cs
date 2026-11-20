using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Gfx = System.Drawing.Graphics;
using LiquidGlass.Desktop.Platform;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 把"卡片背后那一小块桌面"磨成玻璃底。
///
/// 分层窗口的逐像素 alpha 只能做到"半透明"，DWM 不会给WS_EX_LAYERED 的窗口
/// 做背景模糊，所以真玻璃只有两条路：整条链搬到 D3D（像 Dock 本体那样），
/// 或者自己截一小块、自己糊。卡片是 300 多像素的临时窗口，后者便宜得多：
/// BitBlt 一小块 → 缩到 1/6 做两次盒式模糊 → 放大回去画到卡上。
///
/// 结果就是卡片后面真的是"被磨过的壁纸"，而不是一层半透明的灰 —— 这也是
/// Dock 玻璃的观感来源，两者终于对得上了。
/// </summary>
internal static class GlassBackdrop
{
    /// <summary>
    /// 抓 (x,y,w,h) 这块屏幕并返回磨砂后的小图（等比缩小，放大由调用方做）。
    /// 抓不到（受保护内容 / 显示器休眠）时返回 null，调用方退回纯色底。
    /// </summary>
    public static Bitmap? Grab(int x, int y, int w, int h, float scale)
    {
        if (w < 8 || h < 8) return null;

        using Bitmap? shot = ScreenCapture.Grab(x, y, w, h);
        if (shot is null) return null;

        // 1/6 左右的分辨率就够糊了；高 DPI 下按缩放比放大，否则 150% 时糊得不够
        int factor = Math.Clamp((int)MathF.Round(6f * Math.Clamp(scale, 1f, 2f)), 4, 12);
        int sw = Math.Max(6, w / factor);
        int sh = Math.Max(6, h / factor);

        var small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        using (Gfx g = Gfx.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // 先铺不透的底再画：源图是 32bppRgb（无 alpha 通道），
            // 目标若留成透明底，后面 SourceOver 贴到卡片上会整块被擦掉。
            g.Clear(Color.Black);
            g.DrawImage(shot, new Rectangle(0, 0, sw, sh));
        }

        // 两次盒式模糊 ≈ 一次近似高斯的柔化。半径 3 在 1/6 尺寸上相当于
        // 原图 18px 的模糊，和 Dock 玻璃的糊度在同一量级。
        BoxBlur(small, 3);
        BoxBlur(small, 3);

        return small;
    }

    /// <summary>整幅平均亮度（0~255）。卡片据此决定用深色字还是浅色字。</summary>
    public static float AverageLuma(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            byte[] buf = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);

            double sum = 0;
            int count = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int i = row + x * 4;
                    sum += buf[i + 2] * 0.299 + buf[i + 1] * 0.587 + buf[i] * 0.114;
                    count++;
                }
            }

            return count == 0 ? 128f : (float)(sum / count);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>可分离盒式模糊，只处理 RGB（alpha 保持原样，卡片是整块不透明的）。</summary>
    private static void BoxBlur(Bitmap bmp, int radius)
    {
        if (radius < 1) return;

        int w = bmp.Width, h = bmp.Height;
        var rect = new Rectangle(0, 0, w, h);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            int stride = data.Stride;
            byte[] buf = new byte[stride * h];
            byte[] tmp = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);

            int div = radius * 2 + 1;

            // 横向
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int i = -radius; i <= radius; i++)
                        sum += buf[row + Math.Clamp(i, 0, w - 1) * 4 + c];

                    for (int x = 0; x < w; x++)
                    {
                        tmp[row + x * 4 + c] = (byte)(sum / div);
                        int add = Math.Clamp(x + radius + 1, 0, w - 1);
                        int sub = Math.Clamp(x - radius, 0, w - 1);
                        sum += buf[row + add * 4 + c] - buf[row + sub * 4 + c];
                    }
                }
            }

            // 纵向（从 tmp 读、写回 buf）
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int i = -radius; i <= radius; i++)
                        sum += tmp[Math.Clamp(i, 0, h - 1) * stride + col + c];

                    for (int y = 0; y < h; y++)
                    {
                        buf[y * stride + col + c] = (byte)(sum / div);
                        int add = Math.Clamp(y + radius + 1, 0, h - 1);
                        int sub = Math.Clamp(y - radius, 0, h - 1);
                        sum += tmp[add * stride + col + c] - tmp[sub * stride + col + c];
                    }
                }
            }

            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
