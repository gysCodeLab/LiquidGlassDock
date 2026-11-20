using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Gfx = System.Drawing.Graphics;
using LiquidGlass.Desktop.Platform;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 悬停在 Dock 图标上时，从图标上方浮出来的窗口预览卡片。
///
/// 版式只有两层，且**没有任何内边框**：
/// - 顶部一条 26px 的标题带：应用图标 + 名字 + 右上角 ×；
/// - 下面整块就是窗口画面，左右下三边贴着卡片边缘，只受卡片圆角裁剪。
/// 卡片高度按目标窗口的宽高比算出来，所以 DWM 缩略图正好填满，
/// 不会在画面四周留出一圈"框套框"。
///
/// 玻璃本体不是自己画的：交给 <see cref="DockApp.RenderGlassCard"/> 用 Dock
/// 同一条着色器离屏烘一张图（模糊、奶白、折射、边光、外阴影全在里面），
/// 这里只负责把内容叠上去。GPU 不可用时退回 CPU 磨砂（GlassBackdrop）。
///
/// 两个约束写死在设计里：
/// - **绝不抢焦点**（WM_MOUSEACTIVATE → MA_NOACTIVATE）。否则用户只是想看看
///   那个窗口在干什么，正在打字的窗口就被踩到后台去了。
/// - **拿不到画面就明说**。DRM 窗口、画不出来的硬件加速窗口给的是黑帧，
///   与其糊一张黑图，不如画图标 + 一句"暂时拿不到预览"。
/// </summary>
internal sealed class WindowPreviewCard : LayeredWindow
{
    // ---- 逻辑尺寸 ----
    private const float ShadowPx = 10f;     // 四周留给外阴影的透明边（和着色器 ShadowExtent 一致）
    private const float HeaderH = 26f;      // 顶部标题带
    private const float PadX = 10f;
    private const float IconPx = 16f;
    private const float CloseBtn = 16f;

    private const float ThumbMin = 110f;    // 画面最小高度
    private const float ThumbMax = 260f;    // 画面最大高度（很高的窗口会上下留一点）
    private const float ThumbW = 300f;      // 画面宽度（固定，高度随窗口比例）

    /// <summary>卡片最"竖"能到多少：3:2。竖窗口的卡片也压在这条线上。</summary>
    private const float LandscapeAspect = 1.5f;

    /// <summary>卡片最"宽"能到多少：2.6:1。再宽的窗口（带鱼屏）稍微裁掉两边。</summary>
    private const float WideAspect = 2.6f;

    private IntPtr _target;
    private string _appName = "";
    private bool _pinned;

    // 每次 Present 按目标窗口比例算出来的实际尺寸（逻辑像素）
    private float _cardW = ThumbW;
    private float _cardH = HeaderH + 180f;

    private Bitmap? _icon;
    private Bitmap? _thumb;

    /// <summary>卡片玻璃底。优先是 GPU 烘出来的（和 Dock 同一套着色器）。</summary>
    private Bitmap? _glass;
    private bool _glassFromGpu;

    /// <summary>色调偏暗时用浅色字，偏亮时用深色字 —— 玻璃底的颜色是背景给的。</summary>
    private bool _inkLight = true;

    /// <summary>缩略图区是不是由 DWM 实时合成（是的话就不用自己抓帧）。</summary>
    private bool _live;

    /// <summary>拿不到画面时显示的说明文字。</summary>
    private string _placeholder = "暂时拿不到预览";

    /// <summary>DWM 实时缩略图的宿主窗口（盖在缩略图区上）。</summary>
    private readonly PreviewThumbnailHost _host = new();

    /// <summary>0 = 无，1 = 右上角 ×，2 = 左上角应用图标，3 = 画面。</summary>
    private int _hover;

    public WindowPreviewCard(float scale) : base("LiquidGlass.PreviewCard", scale) { }

    public IntPtr Target => _target;

    /// <summary>
    /// 玻璃底图提供者：签名是 (屏幕X, 屏幕Y, 宽, 高, 圆角, 阴影) → 预乘位图。
    /// 由 Dock 注入（它才有 D3D 设备、着色器和桌面采样）。返回 null 表示退回 CPU 磨砂。
    /// </summary>
    public Func<int, int, int, int, float, float, Bitmap?>? GlassProvider;

    public Action<IntPtr>? ActivateRequested;
    public Action? CloseRequested;
    public Action? PinToggleRequested;

    protected override bool AllowActivate => false;

    // ---------------------------------------------------------------- 布局

    private RectangleF CardRect =>
        new(S(ShadowPx), S(ShadowPx), S(_cardW), S(_cardH));

    private RectangleF IconRect => new(
        CardRect.X + S(PadX),
        CardRect.Y + (S(HeaderH) - S(IconPx)) * 0.5f,
        S(IconPx), S(IconPx));

    /// <summary>右上角的关闭 ×。</summary>
    private RectangleF CloseRect => new(
        CardRect.Right - S(PadX) - S(CloseBtn),
        CardRect.Y + (S(HeaderH) - S(CloseBtn)) * 0.5f,
        S(CloseBtn), S(CloseBtn));

    /// <summary>画面区：左右下三边贴死卡片边缘，只被卡片圆角裁掉四个角。</summary>
    private RectangleF ThumbRect => new(
        CardRect.X, CardRect.Y + S(HeaderH), CardRect.Width, CardRect.Height - S(HeaderH));

    private float CardRadius => S(13f);

    // ---------------------------------------------------------------- 生命周期

    /// <summary>
    /// 显示卡片。centerX = 图标的屏幕中心 X，bottomY = 卡片（玻璃面板）的下沿。
    /// </summary>
    public void Present(IntPtr target, string appName, string iconPath, bool pinned,
        int centerX, int bottomY)
    {
        _target = target;
        _appName = appName;
        _pinned = pinned;

        _icon?.Dispose();
        _icon = LoadIcon(iconPath, S(IconPx * 2f));

        Measure(target);

        // 已经在显示 = 只是从一个图标挪到旁边一个。这种时候不重新烘玻璃：
        // 重截会把上一张卡也截进背景里，糊出来带鬼影。
        // 前提是尺寸没变 —— 换到另一个窗口时比例可能不同，尺寸一变就必须重烘。
        int wantW = (int)MathF.Round((_cardW + ShadowPx * 2f) * Scale);
        int wantH = (int)MathF.Round((_cardH + ShadowPx * 2f) * Scale);
        bool moving = IsVisible && _glass is not null
                      && Math.Abs(_glass.Width - wantW) <= 1 && Math.Abs(_glass.Height - wantH) <= 1;

        Create(_cardW + ShadowPx * 2f, _cardH + ShadowPx * 2f);

        int x = centerX - PixelWidth / 2;
        int y = bottomY - S(ShadowPx) - S(_cardH);

        // 贴边收纳 + 顶部别跑出屏幕
        int screenW = Native.GetSystemMetrics(0);
        x = Math.Clamp(x, S(2f), Math.Max(S(2f), screenW - PixelWidth - S(2f)));
        y = Math.Max(y, S(2f));

        if (!moving)
        {
            // 先把自己藏起来，否则截到的是"上一张卡还在屏幕上"的画面
            HideWindow();
            _host.Hide();
            BakeGlass(x, y);
        }

        // 先乐观地认为能拿到实时画面：这样 ShowAt 的第一帧不会先画一层占位、
        // 再被 LoadPreview 改掉（那一帧的闪烁在画面区很明显）。
        // 最小化窗口也一样 —— DWM 会保留它最后一次的合成帧，照样画得出来。
        _live = true;

        ShowAt(x, y);
        LoadPreview(target, x, y);

        Logger.Info($"[preview] 卡片: {appName} target=0x{target.ToInt64():X} " +
                    $"{(  _live ? "实时" : _thumb is not null ? "静帧" : "占位")} " +
                    $"{PixelWidth}x{PixelHeight} " +
                    $"玻璃={(_glass is null ? "无" : _glassFromGpu ? "GPU" : "CPU")} @({x},{y})");
    }

    /// <summary>
    /// 卡片尺寸。**一律横屏**：卡片高度只跟窗口宽高比走，但比例下限锁在 3:2 ——
    /// 一个竖着的窗口（聊天框、工具箱、侧边面板）过去会把卡片顶成一根竖条，
    /// 与旁边那些横向卡片排在一起参差不齐。内容按 3:2 的取景框裁一块，
    /// 比例不动、不拉伸，见 PreviewThumbnailHost.ShowFor。
    /// </summary>
    private void Measure(IntPtr target)
    {
        float aspect = 1.6f;

        // 最小化窗口的 GetWindowRect 不可信，统一走 WindowThumbnail.TargetSize
        if (WindowThumbnail.TargetSize(target, out int w, out int h) && w > 120 && h > 120)
            aspect = w / (float)h;

        aspect = Math.Clamp(aspect, LandscapeAspect, WideAspect);

        _cardW = ThumbW;
        _cardH = HeaderH + Math.Clamp(ThumbW / aspect, ThumbMin, ThumbMax);
    }

    /// <summary>准备玻璃底：先问 Dock 要（同一条着色器），拿不到再用 CPU 磨一张。</summary>
    private void BakeGlass(int cardX, int cardY)
    {
        _glass?.Dispose();
        _glass = null;
        _glassFromGpu = false;

        Bitmap? gpu = GlassProvider?.Invoke(cardX, cardY, PixelWidth, PixelHeight,
            CardRadius, S(ShadowPx));
        if (gpu is not null)
        {
            _glass = gpu;
            _glassFromGpu = true;
        }
        else
        {
            _glass = GlassBackdrop.Grab(cardX, cardY, PixelWidth, PixelHeight, Scale);
        }

        if (_glass is null)
        {
            Logger.Warn("[preview] 玻璃底: 抓不到画面，退回纯色渐变");
            return;
        }

        // 字色只看面板本身（不含外阴影）：阴影是透明的，会把平均值拉黑
        int inset = S(ShadowPx);
        var panel = new Rectangle(inset, inset,
            Math.Max(1, _glass.Width - inset * 2), Math.Max(1, _glass.Height - inset * 2));

        float luma;
        using (Bitmap probe = _glass.Clone(panel, PixelFormat.Format32bppArgb))
            luma = GlassBackdrop.AverageLuma(probe);

        _inkLight = luma < 118f;
        Logger.Info($"[preview] 玻璃底: {_glass.Width}x{_glass.Height} " +
                    $"{(_glassFromGpu ? "GPU" : "CPU")} 面板亮度={luma:0} → " +
                    (_inkLight ? "深底浅字" : "浅底深字"));
    }

    private void LoadPreview(IntPtr target, int cardX, int cardY)
    {
        _thumb?.Dispose();
        _thumb = null;
        _live = false;
        _placeholder = "暂时拿不到预览（窗口受保护或未响应）";

        // 最小化窗口不去特判：DWM 保留了它最后一次的合成帧，DwmRegisterThumbnail
        // 照样能拿到画面（实测 Win11 上完全可用）。以前这里是"最小化就短路",
        // 结果桌面上窗口全收起来之后，卡片对每一个应用都只写「窗口已最小化」——
        // 而这恰恰是最想看一眼里面是什么的时候。
        RectangleF tr = ThumbRect;
        _live = _host.ShowFor(target, cardX + (int)tr.X, cardY + (int)tr.Y,
            (int)tr.Width, (int)tr.Height, (int)CardRadius);

        if (!_live)
        {
            _thumb = WindowThumbnail.Capture(target, (int)tr.Width, (int)tr.Height);
            if (_thumb is null && Native.IsIconic(target))
                _placeholder = "窗口已最小化，暂时拿不到画面";
        }

        Invalidate();
    }

    public void Hide()
    {
        // 每帧都会调到这里（悬停预览关掉时更是每帧），没显示就别碰窗口 ——
        // ShowWindow/UnregisterThumbnail 每帧各来一次是白花。
        if (!IsVisible && !_host.Visible) return;

        _host.Hide();
        if (!IsVisible) return;
        HideWindow();
        _hover = 0;
        Logger.Info("[preview] 卡片已收起");
    }

    public bool Visible => IsVisible;

    /// <summary>鼠标（屏幕坐标）是否压在卡片上。父窗口据此决定"要不要收起"。</summary>
    public bool ContainsCursor(int screenX, int screenY)
    {
        if (!IsVisible) return false;
        if (!TryGetScreenBounds(out int x, out int y, out int w, out int h)) return false;
        return screenX >= x && screenX < x + w && screenY >= y && screenY < y + h;
    }

    /// <summary>
    /// 玻璃面板本身的屏幕矩形（不含四周那圈透明阴影）。
    /// 父窗口拿它拼"图标 → 卡片"的竖直通道：插件判定要按真实可见边界算，
    /// 把 10px 阴影也算进去的话，鼠标明明已经离开卡片了它还赖着不走。
    /// </summary>
    public bool TryGetPanelBounds(out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        if (!IsVisible) return false;
        if (!TryGetScreenBounds(out int wx, out int wy, out _, out _)) return false;

        x = wx + S(ShadowPx);
        y = wy + S(ShadowPx);
        w = S(_cardW);
        h = S(_cardH);
        return true;
    }

    /// <summary>重新抓一帧画面（内容会变，卡片开着的时候定期刷）。</summary>
    public void RefreshThumbnail()
    {
        if (!IsVisible || _target == IntPtr.Zero) return;
        if (_live) return;      // DWM 那边本来就是实时的，别自己再抓
        Bitmap? fresh = WindowThumbnail.Capture(_target, (int)ThumbRect.Width, (int)ThumbRect.Height);
        if (fresh is null) return;      // 抓失败就保留上一张，别把好好的图换成黑块
        _thumb?.Dispose();
        _thumb = fresh;
        Invalidate();
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnPaint(Gfx g, int width, int height)
    {
        RectangleF card = CardRect;

        if (_glass is not null && _glassFromGpu)
        {
            // GPU 玻璃：模糊、奶白、折射、边光、外阴影已经全在图上，整张直接贴
            g.DrawImage(_glass, 0, 0, _glass.Width, _glass.Height);
        }
        else
        {
            DrawCardShadow(g, card, CardRadius);
            DrawCpuGlass(g, card, CardRadius);
        }

        if (!_live) DrawThumbFallback(g, card);
        DrawHeader(g);
        DrawCloseButton(g);
    }

    /// <summary>CPU 兜底的外阴影（GPU 那条路上阴影是着色器画的）。</summary>
    private void DrawCardShadow(Gfx g, RectangleF card, float radius)
    {
        const int layers = 22;
        for (int i = layers; i >= 1; i--)
        {
            float grow = i * MathF.Max(0.9f, S(0.9f));
            var r = RectangleF.Inflate(card, grow, grow);
            r.Offset(0, grow * 0.5f);

            int alpha = (int)MathF.Max(1f, 2f + 15f * (1f - i / (float)layers));
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            UiTheme.FillRound(g, r, radius + grow, brush);
        }
    }

    /// <summary>CPU 兜底的玻璃：磨过的壁纸 + 淡色罩 + 上亮下暗的边光。</summary>
    private void DrawCpuGlass(Gfx g, RectangleF card, float radius)
    {
        using GraphicsPath clip = UiTheme.Round(card, radius);
        GraphicsState state = g.Save();
        g.SetClip(clip, CombineMode.Intersect);

        if (_glass is not null)
            g.DrawImage(_glass, RectangleF.Inflate(card, S(1f), S(1f)));
        else
        {
            using var fallback = new LinearGradientBrush(card, UiTheme.CardTop, UiTheme.CardBottom, 90f);
            g.FillRectangle(fallback, card);
        }

        Color tintTop = _inkLight ? Color.FromArgb(44, 226, 231, 242) : Color.FromArgb(58, 255, 255, 255);
        Color tintBottom = _inkLight ? Color.FromArgb(28, 200, 206, 222) : Color.FromArgb(42, 245, 248, 253);
        using (var tint = new LinearGradientBrush(card, tintTop, tintBottom, 90f))
            g.FillRectangle(tint, card);

        g.Restore(state);

        Color top = _inkLight ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(210, 255, 255, 255);
        Color bottom = _inkLight ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(34, 0, 0, 0);
        using var brush = new LinearGradientBrush(card, top, bottom, 90f);
        using var pen = new Pen(brush, MathF.Max(1f, S(1f)));
        using GraphicsPath rim = UiTheme.Round(RectangleF.Inflate(card, 0.5f, 0.5f), radius + 0.5f);
        g.DrawPath(pen, rim);
    }

    /// <summary>
    /// 没有实时画面时才画：静帧按"铺满裁切"的方式画（等比留边就会变成一圈内框，
    /// 正是要消掉的东西），或者给占位说明。全部裁在卡片圆角里，下沿两个角跟着卡片走。
    /// </summary>
    private void DrawThumbFallback(Gfx g, RectangleF card)
    {
        RectangleF r = ThumbRect;

        using GraphicsPath clip = UiTheme.Round(card, CardRadius);
        GraphicsState state = g.Save();
        g.SetClip(clip, CombineMode.Intersect);

        if (_thumb is not null)
        {
            float scale = Math.Max(r.Width / _thumb.Width, r.Height / _thumb.Height);
            float w = _thumb.Width * scale;
            float h = _thumb.Height * scale;
            g.DrawImage(_thumb, r.Left + (r.Width - w) * 0.5f, r.Top + (r.Height - h) * 0.5f, w, h);
        }
        else
        {
            using (var bg = new SolidBrush(_inkLight
                       ? Color.FromArgb(48, 0, 0, 0)
                       : Color.FromArgb(30, 255, 255, 255)))
                g.FillRectangle(bg, r);
            DrawNoPreview(g, r);
        }

        g.Restore(state);
    }

    private void DrawNoPreview(Gfx g, RectangleF r)
    {
        Font font = UiTheme.Font(S(11f));
        float size = S(46f);
        float cx = r.Left + r.Width * 0.5f;

        if (_icon is not null)
            g.DrawImage(_icon, new RectangleF(cx - size * 0.5f, r.Top + r.Height * 0.5f - size * 0.72f,
                size, size));

        string shown = UiTheme.Ellipsis(g, _placeholder, font, r.Width - S(24f));
        SizeF m = UiTheme.Measure(g, shown, font);
        UiTheme.DrawText(g, shown, font, InkDim, cx - m.Width * 0.5f, r.Bottom - S(34f));
    }

    private void DrawHeader(Gfx g)
    {
        bool hoverIcon = _hover == 2;

        if (hoverIcon)
        {
            // 左键点这个图标 = 取消固定，必须有可见反馈，否则就是一个隐藏陷阱
            using var ring = new Pen(Color.FromArgb(150, UiTheme.Accent), 1.2f);
            UiTheme.FillRound(g, RectangleF.Inflate(IconRect, S(2f), S(2f)), S(5f),
                Color.FromArgb(26, UiTheme.Accent));
            using GraphicsPath p = UiTheme.Round(RectangleF.Inflate(IconRect, S(2f), S(2f)), S(5f));
            g.DrawPath(ring, p);
        }

        if (_icon is not null)
            g.DrawImage(_icon, IconRect);
        else
            UiTheme.FillRound(g, IconRect, S(4f), Color.FromArgb(40, 255, 255, 255));

        // 悬停图标时，右侧让出一块位置写动作名，代替一个看不见的隐含操作
        Font hint = UiTheme.Font(S(11f));
        string action = _pinned ? "取消固定" : "固定到 Dock";
        float hintW = hoverIcon ? UiTheme.Measure(g, action, hint).Width + S(14f) : 0f;
        if (hoverIcon)
            UiTheme.DrawText(g, action, hint, UiTheme.Accent, CloseRect.Left - S(8f) - hintW + S(4f),
                IconRect.Top + (IconRect.Height - UiTheme.Measure(g, action, hint).Height) * 0.5f - S(1f));

        Font font = UiTheme.Font(S(12f), true);
        float maxW = CloseRect.Left - S(8f) - hintW - IconRect.Right - S(8f);
        string name = UiTheme.Ellipsis(g, _appName, font, maxW);
        float nameY = IconRect.Top + (IconRect.Height - UiTheme.Measure(g, name, font).Height) * 0.5f - S(1f);

        // 白字垫一层极淡的深色投影：玻璃底偶尔会是中间调的灰（半透明窗口在后面时），
        // 纯白字在那上面会糊；深字不加，否则边缘发脏。
        if (_inkLight)
            UiTheme.DrawText(g, name, font, Color.FromArgb(70, 0, 0, 0), IconRect.Right + S(7f), nameY + S(1f));

        UiTheme.DrawText(g, name, font, Ink, IconRect.Right + S(7f), nameY);
    }

    private void DrawCloseButton(Gfx g)
    {
        RectangleF r = CloseRect;
        bool hover = _hover == 1;

        if (hover)
            UiTheme.FillRound(g, r, r.Width * 0.5f, Color.FromArgb(34, UiTheme.Danger));

        float pad = r.Width * 0.30f;
        Color color = hover ? UiTheme.Danger : InkDim;

        using var pen = new Pen(color, MathF.Max(1.2f, S(1.2f)))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        g.DrawLine(pen, r.Left + pad, r.Top + pad, r.Right - pad, r.Bottom - pad);
        g.DrawLine(pen, r.Right - pad, r.Top + pad, r.Left + pad, r.Bottom - pad);
    }

    /// <summary>正文墨色：深色玻璃用白字，浅色玻璃用深字。</summary>
    private Color Ink => _inkLight
        ? Color.FromArgb(255, 242, 244, 249)
        : Color.FromArgb(255, 24, 26, 32);

    private Color InkDim => _inkLight
        ? Color.FromArgb(255, 178, 184, 198)
        : Color.FromArgb(255, 92, 98, 112);

    // ---------------------------------------------------------------- 交互

    /// <summary>1 = 右上角 ×，2 = 左上角应用图标（取消固定 / 固定），3 = 画面。</summary>
    private int HitTestButtons(int x, int y)
    {
        // 图标和 × 的命中区都比画出来的大一圈：16px 的目标用手点太苛刻
        if (RectangleF.Inflate(CloseRect, S(4f), S(4f)).Contains(x, y)) return 1;
        if (RectangleF.Inflate(IconRect, S(3f), S(3f)).Contains(x, y)) return 2;
        if (ThumbRect.Contains(x, y)) return 3;
        return 0;
    }

    protected override bool OnMouseMove(int x, int y)
    {
        int hit = HitTestButtons(x, y);
        SetCursorKind(hit != 0);
        if (hit == _hover) return false;
        _hover = hit;
        return true;
    }

    protected override bool OnMouseLeave()
    {
        if (_hover == 0) return false;
        _hover = 0;
        return true;
    }

    protected override bool OnLeftUp(int x, int y)
    {
        switch (HitTestButtons(x, y))
        {
            case 1:
                Logger.Info("[preview] 点右上角 ×：关闭窗口");
                Hide();
                CloseRequested?.Invoke();
                return true;
            case 2:
                Logger.Info("[preview] 点应用图标：固定 / 取消固定");
                Hide();
                PinToggleRequested?.Invoke();
                return true;
            case 3:
                Logger.Info("[preview] 点画面：切到前台");
                Hide();
                ActivateRequested?.Invoke(_target);
                return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- 图标

    private static Bitmap? LoadIcon(string path, int size)
    {
        if (path.Length == 0 || size <= 0) return null;
        byte[]? pixels = IconLoader.Load(path, size);
        if (pixels is null || pixels.Length < size * size * 4) return null;

        // IconLoader 给的是预乘 BGRA，正好对得上 Format32bppPArgb —— 直接搬，不要重算 alpha
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            int rowBytes = size * 4;
            for (int y = 0; y < size; y++)
                Marshal.Copy(pixels, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
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

    public new void Dispose()
    {
        _host.Dispose();
        _glass?.Dispose();
        _glass = null;
        _thumb?.Dispose();
        _thumb = null;
        _icon?.Dispose();
        _icon = null;
        base.Dispose();
    }
}
