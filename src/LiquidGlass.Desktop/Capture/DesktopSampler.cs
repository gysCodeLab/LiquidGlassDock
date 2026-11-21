using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LiquidGlass.Desktop.Graphics;

namespace LiquidGlass.Desktop.Capture;

/// <summary>
/// 桌面采样：DXGI Output Duplication（主方案）。
///
/// 为什么不用 Graphics Capture：
///  - Win11 24H2+ 的无边框采集需要用户授权，Dock 首次启动弹授权框体验不可接受；
///  - WGC 在采集期间可能显示边框提示；
///  - Duplication 在桌面无变化时直接返回 WAIT_TIMEOUT，静止时开销接近 0。
///
/// 递归防护：宿主窗口必须已经 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)，
/// 否则会拍到 Dock 自己，形成反馈回路（闪烁/拖影）。
/// </summary>
internal sealed class DesktopSampler : IDisposable
{
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorSessionDisconnected = unchecked((int)0x887A0028);

    private readonly D3DContext _ctx;
    private readonly IntPtr _monitor;

    private IDXGIOutputDuplication? _dup;
    private ID3D11Texture2D? _region;
    private ID3D11ShaderResourceView? _regionSrv;

    /// <summary>
    /// 上次尝试建立采集会话的 TickCount64。失败后不能每轮都重建 ——
    /// DuplicateOutput 要枚举适配器 + 建输出 + 建会话，几毫秒起步，
    /// 挂在 4Hz 的空闲循环上就是每秒白烧十几毫秒，还顺带把 GPU 叫醒。
    /// </summary>
    private long _lastInitAt;

    /// <summary>
    /// 两次重建之间的最小间隔（毫秒）。默认 1.5 秒。
    /// 锁屏期间 DuplicateOutput 必然 E_ACCESSDENIED，调用方会把间隔调大，
    /// 免得整夜每 1.5 秒枚举一遍适配器；解锁时再调回来并立刻 Reset。
    /// </summary>
    private long _retryIntervalMs = 1500;

    public int RetryIntervalMs
    {
        get => (int)_retryIntervalMs;
        set => _retryIntervalMs = Math.Clamp(value, 200, 60_000);
    }

    /// <summary>
    /// 重建会话后要强制拷贝的帧数。
    ///
    /// 为什么必须强制：重建出来的 Duplication 第一帧虽然带着完整桌面，
    /// 但 TotalMetadataBufferSize 往往是 0（相对"上一帧"没有脏区），会被下面的
    /// 自反馈过滤器当成"没变化"直接丢掉；而这一帧我们已经拿到了，桌面随后如果
    /// 静止（没人动鼠标），下一次成功帧要等到很久以后 —— 期间 region 里留着的
    /// 就是重建瞬间那帧（explorer 重启时的半成品画面）。用户看到的就是
    /// 「Dock 玻璃变成一条彩色大色块，怎么都不恢复」。
    /// </summary>
    private int _forceFrames;

    /// <summary>
    /// 最后一次成功拷贝的时间。作为兜底心跳：超过 HeartbeatMs 没拷贝成功过，
    /// 就借下一帧（哪怕 metadata 为 0）把 region 整块刷一遍，
    /// 保证任何"卡住的脏帧"最多活 HeartbeatMs。
    /// </summary>
    private long _lastCopyAt;

    /// <summary>
    /// 在这之前不做脏区过滤：每一帧都拷下来比像素。
    ///
    /// 为什么要这个：本程序的 Dock 背景取的是"我们自己窗口背后那一小块桌面"，
    /// 而切换前台时，新窗口是被 DWM 直接合成上去的 —— 这一下**未必**会被记进
    /// desktop duplication 的 dirty rect。metadata 为空，我们就把帧丢掉，
    /// 于是玻璃里一直挂着切换前那张画面，直到 heartbeat 兜底才自愈。
    /// 用户看到的正是"我已经到另一个页面了，Dock 和胶囊还停在原来那张背景"。
    ///
    /// 由 DockApp 在解冻（启动台关闭 / 解锁）时调用 ForceCompareFor(2000)。
    /// 代价是这段时间每帧多一次 984x272 的 GPU 拷贝（约 1MB），可以忽略。
    /// </summary>
    private long _forceCompareUntil;

    /// <summary>
    /// 心跳兜底间隔。超过这么久没拷贝成功过，就借下一帧（哪怕 metadata 为 0）
    /// 把 region 整块刷一遍。
    ///
    /// 原来是 5 秒 —— 那意味着"脏区漏报"的最坏情况要卡 5 秒，用户早就看出来了。
    /// 改成 1 秒：只在真的连续 1 秒都拿不到脏区时多拷一次，稳态开销为零。
    /// </summary>
    private const long HeartbeatMs = 1000;

    private int _texW;
    private int _texH;
    private int _regionW = -1;
    private int _regionH = -1;
    private int _regionX = int.MinValue;
    private int _regionY = int.MinValue;

    /// <summary>
    /// <see cref="_region"/> 里是不是真的拷进过桌面像素。
    /// 换区域 / Reset 之后会重建纹理，新建的纹理内容是未定义的 ——
    /// 不区分这一点，渲染那边就可能把一块没写过的新纹理当成"最后一帧"画出去。
    /// </summary>
    private bool _regionHasPixels;

    // ---- 冻结帧 ----
    //
    // 锁屏 / 解锁 / 唤醒的过渡期，唯一干净的画面是"用户锁屏前那一帧桌面"。
    // 但它只存在于 _region 里，而解锁后的 Reset → ForceRegionRebuild 会把 _region
    // 整块重建（旧像素随之消失），过渡期又采不到真桌面（安全桌面拒绝 Duplication，
    // BitBlt 抓到的是锁屏画面）。所以锁屏那一刻把 _region 拷进一份独立纹理，
    // 过渡期就永远有一张干净的桌面可画，跟采集会话的生死完全解耦。
    private ID3D11Texture2D? _frozenTex;
    private ID3D11ShaderResourceView? _frozenSrv;
    private int _frozenW = -1;
    private int _frozenH = -1;
    private int _frozenX = int.MinValue;
    private int _frozenY = int.MinValue;
    private bool _frozenValid;

    /// <summary>同一时刻的第二采样区（状态胶囊）冻结帧。胶囊是独立窗口，得单独存一份。</summary>
    private ID3D11Texture2D? _frozen2Tex;
    private ID3D11ShaderResourceView? _frozen2Srv;
    private int _frozen2W = -1;
    private int _frozen2H = -1;
    private int _frozen2X = int.MinValue;
    private int _frozen2Y = int.MinValue;
    private bool _frozen2Valid;
    private bool _region2HasPixels;

    /// <summary>冻结的兜底有效期（TickCount64）。Freeze 时是"无限"，
    /// 由 Dock 在解锁/唤醒时用 <see cref="ArmFreezeTimeout"/> 开始计时。</summary>
    private long _frozenUntil = long.MaxValue;

    // ---- 第二采样区：Dock 之外那小块（右端的状态胶囊） ----
    //
    // 为什么不能没有它：一个输出只允许一个 Duplication，所以不能给胶囊再开一份会话；
    // 而胶囊在 Dock 玻璃右边、压根不在主采样区的矩形里 —— 主区的纹理拿它去采，
    // 坐标被夹到边缘，着色器就在边缘那一列像素上反复取样。
    // 玻璃看起来就是"一块没有内容的糊色块"，这正是它跟 Dock 本体不像的根因。
    // 现在同一次 AcquireNextFrame 里顺手多抠一块，代价只有一次小 CopySubresourceRegion。
    private ID3D11Texture2D? _region2;
    private ID3D11ShaderResourceView? _region2Srv;
    private int _r2W = -1;
    private int _r2H = -1;
    private int _r2X = int.MinValue;
    private int _r2Y = int.MinValue;

    /// <summary>接下来还要强制拷几次第二区。区域刚变化 / 会话刚重建时置位。</summary>
    private int _secondaryForce;

    // ---- 第三采样区：启动台面板 ----
    //
    // 启动台是全屏窗口，面板比 Dock 条大一个数量级，而它打开的时候 Dock 仍然浮在
    // 上面照常实时显示 —— 一个输出只允许一个 Duplication，所以不能把主区让给启动台，
    // 只能在同一次 AcquireNextFrame 里多抠一块。代价是一次大尺寸 GPU 拷贝，
    // 只在桌面真的变化时才发生。
    private ID3D11Texture2D? _region3;
    private ID3D11ShaderResourceView? _region3Srv;
    private int _r3W = -1;
    private int _r3H = -1;
    private int _r3X = int.MinValue;
    private int _r3Y = int.MinValue;
    private bool _region3HasPixels;
    private int _tertiaryForce;
    private bool _loggedTertiaryRegion;
    private bool _fpTertiaryValid;

    // 启动台的指纹必须是**网格**，不能沿用主区那条 4 条横带。
    //
    // 4 条横带在 773x80 的 Dock 条上覆盖得不错，摊到 1376x916 的面板上就是
    // 每 344x458 才有一个 32x2 的点 —— 面板里只要有窗口在别处打字 / 播放，
    // 一个采样点都碰不到，玻璃就一直挂着打开瞬间那一帧，看起来就是"静止的"。
    // 6x6 网格（36 条 32x8）在同样的面板上间距约 229x153，任何一块正常尺寸的
    // 窗口变化都会压中至少一条。
    private const int TfpCols = 6;
    private const int TfpRows = 6;
    private const int TfpW = 32;
    private const int TfpH = 8;
    private const int TfpBands = TfpCols * TfpRows;
    private ID3D11Texture2D? _fp3Tex;
    private readonly byte[] _fpBuf3 = new byte[TfpW * TfpBands * TfpH * 4];
    private readonly byte[] _fpPrevTertiary = new byte[TfpW * TfpBands * TfpH * 4];

    /// <summary>本轮采样里，第三采样区（启动台面板）的像素是不是真的变了。</summary>
    public bool TertiaryChanged { get; private set; }

    // ---- 像素指纹 ----
    //
    // 为什么必须有这一层：DWM 每次重新合成都可能让 AcquireNextFrame 成功并报出脏区，
    // 哪怕那块脏区只是我们**自己刚 Present 的那一帧**（它设置了 EXCLUDEFROMCAPTURE，
    // 对采集结果一个像素都没影响）。只按 metadata 判定，就会形成
    // 「我画一帧 → 桌面被判为变了 → 我再画一帧」的自反馈：
    // 空闲时间照跑 13~60fps，CPU 白烧，而且永远停不下来。
    //
    // 唯一可靠的判据是**直接比像素**：把采样区里几条横带读回来，和上一轮逐字节比。
    // 只有真的变了才算"新背景"。这样"背景一变就刷新"和"不重绘"可以同时成立 ——
    // 之前的版本是在两者之间二选一（要么 2 秒兜底、要么自反馈），问题就出在这儿。
    private ID3D11Texture2D? _fpTex;
    private const int FpW = 32;
    private const int FpH = 8;

    private readonly byte[] _fpBuf = new byte[FpW * FpH * 4];
    private readonly byte[] _fpPrevMain = new byte[FpW * FpH * 4];
    private readonly byte[] _fpPrevSecondary = new byte[FpW * FpH * 4];
    private bool _fpMainValid;
    private bool _fpSecondaryValid;

    /// <summary>本轮采样里，Dock 主采样区的像素是不是真的变了。</summary>
    public bool MainChanged { get; private set; }

    /// <summary>本轮采样里，第二采样区（状态胶囊）的像素是不是真的变了。</summary>
    public bool SecondaryChanged { get; private set; }

    private bool _loggedSecondaryRegion;

    public bool Available => _dup is not null;

    /// <summary>
    /// 这一帧能不能用。
    ///
    /// 判据只有"会话在 + 拿到过帧 + 实际像素不是全黑"。
    /// 特意**不用** ProtectedContentMaskedOut 当门槛：DWM 会把它偶发地置 true
    /// （屏幕上任何地方有受保护内容、甚至只是叠了个硬件覆盖层就会报），
    /// 实测它每几百毫秒翻一次 —— 拿它做判据的结果就是背景在"实时"和"兜底"
    /// 之间疯狂抖动。像素是不是全黑，只有真的读一眼才知道，那才是决定性的证据。
    /// </summary>
    public bool IsLive => _dup is not null && HasFrame && _regionSrv is not null
                          && !LooksBlack();

    /// <summary>
    /// 黑帧探测结果的缓存。Map 一次 staging 纹理会强制同步 GPU，
    /// 所以最多每秒做一次 —— 玻璃背景黑不黑，一秒的判定粒度完全够用。
    /// </summary>
    public bool LooksBlack()
    {
        long now = Environment.TickCount64;
        if (now - _lastProbeAt < 1000) return _probeBlack;
        _lastProbeAt = now;
        _probeBlack = ProbeRegionBlack();
        return _probeBlack;
    }

    private ID3D11Texture2D? _probeTex;
    private long _lastProbeAt = long.MinValue / 2;
    private bool _probeBlack;

    private bool ProbeRegionBlack()
    {
        if (_region is null || _regionW < 64 || _regionH < 16) return false;

        const int pw = 32, ph = 8;
        try
        {
            if (_probeTex is null)
            {
                var desc = new Texture2DDescription
                {
                    Width = pw,
                    Height = ph,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                };
                _probeTex = _ctx.Device.CreateTexture2D(desc);
            }

            // 取区域正中一条 32x8 的横条：只有 256 个像素，回读代价可以忽略。
            int mx = (_regionW - pw) / 2;
            int my = (_regionH - ph) / 2;
            var box = new Vortice.Mathematics.Box(mx, my, 0, mx + pw, my + ph, 1);
            _ctx.Context.CopySubresourceRegion(_probeTex, 0, 0, 0, 0, _region, 0, box);

            MappedSubresource mapped = _ctx.Context.Map(_probeTex, 0, MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (int y = 0; y < ph; y++)
                {
                    IntPtr row = mapped.DataPointer + y * (int)mapped.RowPitch;
                    for (int x = 0; x < pw; x++)
                    {
                        int i = x * 4;
                        // 阈值放到 6：真正的纯黑帧每一个通道都是 0，
                        // 而任何壁纸（哪怕深色）都会有几个非零像素。
                        if (System.Runtime.InteropServices.Marshal.ReadByte(row, i) > 6 ||
                            System.Runtime.InteropServices.Marshal.ReadByte(row, i + 1) > 6 ||
                            System.Runtime.InteropServices.Marshal.ReadByte(row, i + 2) > 6)
                            return false;
                    }
                }
                return true;
            }
            finally
            {
                _ctx.Context.Unmap(_probeTex, 0);
            }
        }
        catch
        {
            return false;
        }
    }

    // 诊断计数：到底是真的有新帧，还是在空转
    public long HitCount { get; private set; }
    public long SelfLoopCount { get; private set; }
    public long MissCount { get; private set; }
    public long LostCount { get; private set; }
    public string Status { get; private set; } = "未初始化";
    public bool HasFrame { get; private set; }
    public bool ProtectedMasked { get; private set; }

    /// <summary>最近一次成功采集的屏幕区域（物理像素）。</summary>
    public int RegionX => _regionX;
    public int RegionY => _regionY;
    public int RegionW => _regionW;
    public int RegionH => _regionH;

    public ID3D11ShaderResourceView? Srv => _regionSrv;

    /// <summary>第二采样区（状态胶囊）的纹理。没配置时不创建，Srv 保持 null。</summary>
    public ID3D11ShaderResourceView? Srv2 => _region2Srv;
    public int SecondaryX => _r2X;
    public int SecondaryY => _r2Y;
    public int SecondaryW => _r2W;
    public int SecondaryH => _r2H;
    public bool HasSecondary => _region2Srv is not null;

    /// <summary>第二采样区累计成功拷贝的次数。诊断用：判断胶囊的贴图到底有没有在更新。</summary>
    public long SecondaryCopies { get; private set; }

    /// <summary>第二采样区最近一次指纹采样的平均亮度（0~1）。诊断用。</summary>
    public float SecondaryFpLuma { get; private set; }

    /// <summary>主采样区累计成功拷贝的次数。诊断用。</summary>
    public long MainCopies { get; private set; }

    /// <summary>上一次指纹采样的平均亮度（内部传递用）。</summary>
    private float _lastFpLuma;

    /// <summary>第三采样区（启动台面板）的纹理与坐标。没配置或还没拷到像素时保持 null。</summary>
    public ID3D11ShaderResourceView? Srv3 => _region3Srv;
    public int TertiaryX => _r3X;
    public int TertiaryY => _r3Y;
    public int TertiaryW => _r3W;
    public int TertiaryH => _r3H;
    public bool HasTertiary => _region3Srv is not null && _region3HasPixels;

    /// <summary>冻结帧是否可用（锁屏前最后一张真桌面）。</summary>
    public bool FrozenActive => _frozenValid && _frozenSrv is not null;
    public ID3D11ShaderResourceView? FrozenSrv => _frozenSrv;
    public int FrozenX => _frozenX;
    public int FrozenY => _frozenY;
    public int FrozenW => _frozenW;
    public int FrozenH => _frozenH;

    /// <summary>冻结的第二采样区（状态胶囊）。没冻过就是 null。</summary>
    public ID3D11ShaderResourceView? Frozen2Srv =>
        FrozenActive && _frozen2Valid ? _frozen2Srv : null;
    public int FrozenSecondaryX => _frozen2X;
    public int FrozenSecondaryY => _frozen2Y;
    public int FrozenSecondaryW => _frozen2W;
    public int FrozenSecondaryH => _frozen2H;

    /// <summary>冻结帧是否已经过了兜底有效期（一直采不到活桌面时用来收尾）。</summary>
    public bool FrozenExpired => FrozenActive && Environment.TickCount64 > _frozenUntil;

    /// <summary>
    /// 把当前 <see cref="_region"/> 拷进冻结纹理。锁屏 / 挂起时调用，
    /// 只拷已经真的有过像素的那一帧 —— 新纹理（刚重建、还没写过）不能冻。
    /// </summary>
    public bool Freeze()
    {
        if (_region is null || _regionSrv is null || !_regionHasPixels
            || _regionW <= 0 || _regionH <= 0 || _regionX == int.MinValue)
        {
            App.Logger.Warn("[freeze] 没有可用帧可冻结（锁屏前最后一帧会丢）");
            return false;
        }

        try
        {
            if (_frozenTex is null || _frozenW != _regionW || _frozenH != _regionH)
            {
                _frozenSrv?.Dispose();
                _frozenTex?.Dispose();
                _frozenSrv = null;
                _frozenTex = null;

                var desc = new Texture2DDescription
                {
                    Width = (uint)_regionW,
                    Height = (uint)_regionH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                };

                _frozenTex = _ctx.Device.CreateTexture2D(desc);
                _frozenSrv = _ctx.Device.CreateShaderResourceView(_frozenTex, null);
            }

            _ctx.Context.CopyResource(_frozenTex, _region);
            _frozenW = _regionW;
            _frozenH = _regionH;
            _frozenX = _regionX;
            _frozenY = _regionY;
            _frozenValid = true;
            _frozenUntil = long.MaxValue;   // 锁屏可能持续几小时，计时从解锁那刻才开始

            FreezeSecondary();
            App.Logger.Info($"[freeze] 已冻结锁屏前桌面 {_frozenW}x{_frozenH} @({_frozenX},{_frozenY})"
                            + (_frozen2Valid ? $"（含胶囊区 {_frozen2W}x{_frozen2H}）" : ""));
            return true;
        }
        catch (Exception ex)
        {
            _frozenValid = false;
            App.Logger.Warn("[freeze] 冻结失败: " + ex.Message);
            return false;
        }
    }

    private void FreezeSecondary()
    {
        _frozen2Valid = false;
        if (_region2 is null || _region2Srv is null || !_region2HasPixels || _r2W <= 0 || _r2H <= 0) return;

        try
        {
            if (_frozen2Tex is null || _frozen2W != _r2W || _frozen2H != _r2H)
            {
                _frozen2Srv?.Dispose();
                _frozen2Tex?.Dispose();
                _frozen2Srv = null;
                _frozen2Tex = null;

                var desc = new Texture2DDescription
                {
                    Width = (uint)_r2W,
                    Height = (uint)_r2H,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None,
                };

                _frozen2Tex = _ctx.Device.CreateTexture2D(desc);
                _frozen2Srv = _ctx.Device.CreateShaderResourceView(_frozen2Tex, null);
            }

            _ctx.Context.CopyResource(_frozen2Tex, _region2);
            _frozen2W = _r2W;
            _frozen2H = _r2H;
            _frozen2X = _r2X;
            _frozen2Y = _r2Y;
            _frozen2Valid = true;
        }
        catch
        {
            // 胶囊那块冻不上不影响 Dock 本体，退回它自己的兜底即可
            _frozen2Valid = false;
        }
    }

    /// <summary>解锁 / 唤醒后开始给冻结帧计时：超过这段时间还采不到活桌面就放弃它。</summary>
    public void ArmFreezeTimeout(int ms)
    {
        if (FrozenActive) _frozenUntil = Environment.TickCount64 + Math.Max(500, ms);
    }

    /// <summary>放弃冻结帧（已经采到活桌面，或者兜底超时）。</summary>
    public void ReleaseFreeze()
    {
        if (!_frozenValid) return;
        _frozenValid = false;
        _frozen2Valid = false;
        _frozenUntil = long.MaxValue;
    }

    /// <summary>
    /// 接下来这段时间里，不再相信 "metadata 为空 = 桌面没变"，每帧都拷下来比像素。
    ///
    /// 场景：启动台关闭 / 锁屏解锁 —— 桌面刚被换过，但 DWM 有相当大概率不给脏区。
    /// 见 _forceCompareUntil 的说明。
    /// </summary>
    public void ForceCompareFor(int ms)
    {
        long until = Environment.TickCount64 + Math.Max(0, ms);
        if (until > _forceCompareUntil) _forceCompareUntil = until;
    }

    /// <summary>
    /// 指定第二采样区（屏幕物理像素）。坐标/尺寸没变就是空操作 —— 调用方
    /// 每帧无脑调一次不会有多余开销。
    /// </summary>
    public void SetSecondaryRegion(int x, int y, int w, int h)
    {
        if (w < 16 || h < 16)
        {
            DisposeSecondary();
            _r2W = -1;
            _r2H = -1;
            return;
        }

        if (_r2X == x && _r2Y == y && _r2W == w && _r2H == h) return;

        // 只有尺寸变了才重建纹理。坐标变化要复用它 —— Dock 展开的那 300ms 里
        // 胶囊坐标每帧都在动，按坐标重建就是每秒六十次 CreateTexture2D。
        if (_r2W != w || _r2H != h) DisposeSecondary();

        _r2X = x;
        _r2Y = y;
        _r2W = w;
        _r2H = h;
        // 换了地方就必须重新抠一次：上一次的像素是旧位置的，折射会整体错位。
        _secondaryForce = 3;
        // 同理，指纹也要作废：新位置的像素跟旧位置不可比，必须判成"变了"，
        // 否则胶囊会一直挂着上一处的背景。
        _fpSecondaryValid = false;

        if (!_loggedSecondaryRegion)
        {
            _loggedSecondaryRegion = true;
            App.Logger.Info($"[sampler] 第二采样区 {w}x{h} @({x},{y})");
        }
    }

    /// <summary>
    /// 比对一张采样纹理的像素指纹。
    ///
    /// 取纵向四条横带（各 32x2 像素）读回 CPU —— 定死 256 个像素，
    /// 覆盖区域高度的 1/8、3/8、5/8、7/8 四层，横向取中间 32 列。
    /// 不去降采样整张图：那要多一趟 shader；而"壁纸换色、窗口切换、视频播放"
    /// 这类变化在四条横向带上一定有反应，256 个点足够灵敏。
    ///
    /// 每轮只在"确实有可能变化"时才调（见 TryUpdate），静止时一次都不调，
    /// 所以那一次 Map 的同步开销不会进入空闲路径。
    /// </summary>
    private bool FingerprintChanged(ID3D11Texture2D src, int w, int h,
        byte[] prev, ref bool valid)
    {
        if (src is null || w < FpW + 8 || h < FpH) return false;

        try
        {
            if (_fpTex is null)
            {
                var desc = new Texture2DDescription
                {
                    Width = FpW,
                    Height = FpH,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                };
                _fpTex = _ctx.Device.CreateTexture2D(desc);
            }

            // 横向取三处（左 1/8、中、右 7/8），纵向取上下两层。
            // 只取正中一条时，宽区域边缘的变化完全看不见 —— 启动台那种 1400px 的面板
            // 只要变化发生在左右两侧，玻璃就会一直挂着旧背景。四条带的总像素数没变。
            int sx1 = Math.Max(0, w / 8 - FpW / 2);
            int sx2 = Math.Max(0, (w - FpW) / 2);
            int sx3 = Math.Max(0, w * 7 / 8 - FpW / 2);
            int syTop = Math.Clamp(h / 4, 0, Math.Max(0, h - 2));
            int syBottom = Math.Clamp(h * 3 / 4, 0, Math.Max(0, h - 2));
            (int X, int Y)[] spots = { (sx1, syTop), (sx2, syTop), (sx3, syTop), (sx2, syBottom) };
            for (int band = 0; band < spots.Length; band++)
            {
                // 每条带取 2 行，四个 band 拼成 staging 的 8 行
                var box = new Vortice.Mathematics.Box(spots[band].X, spots[band].Y, 0,
                    spots[band].X + FpW, spots[band].Y + 2, 1);
                _ctx.Context.CopySubresourceRegion(_fpTex, 0, 0, (uint)(band * 2), 0, src, 0, box);
            }

            MappedSubresource mapped = _ctx.Context.Map(_fpTex, 0, MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (int y = 0; y < FpH; y++)
                {
                    IntPtr row = mapped.DataPointer + y * (int)mapped.RowPitch;
                    // 一次拷一整行（32 像素 = 128 字节），比逐像素 Marshal.ReadByte 快一个量级
                    System.Runtime.InteropServices.Marshal.Copy(row, _fpBuf, y * FpW * 4, FpW * 4);
                }
            }
            finally
            {
                _ctx.Context.Unmap(_fpTex, 0);
            }

            bool changed = !valid || !_fpBuf.AsSpan().SequenceEqual(prev);
            _fpBuf.CopyTo(prev, 0);
            valid = true;

            // 诊断：把这批采样点的平均亮度留下来。用来对账"DXGI 说没变"和
            // "屏幕上其实已经白了"这类矛盾（见 SecondaryFpLuma）。
            int sum = 0;
            for (int i = 0; i < _fpBuf.Length; i += 4) sum += _fpBuf[i + 1];
            _lastFpLuma = sum / (float)(_fpBuf.Length / 4) / 255f;

            return changed;
        }
        catch
        {
            // 读不回来就当作"变了"：宁可多重绘一帧，也不能让玻璃停在旧画面上
            valid = false;
            return true;
        }
    }

    /// <summary>
    /// 启动台第三区的像素指纹：在整个区域上打一张 6x6 网格（36 条 32x8 横带），
    /// 逐格比对上轮读回的像素。网格间距跟着区域尺寸走，所以无论面板多大、
    /// 变化发生在什么位置，都至少有一条带压在上面。
    /// 只在这块区域存在（启动台开着）且 DWM 报了变化时才调用。
    /// </summary>
    private bool FingerprintChangedGrid(ID3D11Texture2D src, int w, int h)
    {
        if (src is null || w < TfpW + 8 || h < TfpH) return false;

        try
        {
            if (_fp3Tex is null)
            {
                var desc = new Texture2DDescription
                {
                    Width = TfpW,
                    Height = (uint)(TfpBands * TfpH),
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None,
                };
                _fp3Tex = _ctx.Device.CreateTexture2D(desc);
            }

            for (int gy = 0; gy < TfpRows; gy++)
            {
                for (int gx = 0; gx < TfpCols; gx++)
                {
                    // 取每个格子的中心，夹在区域内，避免边上越界
                    int cx = Math.Clamp((int)((gx + 0.5f) * w / TfpCols) - TfpW / 2, 0, Math.Max(0, w - TfpW));
                    int cy = Math.Clamp((int)((gy + 0.5f) * h / TfpRows) - TfpH / 2, 0, Math.Max(0, h - TfpH));
                    var box = new Vortice.Mathematics.Box(cx, cy, 0, cx + TfpW, cy + TfpH, 1);
                    _ctx.Context.CopySubresourceRegion(_fp3Tex, 0, 0,
                        (uint)((gy * TfpCols + gx) * TfpH), 0, src, 0, box);
                }
            }

            int rows = TfpBands * TfpH;
            MappedSubresource mapped = _ctx.Context.Map(_fp3Tex, 0, MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);
            try
            {
                for (int y = 0; y < rows; y++)
                {
                    IntPtr row = mapped.DataPointer + y * (int)mapped.RowPitch;
                    System.Runtime.InteropServices.Marshal.Copy(row, _fpBuf3, y * TfpW * 4, TfpW * 4);
                }
            }
            finally
            {
                _ctx.Context.Unmap(_fp3Tex, 0);
            }

            bool changed = !_fpTertiaryValid || !_fpBuf3.AsSpan().SequenceEqual(_fpPrevTertiary);
            _fpBuf3.CopyTo(_fpPrevTertiary, 0);
            _fpTertiaryValid = true;
            return changed;
        }
        catch
        {
            // 读不回来就当作"变了"：宁可多重绘一帧，也不能让面板停在旧画面上
            _fpTertiaryValid = false;
            return true;
        }
    }

    private void ResetFingerprints()
    {
        _fpMainValid = false;
        _fpSecondaryValid = false;
        _fpTertiaryValid = false;
    }


    public DesktopSampler(D3DContext ctx, IntPtr monitor)
    {
        _ctx = ctx;
        _monitor = monitor;
        TryInit();
    }

    public bool TryInit()
    {
        _lastInitAt = Environment.TickCount64;
        DisposeDuplication();
        try
        {
            IDXGIOutput? output = FindOutputForMonitor(_monitor);
            if (output is null)
            {
                Status = "找不到对应显示器输出";
                return false;
            }

            using (output)
            {
                OutputDescription od = output.Description;
                _texW = od.DesktopCoordinates.Right - od.DesktopCoordinates.Left;
                _texH = od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top;

                using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                IDXGIOutputDuplication? dup;
                try
                {
                    dup = output1.DuplicateOutput(_ctx.Device);
                }
                catch (SharpGen.Runtime.SharpGenException ex)
                {
                    int code = ex.ResultCode.Code;
                    Status = $"DuplicateOutput 失败 0x{code:X8}" + (code == unchecked((int)0x887A0022)
                        ? "（可能被录屏软件占用）" : "");
                    return false;
                }

                if (dup is null)
                {
                    Status = "DuplicateOutput 返回空";
                    return false;
                }

                _dup = dup;
                _forceFrames = 4;
                _lastCopyAt = Environment.TickCount64;
                Status = "运行中";
                return true;
            }
        }
        catch (Exception ex)
        {
            Status = "初始化异常: " + ex.Message;
            return false;
        }
    }

    private IDXGIOutput? FindOutputForMonitor(IntPtr monitor)
    {
        var info = new Platform.Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Platform.Native.MONITORINFO>() };
        if (!Platform.Native.GetMonitorInfo(monitor, ref info)) return null;

        int cx = (info.rcMonitor.Left + info.rcMonitor.Right) / 2;
        int cy = (info.rcMonitor.Top + info.rcMonitor.Bottom) / 2;

        for (uint a = 0; ; a++)
        {
            if (_ctx.Factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Failure) break;
            bool found = false;
            for (uint o = 0; ; o++)
            {
                if (adapter.EnumOutputs(o, out IDXGIOutput output).Failure) break;
                OutputDescription od = output.Description;
                if (od.AttachedToDesktop &&
                    cx >= od.DesktopCoordinates.Left && cx < od.DesktopCoordinates.Right &&
                    cy >= od.DesktopCoordinates.Top && cy < od.DesktopCoordinates.Bottom)
                {
                    found = true;
                    adapter.Dispose();
                    return output;
                }

                output.Dispose();
            }

            adapter.Dispose();
            if (found) break;
        }

        return null;
    }

    private bool EnsureRegion(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return false;
        if (_region is not null && _regionW == w && _regionH == h && _regionX == x && _regionY == y)
            return true;

        _regionSrv?.Dispose();
        _region?.Dispose();
        _regionSrv = null;
        _region = null;

        _regionW = w;
        _regionH = h;
        _regionX = x;
        _regionY = y;

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _region = _ctx.Device.CreateTexture2D(desc);
        _regionSrv = _ctx.Device.CreateShaderResourceView(_region, null);
        // 新纹理还没写过，内容是未定义的，不能当成"有画面"
        _regionHasPixels = false;
        // 而且它必须被下一帧填满，哪怕 DWM 报"没变化" —— 区域刚换过、纹理刚重建，
        // 不强制放行就可能一直挂着未初始化的内容。
        _forceFrames = Math.Max(_forceFrames, 2);

        // 换了纹理就等于换了"上一帧"，旧指纹没有任何可比性
        ResetFingerprints();
        return true;
    }

    /// <summary>
    /// 尝试取一帧并裁剪到指定屏幕矩形。
    /// 返回 true 表示有新内容（需要重绘）。
    /// <paramref name="blockMs"/> &gt; 0 时该调用会阻塞等待桌面变化，
    /// 空闲期靠它代替忙轮询；交互期必须传 0，否则每帧多等十几毫秒。
    /// </summary>
    public bool TryUpdate(int x, int y, int w, int h, int blockMs = 0)
    {
        MainChanged = false;
        SecondaryChanged = false;
        TertiaryChanged = false;
        // 会话没了要自己爬起来。
        //
        // 原来的写法是 `_dup is null → return false`，而 TryInit 只在构造函数里调一次：
        // 锁屏、合盖唤醒、显卡驱动重置、显示器分辨率切换都会让 DuplicateOutput 失效
        // （AccessLost / SessionDisconnected），那之后 _dup 永远是 null，
        // 玻璃就永久退化成着色器里的降级色块 —— 一层蓝灰色渐变，一动不动。
        // 用户看到的就是「莫名其妙变成蓝色静态背景」，而且只有重启程序才好。
        //
        // 现在每次调用都顺手检查，失败后按 RetryIntervalMs 退避重试，
        // 重建成功立刻恢复折射，不需要人管。
        if (_dup is null)
        {
            if (Environment.TickCount64 - _lastInitAt < _retryIntervalMs) return false;
            if (!TryInit()) return false;
            // 新建的会话要用新的显示器尺寸重新量一遍区域
            ForceRegionRebuild();
        }

        if (_region is null && !EnsureRegion(x, y, w, h)) return false;

        // 区域变化时需要重建（屏幕分辨率/位置变了）
        if (_regionW != w || _regionH != h || _regionX != x || _regionY != y)
        {
            if (!EnsureRegion(x, y, w, h)) return false;
        }

        Result r = _dup.AcquireNextFrame((uint)Math.Max(0, blockMs), out OutduplFrameInfo info, out IDXGIResource resource);
        if (r.Code == DxgiErrorWaitTimeout)
        {
            MissCount++;

            return false;
        }

        if (r.Code == DxgiErrorAccessLost || r.Code == DxgiErrorSessionDisconnected)
        {
            LostCount++;
            Status = "采集会话丢失，正在重建";
            // 只把会话丢掉，重建交给下一次 TryUpdate 的退避逻辑。
            // 如果这里直接 TryInit()，而系统又持续返回 AccessLost（锁屏期间就是这样），
            // 每一轮都会全量重建一次采集会话 —— 那是实打实的耗电。
            DisposeDuplication();
            HasFrame = false;
            return false;
        }

        if (r.Failure)
        {
            Status = $"AcquireNextFrame 失败 0x{r.Code:X8}";
            return false;
        }

        // 关键：DWM 每次重合成（包括我们自己的 Present 引起的那次）都会让
        // AcquireNextFrame 返回成功，但画面可能一个像素都没变。这会把
        // Dock 拖进"我画一帧 → 桌面被视为变了 → 我再画一帧"的自反馈，
        // 空闲时白白跑满 60fps。dirty/move 元数据为空即代表真的没变。
        //
        // 但这层过滤有两个必须放行的情况，否则会永久卡在脏画面上：
        //   1. 会话刚重建（_forceFrames > 0）—— 第一帧是完整桌面，metadata 却是 0；
        //   2. 已经 HeartbeatMs 没拷贝成功过 —— 兜底自愈，最多 5 秒把脏帧刷掉。
        bool heartbeat = Environment.TickCount64 - _lastCopyAt >= HeartbeatMs;
        // 解冻后的短窗口里无条件比像素：切换前台那几帧 DWM 经常不报脏区，
        // 过滤掉就会一直停在旧画面（见 _forceCompareUntil 的说明）。
        bool forceCompare = Environment.TickCount64 < _forceCompareUntil;
        if (info.TotalMetadataBufferSize == 0 && info.PointerShapeBufferSize == 0
            && _forceFrames <= 0 && !heartbeat && !forceCompare)
        {
            SelfLoopCount++;
            MissCount++;
            ProtectedMasked = info.ProtectedContentMaskedOut;
            // 主区没变化要丢帧，但第二区可能刚刚换过位置（胶囊被 Dock 推开）
            // 或者会话刚重建 —— 这种时候必须借这一帧把胶囊背后补上，
            // 否则胶囊会一直显示旧位置的背景，直到桌面自己发生别的变化。
            if (_secondaryForce > 0 && _r2W > 0)
            {
                try
                {
                    using ID3D11Texture2D frame2 = resource.QueryInterface<ID3D11Texture2D>();
                    CopySecondary(frame2);
                }
                catch { /* 补拷失败不影响主区判定 */ }
            }

            // 第三区同理：刚换位置 / 刚建纹理，必须借这一帧把背景补上
            if (_tertiaryForce > 0 && _r3W > 0)
            {
                try
                {
                    using ID3D11Texture2D frame3 = resource.QueryInterface<ID3D11Texture2D>();
                    CopyTertiary(frame3);
                }
                catch { /* 同上 */ }
            }

            resource.Dispose();
            _dup.ReleaseFrame();
            return false;
        }

        try
        {
            ProtectedMasked = info.ProtectedContentMaskedOut;
            using ID3D11Texture2D frame = resource.QueryInterface<ID3D11Texture2D>();

            // 屏幕坐标 → 纹理坐标，并夹取到纹理范围内（多屏/负坐标场景必须夹取）
            int sx = Math.Clamp(x, 0, Math.Max(0, _texW - 1));
            int sy = Math.Clamp(y, 0, Math.Max(0, _texH - 1));
            int ex = Math.Clamp(x + w, 0, _texW);
            int ey = Math.Clamp(y + h, 0, _texH);
            int cw = Math.Max(0, ex - sx);
            int ch = Math.Max(0, ey - sy);
            if (cw == 0 || ch == 0) return false;

            var box = new Vortice.Mathematics.Box(sx, sy, 0, sx + cw, sy + ch, 1);
            _ctx.Context.CopySubresourceRegion(_region!, 0, 0, 0, 0, frame, 0, box);
            MainCopies++;

            // 同一次 acquire 里顺手把胶囊那块也抠下来。一帧纹理可以拷任意多次，
            // 不需要第二次 AcquireNextFrame。
            if (_r2W > 0) CopySecondary(frame);

            // 启动台面板（打开时才有）同理，同一帧里再抠一块
            if (_r3W > 0) CopyTertiary(frame);

            // 拷完立刻比像素。这一步是整条链路的判据 ——
            // metadata 说有变化只能证明"DWM 重新合成过"，不能证明"玻璃背后真的变了"。
            // 我们自己的 Present 也会让 metadata 非零，只有比像素能把两者分开。
            MainChanged = FingerprintChanged(_region!, _regionW, _regionH, _fpPrevMain, ref _fpMainValid);
            SecondaryChanged = _r2W > 0 && _region2 is not null
                && FingerprintChanged(_region2, _r2W, _r2H, _fpPrevSecondary, ref _fpSecondaryValid);
            SecondaryFpLuma = _lastFpLuma;
            TertiaryChanged = _r3W > 0 && _region3 is not null
                && FingerprintChangedGrid(_region3, _r3W, _r3H);

            if (_forceFrames > 0) _forceFrames--;
            _lastCopyAt = Environment.TickCount64;
            HasFrame = true;

            // 诊断：metadata 说"没变"、像素却变了 —— 证明脏区确实漏报，
            // 也证明这条路径靠逐帧比对把背景救回来了。只在强制窗口内记，
            // 且只在"真的发生"时记，平时不会刷日志。
            if (forceCompare && MainChanged
                && info.TotalMetadataBufferSize == 0 && info.PointerShapeBufferSize == 0)
            {
                App.Logger.Info("[force] 脏区漏报：metadata 为空但主区画面变了，已强行采纳这一帧");
            }
            _regionHasPixels = true;
            HitCount++;
            return MainChanged || SecondaryChanged || TertiaryChanged;
        }
        finally
        {
            resource.Dispose();
            _dup.ReleaseFrame();
        }
    }

    /// <summary>强制下一轮重建裁剪区域（分辨率 / 显示器位置变了，或会话刚重建过）。</summary>
    public void ForceRegionRebuild()
    {
        _regionW = -1;
        _regionH = -1;
        _regionX = int.MinValue;
        _regionY = int.MinValue;
        _secondaryForce = 4;
        _tertiaryForce = 4;
        ResetFingerprints();
    }

    /// <summary>把一帧里的第二区抠到自己的纹理。失败只记一次，绝不抛到渲染路径上。</summary>
    private void CopySecondary(ID3D11Texture2D frame)
    {
        if (_r2W < 16 || _r2H < 16) return;
        if (!EnsureSecondary(_r2W, _r2H)) return;

        // 与主区同一套取整：胶囊贴着屏幕右边缘时会被裁掉一截，纹理留空即可，
        // 着色器那边是按"纹理 = 采样区"来算 uv 的，尺寸必须始终等于 _r2W/_r2H。
        int sx = Math.Clamp(_r2X, 0, Math.Max(0, _texW - 1));
        int sy = Math.Clamp(_r2Y, 0, Math.Max(0, _texH - 1));
        int ex2 = Math.Clamp(_r2X + _r2W, 0, _texW);
        int ey2 = Math.Clamp(_r2Y + _r2H, 0, _texH);
        int cw = Math.Max(0, ex2 - sx);
        int ch = Math.Max(0, ey2 - sy);
        if (cw <= 0 || ch <= 0) return;

        try
        {
            var box = new Vortice.Mathematics.Box(sx, sy, 0, sx + cw, sy + ch, 1);
            _ctx.Context.CopySubresourceRegion(_region2!, 0, 0, 0, 0, frame, 0, box);
            _region2HasPixels = true;
            _secondaryForce = 0;
            SecondaryCopies++;
        }
        catch (Exception ex)
        {
            _secondaryForce = 0;
            Status = "第二采样区拷贝失败: " + ex.Message;
        }
    }

    private bool EnsureSecondary(int w, int h)
    {
        if (_region2 is not null && _region2Srv is not null) return true;

        _region2Srv?.Dispose();
        _region2?.Dispose();
        _region2Srv = null;
        _region2 = null;

        try
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            _region2 = _ctx.Device.CreateTexture2D(desc);
            _region2Srv = _ctx.Device.CreateShaderResourceView(_region2, null);
            _region2HasPixels = false;
            return _region2Srv is not null;
        }
        catch (Exception ex)
        {
            _region2Srv?.Dispose();
            _region2?.Dispose();
            _region2Srv = null;
            _region2 = null;
            Status = "第二采样区建纹理失败: " + ex.Message;
            return false;
        }
    }

    private void DisposeSecondary()
    {
        _region2Srv?.Dispose();
        _region2?.Dispose();
        _region2Srv = null;
        _region2 = null;
        _region2HasPixels = false;
    }

    /// <summary>
    /// 指定第三采样区（屏幕物理像素）。传 0 尺寸表示"启动台关了，别再拷"。
    /// 和上面那个一样：坐标可以每帧变，只有尺寸变了才重建纹理。
    /// </summary>
    public void SetTertiaryRegion(int x, int y, int w, int h)
    {
        if (w < 16 || h < 16)
        {
            DisposeTertiary();
            _r3W = -1;
            _r3H = -1;
            _r3X = int.MinValue;
            _r3Y = int.MinValue;
            return;
        }

        if (_r3X == x && _r3Y == y && _r3W == w && _r3H == h) return;

        if (_r3W != w || _r3H != h) DisposeTertiary();

        _r3X = x;
        _r3Y = y;
        _r3W = w;
        _r3H = h;
        // 新位置的像素跟旧位置不可比，指纹必须作废 —— 否则启动台会一直挂着上一处的背景
        _tertiaryForce = 3;
        _fpTertiaryValid = false;

        if (!_loggedTertiaryRegion)
        {
            _loggedTertiaryRegion = true;
            App.Logger.Info($"[sampler] 第三采样区（启动台）{w}x{h} @({x},{y})");
        }
    }

    /// <summary>把一帧里的第三区抠到自己的纹理。失败只记状态，绝不抛到渲染路径上。</summary>
    private void CopyTertiary(ID3D11Texture2D frame)
    {
        if (_r3W < 16 || _r3H < 16) return;
        if (!EnsureTertiary(_r3W, _r3H)) return;

        int sx = Math.Clamp(_r3X, 0, Math.Max(0, _texW - 1));
        int sy = Math.Clamp(_r3Y, 0, Math.Max(0, _texH - 1));
        int ex = Math.Clamp(_r3X + _r3W, 0, _texW);
        int ey = Math.Clamp(_r3Y + _r3H, 0, _texH);
        int cw = Math.Max(0, ex - sx);
        int ch = Math.Max(0, ey - sy);
        if (cw <= 0 || ch <= 0) return;

        try
        {
            var box = new Vortice.Mathematics.Box(sx, sy, 0, sx + cw, sy + ch, 1);
            _ctx.Context.CopySubresourceRegion(_region3!, 0, 0, 0, 0, frame, 0, box);
            _region3HasPixels = true;
            _tertiaryForce = 0;
        }
        catch (Exception error)
        {
            _tertiaryForce = 0;
            Status = "第三采样区拷贝失败: " + error.Message;
        }
    }

    private bool EnsureTertiary(int w, int h)
    {
        if (_region3 is not null && _region3Srv is not null) return true;

        _region3Srv?.Dispose();
        _region3?.Dispose();
        _region3Srv = null;
        _region3 = null;

        try
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            };

            _region3 = _ctx.Device.CreateTexture2D(desc);
            _region3Srv = _ctx.Device.CreateShaderResourceView(_region3, null);
            _region3HasPixels = false;
            return _region3Srv is not null;
        }
        catch (Exception ex)
        {
            _region3Srv?.Dispose();
            _region3?.Dispose();
            _region3Srv = null;
            _region3 = null;
            Status = "第三采样区建纹理失败: " + ex.Message;
            return false;
        }
    }

    private void DisposeTertiary()
    {
        _region3Srv?.Dispose();
        _region3?.Dispose();
        _region3Srv = null;
        _region3 = null;
        _region3HasPixels = false;
    }

    /// <summary>
    /// 外部发现"桌面被整体动过"（explorer 重启、任务栏重建、主题切换）时调用：
    /// 重建裁剪区域 + 放行接下来几帧，把玻璃背景强制刷成真实桌面。
    /// Desktop Duplication 在这种情况下不一定报 AccessLost，光靠它自己不会重建。
    /// </summary>
    public void RequestHardRefresh()
    {
        ForceRegionRebuild();
        _forceFrames = 4;
    }

    /// <summary>
    /// 会话整个丢掉，并要求"下一轮立刻重建"（不走 1.5 秒退避）。
    /// 解锁 / 唤醒这类**确定性事件**必须用它：用户已经回到桌面了，
    /// 还要再等一个退避周期才恢复，看到的就是那块蓝灰色板子。
    /// </summary>
    public void Reset()
    {
        DisposeDuplication();
        HasFrame = false;
        ProtectedMasked = false;
        _probeBlack = false;
        _lastProbeAt = long.MinValue / 2;
        _lastInitAt = 0;
        ForceRegionRebuild();
    }
    private void DisposeDuplication()
    {
        _dup?.Dispose();
        _dup = null;
    }

    public void Dispose()
    {
        DisposeDuplication();
        _regionSrv?.Dispose();
        _region?.Dispose();
        _frozenSrv?.Dispose();
        _frozenTex?.Dispose();
        _frozenSrv = null;
        _frozenTex = null;
        _frozen2Srv?.Dispose();
        _frozen2Tex?.Dispose();
        _frozen2Srv = null;
        _frozen2Tex = null;
        DisposeSecondary();
        DisposeTertiary();
        _fp3Tex?.Dispose();
        _fp3Tex = null;
        _probeTex?.Dispose();
        _probeTex = null;
    }
}
