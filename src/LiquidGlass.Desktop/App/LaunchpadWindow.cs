using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LiquidGlass.Desktop.Capture;
using LiquidGlass.Desktop.Graphics;
using LiquidGlass.Desktop.Platform;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Gfx = System.Drawing.Graphics;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 启动台：居中的一块大玻璃，里面是**桌面上的东西**，按类别分组、可搜索。
///
/// 为什么只扫桌面：这块面板的定位是"桌面摊开来看"，不是"开始菜单"。
/// 开始菜单那 108 个装过的程序里，常用的其实都已经钉在 Dock 上了；
/// 而桌面上堆着的那些临时文件、项目目录、下载的视频，才是真正需要
/// 被整理和快速找到的东西。所以清单来源是桌面目录本身。
///
/// 玻璃走的是和 Dock 同一条管线（同一个 D3D 设备、同一份 Glass.hlsl、
/// 同一套 GlassConfig、同一组 RenderRequest 参数），所以边光、折射、
/// 奶白、阴影和 Dock 是同一份像素算出来的。
/// 内容（图标、文字、搜索框、分类条）仍然用 GDI 画 —— 这个字号下 GDI 比 SDF 清晰 ——
/// 画完当作一张预乘纹理，走管线里的图标通道叠在玻璃上。
/// </summary>
internal sealed class LaunchpadWindow : LayeredWindow
{
    private readonly Dictionary<string, Bitmap?> _icons = new();
    private readonly List<(Rectangle Rect, DesktopItem Item)> _tiles = new();
    private readonly List<(Rectangle Rect, DesktopKind? Kind)> _chips = new();

    private List<DesktopItem> _all = new();
    private DesktopKind? _kind;          // null = 全部
    private string _query = "";
    private int _firstRow;
    private int _columns = 8;
    private int _rowsVisible = 4;
    private int _hoverTile = -1;
    private int _hoverChip = -1;
    private bool _hoverClose;
    private bool _hoverSearch;
    private Rectangle _searchRect;
    private Rectangle _closeRect;

    /// <summary>
    /// 亮底换深墨。启动台的白玻璃是通透的，字要是还固定用亮色，
    /// 停在白壁纸（资源管理器、记事本、浏览器空白页都在白底上）上就全看不见。
    /// 胶囊早就有这套逻辑（StatusPill.ProbeInk），这里补上同一份。
    ///
    /// 探测时机只有一个：Show() 里、窗口还没显示的时候抓一次屏幕。
    /// 理由和背景采样完全一样 —— 那时抓到的才是干净桌面；窗口一旦显示，
    /// 抓屏抓到的就是启动台自己，白玻璃会被判成亮底，深色壁纸上也会变成黑字。
    /// </summary>
    private bool _darkInk;

    /// <summary>
    /// 整屏环境亮度 0..1（Show 时探一次）。只给玻璃的"背景压平"做门控：
    /// 白壁纸的环境下才需要把被糊开的桌面内容抬回亮部，暗色壁纸一概不碰。
    /// </summary>
    private float _envLuma = 0.9f;

    /// <summary>
    /// 搜索框是否处于"输入中"。它决定两件事：插入符画不画、边框亮不亮。
    /// 面板一打开就是 true —— 这个界面唯一的键盘入口就是搜索，不给焦点等于
    /// 让用户先猜一下"要不要先点一下"。点玻璃别处会取消，点搜索框又会回来。
    /// </summary>
    private bool _searchFocused;

    /// <summary>第一次键盘输入是否已经记过日志（诊断用，见 LogFirstKeystroke）。</summary>
    private bool _keystrokeLogged;

    /// <summary>输入法正在打、还没确定的拼音（只用于显示，不参与筛选）。</summary>
    private string _composing = "";

    // 左右翻页按钮。矩形在 DrawPagers 里算出来，命中测试直接用，
    // 空矩形（Rectangle.Empty）就代表"这一页没有按钮"。
    private Rectangle _prevRect;
    private Rectangle _nextRect;
    private bool _hoverPrev;
    private bool _hoverNext;
    /// <summary>按钮位置只打一次日志：调布局时要能拿到精确坐标，又不能每帧刷屏。</summary>
    private bool _pagerLogged;
    private bool _backdropDumped;
    private bool _panelDumped;

    /// <summary>本次打开的时刻（TickCount64）。用来挡住"刚显示就被自己关掉"的那一下。</summary>
    private long _openedAt;

    public LaunchpadWindow(float scale) : base("LiquidGlassLaunchpad", scale) { }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
    }

    public void Show()
    {
        long tStart = Environment.TickCount64;

        // 桌面随时在变（新建、删除、改名），每次打开都重扫一遍。
        // 扫描 70 个条目只需要几毫秒，比"看到一个已经删掉的文件"划算得多。
        DesktopCatalog.Invalidate();
        _all = DesktopCatalog.All.ToList();
        long tCatalog = Environment.TickCount64;

        Native.RECT area = MonitorArea();
        int pixelW = area.Right - area.Left;
        int pixelH = area.Bottom - area.Top;
        if (pixelW <= 0 || pixelH <= 0) return;

        _monLeft = area.Left;
        _monTop = area.Top;
        _monW = pixelW;
        _monH = pixelH;

        // Create 内部已处理"已存在则只改尺寸"的情况：首次调用真正建窗口，
        // 之后调用等价于 ApplyScale。这里不能用 ApplyScale——窗口还没建时它什么都不做，
        // ShowAt 碰到 Hwnd==Zero 会静默返回，日志却照样报"已打开"（v1.3 启动台不显示就是这个坑）。
        Create(pixelW / Scale, pixelH / Scale);
        if (Hwnd == IntPtr.Zero)
        {
            Logger.Warn("[launchpad] 窗口创建失败，无法打开");
            return;
        }
        long tCreate = Environment.TickCount64;

        _query = "";
        _composing = "";
        _firstRow = 0;
        _hoverTile = -1;
        _hoverChip = -1;
        _hoverClose = false;
        _hoverSearch = false;
        _searchFocused = true;

        _contentDirty = true;
        _framePending = true;
        _srcFrames = 0;
        _liveFrames = 0;
        _shotDone = false;

        // 关键顺序：此刻窗口还没显示（Create 只建窗不显示），
        // 在这里抓背景拿到的才是**干净的桌面**。
        // 等 ShowAt 之后再抓就会把自己的玻璃一起抓进来 ——
        // 玻璃的输入是玻璃，第二次打开就会变成一块糊掉的灰白板。
        if (_comp is null) InitGpu();
        long tGpu = Environment.TickCount64;

        // 背景必须**实时**跟着桌面走：面板四周是活着的桌面，面板里要是张定格图，
        // 一眼就能看出来（用户原话："不要固定的"、"根据桌面来透明"）。
        PrimeLiveBackdrop();
        long tBackdrop = Environment.TickCount64;

        // 同一个"窗口还没显示"的窗口期，把底色亮度也量掉，决定这一版用深墨还是白墨。
        // 放在 Render() 之前是必须的 —— 内容层的颜色在这一帧就定稿了。
        ProbeInk();
        long tProbe = Environment.TickCount64;

        // 先画好第一帧再让窗口出现。
        //
        // 交换链在没 Present 过之前，DWM 手里的内容是未定义的：直接 ShowWindow，
        // 整块屏幕（连 Dock 一起）会先闪一帧别的颜色。这段渲染要几十毫秒，
        // 但发生在窗口还不可见的时候，用户看不见；挪到显示之后就变成闪屏了
        //（用户反馈的"快速点一下启动台，Dock 闪一下、背景一瞬间变色"）。
        Render();
        long tRender = Environment.TickCount64;

        _openedAt = tRender;
        ShowAt(area.Left, area.Top, activate: true);
        Logger.Info($"[launchpad] 已打开: {_all.Count} 项桌面条目, {PixelWidth}x{PixelHeight}");
        Logger.Info($"[launchpad] 打开耗时: 扫描={tCatalog - tStart} 建窗={tCreate - tCatalog} " +
                    $"GPU={tGpu - tCreate} 背景={tBackdrop - tGpu} 测光={tProbe - tBackdrop} " +
                    $"首帧={tRender - tProbe} 合计={tRender - tStart}ms");
    }

    /// <summary>
    /// 启动后后台预取桌面图标。
    ///
    /// 第一次打开启动台时，每个图标都要走一次 Shell 的
    /// IShellItemImageFactory.GetImage，冷缓存下 30 多个条目足以把 UI 线程卡住几秒
    /// （用户看到的就是"第一次点出不来，一直在卡"）。这里趁 Dock 刚起来、
    /// 用户还没点之前先把它取完，像素进共享缓存，打开时只剩贴图。
    /// </summary>
    public static void PrewarmIcons()
    {
        var thread = new Thread(() =>
        {
            long t0 = Environment.TickCount64;
            try
            {
                var all = DesktopCatalog.All;
                foreach (DesktopItem item in all)
                    IconLoader.Warm(item.Path, 96);
                Logger.Info($"[launchpad] 图标预热完成: {all.Count} 项，{Environment.TickCount64 - t0}ms");
            }
            catch (Exception ex)
            {
                Logger.Warn("[launchpad] 图标预热失败: " + ex.Message);
            }
        })
        {
            IsBackground = true,
            Name = "launchpad-prewarm",
        };
        // Shell 的图标工厂在 STA 下行为最稳（不需要消息泵，GetImage 是同步的）
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Hide()
    {
        HideWindow();
        // 面板关了就别再让采集会话替它抠那一大块 —— 每次多拷 5MB 的显存没有意义
        Sampler?.SetTertiaryRegion(0, 0, 0, 0);
        _liveFrames = 0;
        _query = "";
        _composing = "";
        _tiles.Clear();
        _chips.Clear();
        // 图标缓存故意不清：桌面条目基本稳定，留着下次打开不用再走一遍 Shell 取图
        //（70 个条目重新提取要 200ms 以上，会看到面板先空一下再长出来）。

        // 通知 Dock：可以把桌面背景解冻了。
        // 启动台有四个关闭入口（点空白、Esc、右上角 ×、被 Toggle 关掉），
        // 只有这里统一收口，漏一个 Dock 就一直用那张冻结图。
        Closed?.Invoke();
    }

    /// <summary>面板关闭（任意入口）时回调，Dock 用来解冻背景。</summary>
    public Action? Closed { get; set; }

    // ---------------------------------------------------------------- 墨色（亮底换深字）

    /// <summary>量一次面板背后的底色亮度，决定这一版用深墨还是白墨。</summary>
    private void ProbeInk()
    {
        Native.RECT area = MonitorArea();
        int sw = area.Right - area.Left;
        int sh = area.Bottom - area.Top;

        float luma;
        try
        {
            using Bitmap? shot = ScreenCapture.Grab(area.Left, area.Top, sw, sh);
            if (shot is null) return;

            // 环境亮度看**整屏**，缩到 48x48 求平均。原来只抓正中 96x96，
            // 桌面上开着一个深色窗口时那块正好被窗口占满，算出来是"暗环境"，
            // 于是白壁纸上该抬的面板反而被关掉了。缩略图里窗口只占一部分，
            // 平均下来才是"这个桌面亮不亮"。
            using (var small = new Bitmap(48, 48))
            {
                using (Gfx g = Gfx.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(shot, 0, 0, 48, 48);
                }
                float env = MeanLuma(small);
                if (env >= 0f) _envLuma = env;
            }

            // 墨色仍然看屏幕正中：那里是视线的落点，字压在上面。
            int w = Math.Min(96, Math.Max(16, sw / 6));
            int h = Math.Min(96, Math.Max(16, sh / 6));
            using Bitmap center = shot.Clone(
                new Rectangle((sw - w) / 2, (sh - h) / 2, w, h), PixelFormat.Format32bppRgb);
            luma = MeanLuma(center);
        }
        catch (Exception ex)
        {
            Logger.Warn("[launchpad] 底色探测失败: " + ex.Message);
            return;
        }
        if (luma < 0f) return;

        Logger.Info($"[launchpad] 环境亮度 {_envLuma:F2}，中心 {luma:F2}");

        // 阈值和胶囊保持一致：0.62 换深墨、0.54 换回白墨，中间那段滞回，
        // 卡在 55% 灰的壁纸上字不会来回跳。
        bool dark = _darkInk ? luma > 0.54f : luma > 0.62f;
        if (dark == _darkInk) return;

        _darkInk = dark;
        _contentDirty = true;
        _framePending = true;
        Logger.Info($"[launchpad] 底色亮度 {luma:F2} → 内容改用{(dark ? "深色墨" : "白色墨")}");
    }

    private static float MeanLuma(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        if (w < 1 || h < 1) return -1f;

        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            int stride = w * 4;
            var buf = new byte[stride * h];
            for (int row = 0; row < h; row++)
            {
                Marshal.Copy(data.Scan0 + row * data.Stride, buf, row * stride, stride);
            }

            double sum = 0;
            int n = 0;
            for (int row = 0; row < h; row += 4)
            {
                int baseIdx = row * stride;
                for (int col = 0; col < w; col += 4)
                {
                    int i = baseIdx + col * 4;   // BGRA
                    sum += (0.0722 * buf[i] + 0.7152 * buf[i + 1] + 0.2126 * buf[i + 2]) / 255.0;
                    n++;
                }
            }
            return n == 0 ? -1f : (float)(sum / n);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>主文字：应用名、搜索结果。</summary>
    private Color Ink => _darkInk
        ? Color.FromArgb(240, 18, 22, 30)
        : Color.FromArgb(246, 248, 252);

    /// <summary>次级文字：占位、拼音预览、空状态。</summary>
    private Color InkDim => _darkInk
        ? Color.FromArgb(200, 44, 50, 62)
        : Color.FromArgb(185, 208, 214, 228);

    /// <summary>
    /// 控件里那层薄白纱（搜索框底、悬停底、翻页按钮底）。
    /// 白墨时是白纱压在深玻璃上；深墨时必须翻成黑纱 —— 白纱落在白纸上等于没画，
    /// 搜索框会变成一个"什么都没长出来的洞"。
    /// </summary>
    private Color Veil(int alpha) => _darkInk
        ? Color.FromArgb(Math.Min(255, (int)(alpha * 0.62f)), 12, 16, 24)
        : Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>控件描边，和白纱同一套翻面规则。</summary>
    private Color VeilEdge(int alpha) => _darkInk
        ? Color.FromArgb(Math.Min(255, (int)(alpha * 0.75f)), 10, 14, 22)
        : Color.FromArgb(alpha, 255, 255, 255);

    /// <summary>图标格的悬停底：白墨时提亮，深墨时压深。</summary>
    private Color TileVeil => _darkInk
        ? Color.FromArgb(26, 10, 14, 22)
        : Color.FromArgb(30, 255, 255, 255);

    /// <summary>
    /// 字底下那一像素投影。白字压深玻璃时靠一层暗影分离；深字压白纸时
    /// 反过来用一层极淡的白晕，不然深字会在浅彩色的壁纸上糊进背景。
    /// </summary>
    private Color LabelShadow => _darkInk
        ? Color.FromArgb(120, 255, 255, 255)
        : Color.FromArgb(130, 0, 0, 0);


    private Native.RECT MonitorArea()
    {
        IntPtr monitor = Native.MonitorFromWindow(IntPtr.Zero, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (Native.GetMonitorInfo(monitor, ref info)) return info.rcMonitor;

        return new Native.RECT { Left = 0, Top = 0, Right = Native.GetSystemMetrics(0), Bottom = Native.GetSystemMetrics(1) };
    }

    /// <summary>显示器左上角（屏幕坐标）。玻璃背景快照要按它换算采样区。</summary>
    private int _monLeft;
    private int _monTop;
    private int _monW;
    private int _monH;

    // ---------------------------------------------------------------- GPU 玻璃

    protected override bool UsesGpuBackdrop => true;

    /// <summary>
    /// 启动台必须能被激活，这是"搜索框能用"的前提。
    ///
    /// 基类默认给窗口加 WS_EX_NOACTIVATE（悬浮条 / 预览卡片都要它，免得抢焦点），
    /// 但键盘消息只会投递给**焦点窗口**，带 NOACTIVATE 的窗口永远成不了焦点窗口：
    /// 表现为鼠标悬停正常、图标能点开，一进搜索框就彻底没反应 ——
    /// 用户说的"搜索框点不了"就是这个。Dock 本体仍然保持 NOACTIVATE（点它不该抢焦点）。
    /// </summary>
    protected override bool NoActivate => false;

    private D3DContext? _d3d;
    private GlassRenderer? _renderer;
    private CompositionHost? _comp;

    /// <summary>背景快照。只在实时采样不可用时才用：一张打开瞬间的壁纸。</summary>
    private FallbackBackdrop? _fallback;
    private bool _backdropReady;

    /// <summary>
    /// 与 Dock 共用的桌面采样会话。一个显示器输出只允许一份 Duplication，
    /// 启动台自己开不出来，只能借 Dock 那份多抠一块区域。
    /// </summary>
    public DesktopSampler? Sampler { get; set; }

    /// <summary>本次打开已经画过的帧数。只用来判断"第一帧之后才拍诊断图"。</summary>
    private long _liveFrames;

    /// <summary>首帧背景来源诊断已打印的条数（每次打开重置，最多 3 条）。</summary>
    private int _srcFrames;

    /// <summary>调试用：打开后自动把面板拍下来（LG_LP_SHOT=路径），免去屏幕截图抓不到面板的问题。</summary>
    private bool _shotDone;

    private ID3D11Texture2D? _contentTex;
    private ID3D11ShaderResourceView? _contentSrv;
    private int _contentW;
    private int _contentH;
    private bool _contentDirty = true;
    private bool _framePending;
    private readonly List<IconQuad> _quads = new(1);

    /// <summary>D3D 设备由 Dock 注入 —— 全场只有它持有设备，别处新建会多一份显存和驱动状态。</summary>
    public D3DContext? D3D { get; set; }
    public string? ShaderSource { get; set; }
    public Func<GlassConfig>? GlassConfigProvider { get; set; }
    public Func<float>? TransmissionProvider { get; set; }
    public Func<(float Height, float Radius)>? DockGeometryProvider { get; set; }

    /// <summary>
    /// 玻璃卡片的矩形（窗口坐标，设备像素）。窗口铺满整屏是为了"点空白关掉"，
    /// 但玻璃只占中间一块 —— 全屏玻璃看不出玻璃，因为没有边。
    /// </summary>
    private RectangleF PanelRect()
    {
        float w = MathF.Min(PixelWidth * 0.76f, SF(1280));
        float h = MathF.Min(PixelHeight * 0.72f, SF(820));
        return new RectangleF((PixelWidth - w) * 0.5f, (PixelHeight - h) * 0.5f, w, h);
    }

    /// <summary>
    /// 卡片圆角。Dock 的 30.4 是按 80px 高定的（38% 高度），原样搬到大卡片上
    /// 只剩 3.3%，看着几乎是直角 —— 这也是"两边不像同一块玻璃"的一部分。
    /// 上限从 56 提到 88：56 在 1280px 宽的面板上只有 4.4%，四个角看起来是
    /// "切了一刀的直角"，用户原话是"有四个突出的尖角"。88 之后转角才真的读作圆弧。
    /// 搜索框在顶部正中，加大圆角只是把四个角落挖空，碰不到内容。
    /// </summary>
    private float PanelRadius
        => SF(MathF.Min(PanelRect().Height / MathF.Max(Scale, 0.5f) * 0.38f, 88f));

    protected override void OnWindowCreated()
    {
        if (Hwnd == IntPtr.Zero) return;

        // 系统圆角 + 边框一律关掉：形状完全由我们自己画，系统再补一层圆角会把
        // 四周切出一道直边（我们在窗口里留了外阴影的透明边）。
        int round = Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int border = unchecked((int)Native.DWMWA_COLOR_NONE);
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));

        // 跟 Dock 一样把自己从采集结果里摘掉：启动台铺满整屏，不排除的话
        // 下一帧采到的就是它自己那块玻璃，玻璃的输入是玻璃 —— 立刻糊成一片。
        // 排除之后采到的是"桌面在没有它的时候长什么样"，这才是正确的折射源。
        //
        // 但排除之后 GDI 截图也拍不到面板本身（拍到的永远是它背后的桌面），
        // 调玻璃时等于瞎了一只眼。所以留一个调试后门：LG_LP_NO_EXCLUDE=1 时不设排除，
        // 配合 LG_LP_FREEZE=1 冻结背景（否则采样会把自己套进去），
        // 就能用普通截图工具拍到"面板 + 真实桌面"的同框画面，量偏移用。
        if (Environment.GetEnvironmentVariable("LG_LP_NO_EXCLUDE") == "1")
        {
            Logger.Warn("[launchpad] 调试模式：未设置采集排除（面板会被截进图里，背景已冻结才安全）");
        }
        else
        {
            bool affinity = Native.SetWindowDisplayAffinity(Hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            Logger.Info("[launchpad] SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) = " + affinity +
                        (affinity ? "" : $" err={Marshal.GetLastWin32Error()}"));
        }

        InitGpu();
    }

    private void InitGpu()
    {
        if (_comp is not null) return;

        if (D3D is null || ShaderSource is null || Hwnd == IntPtr.Zero || PixelWidth < 64)
        {
            Logger.Warn($"[launchpad] 玻璃管线未初始化: d3d={(D3D is null ? "null" : "ok")} " +
                        $"shader={(ShaderSource is null ? "null" : "ok")} hwnd={(Hwnd == IntPtr.Zero ? "0" : "ok")} " +
                        $"size={PixelWidth}x{PixelHeight}");
            return;
        }

        try
        {
            _d3d = D3D;
            _renderer = new GlassRenderer(_d3d, ShaderSource);
            _comp = new CompositionHost(_d3d, Hwnd, PixelWidth, PixelHeight);
            _fallback = new FallbackBackdrop(_d3d);
            Logger.Info("[launchpad] 玻璃管线就绪（与 Dock 同一设备/着色器）");
        }
        catch (Exception ex)
        {
            Logger.Warn("[launchpad] 玻璃管线初始化失败: " + ex.Message);
            _comp?.Dispose();
            _comp = null;
            _renderer?.Dispose();
            _renderer = null;
        }
    }

    /// <summary>玻璃要在屏幕上采样的矩形（屏幕物理像素）：面板本身 + 一圈外扩。</summary>
    private Rectangle BackdropBox()
    {
        RectangleF panel = PanelRect();
        // 外扩是给折射留的：着色器会在边缘向外取样，取不到就是一圈拉伸的毛边
        int margin = S(48);
        return new Rectangle(
            _monLeft + (int)MathF.Round(panel.X) - margin,
            _monTop + (int)MathF.Round(panel.Y) - margin,
            (int)MathF.Round(panel.Width) + margin * 2,
            (int)MathF.Round(panel.Height) + margin * 2);
    }

    /// <summary>实时背景的采样框（屏幕物理像素）。打开期间固定不变。</summary>
    private Rectangle _liveBox;

    /// <summary>上一次真的重画的时刻（TickCount64），给 25fps 节流用。</summary>
    private long _lastLiveAt;

    /// <summary>节流期间攒下的"桌面变了"，下一轮补一次。</summary>
    private bool _livePending;

    private bool _liveSourceLogged;

    /// <summary>25fps 封顶：桌面每变一次就整屏重画一遍太浪费，人眼也看不出来。</summary>
    private const int LiveIntervalMs = 40;

    private bool LiveReady()
        => Sampler is { HasTertiary: true, Srv3: not null }
           && Sampler.TertiaryX == _liveBox.X && Sampler.TertiaryY == _liveBox.Y
           && Sampler.TertiaryW == _liveBox.Width && Sampler.TertiaryH == _liveBox.Height;

    /// <summary>
    /// Dock 主循环每采到一帧就通知一次。桌面内容变了才重画 —— 面板开着但
    /// 桌面没动的时候一帧都不该重画（整屏模糊 + 上传，白烧电）。
    /// </summary>
    public void NotifyBackdrop(bool changed)
    {
        if (!IsVisible) return;
        if (!changed)
        {
            // 没变化：只有攒了补帧才画，否则什么都不做
            if (!_livePending) return;
        }

        long now = Environment.TickCount64;
        if (now - _lastLiveAt < LiveIntervalMs)
        {
            _livePending = true;   // 太快了，下一轮补
            return;
        }

        _lastLiveAt = now;
        _livePending = false;
        Render();
    }

    /// <summary>
    /// 让玻璃直接吃 Dock 那条实时采样管线：打开前把面板对应的屏幕矩形交给采样会话，
    /// 之后桌面每变一次，Dock 主循环就喂一帧新的进来。
    /// 采样不可用（没有共享会话、锁屏、刚重建图形栈）时退回打开瞬间的壁纸快照。
    /// </summary>
    private void PrimeLiveBackdrop()
    {
        _liveBox = BackdropBox();
        _liveFrames = 0;
        _liveSourceLogged = false;
        _lastLiveAt = 0;
        _livePending = false;

        if (Sampler is null)
        {
            Logger.Warn("[launchpad] 没有共享采样会话，背景退回壁纸快照");
            PrimeWallpaper();
            return;
        }

        Sampler.SetTertiaryRegion(_liveBox.X, _liveBox.Y, _liveBox.Width, _liveBox.Height);

        // 窗口还没显示，这一帧采到的就是干净的桌面；同时把刚建的纹理填上内容，
        // 否则第一帧拿到的是还没写过的纹理。
        //
        // 第一次 TryUpdate 经常"还不行"（重建区域后要等采集端下一代帧），
        // 这时会退回壁纸快照，于是打开瞬间是壁纸的颜色、几十毫秒后才变成真正的
        // 桌面 —— 观感就是"背景变了一下颜色"。这里重试几次把这几十毫秒吃掉，
        // 代价是打开前多等最多 40ms，换来首帧就是最终画面。
        bool ready = false;
        for (int attempt = 0; attempt < 5 && !ready; attempt++)
        {
            Sampler.TryUpdate(_liveBox.X, _liveBox.Y, _liveBox.Width, _liveBox.Height, 0);
            ready = LiveReady();
            if (!ready) Thread.Sleep(8);
        }

        Logger.Info($"[launchpad] 实时背景{(ready ? "就绪" : "未就绪，先用壁纸快照")} " +
                    $"box={_liveBox} 采样={Sampler.Status}");

        if (!ready) PrimeWallpaper();
    }

    /// <summary>
    /// 把整屏壁纸裁一块上传成折射源（抓屏只在壁纸不可用时兜底）。
    ///
    /// 抓屏这条路的问题是内容：BitBlt 抓到的是「壁纸 + 桌面上的全部图标 + 任何开着的窗口」，
    /// 玻璃再把它原样显示一遍，面板里就会浮出一层桌面图标残影（左上角那个
    /// 「夏夜的烟火」文件夹就是这么进去的），整体发脏发灰。
    /// Dock 之所以没这个毛病，纯粹因为它贴在屏幕最底部、背后只有壁纸。
    /// 所以启动台直接把壁纸当折射源：内容干净，也不可能抓到自己。
    /// </summary>
    private void PrimeWallpaper()
    {
        if (_fallback is null || _comp is null) return;

        RectangleF panel = PanelRect();
        int cw = Math.Max(64, (int)MathF.Round(panel.Width));
        int ch = Math.Max(64, (int)MathF.Round(panel.Height));

        // 折射会把边缘外的像素拉进来，所以快照要比面板大一圈
        int margin = 48;
        int bx = _monLeft + (int)MathF.Round(panel.X) - margin;
        int by = _monTop + (int)MathF.Round(panel.Y) - margin;
        int bw = cw + margin * 2;
        int bh = ch + margin * 2;

        bool primed = false;
        Bitmap? wallpaper = WallpaperSource.Monitor(_monW, _monH);
        if (wallpaper is not null)
        {
            using Bitmap? piece = WallpaperSource.Crop(wallpaper, bx - _monLeft, by - _monTop, bw, bh);
            if (piece is not null) primed = _fallback.Upload(piece, bx, by);
        }

        // 壁纸拿不到（纯色桌面、幻灯片正在换图、解码失败）才退回抓屏。
        // 此时面板里会出现桌面图标残影，但总比一块纯色板好。
        if (!primed)
        {
            _fallback.Refresh(bx, by, bw, bh, int.MaxValue);
            Logger.Warn("[launchpad] 壁纸底图不可用，已退回抓屏（面板内可能看到桌面图标残影）");
        }

        _backdropReady = _fallback.Srv is not null;

        // 诊断：把玻璃真正拿去当输入的那张图落盘。
        // 「面板是深灰色」这种事只有两种可能 —— 抓错了，或者抓对了但后面被压暗；
        // 看一眼这张图就能分辨，比猜快得多。用 LG_DUMP_BACKDROP 指定目录才触发。
        if (Environment.GetEnvironmentVariable("LG_DUMP_BACKDROP") is { Length: > 0 } dumpDir
            && !_backdropDumped)
        {
            _backdropDumped = true;
            using Bitmap? shot = ScreenCapture.Grab(bx, by, bw, bh);
            if (shot is not null)
            {
                Directory.CreateDirectory(dumpDir);
                shot.Save(Path.Combine(dumpDir, "backdrop.png"), System.Drawing.Imaging.ImageFormat.Png);
                Logger.Info($"[launchpad] 诊断: 背景快照已存到 {dumpDir}");
            }
        }

        Logger.Info($"[launchpad] 背景快照{(_backdropReady ? "就绪" : "失败")} " +
                    $"box=({bx},{by}) {bw}x{bh} src={(primed ? "wallpaper" : "screen")}");
    }

    protected override void OnGpuFrame()
    {
        _framePending = false;
        if (Hwnd == IntPtr.Zero) return;

        if (_comp is null) InitGpu();
        if (_comp is null || _renderer is null || _d3d is null) return;

        int w = PixelWidth, h = PixelHeight;
        if (w < 64 || h < 64) return;
        if (_comp.Width != w || _comp.Height != h) _comp.Resize(_d3d, w, h);

        RectangleF panel = PanelRect();
        int cw = Math.Max(64, (int)MathF.Round(panel.Width));
        int ch = Math.Max(64, (int)MathF.Round(panel.Height));

        if (_contentDirty || _contentSrv is null || _contentW != cw || _contentH != ch)
        {
            if (!RebuildContent(cw, ch)) return;
        }

        // 优先吃实时采样；采样不可用（会话丢失、锁屏、刚重启图形栈）才退回壁纸快照。
        // 两者的区别只在这一行：纹理从哪来、坐标从哪算。
        ID3D11ShaderResourceView? srv;
        int capX, capY, capW, capH;
        if (LiveReady())
        {
            srv = Sampler!.Srv3;
            capX = Sampler.TertiaryX;
            capY = Sampler.TertiaryY;
            capW = Math.Max(8, Sampler.TertiaryW);
            capH = Math.Max(8, Sampler.TertiaryH);
            if (!_liveSourceLogged)
            {
                _liveSourceLogged = true;
                Logger.Info($"[launchpad] 玻璃背景: 实时桌面 {capW}x{capH} @({capX},{capY})");
            }
        }
        else
        {
            srv = _fallback?.Srv;
            capX = _fallback?.X ?? 0;
            capY = _fallback?.Y ?? 0;
            capW = Math.Max(8, _fallback?.W ?? 8);
            capH = Math.Max(8, _fallback?.H ?? 8);
        }
        _liveFrames++;

        if (srv is null)
        {
            // 快照也没拿到：退回中性色渲染，别把一整块黑玻璃贴到桌面上
            _framePending = true;
        }

        // 首帧来源诊断：只记前三条，够判断"打开瞬间那块冷灰蓝"到底来自哪。
        if (_srcFrames < 3)
        {
            Logger.Info($"[launchpad] 帧源#{_srcFrames} {(srv is null ? "none(降级色)" : "有纹理")} " +
                        $"live={LiveReady()} fallbackReady={_fallback?.Ready}");
            _srcFrames++;
        }

        // 已经画过至少一帧之后，如果这一帧没有背景可用，就直接不画 ——
        // 保留交换链里上一帧，而不是让着色器去画那块中性灰（配上冷偏就是
        // 用户看到的"发冷蓝色加灰色"）。只有在首帧才允许降级：
        // 那时交换链是空的，不画等于窗口一片空白。
        if (srv is null && _liveFrames > 1) return;

        GlassConfig g = GlassConfigProvider?.Invoke() ?? new GlassConfig();
        float transmission = TransmissionProvider?.Invoke() ?? Math.Clamp(g.Transmission, 0f, 1f);

        // Match the effective band after the shader caps it against the Dock height.
        float dockHeight = DockGeometryProvider?.Invoke().Height ?? 80f;
        float band = MathF.Min(g.RefractBand, dockHeight * 0.275f);

        var req = new RenderRequest
        {
            TargetRtv = _comp.Rtv,
            CaptureSrv = srv,
            CaptureWidth = capW,
            CaptureHeight = capH,
            // 这个窗口铺满整块屏幕、左上角就在显示器原点，着色器里的 px 已经是
            // 「屏幕坐标 - 显示器原点」。再减 capX 就够；以前这里额外加了
            // panel.X/Y，等于把背景按面板位置又平移一次 —— 透过玻璃看到的桌面
            // 正好偏了一个面板原点（640,390），就是用户说的"背景错位"。
            // Dock / 胶囊的窗口只有自己那么大，px 是窗口内坐标，所以它们要加窗口位置。
            CaptureOffsetX = _monLeft - capX,
            CaptureOffsetY = _monTop - capY,
            WindowW = w,
            WindowH = h,
            PanelX = panel.X,
            PanelY = panel.Y,
            PanelW = panel.Width,
            PanelH = panel.Height,
            Radius = PanelRadius,
            // 外阴影在启动台上是负分：面板后面就是活的桌面，一圈黑晕压上去
            // 只会把玻璃和桌面之间隔出一条"墙缝"，看着像贴了一张卡片。
            ShadowExtent = 0f,
            Shadow = 0f,
            RefractBand = g.EnableRefraction ? band * Scale : 0f,
            RefractAmount = g.EnableRefraction ? g.RefractAmount * Scale : 0f,
            Dispersion = g.EnableRefraction ? g.Dispersion : 0f,
            Bulge = g.Bulge,
            RimIntensity = g.RimIntensity,
            RimWidth = g.RimWidth * Scale,
            // Gloss 与 Dock 同一个数。之前在这里乘 2.2 是想补回大面板被尺寸
            // 系数压掉的光泽，但那等于给启动台换了一种材质 —— 观感一旦不同，
            // 用户立刻能看出来（"玻璃效果跟 dock 栏不一样"）。宁可光弱一点，
            // 也要保证是同一块料。
            Gloss = g.Gloss,
            Tint = g.Tint,
            Exposure = g.Exposure,
            Saturation = g.Saturation,
            Contrast = g.Contrast,
            // 散射与 Dock 共用配置，避免另加一层不同的雾感。
            Scatter = g.Scatter,
            Milk = g.Milk,
            Rolloff = g.Rolloff,
            EdgeSpec = g.EdgeSpec,
            // 白底烟熏：与 Dock 用同一个数。启动台的文字是白色的，面板一旦
            // 在白壁纸上退成"几乎看不见的一层白"，字就同时消失 —— 这也是
            // 用户说"启动台和 dock 栏不是一个效果"的真正原因。
            Smoke = g.Smoke,
            BlurStep = g.BlurStep * (1f - 0.34f * transmission) * Scale,
            Transmission = transmission,
            BodyR = g.BodyR,
            BodyG = g.BodyG,
            BodyB = g.BodyB,
            BodyAlpha = g.BodyAlpha,
            DpiScale = Scale,
            Time = (float)(Environment.TickCount64 / 1000.0),
            Icons = _quads,
        };

        _quads.Clear();
        _quads.Add(new IconQuad
        {
            Texture = _contentSrv!,
            X = panel.X,
            Y = panel.Y,
            W = cw,
            H = ch,
            Alpha = 1f,
        });

        try
        {
            _renderer.Render(req);

            // 诊断必须在 Present **之前**：翻转模型交换链 Present 之后后备缓冲内容就不保证了，
            // 之前放在后面导出，整张图是空的（只剩 DumpFrame 自己画的透明网格）。
            DumpPanelIfAsked();

            _comp.Present(0u);

            // 诊断：把这一帧的最终缓冲落盘。窗口带 WDA_EXCLUDEFROMCAPTURE，
            // 屏幕截图抓不到面板本身（GDI 抓到的是它背后的桌面），所以「背景偏了多少」
            // 只能从渲染结果自己量。用 LG_DUMP_PANEL=<路径> 触发，只导一次。
            if (!_shotDone
                && Environment.GetEnvironmentVariable("LG_LP_SHOT") is { Length: > 0 } shotPath)
            {
                _shotDone = true;
                CapturePanelShot(shotPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("[launchpad] 渲染失败: " + ex.Message);
        }
    }

    /// <summary>按需把当前渲染结果导出成 PNG（LG_DUMP_PANEL），必须在 Present 之前调用。</summary>
    private void DumpPanelIfAsked()
    {
        if (_panelDumped) return;
        if (Environment.GetEnvironmentVariable("LG_DUMP_PANEL") is not { Length: > 0 } path) return;

        _panelDumped = true;
        try
        {
            _comp!.DumpFrame(_d3d!, path);
            Logger.Info($"[launchpad] 面板帧已导出: {path} panel={PanelRect()} " +
                        $"cap={Sampler?.TertiaryX},{Sampler?.TertiaryY} {Sampler?.TertiaryW}x{Sampler?.TertiaryH}");
        }
        catch (Exception ex)
        {
            Logger.Warn("[launchpad] 面板帧导出失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 调试拍图：临时摘掉采集排除 → 用冻结的背景重画一帧 → BitBlt 面板区域 → 恢复排除。
    /// 全程约 200ms，中途不会让采样吃到自己（背景已冻结、且只重画不重采）。
    /// </summary>
    private void CapturePanelShot(string path)
    {
        Native.SetWindowDisplayAffinity(Hwnd, Native.WDA_NONE);

        try
        {
            Render();
            Thread.Sleep(90);
            Render();
            Thread.Sleep(40);

            Rectangle box = BackdropBox();
            bool ok = ScreenCapture.SaveBmp(box.X, box.Y, box.Width, box.Height, path);
            Logger.Info($"[launchpad] 自动截图{(ok ? "成功" : "失败")}: {path} box={box} " +
                        $"panel={PanelRect()} cap={Sampler?.TertiaryX},{Sampler?.TertiaryY} " +
                        $"{Sampler?.TertiaryW}x{Sampler?.TertiaryH}");
        }
        catch (Exception ex)
        {
            Logger.Warn("[launchpad] 自动截图异常: " + ex.Message);
        }
        finally
        {
            Native.SetWindowDisplayAffinity(Hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        }
    }

    private bool RebuildContent(int w, int h)
    {
        _contentDirty = false;

        try
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Gfx gfx = Gfx.FromImage(bmp))
            {
                UiTheme.Prepare(gfx);
                gfx.Clear(Color.Transparent);

                // 内容层是一整张**矩形**贴图，贴到玻璃上时不会继承着色器画的那个
                // 圆角 —— 而里面第一件事就是满铺一层托底暗色，于是四个圆角外面
                // 各露出一小块直角，远看就是"圆角外面还套了一个直角框"。
                // 内容统一在圆角内裁掉。半径比玻璃小 2.5px，让内容层退到玻璃壁
                // 内侧，边缘不跟玻璃抢那一个像素。
                float clipR = MathF.Max(0f, PanelRadius - SF(2.5f));
                using (GraphicsPath clip = UiTheme.Round(new RectangleF(0, 0, w, h), clipR))
                {
                    gfx.SetClip(clip, CombineMode.Replace);
                    DrawPanel(gfx, w, h);
                }
            }

            byte[] pixels = ToPremultipliedBytes(bmp, out int stride);

            _contentSrv?.Dispose();
            _contentSrv = null;
            _contentTex?.Dispose();
            _contentTex = null;

            var desc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                var data = new SubresourceData(handle.AddrOfPinnedObject(), (uint)stride);
                _contentTex = _d3d!.Device.CreateTexture2D(desc, data);
                _contentSrv = _d3d.Device.CreateShaderResourceView(_contentTex, null);
            }
            finally
            {
                handle.Free();
            }

            _contentW = w;
            _contentH = h;
            return _contentSrv is not null;
        }
        catch (Exception ex)
        {
            Logger.Warn("[launchpad] 内容纹理生成失败: " + ex.Message);
            return false;
        }
    }

    private static byte[] ToPremultipliedBytes(Bitmap bmp, out int stride)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            stride = bmp.Width * 4;
            var buffer = new byte[stride * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * stride, stride);
            return buffer;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ---------------------------------------------------------------- 数据

    private List<DesktopItem> Filtered()
    {
        if (_kind is null && _query.Length == 0) return _all;

        IEnumerable<DesktopItem> seq = _all;
        if (_kind is { } k) seq = seq.Where(i => i.Kind == k);

        if (_query.Length > 0)
        {
            string q = _query.Trim();
            seq = seq.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return seq.ToList();
    }

    private int CountOf(DesktopKind kind) => _all.Count(i => i.Kind == kind);

    private int MaxFirstRow(int total)
    {
        int rows = (total + _columns - 1) / _columns;
        return Math.Max(0, rows - _rowsVisible);
    }

    // ---------------------------------------------------------------- 绘制

    /// <summary>
    /// GPU 模式下这个入口走不到（基类把 Render 直接分给了 OnGpuFrame），
    /// 保留它只为 GDI 回退时还能画出点东西，顺便当"只画内容"的诊断通道。
    /// </summary>
    protected override void OnPaint(Gfx g, int width, int height)
        => DrawPanel(g, width, height);

    /// <summary>
    /// 画玻璃面板里的内容。坐标系是**面板本地坐标**（0,0 = 玻璃左上角），
    /// 因为这块内容会被当成一张纹理贴在玻璃上。
    /// </summary>
    private void DrawPanel(Gfx g, int W, int H)
    {
        // 玻璃自己就是背板。这里只压极淡的一层暗色（18/255 ≈ 7%）：
        // 完全不加时，亮壁纸上白色文件名会糊进背景；加多了就变成"半透明黑板"，
        // 玻璃的通透感和 Dock 对不上 —— 所以取一个刚好托住字、又看不出蒙层的值。
        using (var veil = new SolidBrush(Color.FromArgb(18, 6, 10, 18)))
            g.FillRectangle(veil, 0, 0, W, H);

        DrawSearch(g, W, H);
        int chipsBottom = DrawChips(g, W);

        // ---- 网格 ----
        List<DesktopItem> items = Filtered();
        // cellH 112 是算出来的：面板 820 高、网格起点约 174、底部留 86，
        // 剩 560 —— 正好放 5 行。116 会挤掉一行，80 个条目就从两页变三页。
        int cellW = S(122), cellH = S(112);
        // 网格要给两侧的翻页按钮让位：按钮占 S(14) 边距 + S(46) 直径，
        // 原来按 (W - 96) 排 10 列时，最后一列正好被右边的圆按钮压住，
        // 点下去分不清是想翻页还是想开图标。留出两侧各 S(78) 就彻底分开了。
        int pagerZone = S(98);
        _columns = Math.Max(3, (W - S(96) - pagerZone * 2) / cellW);
        int gridW = _columns * cellW;
        int left = (W - gridW) / 2;
        int gridTop = chipsBottom + S(24);

        // 底部留 S(86)：最后一行图标的名字和页脚提示之间必须有一口气，
        // 贴在一起会让页脚看起来像是蹭在文件名上。
        _rowsVisible = Math.Max(1, (H - gridTop - S(86)) / cellH);
        _firstRow = Math.Clamp(_firstRow, 0, MaxFirstRow(items.Count));

        _tiles.Clear();
        int start = _firstRow * _columns;
        int capacity = _rowsVisible * _columns;

        for (int i = 0; i < capacity; i++)
        {
            int index = start + i;
            if (index >= items.Count) break;

            int col = i % _columns;
            int row = i / _columns;
            var cell = new Rectangle(left + col * cellW, gridTop + row * cellH, cellW, cellH);
            DrawTile(g, items[index], cell, i == _hoverTile);
        }

        if (items.Count == 0)
        {
            string empty = _query.Length > 0
                ? $"没有找到「{_query}」"
                : _kind is { } k ? $"桌面上没有{Label(k)}" : "桌面上没有东西";
            Font f = UiTheme.Font(S(14f));
            float w = UiTheme.Measure(g, empty, f).Width;
            UiTheme.DrawText(g, empty, f, InkDim, (W - w) / 2f, gridTop + S(60));
        }

        DrawFooter(g, W, H);
        DrawPagers(g, W, gridTop, gridTop + _rowsVisible * cellH);
    }

    /// <summary>
    /// 左右两侧的翻页按钮。只在多页时出现，第一页的左键和最后一页的右键压成淡色
    /// （而不是直接消失）—— 位置固定，鼠标不用重新找。
    /// </summary>
    private void DrawPagers(Gfx g, int W, int gridTop, int gridBottom)
    {
        _prevRect = Rectangle.Empty;
        _nextRect = Rectangle.Empty;

        (int page, int pages, _) = PageInfo();
        if (pages <= 1) return;

        int d = S(46);
        int y = (gridTop + gridBottom) / 2 - d / 2;
        // 太贴边会显得像"挂在玻璃外面"。留 S(34)，按钮才像浮在玻璃上。
        int inset = S(34);

        _prevRect = new Rectangle(inset, y, d, d);
        _nextRect = new Rectangle(W - inset - d, y, d, d);

        DrawArrowButton(g, _prevRect, left: true, enabled: page > 1, hover: _hoverPrev);
        DrawArrowButton(g, _nextRect, left: false, enabled: page < pages, hover: _hoverNext);

        if (!_pagerLogged)
        {
            _pagerLogged = true;
            Logger.Info($"[launchpad] 翻页按钮 prev={_prevRect} next={_nextRect} 页数={pages}");
        }
    }

    private void DrawArrowButton(Gfx g, Rectangle r, bool left, bool enabled, bool hover)
    {
        // 玻璃上的按钮不能是实心白块，那样像贴了张纸。用低透明度白 + 一圈更亮的边，
        // 它才读成"浮在玻璃上的一层薄材质"。
        // 不可用时也要看得见：第一页的左键如果画到几乎透明，用户会以为"这版没做按钮"。
        // 压到 40% 灰就是标准的"在此但不可点"。
        int fill = enabled ? (hover ? 96 : 52) : 26;
        UiTheme.FillRound(g, r, r.Width / 2f, Veil(fill));
        UiTheme.StrokeRound(g, r, r.Width / 2f,
            VeilEdge(enabled ? (hover ? 96 : 62) : 40), 1f);

        // 箭头是白墨里最亮的一笔（245），深墨时它必须是最暗的一笔，否则白底上整根箭头消失。
        Color ink = _darkInk
            ? (enabled
                ? Color.FromArgb(hover ? 235 : 150, 18, 22, 30)
                : Color.FromArgb(90, 18, 22, 30))
            : (enabled
                ? Color.FromArgb(hover ? 245 : 198, 240, 246, 255)
                : Color.FromArgb(105, 240, 246, 255));

        float cx = r.Left + r.Width / 2f;
        float cy = r.Top + r.Height / 2f;
        float s = r.Width * 0.17f;
        float dx = left ? -s * 0.52f : s * 0.52f;

        using var pen = new Pen(ink, MathF.Max(1.9f, S(2.1f)))
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        };

        g.DrawLines(pen, new[]
        {
            new PointF(cx - dx, cy - s),
            new PointF(cx + dx, cy),
            new PointF(cx - dx, cy + s),
        });
    }

    private static string Label(DesktopKind k) => k.Label();

    private static Color ControlPaintDark => UiTheme.Blend(UiTheme.BackdropTop, Color.Black, 0.28f);

    private void DrawSearch(Gfx g, int W, int H)
    {
        int boxW = Math.Min(S(560), (int)(W * 0.46f));
        int boxH = S(46);
        _searchRect = new Rectangle((W - boxW) / 2, S(50), boxW, boxH);

        var r = _searchRect;
        // 三种状态要给三种不同的亮度：静止、鼠标划过、真的在里面打字。
        // 只做"划过变亮"是不够的 —— 点下去之后如果外观不变，用户会以为点击没生效
        //（这次的反馈就是"搜索框点不了"）。聚焦态因此再加一圈更亮的描边。
        int fill = _searchFocused ? 62 : _hoverSearch ? 52 : 42;
        int stroke = _searchFocused ? 132 : _hoverSearch ? 84 : 52;
        UiTheme.FillRound(g, r, r.Height / 2f, Veil(fill));
        UiTheme.StrokeRound(g, r, r.Height / 2f, VeilEdge(stroke),
            _searchFocused ? MathF.Max(1.2f, S(1.3f)) : 1f);

        // 放大镜
        float iconX = r.Left + S(20), iconY = r.Top + r.Height / 2f;
        using (var pen = new Pen(Color.FromArgb(150, InkDim), MathF.Max(1.4f, S(1.6f))))
        {
            g.DrawEllipse(pen, iconX - S(6), iconY - S(6), S(12), S(12));
            g.DrawLine(pen, iconX + S(4), iconY + S(4), iconX + S(9), iconY + S(9));
        }

        Font font = UiTheme.Font(S(14.5f));
        float textX = r.Left + S(38);
        float textY = r.Top + (r.Height - font.Height) / 2f - S(1);
        bool empty = _query.Length == 0 && _composing.Length == 0;

        if (empty)
        {
            // 占位文字要让开插入符：光标停在起点，文字压在光标上会糊成一团。
            // 颜色也不能用 UiTheme.Dim 的原值 —— 那层灰在深玻璃上几乎看不见，
            // 空搜索框就变成一个"什么都没有的胶囊"，更像坏了。
            UiTheme.DrawText(g, "搜索桌面", font, InkDim,
                textX + S(9), textY);
        }
        else
        {
            // 已经有确定的字：正常色画完，再在后面接一段拼音预览。
            // 这里以前用 UiTheme.Text（跟随系统深浅色），和玻璃底板完全脱钩：
            // 系统深色模式 + 白壁纸 = 白字白底。现在统一走 Ink。
            if (_query.Length > 0)
                UiTheme.DrawText(g, _query, font, Ink, textX, textY);

            if (_composing.Length > 0)
            {
                // 拼音是"还没定的东西"，用淡一档的颜色 + 一条下划线表示，
                // 和已确定的文字拉开层次，也顺便告诉用户"再按一下空格就落字"。
                float composeX = textX + UiTheme.Measure(g, _query, font).Width;
                Color preview = Color.FromArgb(190, InkDim);
                UiTheme.DrawText(g, _composing, font, preview, composeX, textY);
                using var ul = new Pen(Color.FromArgb(120, InkDim), MathF.Max(1f, S(1f)));
                float w = UiTheme.Measure(g, _composing, font).Width;
                g.DrawLine(ul, composeX, textY + font.Height, composeX + w, textY + font.Height);
            }
        }

        // 插入符：聚焦就画，空输入时停在起点。
        // 以前只在有字的时候画 —— 于是"刚点进来、还没打字"的搜索框看起来和
        // 一个装饰性胶囊完全一样，谁都不知道它其实在等键盘输入。
        if (_searchFocused)
        {
            float caretX = empty
                ? textX
                : textX + UiTheme.Measure(g, _query + _composing, font).Width + S(1);
            using var caret = new Pen(UiTheme.Accent, MathF.Max(1f, S(1.5f)));
            g.DrawLine(caret, caretX, r.Top + S(13), caretX, r.Bottom - S(13));
        }

        // 右上角关闭
        _closeRect = new Rectangle(W - S(64), S(46), S(34), S(34));
        if (_hoverClose)
            UiTheme.FillRound(g, _closeRect, S(10f), TileVeil);

        Color mark = _hoverClose ? Ink : Color.FromArgb(170, InkDim);
        using (var pen = new Pen(mark, MathF.Max(1.4f, S(1.5f))))
        {
            var c = _closeRect;
            float inset = c.Width * 0.32f;
            g.DrawLine(pen, c.Left + inset, c.Top + inset, c.Right - inset, c.Bottom - inset);
            g.DrawLine(pen, c.Right - inset, c.Top + inset, c.Left + inset, c.Bottom - inset);
        }
    }

    /// <summary>
    /// 分类切换条。返回它底边的 y，网格从这里往下排。
    /// 空分类直接不显示 —— 摆一个永远点不出东西的标签只会让人以为自己点错了。
    /// </summary>
    private int DrawChips(Gfx g, int W)
    {
        _chips.Clear();

        Font font = UiTheme.Font(S(13f));
        int padX = S(17), gap = S(8), h = S(34), y = _searchRect.Bottom + S(20);

        var entries = new List<(string Text, DesktopKind? Kind)>
        {
            ($"全部 {_all.Count}", null),
        };
        foreach (DesktopKind k in DesktopKindInfo.Order)
        {
            int n = CountOf(k);
            if (n == 0) continue;
            entries.Add(($"{k.Label()} {n}", k));
        }

        var widths = new List<int>(entries.Count);
        foreach ((string text, _) in entries)
            widths.Add((int)MathF.Round(UiTheme.Measure(g, text, font).Width) + padX * 2);

        int total = widths.Sum() + gap * (entries.Count - 1);
        int x = Math.Max(S(40), (W - total) / 2);

        for (int i = 0; i < entries.Count; i++)
        {
            var r = new Rectangle(x, y, widths[i], h);
            bool active = entries[i].Kind == _kind;
            bool hover = i == _hoverChip;

            // 选中态在深玻璃上是"一块白药丸 + 深字"，在白玻璃上必须整个翻过来，
            // 否则白药丸压在白纸上等于没选中，只剩下字的颜色在说谎。
            if (active)
                UiTheme.FillRound(g, r, h / 2f, _darkInk
                    ? Color.FromArgb(230, 16, 20, 28)
                    : Color.FromArgb(222, 255, 255, 255));
            else if (hover)
                UiTheme.FillRound(g, r, h / 2f, Veil(48));
            else
                UiTheme.FillRound(g, r, h / 2f, Veil(26));

            Color color = active
                ? (_darkInk ? Color.FromArgb(242, 246, 248, 252) : Color.FromArgb(240, 14, 18, 26))
                : Ink;
            float tw = UiTheme.Measure(g, entries[i].Text, font).Width;
            UiTheme.DrawText(g, entries[i].Text, font, color,
                r.X + (r.Width - tw) / 2f, r.Y + (r.Height - font.Height) / 2f - S(1));

            _chips.Add((r, entries[i].Kind));
            x += r.Width + gap;
        }

        return entries.Count > 0 ? y + h : _searchRect.Bottom;
    }

    private void DrawTile(Gfx g, DesktopItem item, Rectangle cell, bool hover)
    {
        int iconSize = S(56);
        var iconRect = new Rectangle(cell.X + (cell.Width - iconSize) / 2, cell.Y + S(8), iconSize, iconSize);

        // 命中区比图标大一圈：图标本身只有 58px，必须给整格都能点
        var hit = new Rectangle(cell.X + S(6), cell.Y + S(2), cell.Width - S(12), cell.Height - S(6));
        _tiles.Add((hit, item));

        if (hover)
            UiTheme.FillRound(g, hit, S(14f), _darkInk
                ? Color.FromArgb(22, 10, 14, 22)
                : Color.FromArgb(24, 255, 255, 255));

        Bitmap? icon = Icon(item.Path);
        if (icon is not null)
        {
            g.DrawImage(icon, iconRect);
        }
        else
        {
            UiTheme.FillRound(g, iconRect, S(12f), TileVeil);
        }

        // 12 → 13.5：12px 的名字贴在 58px 的图标下面偏小，一行 10 个格子时
        // 远看像一排灰色噪点，认不出是什么。放大一号仍放得下（行高 112，
        // 图标 58 + 间距 8 + 一行字约 19），不会顶到下一行。
        Font font = UiTheme.Font(S(13.5f));
        string label = UiTheme.Ellipsis(g, item.Name, font, cell.Width - S(10));
        float w = UiTheme.Measure(g, label, font).Width;
        // 文件名画在玻璃上，不能跟系统主题走：浅色模式下 UiTheme.Text 是近黑色，
        // 压在深色玻璃上等于隐身（用户看到的就是"整块糊了"）。所以按**玻璃的底色**
        // 决定墨色（Ink），再垫一像素反向影把字从背景里拉出来。
        float labelX = cell.X + (cell.Width - w) / 2f;
        float labelY = iconRect.Bottom + S(8);
        UiTheme.DrawText(g, label, font, LabelShadow, labelX, labelY + S(1));
        UiTheme.DrawText(g, label, font, Ink, labelX, labelY);
    }

    /// <summary>当前页码 / 总页数。页脚文字和两侧翻页按钮共用，避免两处算得不一样。</summary>
    private (int Page, int Pages, int Rows) PageInfo()
    {
        List<DesktopItem> items = Filtered();
        int cols = Math.Max(_columns, 1);
        int rows = (items.Count + cols - 1) / cols;
        int pages = _rowsVisible > 0 ? Math.Max(1, (rows + _rowsVisible - 1) / _rowsVisible) : 1;
        int page = _rowsVisible > 0 ? Math.Min(pages, _firstRow / _rowsVisible + 1) : 1;
        return (page, pages, rows);
    }

    private void DrawFooter(Gfx g, int W, int H)
    {
        (int page, int pages, _) = PageInfo();
        if (pages <= 1) return;

        // 页脚那行操作提示（"Esc 关闭 · 滚轮翻页 · 直接输入名字筛选"）已删掉：
        // 图标网格底下再挂一行灰字，整块面板看起来就多了条"工具栏"，
        // 而且这些操作都能一眼看出来，写着反而掉档。分页点是唯一保留的指示器。
        int dotR = S(3);
        int gap = S(12);
        int totalW = pages * gap;
        int startX = (W - totalW) / 2;
        for (int i = 0; i < pages; i++)
        {
            bool active = i == page - 1;
            using var brush = new SolidBrush(active ? UiTheme.Accent : Color.FromArgb(80, InkDim));
            g.FillEllipse(brush, startX + i * gap, H - S(56), dotR * 2, dotR * 2);
        }
    }

    private Bitmap? Icon(string path)
    {
        if (_icons.TryGetValue(path, out Bitmap? cached)) return cached;

        Bitmap? bitmap = null;
        const int Size = 96;
        byte[]? px = IconLoader.Load(path, Size);   // 预乘 BGRA
        if (px is not null)
        {
            bitmap = new Bitmap(Size, Size, PixelFormat.Format32bppPArgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, Size, Size),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < Size; y++)
                    Marshal.Copy(px, y * Size * 4, data.Scan0 + y * data.Stride, Size * 4);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        _icons[path] = bitmap;
        return bitmap;
    }

    // ---------------------------------------------------------------- 交互

    /// <summary>窗口坐标 → 面板本地坐标。玻璃只占屏幕中间一块，内容坐标全在里面。</summary>
    private Point ToPanel(int x, int y)
    {
        RectangleF panel = PanelRect();
        return new Point(x - (int)MathF.Round(panel.X), y - (int)MathF.Round(panel.Y));
    }

    private bool InsidePanel(int x, int y)
    {
        RectangleF panel = PanelRect();
        return x >= panel.Left && x <= panel.Right && y >= panel.Top && y <= panel.Bottom;
    }

    protected override bool OnMouseMove(int x, int y)
    {
        var client = ToPanel(x, y);
        int index = _tiles.FindIndex(t => t.Rect.Contains(client));
        int chip = _chips.FindIndex(c => c.Rect.Contains(client));
        bool close = _closeRect.Contains(client);
        bool search = _searchRect.Contains(client);
        bool prev = !_prevRect.IsEmpty && _prevRect.Contains(client);
        bool next = !_nextRect.IsEmpty && _nextRect.Contains(client);

        // 搜索框用 I 形光标：这是系统级的"这里可以打字"的通用语言，
        // 比任何自绘的高亮都直白。
        if (search) SetCursorText();
        else SetCursorKind(index >= 0 || chip >= 0 || close || prev || next);

        // 只有悬停目标变化才重画：在空白处划过去不该产生任何绘制
        if (index == _hoverTile && chip == _hoverChip && close == _hoverClose
            && search == _hoverSearch && prev == _hoverPrev && next == _hoverNext) return false;

        _hoverTile = index;
        _hoverChip = chip;
        _hoverClose = close;
        _hoverSearch = search;
        _hoverPrev = prev;
        _hoverNext = next;
        _contentDirty = true;   // GPU 模式：内容纹理要重建，否则高亮不会出现
        return true;
    }

    /// <summary>
    /// 按下就把键盘焦点抢过来。
    ///
    /// 启动台是从 Dock 里弹出来的，显示那一刻的前台锁有时会挡掉 ShowAt 里那次
    /// SetForegroundWindow；窗口没拿到前台就又回到"打字没反应"。鼠标按下是
    /// 系统认可的合法激活时机，这里补一次最稳。
    /// </summary>
    protected override bool OnLeftDown(int x, int y)
    {
        if (Hwnd != IntPtr.Zero) Native.SetForegroundWindow(Hwnd);
        return false;
    }

    /// <summary>把搜索框置为输入中，顺便确保窗口在前台（IME 候选框也要挂在焦点窗口上）。</summary>
    private bool FocusSearch()
    {
        if (Hwnd != IntPtr.Zero) Native.SetForegroundWindow(Hwnd);
        if (_searchFocused) return false;

        _searchFocused = true;
        _contentDirty = true;
        return true;
    }

    protected override bool OnLeftUp(int x, int y)
    {
        // 刚弹出的头 450ms 内，落在玻璃外面的点击一律忽略。
        //
        // 面板是全屏 TOPMOST 窗口，显示的一瞬间正好盖住鼠标所在的位置（Dock 在底部），
        // 点开始按钮的那一次"抬起"有可能直接落到这块刚出现、还热乎的面板上，
        // 被下面那句"点玻璃外=关掉"当成用户想关闭 —— 表现就是**闪一下又没了**。
        // 玻璃内部的点击不受影响，所以不影响正常使用。
        if (Environment.TickCount64 - _openedAt < 450 && !InsidePanel(x, y)) return false;

        // 点玻璃外面 = 关掉。这也是它铺满整屏的原因：不用精确瞄准关闭按钮。
        if (!InsidePanel(x, y))
        {
            Hide();
            return false;
        }

        var pt = ToPanel(x, y);

        if (_closeRect.Contains(pt))
        {
            Hide();
            return false;
        }

        // 分类切换
        foreach ((Rectangle rect, DesktopKind? kind) in _chips)
        {
            if (!rect.Contains(pt)) continue;
            if (_kind == kind) return false;

            _kind = kind;
            _firstRow = 0;
            _hoverTile = -1;
            _contentDirty = true;
            return true;
        }

        // 点搜索框 = 把输入焦点交给它（以前这里只是一句 return false，
        // 于是整个面板没有任何一处能让搜索框"活过来"，点上去毫无反馈）。
        if (_searchRect.Contains(pt)) return FocusSearch();

        // 两侧翻页按钮：空矩形（单页时）不会命中，所以不用额外判断
        if (!_prevRect.IsEmpty && _prevRect.Contains(pt)) return PageBy(-1);
        if (!_nextRect.IsEmpty && _nextRect.Contains(pt)) return PageBy(1);

        foreach ((Rectangle rect, DesktopItem item) in _tiles)
        {
            if (!rect.Contains(pt)) continue;

            Logger.Info($"[launchpad] 打开: {item.Name} ({item.Path})");
            Launcher.Launch(item.Path, null);
            Hide();
            return false;
        }

        return false;
    }

    protected override bool OnWheel(int delta, int x, int y)
    {
        int step = delta > 0 ? -1 : 1;
        return WheelLike(step);
    }

    protected override bool OnChar(char c)
    {
        if (char.IsControl(c)) return false;
        if (_query.Length >= 24) return false;

        _query += c;
        _firstRow = 0;
        _contentDirty = true;
        LogFirstKeystroke();
        return true;
    }

    /// <summary>
    /// 只记第一次键盘输入。这一行是给"搜索框点不动"这类问题的一锤定音：
    /// 日志里有它 = 键盘链路通了，问题只可能在绘制；没有它 = 窗口没拿到焦点。
    /// 每次输入都记会把日志刷满，所以只记一次。
    /// </summary>
    private void LogFirstKeystroke()
    {
        if (_keystrokeLogged) return;
        _keystrokeLogged = true;
        Logger.Info("[launchpad] 键盘输入已接通（搜索框可以打字了）");
    }

    /// <summary>
    /// 中文输入法的结果串是一次给一整个词（"桌面"两字一起来）。逐字走 OnChar
    /// 会让一次输入触发两次纹理重建，所以这里一次性拼进去。
    /// </summary>
    protected override bool OnImeText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
            if (!char.IsControl(c)) sb.Append(c);

        if (sb.Length == 0) return false;

        int room = 24 - _query.Length;
        if (room <= 0) return false;

        _query += sb.Length > room ? sb.ToString(0, room) : sb.ToString();
        _firstRow = 0;
        _contentDirty = true;
        LogFirstKeystroke();
        return true;
    }

    /// <summary>拼音预览。只影响显示，筛选仍然只看已确定的 _query。</summary>
    protected override bool OnImeComposing(string text)
    {
        if (_composing == text) return false;

        _composing = text;
        _contentDirty = true;
        return true;
    }

    protected override bool OnKeyDown(byte vk)
    {
        switch (vk)
        {
            case Native.VK_ESCAPE:
                Hide();
                return false;

            case Native.VK_BACK:
                if (_query.Length == 0) return false;
                _query = _query[..^1];
                _firstRow = 0;
                _contentDirty = true;
                return true;

            case Native.VK_RETURN:
            {
                List<DesktopItem> items = Filtered();
                if (items.Count == 0) return false;
                Logger.Info($"[launchpad] 回车打开: {items[0].Name}");
                Launcher.Launch(items[0].Path, null);
                Hide();
                return false;
            }

            case Native.VK_DOWN:
            case Native.VK_NEXT:
                return WheelLike(1);

            case Native.VK_UP:
            case Native.VK_PRIOR:
                return WheelLike(-1);
        }

        return false;
    }

    private bool WheelLike(int dir)
    {
        int next = Math.Clamp(_firstRow + dir, 0, MaxFirstRow(Filtered().Count));
        if (next == _firstRow) return false;

        _firstRow = next;
        _contentDirty = true;
        return true;
    }

    /// <summary>整页翻。滚轮和方向键按行滚，两侧按钮按页滚 —— 一页刚好是满屏的十列五行。</summary>
    private bool PageBy(int dir)
    {
        int step = Math.Max(_rowsVisible, 1);
        int next = Math.Clamp(_firstRow + dir * step, 0, MaxFirstRow(Filtered().Count));
        if (next == _firstRow) return false;

        _firstRow = next;
        _contentDirty = true;
        return true;
    }

    public override void Dispose()
    {
        Sampler?.SetTertiaryRegion(0, 0, 0, 0);
        _contentSrv?.Dispose();
        _contentTex?.Dispose();
        _contentSrv = null;
        _contentTex = null;
        _comp?.Dispose();
        _comp = null;
        _renderer?.Dispose();
        _renderer = null;
        _fallback?.Dispose();
        _fallback = null;
        _icons.Clear();
        base.Dispose();
    }
}
