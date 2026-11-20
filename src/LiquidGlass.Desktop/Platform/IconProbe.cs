using System.Runtime.InteropServices;
using LiquidGlass.Desktop.App;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 诊断用：把每个 Dock 项目的图标加载结果落盘 + 打统计，
/// 用来把"图标加载失败"和"图标绘制失败"两件事彻底分开。
/// </summary>
internal static class IconProbe
{
    public static int Run(string outDir, int size)
    {
        Directory.CreateDirectory(outDir);
        DockConfig cfg = DockConfig.Load();
        Logger.Info($"图标诊断: {cfg.Items.Count} 项, 目标尺寸 {size}px → {outDir}");

        int index = 0;
        foreach (DockItemConfig item in cfg.Items)
        {
            byte[]? px = IconLoader.Load(item.Path, size);
            if (px is null)
            {
                Logger.Warn($"  [{index}] {item.Name}: Load=null（会走占位矩形）");
                index++;
                continue;
            }

            int opaque = 0, transparent = 0, maxA = 0;
            long sumA = 0;
            for (int i = 3; i < px.Length; i += 4)
            {
                int a = px[i];
                sumA += a;
                if (a == 0) transparent++;
                else if (a >= 250) opaque++;
                if (a > maxA) maxA = a;
            }

            int n = size * size;
            Logger.Info($"  [{index}] {item.Name}: maxAlpha={maxA} avgAlpha={sumA / (double)n:F1} " +
                        $"opaque={opaque * 100.0 / n:F1}% transparent={transparent * 100.0 / n:F1}%");

            // 铺到棋盘上，直接看图标是否真的画出来了（预乘 → 直通）
            byte[] view = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                int a = px[i + 3];
                bool dark = ((x / 8) + (y / 8)) % 2 == 0;
                int bg = dark ? 70 : 200;
                int b = px[i] + bg * (255 - a) / 255;
                int g = px[i + 1] + bg * (255 - a) / 255;
                int r = px[i + 2] + bg * (255 - a) / 255;
                view[i] = (byte)Math.Clamp(b, 0, 255);
                view[i + 1] = (byte)Math.Clamp(g, 0, 255);
                view[i + 2] = (byte)Math.Clamp(r, 0, 255);
                view[i + 3] = 255;
            }

            string safe = string.Concat(item.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
            MaterialLab.WriteBmp(Path.Combine(outDir, $"icon{index}-{safe}.bmp"), size, size, view);
            index++;
        }

        return 0;
    }
}
