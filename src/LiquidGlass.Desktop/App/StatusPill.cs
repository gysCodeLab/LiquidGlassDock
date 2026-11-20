using System.Drawing;
using System.Drawing.Imaging;
using Gfx = System.Drawing.Graphics;
using LiquidGlass.Desktop.Capture;
using LiquidGlass.Desktop.Graphics;
using LiquidGlass.Desktop.Platform;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// Dock 右端的系统状态胶囊：电量、网络、时间。
///
/// 为什么需要它：整条任务栏被设成全透明之后，托盘区（时钟、网络、音量、电量、
/// 快捷设置）跟着一起看不见了 —— 它们都是 Shell_TrayWnd 的子窗口，没有单独救回来的办法。
/// 这个小胶囊把其中最重要的三样重新摆回屏幕上。
///
/// **玻璃怎么做的**（这是它存在的全部难点）：
/// 早先的版本是"离屏烘一张静态玻璃图贴上去"。看着像玻璃，但放到 Dock 旁边就露馅了：
/// Dock 的玻璃每一帧都在按背后的真实桌面重算折射和边光，胶囊却是定格的一帧，
/// 而且采样区只覆盖 Dock 那条矩形 —— 胶囊在它右边，取到的是被夹到边缘的一列像素。
/// 屏幕上就是「一块没有内容的糊色块」。
///
/// 现在走的是**跟 Dock 本体完全相同的管线**：同一个 D3D 设备、同一个 GlassRenderer、
/// 同一份 Glass.hlsl、同一套 GlassConfig、同一个 DXGI 桌面采样会话（第二采样区）、
/// 同一个帧时间轴。所以模糊、奶白、折射、边光、色散、外阴影是同一套像素算出来的，
/// 不是"照着调的近似值"。
///
/// 窗口是 WS_EX_NOREDIRECTIONBITMAP + composition swapchain，DWM 直接合成；
/// 文字和图标仍然用 GDI 画（小字号 GDI 比 SDF 清晰得多），画完当作一张预乘纹理
/// 交给同一条管线的图标通道叠上去 —— 跟 Dock 画图标是同一回事。
///
/// 点击整块胶囊 → 发 Win+A 打开系统自带的快速设置面板。那里有网络、蓝牙、亮度、
/// 音量的完整开关，比自己重造一套 UI 靠谱得多：不用维护、不会跟系统更新打架。
/// </summary>
internal sealed class StatusPill : LayeredWindow
{
    // ---- 逻辑尺寸 ----
    // 外阴影留边跟 Dock 同比例：Dock 是 26 / 80 的面板高，胶囊 59 高就取 20。
    // 之前给 10 的时候阴影又紧又硬，贴在 Dock 旁边一眼就看得出"不是一条玻璃"。
    private const float ShadowPx = 20f;
    private const float PadX = 16f;       // 左右内边距

    /// <summary>有电池（笔记本）时的宽度：电池图标 + 百分比 + 网络 + 时钟刚好排满。</summary>
    private const float PillWideW = 180f;

    /// <summary>没电池（台式机）时的宽度：只有网络图标和时钟，收窄一点不然中间一大片空。</summary>
    private const float PillNarrowW = 130f;

    private float _pillW = PillWideW;
    private float _pillH = 56f;

    private StatusSnapshot _status;
    private DateTime _now = DateTime.Now;

    /// <summary>当前窗口左上角的屏幕坐标（物理像素）。采样区和渲染偏移都以它为准。</summary>
    private int _screenX;
    private int _screenY;

    /// <summary>光标压在胶囊上。</summary>
    private bool _hover;

    /// <summary>
    /// 玻璃底下是浅色 → 内容改画深色墨。
    ///
    /// 为什么必须有这一条：胶囊的玻璃是「模糊 + 加压暗 + 部分透明」，在白色壁纸或
    /// 浅色窗口上算出来就是一块接近白的板。原来那套白字在上面几乎看不见，
    /// 只剩一层投影的糊边 —— 用户看到的就是「胶囊那部分差点意思」。
    /// 判据直接看玻璃背后的真实像素（采样窗口自己带 WDA_EXCLUDEFROMCAPTURE，
    /// 抓不到自己，抓到的就是底下的壁纸），见 ProbeInk。
    /// </summary>
    private bool _darkInk;

    /// <summary>上次探测底色的时刻（TickCount64）。</summary>
    private long _lastInkProbeAt = long.MinValue / 2;

    /// <summary>第一次探测结果已经打过日志。</summary>
    private bool _inkProbeLogged;

    /// <summary>这一帧要不要画。内容/尺寸/悬停变了才置位，避免空闲时白烧 GPU。</summary>
    private bool _framePending;

    // ---------------------------------------------------------------- GPU 管线

    protected override bool UsesGpuBackdrop => true;

    private D3DContext? _d3d;
    private GlassRenderer? _renderer;
    private CompositionHost? _comp;

    /// <summary>采集失效时自己抓一小块顶上 —— 跟 Dock 的兜底是同一个类，同一套逻辑。</summary>
    private FallbackBackdrop? _fallback;

    private ID3D11Texture2D? _contentTex;
    private ID3D11ShaderResourceView? _contentSrv;
    private int _contentW;
    private int _contentH;
    private bool _contentDirty = true;
    private bool _gpuFailed;
    private bool _loggedSecondary;

    /// <summary>上一帧的折射源是不是 DXGI 实时桌面。用来在兜底图升级成实时桌面时补一帧。</summary>
    private bool _usingRealtime;

    /// <summary>渲染用的 D3D 设备，由 Dock 注入（它才是设备的持有者）。</summary>
    public D3DContext? D3D { get; set; }

    /// <summary>着色器源码。跟 Dock 读的是同一个文件，保证是同一份代码。</summary>
    public string? ShaderSource { get; set; }

    /// <summary>玻璃参数来源。必须跟 Dock 本体是同一个对象，否则观感立刻分家。</summary>
    public Func<GlassConfig>? GlassConfigProvider { get; set; }

    /// <summary>通透度来源（可能被 --transmission 覆盖，所以不能直接读配置）。</summary>
    public Func<float>? TransmissionProvider { get; set; }

    /// <summary>桌面采样器。和 Dock 共用同一个会话，胶囊用的是它的第二采样区。</summary>
    public DesktopSampler? Sampler { get; set; }

    /// <summary>点击胶囊时执行 —— 一般是发 Win+A 打开原生快速设置。</summary>
    public Action? QuickSettingsRequested;

    /// <summary>这一块永远不该抢焦点：用户点它只是想开个面板，不能把正在打字的窗口踩下去。</summary>
    protected override bool AllowActivate => false;

    public StatusSnapshot Snapshot => _status;

    /// <summary>还有一帧没画（Dock 的主循环据此决定这一轮要不要出帧）。</summary>
    public bool FramePending => _framePending;

    /// <summary>
    /// 采样器确认"胶囊背后的像素真的变了"时调用 —— 立刻重画。
    ///
    /// 这里**不再限频**。上一版为了断自反馈把它限到 400ms，代价是换壁纸、切窗口之后
    /// 胶囊要等最多 0.4 秒才变色，跟 Dock 本体明显不同步。
    /// 现在自反馈是在采样端用像素指纹断掉的（DesktopSampler.FingerprintChanged）：
    /// 胶囊自己的 Present 会被 EXCLUDEFROMCAPTURE 排除，采回来的像素一个字节都不变，
    /// 所以能走到这里的每一次调用，背后都是一次真实的背景变化，再限频就是纯加延迟。
    /// </summary>
    public void NotifyBackgroundChanged()
    {
        _framePending = true;
        _lastBgNotifyAt = Environment.TickCount64;
    }

    /// <summary>临时诊断窗口（TickCount64）。之前会记录每一帧的来源与追帧耗时，用来定位胶囊延迟。</summary>
    public long LagDiagUntilMs;

    /// <summary>上一次被通知"背景变了"的时刻，用来量"通知→真的画出去"的耗时。</summary>
    private long _lastBgNotifyAt;

    private string _lastLoggedSrc = "";

    /// <summary>
    /// 接下来这段时间里，每一轮主循环都重画一次胶囊（毫秒）。
    ///
    /// 为什么需要它：胶囊只在「背景确实变了」时才重画（见 NotifyBackgroundChanged），
    /// 而"变了没有"是靠第二采样区的像素指纹判断的 —— 那块只有 312x208，
    /// 指纹只取中间 32 列的四条横带，切换页面时有可能整块没被采到，
    /// 于是胶囊继续画上一页的背景，一直到 WantsSilentRefresh 兜底。
    ///
    /// 启动台关闭 / 解冻那一下正是最容易漏的时刻：桌面刚换、指纹还没建立基线。
    /// 这里给一个短窗口，窗口内不看指纹，只要有帧就直接重画。胶囊是个 180x80 的小面板，
    /// 连画一秒的代价可以忽略。
    /// </summary>
    public void ForceRepaintFor(int ms)
    {
        _forceRepaintUntil = Math.Max(_forceRepaintUntil, Environment.TickCount64 + Math.Max(0, ms));
        _framePending = true;
        // 底色探测同时提速：一会儿可能换成白底页面，字色必须在一百多毫秒内跟上，
        // 否则那 1.5 秒里是"白字压白玻璃"，看起来就是胶囊根本没切换过来。
        _inkProbeFastUntil = Environment.TickCount64 + 2500;
        _lastInkProbeAt = long.MinValue / 2;
    }

    /// <summary>底色探测提速窗口的截止时刻。窗口内按 120ms 一次，平时 1.5 秒一次。</summary>
    private long _inkProbeFastUntil;

    /// <summary>上一次写进日志的亮度，避免探测提速后刷屏。</summary>
    private float _lastProbeLuma = -1f;

    /// <summary>强制重画窗口的截止时刻（TickCount64）。</summary>
    private long _forceRepaintUntil;

    /// <summary>太久没出帧（背景变了但谁都没通知它）→ Dock 该让 Place 再走一次。</summary>
    // 兜底自刷新的间隔。原来是 4 秒 —— 一旦指纹漏判（切换页面时很可能漏），
    // 胶囊就会挂着上一页的背景整整四秒，这就是用户说的"胶囊切换不及时"。
    // 1 秒和 Dock 的 heartbeat 一个量级：最坏也只错一眨眼的功夫，成本可以忽略。
    public bool WantsSilentRefresh => Environment.TickCount64 - _lastPaintAt > 1000;

    /// <summary>上一帧真正画出去的时刻（TickCount64）。</summary>
    private long _lastGpuFrameAt;
    /// <summary>玻璃参数只打一次日志，避免每帧刷屏。</summary>
    private bool _paramsLogged;

    /// <summary>上一次成帧的时刻，兜底自刷新的基准。</summary>
    private long _lastPaintAt;

    public StatusPill(float scale) : base("LiquidGlass.StatusPill", scale) { }

    // ---------------------------------------------------------------- 几何

    private RectangleF Pill => new(S(ShadowPx), S(ShadowPx), S(_pillW), S(_pillH));

    private float Radius => S(_pillH * 0.38f);

    /// <summary>面板高度（物理像素）。折射带、边光宽都要按它缩放。</summary>
    private float PanelH => S(_pillH);

    /// <summary>整个窗口的逻辑宽度（含左右外阴影留边）。Dock 算屏幕右边界兜底时要用。</summary>
    public float WindowLogicalWidth => _pillW + ShadowPx * 2f;

    /// <summary>逻辑高度（含上下外阴影留边）。</summary>
    public float WindowLogicalHeight => _pillH + ShadowPx * 2f;

    /// <summary>
    /// 外阴影留边有多少逻辑像素。Dock 摆位置时要减掉它 ——
    /// 窗口坐标是"含阴影的外框"，而用户看到的是里面的玻璃面板。
    /// 不减的话胶囊整体会往右下偏一个阴影的距离，跟 Dock 的上下边对不齐。
    /// </summary>
    public float ShadowOffset => ShadowPx;

    // ---------------------------------------------------------------- 对外

    /// <summary>
    /// 摆到指定屏幕位置并刷新内容。
    ///
    /// 位置或尺寸变了才动窗口，内容变了才重画纹理 —— 这个函数每秒会被调一次
    /// （时间在走），不能每次都 SetWindowPos + 重新渲染。
    /// </summary>
    public void Place(int x, int y, float height, StatusSnapshot status, DateTime now)
    {
        // 有没有电池决定宽度：笔记本三项排得开，台式机只有两项，收窄才不会中间空一大片。
        // 判据只看 HasBattery —— 不能再看 Percent >= 0：驱动偶尔会报"电池在位但读不到百分比"，
        // 那时把整个电池块藏掉，用户看到的就是「电量怎么没了」。
        // 有电池就固定这个宽度。充电状态不再额外加宽 ——
        // 充电只体现为电池里那道闪电，不占文字位置。
        bool hasBattery = status.HasBattery;
        float targetW = hasBattery ? PillWideW : PillNarrowW;

        bool sizeChanged = Math.Abs(height - _pillH) > 0.5f || Math.Abs(targetW - _pillW) > 0.5f;
        bool contentChanged = ContentChanged(status, now);

        bool wasVisible = IsVisible;

        _pillH = height;
        _pillW = targetW;
        _status = status;
        _now = now;

        if (contentChanged || sizeChanged) _contentDirty = true;

        if (sizeChanged || Hwnd == IntPtr.Zero)
            Create(_pillW + ShadowPx * 2f, _pillH + ShadowPx * 2f);

        bool moved = x != _screenX || y != _screenY;
        _screenX = x;
        _screenY = y;

        // 底色变了就先换墨色，再决定这一帧画什么。必须排在 _contentDirty 判之前，
        // 否则换色的那一秒会先出去一帧旧颜色的字。
        ProbeInk();

        // 告诉采样器"我背后是哪块屏幕"。必须排在渲染之前 —— 这一帧就要用上，
        // 晚一步的话首帧永远取的是上一处位置的像素。
        if (Sampler is not null)
        {
            DescribeCapture(out int capX, out int capY, out int capW, out int capH);
            Sampler.SetSecondaryRegion(capX, capY, capW, capH);

            // 兜底图先用着，但实时桌面一旦就绪就得换过来：
            // 否则胶囊会一直停在"抓屏的那一帧"上，桌面动起来它不动。
            if (Sampler.Srv2 is not null && !_usingRealtime) _framePending = true;
        }

        if (!wasVisible)
        {
            ShowAt(x, y);
            // 重新显示时显存里还是上次那张画面。不主动欠一帧，窗口挂回来就是旧背景，
            // 而此刻桌面往往正好被换过（从启动台切回来第一件事就是这个）。
            _framePending = true;
        }
        else if (moved || sizeChanged)
        {
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, x, y, PixelWidth, PixelHeight,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        if (_contentDirty) _framePending = true;
        // 挪了位置也要重画：玻璃里的折射是按"背后哪块屏幕"算的，
        // 只把窗口搬走、像素不重算，边缘那一圈就指向了旧地方。
        if (moved) _framePending = true;

        // 兜底自刷新：正常情况下背景一变就会被 NotifyBackgroundChanged 叫醒，
        // 这里只防"采样彻底停了"（会话丢失、显示器拔掉）那种情况 ——
        // 四秒一次，成本可以忽略，但绝不能让玻璃永远停在旧画面上。
        if (Environment.TickCount64 - _lastPaintAt > 4000) _framePending = true;

        if (_framePending) Render();
    }

    public void Hide()
    {
        _framePending = false;
        HideWindow();
    }

    /// <summary>内容有没有变化：电量、充电、联网、有线无线，或者跳到下一分钟。</summary>
    private bool ContentChanged(StatusSnapshot s, DateTime now)
        => s.HasBattery != _status.HasBattery || s.Percent != _status.Percent ||
           s.Charging != _status.Charging || s.Online != _status.Online ||
           s.Wifi != _status.Wifi || now.Minute != _now.Minute || now.Hour != _now.Hour;

    /// <summary>
    /// 告诉采样器"胶囊背后是哪一块屏幕"。Dock 在每轮采样前问一次。
    /// 只算坐标，不碰 GPU —— 采样和渲染是两条独立的路径。
    /// </summary>
    public void DescribeCapture(out int x, out int y, out int w, out int h)
    {
        // 多留一圈：折射会把边缘外的像素拉进来，余量不够边上就是一圈死像素
        int margin = (int)MathF.Round(44f * Scale);
        x = _screenX - margin;
        y = _screenY - margin;
        w = RoundTo(PixelWidth + margin * 2, 8);
        h = RoundTo(PixelHeight + margin * 2, 8);

        // 整块必须落在屏幕内。
        //
        // 这一条不做的话，胶囊贴着屏幕底边时采样区会越界十几行，而
        // CopySubresourceRegion 只会拷到屏幕内的部分 —— 纹理底部那十几行
        // 就是新建纹理的未定义内容（实际是黑的）。模糊链会把它揉进整张图，
        // 玻璃于是整体发灰发白，看着跟 Dock 完全不是一个色。宁可上下留边不对称。
        int screenW = Native.GetSystemMetrics(0);
        int screenH = Native.GetSystemMetrics(1);
        if (x + w > screenW) x = Math.Max(0, screenW - w);
        if (y + h > screenH) y = Math.Max(0, screenH - h);
        if (x < 0) x = 0;
        if (y < 0) y = 0;
    }

    private static int RoundTo(int value, int multiple) => (value + multiple - 1) / multiple * multiple;

    /// <summary>
    /// 量一下玻璃背后那块桌面的平均亮度，决定内容用深墨还是白墨。
    ///
    /// 用 BitBlt 抓面板那一小块再降采样成平均值：一次拷贝在 1ms 量级，
    /// 而且 1.5 秒才做一次，比走 GPU 回读稳得多（回读会卡管线，为一个颜色判断不值）。
    /// 抓到的是**壁纸本身**而不是玻璃：胶囊窗口带 WDA_EXCLUDEFROMCAPTURE，
    /// 屏幕抓取里看不到它，所以量出来的正是玻璃要压的那层底。
    ///
    /// 带 0.08 的滞回：亮度刚好卡在阈值上的壁纸（比如 55% 灰）不会让字来回跳色。
    /// </summary>
    private void ProbeInk()
    {
        long now = Environment.TickCount64;
        // 平时 1.5 秒一次（一次 BitBlt 在 1ms 量级，够省）；刚切完页面那两秒提到 120ms 一次，
        // 否则白底页面上会有一段"白字看不见"的空窗 —— 实测那段最长 1.6 秒。
        long gap = now < _inkProbeFastUntil ? 120 : 1500;
        if (now - _lastInkProbeAt < gap) return;
        _lastInkProbeAt = now;
        if (Hwnd == IntPtr.Zero) return;

        int x = _screenX + (int)MathF.Round(S(ShadowPx));
        int y = _screenY + (int)MathF.Round(S(ShadowPx));
        int w = Math.Max(8, (int)MathF.Round(S(_pillW)));
        int h = Math.Max(8, (int)MathF.Round(S(_pillH)));

        float luma;
        try
        {
            using Bitmap? shot = ScreenCapture.Grab(x, y, w, h);
            if (shot is null) return;
            luma = MeanLuma(shot);
        }
        catch (Exception ex)
        {
            Logger.Warn("[pill] 底色探测失败: " + ex.Message);
            return;
        }
        if (luma < 0f) return;

        // 提速窗口里探测很密（120ms 一次），只在亮度真的变了才记，
        // 否则一次切换就能刷十几行一样的数字。
        if (Environment.TickCount64 < LagDiagUntilMs && Math.Abs(luma - _lastProbeLuma) > 0.05)
        {
            _lastProbeLuma = luma;
            Logger.Info($"[pill] 底色探测 luma={luma:F2} 当前={(_darkInk ? "深墨" : "白墨")}");
        }

        // 白底上玻璃是通透的（Smoke = 0），白字压上去等于没字 —— 所以按底色亮度
        // 换深墨，和 macOS 菜单栏在浅色壁纸上转深色字是同一套逻辑。
        //
        // 阈值 0.62 是量出来的：玻璃自身只把底压暗 1~2%，底色 luma 一旦过 0.62，
        // 玻璃体实际落在 0.60 以上，白字的对比度就掉到看不清的程度。
        // 0.54 是回落阈值，留 0.08 滞回，卡在 55% 灰的壁纸上字不会来回跳色。
        bool dark = _darkInk ? luma > 0.54f : luma > 0.62f;
        if (dark == _darkInk)
        {
            // 第一次量出来要留一条日志：否则"到底有没有在探测"只能靠猜。
            if (!_inkProbeLogged)
            {
                _inkProbeLogged = true;
                Logger.Info($"[pill] 底色亮度 {luma:F2} → 沿用{(dark ? "深色墨" : "白色墨")}");
            }
            return;
        }

        _inkProbeLogged = true;
        _darkInk = dark;
        _contentDirty = true;
        _framePending = true;
        Logger.Info($"[pill] 底色亮度 {luma:F2} → 内容改用{(dark ? "深色墨" : "白色墨")}");
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
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + row * data.Stride, buf, row * stride, stride);
            }

            // 隔行隔列采样：这块区域是纯色壁纸/窗口，整片扫一遍没有任何额外信息。
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


    // ---------------------------------------------------------------- 窗口

    protected override void OnWindowCreated()
    {
        if (Hwnd == IntPtr.Zero) return;

        // 关键：跟 Dock 一样把自己从所有采集结果里摘掉。
        // 胶囊就在自己的第二采样区里，不摘掉就是"我拍我"的无限套娃。
        bool affinity = Native.SetWindowDisplayAffinity(Hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        if (!affinity) Logger.Warn("胶囊 SetWindowDisplayAffinity 失败: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());

        int round = Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int border = unchecked((int)Native.DWMWA_COLOR_NONE);
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));

        InitGpu();
    }

    private void InitGpu()
    {
        if (_comp is not null || _gpuFailed) return;
        if (D3D is null || ShaderSource is null || Hwnd == IntPtr.Zero) return;

        try
        {
            _d3d = D3D;
            _renderer = new GlassRenderer(_d3d, ShaderSource);
            _comp = new CompositionHost(_d3d, Hwnd, PixelWidth, PixelHeight);
            _fallback = new FallbackBackdrop(_d3d);
            Logger.Info("[pill] 玻璃管线就绪（与 Dock 同一设备/着色器/采样会话）");
        }
        catch (Exception ex)
        {
            _gpuFailed = true;
            Logger.Warn("[pill] 玻璃管线初始化失败: " + ex.Message);
            _comp?.Dispose();
            _comp = null;
            _renderer?.Dispose();
            _renderer = null;
        }
    }

    // ---------------------------------------------------------------- 渲染

    protected override void OnGpuFrame()
    {
        _framePending = false;
        if (Hwnd == IntPtr.Zero) return;

        if (_comp is null && !_gpuFailed) InitGpu();
        if (_comp is null || _renderer is null || _d3d is null) return;

        int w = PixelWidth, h = PixelHeight;
        if (w < 32 || h < 32) return;

        if (_comp.Width != w || _comp.Height != h) _comp.Resize(_d3d, w, h);

        if (_contentDirty || _contentSrv is null || _contentW != w || _contentH != h)
        {
            if (!RebuildContent(w, h)) return;
        }

        GlassConfig g = GlassConfigProvider?.Invoke() ?? new GlassConfig();
        float transmission = TransmissionProvider?.Invoke() ?? Math.Clamp(g.Transmission, 0f, 1f);

        // ---- 背景源：跟 Dock 同一优先级，冻结帧 > 实时桌面 > CPU 兜底 > 无 ----
        //
        // 冻结帧排最前：锁屏/解锁过渡期 Dock 本体画的是锁屏前那一帧，
        // 胶囊如果还在采过渡画面，两块玻璃就会一块正常一块灰蓝 —— 并排看非常明显。
        ID3D11ShaderResourceView? srv = Sampler is { FrozenActive: true } ? Sampler.Frozen2Srv : null;
        int capX, capY, capW, capH;
        if (srv is not null)
        {
            capX = Sampler!.FrozenSecondaryX;
            capY = Sampler.FrozenSecondaryY;
            capW = Math.Max(8, Sampler.FrozenSecondaryW);
            capH = Math.Max(8, Sampler.FrozenSecondaryH);
        }
        else if ((srv = Sampler?.Srv2) is not null)
        {
            capX = Sampler!.SecondaryX;
            capY = Sampler.SecondaryY;
            capW = Math.Max(8, Sampler.SecondaryW);
            capH = Math.Max(8, Sampler.SecondaryH);
            if (!_loggedSecondary)
            {
                _loggedSecondary = true;
                Logger.Info($"[pill] 背景: DXGI 第二采样区 {capW}x{capH} @({capX},{capY})");
            }
        }
        else
        {
            // 采集还没铺到胶囊这一块（开机头一两百毫秒）或者会话丢了：
            // 自己 BitBlt 抓一小块真实桌面顶上，绝不能给着色器一个空纹理。
            DescribeCapture(out capX, out capY, out capW, out capH);
            _fallback?.Refresh(capX, capY, capW, capH, 400);

            // 注意这里看的是 Ready 而不是 Refresh 的返回值。
            // Refresh 在"没到刷新间隔"时返回 false，但图还在 —— 之前用返回值做判据，
            // 结果除了刚抓的那一帧，其余每一帧都把 srv 当成 null 交上去，
            // 着色器走"无纹理"分支，画出来就是一块跟背景毫无关系的中性灰。
            if (_fallback is { Ready: true })
            {
                srv = _fallback.Srv;
                capX = _fallback.X;
                capY = _fallback.Y;
                capW = Math.Max(8, _fallback.W);
                capH = Math.Max(8, _fallback.H);
            }

            // 连兜底都没抓到（极短的一瞬）→ 下一轮再试。
            // 不能在这里无条件要求重画：静止桌面上 DXGI 本来就拿不到新帧，
            // 一直置位就会变成每秒四次白渲染。
            if (srv is null) _framePending = true;
        }

        _usingRealtime = Sampler is not null && ReferenceEquals(srv, Sampler.Srv2);

        // 临时诊断：胶囊这一帧到底画的是"冻结帧"还是"实时桌面"，以及从"背景变了"
        // 到真正画出去过了多久。用户报的"切到别的任务，胶囊跟不上"只靠肉眼没法定位，
        // 必须看这两个值（见 DockApp.CloseLaunchpad 里的 LagDiagUntilMs 设置）。
        // 只在来源真的换了、或者间隔超过 200ms 时记一条，避免 60Hz 刷屏。
        if (Environment.TickCount64 < LagDiagUntilMs)
        {
            bool frozenNow = Sampler is { FrozenActive: true };
            string src = frozenNow ? "冻结帧" : (_usingRealtime ? "实时桌面" : "兜底抓屏");
            long since = _lastBgNotifyAt > 0 ? Environment.TickCount64 - _lastBgNotifyAt : -1;
            // 只在来源真的换了时记一条（正常就是每次切换页面一行），避免 60Hz 刷屏。
            if (src != _lastLoggedSrc)
            {
                _lastLoggedSrc = src;
                Logger.Info($"[pill] 重画 src={src} 距上次通知={since}ms");
            }
        }

        // 胶囊和 Dock 是同一个高度（都是 _panelH），所以这里其实不该再压缩。
        //
        // 上一版按"药丸比 Dock 矮"的思路把它按 0.55~1 缩，结果胶囊的折射和边光
        // 被削掉三成，两边并排看明显不是一条玻璃 —— 这正是"胶囊效果跟 Dock 不一样"
        // 的真正来源。现在高度已经对齐，比例只在极端小尺寸下兜一下底，
        // 下限抬到 0.8：既保住"小面积上边缘光不糊满整块"，又不会把玻璃感削没。
        float sizeRatio = Math.Clamp(_pillH / 78f, 0.80f, 1f);

        var req = new RenderRequest
        {
            TargetRtv = _comp.Rtv,
            CaptureSrv = srv,
            CaptureWidth = capW,
            CaptureHeight = capH,
            CaptureOffsetX = _screenX - capX,
            CaptureOffsetY = _screenY - capY,
            WindowW = w,
            WindowH = h,
            PanelX = SF(ShadowPx),
            PanelY = SF(ShadowPx),
            PanelW = w - SF(ShadowPx) * 2f,
            PanelH = h - SF(ShadowPx) * 2f,
            Radius = Radius,
            ShadowExtent = SF(ShadowPx),
            // 和 Dock 一致：不再画外阴影。ShadowPx 那圈留白保留，它同时是圆角
            // 抗锯齿和边光的余量，只是不再往里填黑。
            Shadow = 0f,
            RefractBand = g.EnableRefraction ? g.RefractBand * sizeRatio * Scale : 0f,
            RefractAmount = g.EnableRefraction ? g.RefractAmount * sizeRatio * Scale : 0f,
            Dispersion = g.EnableRefraction ? g.Dispersion : 0f,
            Bulge = g.Bulge,
            RimIntensity = g.RimIntensity,
            RimWidth = g.RimWidth * sizeRatio * Scale,
            Gloss = g.Gloss,
            Tint = g.Tint,
            Exposure = g.Exposure,
            Saturation = g.Saturation,
            Contrast = g.Contrast,
            Scatter = g.Scatter,
            Milk = g.Milk,
            Rolloff = g.Rolloff,
            EdgeSpec = g.EdgeSpec,
            // 和 Dock / 启动台同一个烟熏强度。胶囊原来是唯一一个"白底变白玻璃"的地方，
            // 于是白壁纸上出现深灰的 Dock 配一块发白的胶囊，一眼就是两套料。
            Smoke = g.Smoke,
            BlurStep = g.BlurStep * (1f - 0.34f * transmission) * Scale,
            Transmission = transmission,
            BodyR = g.BodyR,
            BodyG = g.BodyG,
            BodyB = g.BodyB,
            BodyAlpha = g.BodyAlpha,
            DpiScale = Scale,
            Time = (float)(Environment.TickCount64 / 1000.0),
            Icons = _contentQuads,
        };

        _contentQuads.Clear();
        _contentQuads.Add(new IconQuad
        {
            Texture = _contentSrv!,
            X = 0,
            Y = 0,
            W = w,
            H = h,
            Alpha = 1f,
        });

        // 一次性把实际喂给着色器的参数打出来，方便和 Dock 那条 [dock] 日志逐项对账。
        // 「胶囊和 Dock 是不是同一种玻璃」这个问题，靠肉眼比截图容易反复争论，参数表最直接。
        if (!_paramsLogged)
        {
            _paramsLogged = true;
            Logger.Info($"[pill] 玻璃参数 h={_pillH:F1} ratio={sizeRatio:F3} "
                + $"trans={req.Transmission:F2} body={req.BodyAlpha:F3} milk={req.Milk:F3} scatter={req.Scatter:F2} "
                + $"band={req.RefractBand:F1} amount={req.RefractAmount:F1} rim={req.RimWidth:F1}/{req.RimIntensity:F2} "
                + $"gloss={req.Gloss:F2} edge={req.EdgeSpec:F2} blur={req.BlurStep:F2} "
                + $"radius={req.Radius:F1} shadow={req.Shadow:F2} panel={req.PanelW:F0}x{req.PanelH:F0}");
        }

        try
        {
            _renderer.Render(req);
            _comp.Present(0u);
            _lastGpuFrameAt = Environment.TickCount64;
            _lastPaintAt = _lastGpuFrameAt;
        }
        catch (Exception ex)
        {
            Logger.Warn("[pill] 渲染失败: " + ex.Message);
        }

        // 强制重画窗口内一直欠着自己一帧（见 ForceRepaintFor）：
        // 下一轮主循环会立刻再画，于是胶囊在"刚切完页面"的这一秒里是逐帧跟进的，
        // 不再依赖"指纹判出背景变了"这个可能漏判的信号。
        if (Environment.TickCount64 < _forceRepaintUntil) _framePending = true;
    }

    private readonly List<IconQuad> _contentQuads = new(1);

    /// <summary>
    /// GDI 路径的落点。GPU 模式下永远走不到这里（基类在 UsesGpuBackdrop 时
    /// 直接把 Render 分给 OnGpuFrame），保留实现只是为了让抽象契约成立，
    /// 顺便留一条"把内容单独画出来看"的诊断手段。
    /// </summary>
    protected override void OnPaint(Gfx g, int width, int height)
        => DrawContent(g, Pill, width, height);

    // ---------------------------------------------------------------- 内容纹理

    /// <summary>
    /// 把文字和图标画成一张预乘 ARGB 位图，再上传成纹理交给玻璃管线。
    ///
    /// 为什么不走 SDF：胶囊里是 12~13px 的中文数字混排，SDF 在这个字号上
    /// 边缘会糊、笔画的粗细也不匀。GDI 的灰度抗锯齿在同样的像素密度下更干净。
    /// 代价是内容一变就要重画一次 —— 一分钟一次，可以忽略。
    /// </summary>
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
                DrawContent(gfx, Pill, w, h);
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
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            var handle = System.Runtime.InteropServices.GCHandle.Alloc(
                pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
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
            Logger.Warn("[pill] 内容纹理生成失败: " + ex.Message);
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
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, buffer, y * stride, stride);
            }
            return buffer;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // ---------------------------------------------------------------- 内容绘制

    // 文字默认"白字 + 淡黑投影"：胶囊背后的壁纸什么颜色都有可能，
    // 纯白字在浅色壁纸上会糊进玻璃里。投影只要一像素就够把字托起来。
    //
    // 但白壁纸 / 浅色窗口这件事投影救不了 —— 玻璃体本身就是接近白的一块，
    // 白字叠白玻璃等于没字。所以还要一套深色墨，按玻璃底下的亮度自动切换，
    // 见 ProbeInk 与 _darkInk。和 macOS 菜单栏在浅色壁纸上转深色字是同一个道理。
    private static readonly Color InkLight = Color.FromArgb(242, 255, 255, 255);
    private static readonly Color InkLightDim = Color.FromArgb(190, 255, 255, 255);
    private static readonly Color InkLightShadow = Color.FromArgb(96, 0, 0, 0);
    /// <summary>
    /// 小图标（Wi-Fi 弧）专用衬底色。
    /// 只用来"垫"一层极细的暗边（每侧 0.25px），不能直接拿去描一圈，
    /// 否则细线图形立刻变成套了黑框。
    /// </summary>
    private static readonly Color IconLightShadow = Color.FromArgb(70, 0, 0, 0);

    private static readonly Color InkDark = Color.FromArgb(238, 18, 21, 28);
    private static readonly Color InkDarkDim = Color.FromArgb(176, 18, 21, 28);
    /// <summary>浅底下反向用一层极淡的白投影：深字压在略带灰的玻璃上，也需要被"托"一下。</summary>
    private static readonly Color InkDarkShadow = Color.FromArgb(84, 255, 255, 255);
    private static readonly Color IconDarkShadow = Color.FromArgb(64, 255, 255, 255);

    private Color Ink => _darkInk ? InkDark : InkLight;
    private Color InkDim => _darkInk ? InkDarkDim : InkLightDim;
    private Color InkShadow => _darkInk ? InkDarkShadow : InkLightShadow;
    private Color IconShadow => _darkInk ? IconDarkShadow : IconLightShadow;

    private static void DrawShadowed(Gfx g, string text, Font font, Color color, Color shadow, float x, float y)
    {
        float d = MathF.Max(0.8f, font.Size * 0.06f);
        UiTheme.DrawText(g, text, font, shadow, x, y + d);
        UiTheme.DrawText(g, text, font, color, x, y);
    }

    private void DrawContent(Gfx g, RectangleF pill, int winW, int winH)
    {
        // 悬停：整块药丸压一层极淡的白，只是"这块能点"的暗示。
        // 画在内容层而不是着色器里，是为了省一个 uniform —— 内容纹理本来就要重建。
        if (_hover)
        {
            using var clip = UiTheme.Round(pill, Radius);
            System.Drawing.Drawing2D.GraphicsState state = g.Save();
            g.SetClip(clip, System.Drawing.Drawing2D.CombineMode.Intersect);
            // 白底上压白是看不见的，浅色墨下改成压一点点黑，暗示一样成立。
            using var hb = new SolidBrush(_darkInk
                ? Color.FromArgb(16, 0, 0, 0)
                : Color.FromArgb(20, 255, 255, 255));
            g.FillRectangle(hb, pill);
            g.Restore(state);
        }

        float cy = pill.Top + pill.Height * 0.5f;
        float x = pill.Left + S(PadX);
        bool hasBattery = _status.HasBattery;

        // 时间右对齐，先量出它占多宽，前面的内容才有边界
        string clock = $"{_now.Hour:00}:{_now.Minute:00}";
        // 时间是这个胶囊里唯一"每时每刻都在被看"的东西，字重必须比旁边重一档，
        // 否则整块读起来是四个同等重要的碎元素堆在一起（就是"差点意思"）。
        Font clockFont = UiTheme.Font(S(16f), true);
        SizeF clockSize = UiTheme.Measure(g, clock, clockFont);
        float clockLeft = pill.Right - S(PadX) - clockSize.Width;

        if (hasBattery)
        {
            float used = DrawBattery(g, x, cy, _status, out _);
            x += used + S(9f);
        }

        // 有电池时网络图标在电池和时钟之间居中；没电池（台式机）时直接靠左，
        // 跟右边的时钟形成"一左一右"，中间留白比挤在中间更像托盘。
        float netW = S(19f);
        float netLeft = x;
        if (hasBattery)
        {
            float room = clockLeft - S(10f) - x;
            if (room > netW) netLeft = x + (room - netW) * 0.5f;
        }

        DrawNetwork(g, netLeft, cy, netW);
        DrawShadowed(g, clock, clockFont, Ink, InkShadow, clockLeft, cy - clockSize.Height * 0.5f + S(0.5f));
    }

    /// <summary>
    /// 电池：外壳 + 正极 + 按比例填充。低于 20% 且没在充电时转成警示色 ——
    /// 托盘里那个小图标没地方写"快没电了"，只能靠颜色。
    /// </summary>
    private float DrawBattery(Gfx g, float x, float cy, StatusSnapshot s, out float used)
    {
        float w = S(25.5f);
        float h = S(14f);
        float nub = S(2.9f);

        // 百分比读不到（-1）时只画空壳，不写数字也不转红 ——
        // "读不到"不等于"没电了"，把这种情况染成警告色是误导。
        bool low = s.Percent is >= 0 and <= 20 && !s.Charging;
        Color body = low ? Color.FromArgb(240, 255, 92, 84) : Ink;

        var shell = new RectangleF(x, cy - h * 0.5f, w, h);

        // 先画一层深色投影，再画本体：电池是细线图形，没有投影在浅壁纸上会消失。
        //
        // 投影必须**往下错开 1px**，不能原地往外扩 —— 原地扩出来是一圈等宽的深色环，
        // 而电池只有 13px 高，这圈环就占了三分之一，看上去像电池外面套了个黑框。
        // 错开以后才是"投影"，只在下方露一条。
        using (var glowPen = new Pen(InkShadow, MathF.Max(1.6f, S(1.8f))))
        {
            var shadowRect = new RectangleF(
                shell.X, shell.Y + MathF.Max(1f, S(1f)), shell.Width, shell.Height);
            using var path = UiTheme.Round(RectangleF.Inflate(shadowRect, S(0.3f), S(0.3f)), S(2.6f));
            g.DrawPath(glowPen, path);
        }

        using (var pen = new Pen(body, MathF.Max(1f, S(1.1f))))
        {
            using var path = UiTheme.Round(shell, S(2.2f));
            g.DrawPath(pen, path);
        }

        // 正极小凸起
        using (var brush = new SolidBrush(body))
            UiTheme.FillRound(g, new RectangleF(shell.Right + S(0.5f), cy - h * 0.26f, nub, h * 0.52f),
                S(1f), brush);

        float innerPad = S(2.2f);
        float maxFill = w - innerPad * 2f;
        if (s.Percent > 0)
        {
            float fillW = MathF.Max(S(1.4f), maxFill * (s.Percent / 100f));
            using var brush = new SolidBrush(body);
            UiTheme.FillRound(g, new RectangleF(shell.Left + innerPad, shell.Top + innerPad,
                fillW, h - innerPad * 2f), S(1.2f), brush);
        }

        // 充电中：在电池上方压一个小闪电，代替"里面是满的"那种误导
        if (s.Charging) DrawBolt(g, shell, body);

        // 百分比文字
        //
        // 起点必须绕过正极凸起（nub）：外壳右边不是电池的右边，
        // 正极还往外伸 0.5 + nub。原来直接 shell.Right + 5，扣掉 nub 实际只剩
        // 1.7px，数字几乎贴在正极上 —— 这就是"电量和百分比离得太近"。
        const float gap = 6f;
        string pct = s.Percent >= 0 ? $"{s.Percent}%" : "";
        float textW = 0f;
        if (pct.Length > 0)
        {
            Font font = UiTheme.Font(S(13f), true);
            SizeF m = UiTheme.Measure(g, pct, font);
            float lead = nub + S(gap);
            float tx = shell.Right + lead;
            Color c = low ? Color.FromArgb(240, 255, 100, 92) : InkDim;
            DrawShadowed(g, pct, font, c, InkShadow, tx, cy - m.Height * 0.5f);
            textW = lead + m.Width;
        }

        // used 必须把正极算进去，否则后面的网络图标会压到数字上
        used = w + textW;
        return used;
    }

    /// <summary>
    /// 电池里的充电闪电。
    ///
    /// 之前这道闪电只有电池高度的三分之一，13px 的电池里就是 4px 高的小白点，
    /// 而且和电池填充同色（都是 Ink），满电时直接糊成一片白 —— 等于没画。
    /// 现在改成：闪电撑满电池内腔并往上下各探出一点，用深色实心（相当于在浅色
    /// 填充上"挖"出一道闪电），再沿轮廓描一圈电池自身的颜色。
    /// 深色保证满电时也能看清，外圈留白保证在深背景或低电量红填充上仍有边界。
    /// </summary>
    private void DrawBolt(Gfx g, RectangleF shell, Color outline)
    {
        float cx = shell.Left + shell.Width * 0.5f;
        float cy = shell.Top + shell.Height * 0.5f;
        float u = shell.Height * 0.46f;

        var pts = new[]
        {
            new PointF(cx + u * 0.42f, cy - u * 1.02f),
            new PointF(cx - u * 0.72f, cy + u * 0.10f),
            new PointF(cx - u * 0.06f, cy + u * 0.10f),
            new PointF(cx - u * 0.34f, cy + u * 1.02f),
            new PointF(cx + u * 0.76f, cy - u * 0.14f),
            new PointF(cx + u * 0.08f, cy - u * 0.14f),
        };

        // 外圈：用电池本体色描一道细边，给闪电在任何底色上留出轮廓
        using (var pen = new Pen(outline, MathF.Max(1.6f, S(1.7f)))
        {
            LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
        })
        {
            g.DrawPolygon(pen, pts);
        }

        // 内芯：跟电池本体反色才看得见 —— 白填充上用近黑，深填充上用近白。
        // 原来固定用近黑，浅底一换深墨，闪电就整块糊进电池里了。
        using var brush = new SolidBrush(_darkInk
            ? Color.FromArgb(245, 252, 253, 255)
            : Color.FromArgb(245, 10, 14, 22));
        g.FillPolygon(brush, pts);
    }

    /// <summary>
    /// 网络：在线画三段 Wi-Fi 弧（有线在线时弧下面补一条横线表示"插着线"），
    /// 断网改画地球 —— 跟 Windows 托盘一致。
    ///
    /// 断网时原来画的是"Wi-Fi 弧 + 红斜杠"，但那个符号表达的是"信号被划掉了"，
    /// 而断网时根本没有信号可谈（WLAN 可能压根没连）。Windows 自己这时候显示的是
    /// 一颗地球，用户一眼就能对上系统自己的说法。
    /// </summary>
    private void DrawNetwork(Gfx g, float x, float cy, float size)
    {
        if (!_status.Online)
        {
            DrawGlobe(g, x, cy, size);
            return;
        }

        float stroke = MathF.Max(1.15f, S(1.15f));

        // 圆心在底部中央，向上画弧
        float cx = x + size * 0.5f;
        float baseY = cy + size * 0.30f;

        // 衬底：同一组图形先垫一层深色，再画细的本体。
        //
        // 这里连续踩过三次，都是"黑框"：
        //   1. 衬底比本体粗 1.6px → 三段弧的圆头端点互相叠出深色疙瘩；
        //   2. 弧的"发射源"那个点用 FillRectangle 画成方块 → 衬底的四个角露出来；
        //   3. 前两个修完还是被看出来 —— 因为衬底不但比本体粗 1.0px，还整体**往下偏 0.95px**。
        //      偏移 + 加粗叠加起来，每条弧下方会露出 1.8px 的深色，三条叠在一起就是一圈黑边。
        // 现在：不偏移、只粗 0.5px、alpha 70。每侧露出的深色只有 0.25px，
        // 抗锯齿摊开后只是一点点锐度，任何底色上都读不出"框"，同时还能托住白线。
        float underStroke = stroke + MathF.Max(0.4f, S(0.5f));
        DrawNetworkShape(g, cx, baseY, size, cy, IconShadow, underStroke, 0f, 0f);
        DrawNetworkShape(g, cx, baseY, size, cy, Ink, stroke, stroke * 0.62f, 0f);
    }

    /// <summary>
    /// 断网时的地球：圆 + 一条竖经线 + 赤道。
    /// 15px 见方画不下经纬网格，这三笔已经是"地球"的最小可用轮廓。
    /// 颜色跟在线时一样用 Ink，不染红 —— Windows 的托盘也不为断网变红，
    /// 红色留给"电池快没了"那种真正需要警示的地方。
    /// </summary>
    private void DrawGlobe(Gfx g, float x, float cy, float size)
    {
        // 比 Wi-Fi 弧细一档：弧只有三笔，地球有七笔，同样粗细会显得糊
        float stroke = MathF.Max(1f, S(1f));
        float underStroke = stroke + MathF.Max(0.4f, S(0.5f));
        float r = size * 0.42f;
        float cx = x + size * 0.5f;

        DrawGlobeShape(g, cx, cy, r, IconShadow, underStroke);
        DrawGlobeShape(g, cx, cy, r, Ink, stroke);
    }

    private static void DrawGlobeShape(Gfx g, float cx, float cy, float r, Color ink, float stroke)
    {
        using var pen = new Pen(ink, stroke);

        g.DrawEllipse(pen, cx - r, cy - r, r * 2f, r * 2f);

        // 经线不能画宽：占到圆直径的四成以上，整体就读成"圆里套了个椭圆"，
        // 像比例尺符号而不是地球。窄到三成、和赤道形成十字才认得出是球体。
        g.DrawEllipse(pen, cx - r * 0.30f, cy - r, r * 0.60f, r * 2f);

        g.DrawLine(pen, cx - r, cy, cx + r, cy);

        // 两条纬线：弦长按圆的方程取，端点才能正好落在圆上。
        // 不能贴近赤道 —— 三条横线挤在中间那一段，15px 下会糊成一团条纹。
        float latY = r * 0.62f;
        float halfW = MathF.Sqrt(MathF.Max(0f, r * r - latY * latY));
        g.DrawLine(pen, cx - halfW, cy - latY, cx + halfW, cy - latY);
        g.DrawLine(pen, cx - halfW, cy + latY, cx + halfW, cy + latY);
    }

    private void DrawNetworkShape(Gfx g, float cx, float baseY, float size, float cy,
        Color ink, float stroke, float dotR, float dy)
    {
        baseY += dy;
        cy += dy;
        using var pen = new Pen(ink, stroke)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };

        for (int i = 0; i < 3; i++)
        {
            float r = size * (0.24f + i * 0.24f);
            var box = new RectangleF(cx - r, baseY - r, r * 2f, r * 2f);
            g.DrawArc(pen, box, 225f, 90f);
        }

        // 底部的小圆点：三条弧的"发射源"，没有它看起来像三条孤立的线。
        // 必须是圆（圆头线段），画成方块会在投影层露出四个黑角。
        if (dotR > 0.15f)
        {
            using var brush = new SolidBrush(ink);
            g.FillEllipse(brush, cx - dotR, baseY - dotR, dotR * 2f, dotR * 2f);
        }

        // 有线在线：补一条横线当"网线口"，跟纯无线区分开
        if (!_status.Wifi)
        {
            using var wire = new Pen(ink, stroke)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawLine(wire, cx - size * 0.30f, cy + size * 0.48f, cx + size * 0.30f, cy + size * 0.48f);
        }
    }

    // ---------------------------------------------------------------- 交互

    protected override bool OnMouseMove(int x, int y)
    {
        if (_hover) return false;
        _hover = true;
        SetCursorKind(true);
        _contentDirty = true;
        _framePending = true;
        return true;
    }

    protected override bool OnMouseLeave()
    {
        if (!_hover) return false;
        _hover = false;
        SetCursorKind(false);
        _contentDirty = true;
        _framePending = true;
        return true;
    }

    protected override bool OnLeftUp(int x, int y)
    {
        QuickSettingsRequested?.Invoke();
        return true;
    }

    public override void Dispose()
    {
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
        base.Dispose();
    }
}
