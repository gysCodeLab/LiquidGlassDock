using System.Drawing;
using Gfx = System.Drawing.Graphics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 设置面板与启动台的配色、字体、圆角绘制。
///
/// 为什么单独一层：两套界面的"卡片 / 文字 / 开关"必须是同一套观感，
/// 各画各的很快就会出现两种灰、两种圆角。这里只放纯绘制，不碰业务。
/// </summary>
internal static class UiTheme
{
    private static readonly bool Light = DetectLight();

    // ---- 深色（默认）----
    private static readonly Color DarkCardTop = Color.FromArgb(255, 34, 36, 43);
    private static readonly Color DarkCardBottom = Color.FromArgb(255, 24, 26, 32);
    private static readonly Color DarkEdge = Color.FromArgb(34, 255, 255, 255);
    private static readonly Color DarkText = Color.FromArgb(255, 240, 241, 245);
    private static readonly Color DarkDim = Color.FromArgb(255, 150, 156, 171);
    private static readonly Color DarkTrackOff = Color.FromArgb(255, 58, 62, 74);
    private static readonly Color DarkHover = Color.FromArgb(14, 255, 255, 255);
    private static readonly Color DarkSunken = Color.FromArgb(20, 0, 0, 0);

    // ---- 浅色 ----
    private static readonly Color LightCardTop = Color.FromArgb(255, 252, 252, 254);
    private static readonly Color LightCardBottom = Color.FromArgb(255, 242, 243, 247);
    private static readonly Color LightEdge = Color.FromArgb(26, 0, 0, 0);
    private static readonly Color LightText = Color.FromArgb(255, 26, 28, 33);
    private static readonly Color LightDim = Color.FromArgb(255, 107, 114, 128);
    private static readonly Color LightTrackOff = Color.FromArgb(255, 207, 212, 221);
    private static readonly Color LightHover = Color.FromArgb(10, 0, 0, 0);
    private static readonly Color LightSunken = Color.FromArgb(14, 0, 0, 0);

    public static Color CardTop => Light ? LightCardTop : DarkCardTop;
    public static Color CardBottom => Light ? LightCardBottom : DarkCardBottom;
    public static Color Edge => Light ? LightEdge : DarkEdge;
    public static Color Text => Light ? LightText : DarkText;
    public static Color Dim => Light ? LightDim : DarkDim;
    public static Color TrackOff => Light ? LightTrackOff : DarkTrackOff;
    public static Color Hover => Light ? LightHover : DarkHover;
    public static Color Sunken => Light ? LightSunken : DarkSunken;

    public static Color Accent => Color.FromArgb(255, 58, 122, 255);
    /// <summary>当前是不是浅色主题。淡色叠层（白 5% / 黑 5%）必须跟着换色，
    /// 否则浅色卡片上的按钮、分隔线会整块消失。</summary>
    public static bool LightTheme => Light;    public static Color AccentDim => Color.FromArgb(255, 46, 96, 200);
    public static Color Danger => Color.FromArgb(255, 226, 74, 74);
    public static Color Knob => Color.FromArgb(255, 255, 255, 255);
    public static Color KnobOff => Light ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 206, 211, 222);

    /// <summary>启动台的整屏底色（和 Dock 玻璃同一色系，切换时不会突然跳色）。</summary>
    public static Color BackdropTop => Light ? Color.FromArgb(255, 236, 238, 243) : Color.FromArgb(255, 22, 24, 29);

    private static bool DetectLight()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 字体

    private static readonly string Family = PickFamily();
    private static readonly Dictionary<(int Size, bool Bold), Font> Fonts = new();

    private static string PickFamily()
    {
        // 中文界面优先雅黑：它的小字号字形比默认字体饱满，12px 也不糊。
        foreach (string name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Arial" })
        {
            try
            {
                using var probe = new FontFamily(name);
                if (probe.Name.Length > 0) return name;
            }
            catch { /* 该字体不存在，试下一个 */ }
        }

        return FontFamily.GenericSansSerif.Name;
    }

    /// <summary>按**像素**取字体。GDI+ 默认单位是磅，12 磅在 100% 缩放下等于 16px，会整体偏大。</summary>
    public static Font Font(float px, bool bold = false)
    {
        int size = Math.Max(8, (int)MathF.Round(px));
        var key = (size, bold);
        if (Fonts.TryGetValue(key, out Font? cached)) return cached;

        var font = new Font(Family, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        Fonts[key] = font;
        return font;
    }

    // ---------------------------------------------------------------- 绘制

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = MathF.Min(radius * 2f, MathF.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Gfx g, RectangleF r, float radius, Brush brush)
    {
        using GraphicsPath path = Round(r, radius);
        g.FillPath(brush, path);
    }

    public static void FillRound(Gfx g, RectangleF r, float radius, Color color)
    {
        using var brush = new SolidBrush(color);
        FillRound(g, r, radius, brush);
    }

    public static void StrokeRound(Gfx g, RectangleF r, float radius, Color color, float width = 1f)
    {
        using GraphicsPath path = Round(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }

    /// <summary>统一开启抗锯齿。文字用灰度抗锯齿：ClearType 在透明底上会出彩边。</summary>
    public static void Prepare(Gfx g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
    }

    public static void DrawText(Gfx g, string text, Font font, Color color, float x, float y)
    {
        using var brush = new SolidBrush(color);
        g.DrawString(text, font, brush, x, y, StringFormat.GenericTypographic);
    }

    public static void DrawText(Gfx g, string text, Font font, Color color, PointF at)
        => DrawText(g, text, font, color, at.X, at.Y);

    public static SizeF Measure(Gfx g, string text, Font font)
        => g.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic);

    /// <summary>超宽就截断加省略号。用实际像素量，不靠字数猜。</summary>
    public static string Ellipsis(Gfx g, string text, Font font, float maxWidth)
    {
        if (maxWidth <= 0) return "";
        if (Measure(g, text, font).Width <= maxWidth) return text;

        const string Tail = "…";
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            string probe = text[..mid] + Tail;
            if (Measure(g, probe, font).Width <= maxWidth) lo = mid;
            else hi = mid - 1;
        }

        return lo <= 0 ? Tail : text[..lo] + Tail;
    }

    /// <summary>外阴影：多层低透明度圆角矩形叠出柔和过渡，比高斯模糊便宜得多。</summary>
    public static void DrawShadow(Gfx g, RectangleF card, float radius, int spread)
    {
        for (int i = spread; i >= 1; i--)
        {
            float grow = i * 1.35f;
            var r = RectangleF.Inflate(card, grow, grow);
            r.Offset(0, i * 0.9f);
            int alpha = (int)MathF.Max(1f, 16f - i * 1.6f);
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            FillRound(g, r, radius + grow, brush);
        }
    }

    /// <summary>开关：胶囊轨道 + 圆形滑块。on 时滑块在右，关在左。</summary>
    public static void DrawToggle(Gfx g, RectangleF track, bool on, float progress = -1f)
    {
        float t = progress < 0 ? (on ? 1f : 0f) : Math.Clamp(progress, 0f, 1f);
        float radius = track.Height * 0.5f;

        Color off = TrackOff;
        Color fill = Blend(off, Accent, t);
        FillRound(g, track, radius, fill);

        // 开启时给轨道一圈很淡的同色光晕，视觉上"亮起来"
        if (t > 0.02f)
        {
            using var glow = new Pen(Color.FromArgb((int)(46 * t), Accent), 1f);
            using GraphicsPath path = Round(RectangleF.Inflate(track, 0.5f, 0.5f), radius + 0.5f);
            g.DrawPath(glow, path);
        }

        float inset = track.Height * 0.115f;
        float diameter = track.Height - inset * 2f;
        float left = track.Left + inset;
        float right = track.Right - inset - diameter;
        float cx = left + (right - left) * t;

        var knob = new RectangleF(cx, track.Top + inset, diameter, diameter);
        using var shadow = new SolidBrush(Color.FromArgb(46, 0, 0, 0));
        FillRound(g, new RectangleF(knob.X, knob.Y + 1f, knob.Width, knob.Height), diameter * 0.5f, shadow);
        FillRound(g, knob, diameter * 0.5f, on ? Knob : KnobOff);
    }

    public static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}
