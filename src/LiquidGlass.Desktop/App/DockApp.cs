using System.Diagnostics;
using System.Runtime.InteropServices;
using LiquidGlass.Desktop.Capture;
using LiquidGlass.Desktop.Dock;
using LiquidGlass.Desktop.Graphics;
using LiquidGlass.Desktop.Platform;
using Vortice.Direct3D11;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// Dock 主窗口：Win32 宿主 + DComp 合成 + 玻璃渲染 + 交互循环。
/// 全部逻辑在 UI 线程，无跨进程边界（这是"跟手"的前提）。
/// </summary>
internal sealed class DockApp : IDisposable
{
    internal const string ClassName = "LiquidGlassDesktopDock";
    /// <summary>
    /// 空闲期轮询上限（Hz）。指针移入靠 WM_MOUSEMOVE 唤醒，不靠这个轮询，
    /// 所以它只决定"桌面背景变化多久被发现一次"和"离开后多久彻底安静"。
    /// 原来是 30Hz —— 一天 250 万次空转，纯烧电。
    /// </summary>
    private const int IdleCap = 4;

    /// <summary>
    /// 悬停预览的轮询上限（Hz）。只在"指针停在 Dock 上等 2 秒"和"卡片已经开着"
    /// 这两种状态下生效，其余时间仍旧走 IdleCap。没有它，2 秒的悬停延迟会被
    /// 250ms 的空闲节拍量化成 2.0~2.25 秒，而鼠标离开卡片后还要再挂 250ms 才消失。
    /// </summary>
    private const int PointerPollCap = 60;

    /// <summary>
    /// 空闲期背景采样间隔（秒）。现在只在"没有采样器"的情况下才会用到。
    ///
    /// 曾经设成 1 秒（为了省电），结果被用户抓到一个真实可感的 lag：
    /// 关掉一个全屏黑色软件后，玻璃还黑着不动，要等近一秒才变回来。
    /// 真正的解法不是把轮询调密（一次 `AcquireNextFrame` 要 10ms 左右，
    /// 拉满 10Hz 就是 10% 单核），而是让**阻塞等待**当节拍（见 IdleSampleBlockMs）。
    /// </summary>
    private const double IdleSampleInterval = 0.25;

    /// <summary>
    /// 空闲期 AcquireNextFrame 的阻塞上限（毫秒）。
    ///
    /// 这是"零延迟且不烧 CPU"的关键：桌面没变时它阻塞在内核里（不占一个 CPU 周期），
    /// 一变就立刻返回。14ms 略低于一个 60Hz 合成周期，保证变化能在下一拍就被看到。
    /// 指针压在 Dock 上时传 0 —— 那段主循环要按 60Hz 转去轮询光标，不能睡。
    /// </summary>
    private const int IdleSampleBlockMs = 14;

    /// <summary>
    /// 兜底背景（CPU 抓屏）的刷新间隔（毫秒）。
    ///
    /// 只在 DXGI 采集不可用时才会跑：一次 BitBlt 约 0.6ms（本机 1005x171），
    /// 2Hz 就是千分之一点二的单核占用，换"任何时候玻璃里都是真实壁纸"，
    /// 比一块静止的蓝灰色板子强太多。（这块板子会把用户吓到以为程序坏了。）
    /// </summary>
    private const int FallbackRefreshMs = 500;

    /// <summary>
    /// 窗口事件后进入"追帧档"的时长（秒）。
    ///
    /// 打开 / 关闭 / 最小化 / 切换前台时，Windows 都会通过 SetWinEventHook 通知我们
    /// （WindowTracker 一直在监听，过去只用来更新图标）。收到通知就立刻重采一帧背景，
    /// 并在接下来这段时间里把采样提到 12Hz、渲染提到 BoostCap ——
    /// 覆盖窗口淡出动画、explorer 重排等尾随变化。0.8 秒后自动回落，稳态一分不多花。
    /// </summary>
    private const double DesktopBoostSeconds = 0.8;

    /// <summary>追帧档的渲染上限（Hz）。和动画期一样只求"看得见跟上"，不求 60。</summary>
    private const int BoostCap = 30;

    /// <summary>
    /// 解冻之后多久内强制逐帧比像素（毫秒）。见 DesktopSampler.ForceCompareFor。
    ///
    /// 2 秒足够覆盖"启动台收起 → 新前台窗口画出第一帧"这段，再长就是白拷。
    /// </summary>
    private const int ForceCompareAfterThawMs = 2000;

    /// <summary>
    /// 启动台关闭后，胶囊在多长时间内不看指纹、逐帧重画（毫秒）。
    ///
    /// 实测：点 Dock 图标离开启动台，Dock 本体 6ms 就换好了，胶囊却在之后三秒里
    /// 一次重画都没有 —— 因为它只靠第二采样区的像素指纹来知道"背景变了"，
    /// 而那块指纹（312x208 里取中间 32 列的四条横带）会漏判。
    /// 1.2 秒足够覆盖"新前台窗口把底部背景刷掉"这段，胶囊只有 180x80，代价可忽略。
    /// </summary>
    private const int PillThawMs = 1200;

    private const int CS_HREDRAW = 0x0002;
    private const int CS_VREDRAW = 0x0001;

    private readonly DockConfig _config;
    private readonly int _runSeconds;
    private readonly string? _shotPath;

    /// <summary>截图时刻（秒）。默认 1.0；测动画/滚动时往后推。</summary>
    private readonly double _shotAt;
    /// <summary>
    /// --transmission 的临时覆盖值（&lt;0 = 用配置）。只影响这一次渲染，
    /// **不会写回 config.json** —— 之前直接改 config.Glass 试参数，
    /// 退出时被顺带保存，把用户调好的值覆盖掉了。
    /// </summary>
    private readonly float _transmissionOverride = -1f;
    /// <summary>本次是否派了守护进程（派了才需要在退出前写"正常退出"标记）。</summary>
    private bool _watchdogSpawned;
    private readonly bool _safeMode;
    private readonly float _forcedPointerX;
    private readonly bool _sweep;
    private readonly string? _dumpFramePath;
    private readonly int _effectiveCap;
    private readonly bool _hideWindow;
    private readonly int _clickIndex;
    private bool _clickSent;
    private double _sweepStart;
    private double _nextFrameAt;

    // ---- 任务栏接管 ----
    private bool _hideTaskbar;              // 当前是否由本进程托管任务栏
    private double _taskbarCheckAt;         // 下次守护检查时刻
    /// <summary>
    /// 任务栏守护间隔（秒）。
    ///
    /// 之前默认 250ms —— 每次 EnsureHidden 都要 EnumWindows 全量扫一遍，
    /// 几毫秒起步，乘上 4Hz 就是空闲期最大的 CPU 开销，也是"待机耗电"的常客之一。
    /// 它本来就是纯兜底（explorer 崩溃重建、主题切换才会丢样式），两秒内补回
    /// 根本看不出区别。紧急恢复路径另有 WM_APP 消息和样式变化钩子，不受这里影响。
    /// </summary>
    private readonly double TaskbarCheckInterval =
        double.TryParse(Environment.GetEnvironmentVariable("LGD_GUARD_MS"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double guardMs) && guardMs > 0
            ? guardMs / 1000.0
            : 2.0;

    private Native.WndProc? _wndProc;
    private IntPtr _hwnd;
    private bool _quit;

    private D3DContext? _ctx;
    private CompositionHost? _comp;
    private GlassRenderer? _renderer;
    private DesktopSampler? _sampler;
    /// <summary>采集会话不可用时的 CPU 抓屏兜底，保证玻璃永不退化成蓝/黑色块。</summary>
    private FallbackBackdrop? _fallback;
    private string _shaderSource = "";

    /// <summary>会话是否已锁定。锁屏期间 Duplication 必然失败，重试间隔放宽，别空转。</summary>
    private bool _sessionLocked;
    /// <summary>&gt; 0 = 到这个时刻重建采集会话（解锁/唤醒后等 DWM 把桌面合成出来）。</summary>
    private double _samplerRebuildAt;
    private double _desktopPassthroughUntil;

    /// <summary>
    /// 冻结帧（锁屏前最后一张真桌面）从解锁/唤醒那刻起最多再撑多久。
    /// 正常一两百毫秒就等到活帧了；超过这个时间说明采集真出了问题，
    /// 宁可退回 CPU 兜底，也不能让 Dock 一直挂着旧桌面。
    /// </summary>
    private const int FreezeTimeoutMs = 5000;

    /// <summary>RegisterSuspendResumeNotification 返回的句柄，退出时要还。</summary>
    private IntPtr _powerNotify;

    /// <summary>
    /// 当前背景来源：0 = DXGI 实时桌面，1 = CPU 抓屏兜底，2 = 无可用背景（中性灰）。
    /// 只在切换时记一条日志 —— 下次再有人问"为什么玻璃变蓝了"，翻日志一眼就能看到
    /// 那一刻到底是采集断了还是兜底也没抓到。
    /// </summary>
    private int _backdropMode = -1;

    /// <summary>当前是否在用 DXGI 实时背景（带迟滞，不跟着探测结果每帧翻）。</summary>
    private bool _backdropLive;

    /// <summary>状态翻转的确认时刻（&gt;0 表示"已经观察到新状态、正在等它稳住"）。</summary>
    private double _backdropFlipAt;

    // ---- 几何（全部物理像素）----
    private int _winX, _winY, _winW, _winH;
    /// <summary>上一次真的调过 SetWindowRgn 的上边界（整数像素），用来把重建次数压到最少。</summary>
    private int _regionTop = int.MinValue;
    private float _scale = 1f;
    /// <summary>首次量出来的 DPI 缩放，之后一直沿用，避免重排时尺寸跳变。</summary>
    private float _scaleLocked;
    private float _iconSize, _gap, _padX, _padY, _barH, _radius, _shadowExtent;

    /// <summary>玻璃左右两侧的实际外边距。左边要扣掉首个图标自己留的边，见 FirstArtInset。</summary>
    private float _padLeft, _padRight;
    private float _baseTotal, _originX;

    /// <summary>右端时钟占用的宽度（物理像素，0 = 关闭）。它只占玻璃内边距，不是槽位，不放大。</summary>
    private float _clockW;
    /// <summary>上一次画时钟时的分钟数。变了就强制重画一帧。</summary>
    private int _clockMinute = -1;
    /// <summary>玻璃参数只打一次日志，避免每帧刷屏。</summary>
    private bool _glassParamsLogged;

    /// <summary>"本帧无背景样本"的告警节流（最多每秒一条）。</summary>
    private long _noSrvLogAt;

    /// <summary>空闲态内容宽（所有图标按原尺寸排开的总宽）。</summary>
    private float _idleInnerW;

    /// <summary>激活态内容宽：空闲宽 + 两侧放大预留，指针扫到任何位置都不越界。</summary>
    private float _activeInnerW;

    /// <summary>
    /// 玻璃在最大放大态下的宽度。只用来定窗口尺寸和写日志，
    /// 实际玻璃宽度是运行期算的（见 <see cref="LayoutGlass"/>）。
    /// </summary>
    private float _maxGlassW;

    /// <summary>
    /// 玻璃右边缘的弹簧。左边缘是常量（见 <see cref="LayoutGlass"/>），只有右边缘会动。
    /// </summary>
    private Spring _panelRightSpring;

    private float _panelX, _panelW, _panelY, _panelH;
    private float _iconBaseline;   // 图标底边：放大时向上生长，而不是向两侧溢出
    private int _captureMargin;

    // ---- 溢流滚动 ----
    //
    // 图标多到整条排不下时怎么办？之前是"窗口按最大宽度算死"，于是几百个图标
    // 会把窗口顶得比屏幕还宽 —— 两头的图标直接落到屏幕外，而且窗口自己也被系统裁掉。
    //
    // 现在窗口宽度封顶在屏幕内，内容改成可横向滑动的长条：
    //   · 新应用照常追加到最右端（用户要的"默认添加到后边"）；
    //   · 指针压到 Dock 右端那一小块"热区"里，内容就持续向左推进（看起来向右滑）；
    //   · 滚轮也能滚，而且鼠标离开后停在那，不会自己弹回去。
    private bool _overflow;
    private float _scrollOffset;     // 当前滚动量（逻辑像素）
    private float _scrollTarget;     // 目标值，弹簧追它
    private float _scrollMax;        // 当前最大可滚量
    private float _viewportW;        // 玻璃可视宽度
    private float _sidePad;

    private long _scrollLogAt;

    /// <summary>推到最右端时的推进速度（逻辑像素/秒）。再快就看不清图标了。</summary>
    private const float OverflowScrollSpeed = 900f;

    private float[] _left = Array.Empty<float>();
    private float[] _scaleArr = Array.Empty<float>();
    private float[] _widths = Array.Empty<float>();
    /// <summary>槽位之间的间距（长度 = 槽位数 - 1）。分隔线两侧用半个间距，
    /// 这样它只是"占住一段正常的空隙"，不会把左右两组图标整体撑开。</summary>
    private float[] _gaps = Array.Empty<float>();

    private readonly List<IconQuad> _quads = new(48);
    private int _iconTextureSize = 128;

    // ---- 运行中应用（把任务栏搬进 Dock）----
    private readonly List<DockSlot> _slots = new(12);
    /// <summary>合成后的固定项（Windows 任务栏同步 + 用户手加），槽位构建的唯一输入。</summary>
    private readonly List<DockItemConfig> _pins = new(16);
    /// <summary>任务栏固定项目录的签名，用来发现"用户在 Windows 上又钉了一个"。</summary>
    private string _pinDirSignature = "";
    private double _pinRescanAt;
    private WindowTracker? _tracker;
    private ID3D11ShaderResourceView? _dotTex, _sepTex;
    /// <summary>指示点的实际像素尺寸。纹理按这个尺寸 1:1 生成，缩放会糊。</summary>
    private int _dotPx = 4;
    private string _slotSignature = "";
    private float _sepWidth, _dotSize, _dotGap;
    private int _monLeft, _monRight, _monBottom;

    // ---- 交互状态 ----
    private Spring _amp;
    private int _hoverIndex = -1;
    private float _pointerX = float.NaN;
    /// <summary>
    /// 悬停状态。用属性而不是裸字段，是为了在状态真正翻转时留一行日志——
    /// 悬停失效这类问题只能靠状态轨迹定位（渲染结果被采集排除，截图拍不到）。
    /// 日志只在本机调试开关打开时输出，正常运行不写盘。
    /// </summary>
    private bool PointerInside
    {
        get => _pointerInsideField;
        set
        {
            if (_pointerInsideField == value) return;
            _pointerInsideField = value;
            if (_pointerLog)
                Logger.Info($"[pointer] inside={value} ({_pointerReason})");
            // 悬停态会改变窗口形状（见 UpdateWindowRegion），必须跟着刷新
            UpdateWindowRegion();
        }
    }

    private bool _pointerInsideField;
    private string _pointerReason = "";
    private static readonly bool _pointerLog =
        Environment.GetEnvironmentVariable("LGD_DEBUG_POINTER") == "1";
    private bool _dirty = true;
    /// <summary>上一次记过日志的采集状态。只在状态真的变了时写，否则 4Hz 会把日志刷爆。</summary>
    private string _samplerStatus = "";
    /// <summary>系统正在睡眠 / 待机。为真时主循环只做无超时的消息等待，一帧都不渲染。</summary>
    private bool _suspended;
    /// <summary>窗口是否已经露面。首帧画出来之前一直是 false，避免露出未初始化的黑块。</summary>
    private bool _windowShown;

    private readonly FrameStats _stats = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;
    private double _startedAt;
    private bool _shotTaken;
    private bool _statsWarmedUp;
    private double _sampleMs;
    private long _sampleCalls;
    private bool _lastAnimating;

    /// <summary>上一帧的指针位置，用来判断"指针还在动" —— 它决定滑动期给不给满帧。</summary>
    private float _lastPointerX = float.NaN;

    // 诊断：LGD_FPS_LOG=1 时每 2 秒打一行实际帧率。排查"滑动是否真的掉帧"时开，
    // 平时不产生任何日志与开销（一次时间比较）。
    private static readonly bool _fpsLog =
        Environment.GetEnvironmentVariable("LGD_FPS_LOG") == "1";

    // 诊断：LGD_LAG_LOG=1 时，每个窗口事件打印"事件到达→拿到新帧"的耗时。
    // 这是"玻璃跟不跟得上系统变化"的唯一硬指标，排查颜色滞后时开。
    private static readonly bool _lagLog =
        Environment.GetEnvironmentVariable("LGD_LAG_LOG") == "1";
    private double _boostAt;
    private double _fpsWinStart, _fpsPrevAt = -1, _fpsGapSum, _fpsGapMax;
    private double _fpsSampleSum, _fpsSampleMax;
    private double _fpsTickSum, _fpsTickMax;
    private double _fpsPollSum, _fpsPollMax;
    private double _fpsWaitSum, _fpsWaitMax;
    private int _fpsFrames, _fpsSlow;
    private int _fpsGc0, _fpsGc1, _fpsGc2;

    // 定位丢帧用：上一轮各阶段的实测耗时，出现异常间隔时一并打出来。
    private double _lastTickCost, _lastFlushMs, _lastSampleMs, _lastMsgMs;

    // 高精度等待用的定时器句柄，以及 MsgWait 需要的句柄数组（一个元素）。
    private IntPtr _waitTimer = IntPtr.Zero;
    private IntPtr _waitHandles = IntPtr.Zero;

    // 帧延迟等待对象单独一组句柄。不能复用 _waitHandles ——
    // 那个槽位里放的是 _waitTimer，空闲期的 WaitUntil 还要用它。
    private IntPtr _latencyHandles = IntPtr.Zero;

    // 本轮的起始时刻（秒）。
    private double _frameStartAt;

    // 慢帧现场（只在 LGD_FPS_LOG 下采集，且只记超预算的帧）。
    private readonly List<string> _slowFrames = new(40);

    // 位移诊断（LGD_MOTION_LOG=路径）：每渲染帧记一行 t,pointerX,left,scale。
    // 判"抖"不能只看帧率 —— 帧间隔很均匀但图标位置出现反向位移，看着一样是抖。
    // 攒在内存里、退出时一次写盘，避免日志 I/O 本身成为病因。
    private static readonly string? _motionPath =
        Environment.GetEnvironmentVariable("LGD_MOTION_LOG");
    private readonly List<float> _motion = new(65536);
    private int _motionSlot = 3;

    // 动画期桌面采样的限频：约 12Hz。采样本身要等一个合成周期（实测 10ms），
    // 每帧都采就跟垂直同步互相等着，帧长必然翻倍。
    private const double AnimSampleInterval = 1.0 / 12.0;
    private double _lastAnimSampleAt;

    /// <summary>空闲期最近一次背景采样的时刻（秒）。</summary>
    private double _lastIdleSampleAt;
    /// <summary>追帧档的截止时刻（秒）。之前表示"窗口刚动过，玻璃要跟紧"。</summary>
    private double _desktopBoostUntil;

    /// <summary>
    /// 追帧日志的额外窗口（秒）。从启动台点进别的任务后，3 秒内无论有没有
    /// 开 LGD_LAG_LOG 都记"事件→新帧"的耗时 —— 这条路径上"背景停在原页面"
    /// 只能靠日志定位，让用户复现一次比猜十次强。
    /// </summary>
    private double _lagDiagUntil;

    /// <summary>上一条"没拿到新帧"日志的时刻，把 60Hz 的失败也压成 10Hz 记录。</summary>
    private double _lagMissLogAt;

    /// <summary>上一次看到的窗口事件累计数。它一变就说明桌面构图换了。</summary>
    private long _lastTrackerEvents;

    /// <summary>窗口事件要求"立刻回到主循环重画"，用于打断空闲期的定长等待。</summary>
    private bool _wakeRequested;

    /// <summary>上一次因窗口事件唤醒的时刻（秒），用于合并事件簇拥。</summary>
    private double _lastWakeAt;

    /// <summary>"暂时让开"状态：整条 Dock 下滑出屏幕，到点自己滑回来。</summary>
    private bool _ducked;
    private double _duckUntil;

    /// <summary>
    /// 需要重新把 Dock/胶囊提到 TOPMOST 最前的时刻（秒）。
    ///
    /// 用户报过：点开启动台、再点 Dock 图标切到别的任务之后，Dock 沉到壁纸后面、
    /// 图标全部看不见，点击直接穿透到桌面。启动台是全屏 TOPMOST 且会被激活
    /// （搜索框要键盘），它一收起来，同属 TOPMOST 的 Dock 就被排到后面去了。
    /// 打开时我们提过一次（BringOverlaysToTop），关闭路径却没人提 —— 这里补上，
    /// 而且要多等一拍再提：DWM 处理完窗口隐藏之后才重排层级，同一帧里提会被覆盖。
    /// </summary>
    private double _topmostReassertAt;

    /// <summary>相邻两帧的间隔（毫秒）。流畅度不看"单帧画得多快"，看这个的抖动。</summary>
    private readonly List<double> _frameGaps = new(4096);
    private double _lastFrameAt = -1;
    private double _presentTotal;
    private long _presentCalls;

    public DockApp(DockConfig config, int runSeconds, string? shotPath, bool safeMode,
        float forcedPointerX = float.NaN, bool sweep = false, string? dumpFramePath = null,
        int frameCap = 0, bool hideWindow = false, int clickIndex = -1, bool hideTaskbar = false,
        bool openSettings = false, bool openLaunchpad = false, float transmissionOverride = -1f,
        double shotAt = 1.0)
    {
        _config = config;
        _runSeconds = runSeconds;
        _shotPath = shotPath;
        _shotAt = shotAt < 0.2 ? 0.2 : shotAt;
        _safeMode = safeMode;
        _forcedPointerX = forcedPointerX;
        _sweep = sweep;
        _dumpFramePath = dumpFramePath;
        // 0 = 默认 60Hz 上限；负数 = 不设限（仅供压力测试）
        _effectiveCap = frameCap < 0 ? 0 : (frameCap == 0 ? 60 : frameCap);
        _hideWindow = hideWindow;
        _clickIndex = clickIndex;
        _hideTaskbar = hideTaskbar;
        _openSettings = openSettings;
        _openLaunchpad = openLaunchpad;
        _transmissionOverride = transmissionOverride;
    }

    /// <summary>设置面板与启动台：都是懒创建，没打开过就不占任何资源。</summary>
    private SettingsPanel? _settings;
    private LaunchpadWindow? _launchpad;
    private readonly bool _openSettings;
    private readonly bool _openLaunchpad;
    private bool _devPanelsOpened;

    // ---- 悬停预览 ----
    /// <summary>悬停预览卡片。懒创建：从没悬停过就一个窗口都不建。</summary>
    private WindowPreviewCard? _preview;

    /// <summary>卡片正在展示哪个槽位（-1 = 没展示）。</summary>
    private int _previewSlot = -1;

    /// <summary>当前指针停在哪个槽位上，以及从什么时候开始停的。</summary>
    private int _hoverSlot = -1;
    private double _hoverSlotSince;

    /// <summary>缩略图下次刷新时刻。窗口内容会变，卡片开着时每秒重抓一帧。</summary>
    private double _previewRefreshAt;

    // ---- 系统状态胶囊（把托盘搬回来）----
    /// <summary>
    /// Dock 右端的电量/网络/时间胶囊。懒创建，第一次摆位时才建窗口。
    ///
    /// 存在的理由：整条任务栏被设成全透明后，托盘区（时钟、网络、音量、电量、
    /// 快捷设置）跟着一起消失了 —— 它们是 Shell_TrayWnd 的子窗口，没法单独救回来。
    /// 这个胶囊把最要紧的三样接回来；点一下还能开原生快速设置。
    /// </summary>
    private StatusPill? _statusPill;

    // ---- 显示 / 隐藏 Dock 的快捷键 ----
    /// <summary>低级鼠标钩子句柄（IntPtr.Zero = 没装）。</summary>
    private IntPtr _mouseHook;
    /// <summary>钩子委托必须由托管对象持有，否则 GC 回收后系统回调就崩了。</summary>
    private Native.LowLevelMouseProc? _mouseHookProc;
    /// <summary>
    /// 低级键盘钩子。设置面板录制快捷键时，键盘消息只发给前台窗口 ——
    /// 面板没抢到焦点就一个键都收不到。钩子拿的是系统级按键，跟焦点无关。
    /// </summary>
    private IntPtr _keyboardHook;
    private Native.LowLevelKeyboardProc? _keyboardHookProc;
    /// <summary>钩子里自己维护的修饰键状态（LL 钩子触发时 GetAsyncKeyState 还没更新）。</summary>
    private bool _capCtrl, _capAlt, _capShift, _capWin;
    /// <summary>
    /// 当前键盘快捷键在钩子侧的匹配条件（0 = 当前不是键盘组合）。
    /// 键盘组合不再用 RegisterHotKey：那会全局吞键，Ctrl+C 一按连"复制"都收不到。
    /// 改成钩子观察 + 指针必须在 Dock 上，才动手。
    /// </summary>
    private bool _kbShortcutArmed;
    private uint _kbShortcutVk;
    private int _kbShortcutMods;   // 1=Ctrl 2=Alt 4=Shift 8=Win
    private bool _kbShortcutFired; // 按住不放时只触发一次
    /// <summary>Dock 是不是被快捷键藏起来了（决定下一次快捷键是显示还是隐藏）。</summary>
    private bool _dockHiddenByShortcut;
    /// <summary>「双击 / 三击 Dock」：连续点击计数与上一次点击时间。</summary>
    private int _dockClicks;
    private long _lastDockClickMs;

    public int Run()
    {
        // 1ms 定时器精度。没有它，Thread.Sleep(1) 实际睡 15.6ms，
        // 60Hz 排帧被量化成 15.6 / 31.2 两档交替 —— 这就是"一块一块地跳"的来源。
        // 只在本进程存活期间生效，退出时一定还回去（否则会拖累整机功耗）。
        Native.TimeBeginPeriod(1);

        // 渲染线程抬到 AboveNormal：动画要卡在 16.7ms 的预算里，
        // 一次被后台任务抢占就会掉一帧。不用 Highest —— 那会跟输入处理抢。
        try { Thread.CurrentThread.Priority = ThreadPriority.AboveNormal; }
        catch { /* 权限不足就算了，不影响功能 */ }

        try
        {
            // 任务栏的隐藏**不在这里做**，推迟到 Dock 画出第一帧之后。
            //
            // 原来的顺序是：先隐形任务栏 → 建窗口 → 立刻 ShowWindow → 才初始化 D3D。
            // 中间那几百毫秒里，屏幕上是一条已经消失的任务栏 + 一块 1030x171 的
            // 未初始化黑矩形（DComp 还没有内容，窗口默认就是黑的）。
            // 用户开机看到的那一下"短暂黑屏"就是这么来的 —— 不是显卡问题，是我们自己的窗口。
            //
            // 现在的顺序：建窗口（不显示）→ 初始化 → 画首帧 → 显示 → 隐形任务栏。
            // 任务栏消失和 Dock 出现之间只隔一帧，观感是直接换上去的。
            if (_hideTaskbar) _taskbarCheckAt = _clock.Elapsed.TotalSeconds + TaskbarCheckInterval;

            // 长驻实例才需要守护：--shot / --seconds 这类一次性的诊断运行
            // 自己会正常退出，给它派守护只会多出一个没人等的进程。
            if (_shotPath is null && _runSeconds <= 0)
            {
                DockWatchdog.Spawn();
                _watchdogSpawned = true;
            }

            // 先有槽位再有窗口：Dock 的宽度取决于"固定项 + 运行区"的个数，
            // 顺序反了会先按旧尺寸建窗口、再立刻改一次，白闪一下。
            _tracker = new WindowTracker();
            _tracker.DesktopChanged = OnDesktopChanged;
            _tracker.Sync();
            SyncPins();
            RebuildSlots();

            CreateWindow();
            // 窗口形状必须在创建后立刻定型：RelayoutAll 只在显示器变化时才跑，
            // 不在这里设的话 region 从未生效，玻璃上半部照样整个拦点击。
            UpdateWindowRegion();
            InitGraphics();
            ApplyToggleShortcut();
            if (_hideWindow)
            {
                Native.ShowWindow(_hwnd, 0 /* SW_HIDE */);
                Logger.Info("诊断：窗口已隐藏，仅保留渲染与采样");
            }

            _sweepStart = _clock.Elapsed.TotalSeconds;

            // 高精度等待定时器：Win10 1803+ 才有，拿不到就退回毫秒级等待。
            _waitTimer = Native.CreateWaitableTimerExW(IntPtr.Zero, null,
                Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Native.TIMER_ALL_ACCESS);
            if (_waitTimer != IntPtr.Zero)
            {
                _waitHandles = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_waitHandles, _waitTimer);
            }
            else
            {
                Logger.Warn("高精度定时器不可用，帧节拍退回毫秒级等待（约 1ms 抖动）");
            }

            // 帧延迟等待对象的句柄数组（也只有一个元素）。只做诊断：
            // 这台机器上它不阻塞，不能当节拍器用，所以记录一下有没有拿到就够了。
            _latencyHandles = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr latency = _comp?.FrameLatencyHandle ?? IntPtr.Zero;
            Marshal.WriteIntPtr(_latencyHandles, latency);
            Logger.Info("帧节拍: Present(0) + DwmFlush 对齐合成周期" +
                        (latency != IntPtr.Zero
                            ? "；帧延迟等待对象已获取（仅诊断，不用于限流）"
                            : "；无帧延迟等待对象"));
            Logger.Info($"Dock 启动: {_winW}x{_winH} @({_winX},{_winY}) scale={_scale:F2} adapter={_ctx!.AdapterName} {AppVersion}");
        Logger.Info($"布局: 槽位={_slots.Count} 空闲宽={_idleInnerW:F0} 放大宽={_activeInnerW:F0} " +
                    $"放大预留={(_activeInnerW - _idleInnerW) * 0.5f:F0}/边 图标={_iconSize:F0} 间距={_gap:F0} " +
                    $"原点X={_originX:F0} 基线Y={_iconBaseline:F0}");
        Logger.Info($"玻璃: 窗口=({_winX},{_winY}) {_winW}x{_winH} " +
                    $"空闲矩形=({_panelX:F0},{_panelY:F0}) {_panelW:F0}x{_panelH:F0} " +
                    $"屏幕X[{_winX + _panelX:F0}..{_winX + _panelX + _panelW:F0}] " +
                    $"最大右端={_winX + _panelX + _maxGlassW:F0} " +
                    $"Y[{_winY + _panelY:F0}..{_winY + _panelY + _panelH:F0}]");

            // 开局先备一份背景再进主循环。
            //
            // 第一帧是在循环里画完才 RevealWindow 的，而采样排在渲染之后 ——
            // 不预热的话，开机露面的那一瞬间玻璃里没有背景可采，只能画降级色块，
            // 用户看到的就是"开机闪一下灰蓝"。这里抢在循环前采一次：
            // DXGI 通就灌真实桌面，不通就直接灌 CPU 兜底图。
            SampleBackground(0);

            double endAt = _runSeconds > 0 ? _runSeconds : double.MaxValue;
            while (!_quit)
            {
                _frameStartAt = _clock.Elapsed.TotalSeconds;

                double tMsg = _clock.Elapsed.TotalSeconds;
                PumpMessages(3.0);
                _lastMsgMs = (_clock.Elapsed.TotalSeconds - tMsg) * 1000.0;

                if (_quit) break;

                if (!_sessionLocked && _samplerRebuildAt > 0 &&
                    _clock.Elapsed.TotalSeconds >= _samplerRebuildAt)
                {
                    _samplerRebuildAt = 0;
                    if (_sampler is { Available: false }) _sampler.TryInit();
                    _dirty = true;
                    Logger.Info("解锁采集重建: " + (_sampler?.Status ?? "无采样"));
                }

                // 睡眠中：一帧都不画，也不设任何定时器。
                //
                // 这是「合盖一晚掉很多电」的正解。现代待机下进程不会被挂起，
                // 空闲档再低也是 4Hz：每秒 4 次 Present + 1 次桌面采样，
                // 每次都把 GPU 从低功耗里拽出来一次，一晚上两万多次。
                // 挂起通知一到就彻底撒手，睡眠期间这个进程的活动次数是 0。
                //
                // 60 秒的兜底不是为了省电，是为了防「恢复通知丢了」把循环永久卡死 ——
                // 一晚 480 次空转，总耗时不到 0.1 秒，可以忽略。
                if (_suspended)
                {
                    Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 60_000, Native.QS_ALLINPUT, 0);
                    continue;
                }

                bool rendered = Tick();
                FpsTick(rendered);

                // 首帧画完才让窗口露面。1.5 秒的兜底是防"渲染因为某种原因一直失败"
                // 把 Dock 变成永远看不见的幽灵进程 —— 宁可露出黑块，也不能不出。
                if (!_windowShown && (rendered || _clock.Elapsed.TotalSeconds > 1.5))
                    RevealWindow();

                // 调试入口：--settings / --launchpad 用来自查界面，正常启动不会走这里
                if (_windowShown && !_devPanelsOpened && (_openSettings || _openLaunchpad))
                {
                    _devPanelsOpened = true;
                    if (_openSettings) OpenSettings();
                    if (_openLaunchpad) OpenLaunchpad();
                }

                if (_shotPath is not null && !_shotTaken && _clock.Elapsed.TotalSeconds > _shotAt)
                {
                    _shotTaken = true;
                    CaptureShot(_shotPath);
                    break;
                }

                // 2 秒后进入稳态，性能统计从这里开始算 CPU
                if (!_statsWarmedUp && _clock.Elapsed.TotalSeconds > 2.0)
                {
                    _statsWarmedUp = true;
                    _stats.MarkWarmup(_clock.Elapsed.TotalSeconds);
                }

                if (_clock.Elapsed.TotalSeconds >= endAt)
                {
                    Logger.Info("到达运行时长，正常退出");
                    break;
                }

                // 守护任务栏要不要现在做，看的是"上一帧还在不在动画中"。
                // EnsureHidden 每次都要 EnumWindows 全量扫一遍找任务栏，几毫秒起步 ——
                // 它 250ms 跑一次，正好等于每 2 秒 8 次，插进 16.7ms 的帧里就是 8 次可见的顿。
                // 这是纯兜底（explorer 重建才需要），推迟到指针停下来再做完全没影响。
                // 窗口一动，玻璃就得跟着动。
                //
                // WindowTracker 里那套 SetWinEventHook 本来就在监听窗口的
                // CREATE / DESTROY / SHOW / HIDE / 前台切换，但过去只用来更新图标列表。
                // 而玻璃的颜色完全来自桌面抓屏，抓屏又只在空闲档 4Hz 的节奏上跑 ——
                // 于是"打开一个黑色软件"和"关掉它"这种突变，玻璃要等下一拍才知道，
                // 用户看到的就是「页面都变了，Dock 还没变」。
                //
                // 事件一到就立刻进追帧档：马上重采一帧（几十毫秒内玻璃就变色），
                // 之后 0.8 秒里保持 12Hz 采样 + 30Hz 渲染，把淡出动画那些尾巴也吃掉。
                // 静止时事件计数不动，一分钱不多花。
                if (_tracker is not null)
                {
                    long events = _tracker.EventCount;
                    if (events != _lastTrackerEvents)
                    {
                        _lastTrackerEvents = events;
                        _dirty = true;
                        _lastIdleSampleAt = 0;   // 允许这一轮立刻采样，不等节流
                        _desktopBoostUntil = _clock.Elapsed.TotalSeconds + DesktopBoostSeconds;
                    }
                }

                // "暂时让开"到点了就回来。空闲期 4Hz 检查一次，最多晚 250ms，无感。
                if (_ducked && _clock.Elapsed.TotalSeconds >= _duckUntil)
                    Undock();

                // 启动台收起之后把 Dock/胶囊重新提回 TOPMOST 最前（见 _topmostReassertAt）。
                if (_topmostReassertAt > 0 && _clock.Elapsed.TotalSeconds >= _topmostReassertAt)
                {
                    _topmostReassertAt = 0;
                    BringOverlaysToTop();
                    // 提完还要一帧：窗口被重新排到前面以后，它自己那块内容没变，
                    // DWM 不会自动重画，得让我们把同一帧再提交一次，否则会看到旧底图。
                    _dirty = true;
                }

                if (!_lastAnimating) GuardTaskbar();

                // 动画期与空闲期彻底分开处理。
                //
                // 之前这里有个隐蔽的时序 bug：开头的"空闲采样"读的是**上一轮**的
                // _lastAnimating。指针刚移进来的那几轮，上一轮还是"没动画"，
                // 于是先阻塞采样 12ms，接着 Tick 里判定为动画、又去等一个合成周期 ——
                // 12 + 12 ≈ 24ms，超过一拍，直接丢帧。现场日志抓到的正是
                // `[gap] 33.0ms tick=0.6 flush=12.0`：两个阶段各自都正常，
                // 加起来却没人管总数。现在动画路径上只有"采样(不阻塞) + 等合成"。
                if (_lastAnimating)
                {
                    // 动画期的采样必须限频，理由是一次实测出来的事故：
                    //
                    // AcquireNextFrame 在这台机器上平均要 10ms、最长 16.7ms ——
                    // 也就是说它自己就在等下一个合成周期。每帧都采，它就和节拍器互相等，
                    // 帧长必然翻倍。玻璃折射用的桌面背景根本不需要 60Hz 更新：
                    // 指针在 Dock 上滑动时，背后桌面的变化是静止的。
                    // 降到 12Hz，画面完全看不出差别，却把 60 帧里 55 帧的预算还给渲染。
                    double nowS = _clock.Elapsed.TotalSeconds;
                    if (nowS - _lastAnimSampleAt >= AnimSampleInterval)
                    {
                        _lastAnimSampleAt = nowS;
                        SampleBackground(0);
                    }

                    // 节拍 = 等 DWM 完成这一轮合成。
                    //
                    // 顺序是：Tick 里 Present(0) 把帧交给 DWM（不阻塞）→ 采样（限频）→
                    // DwmFlush 阻塞到"本帧被合成"的那一刻。返回时正好是下一个合成周期的起点，
                    // 于是下一次 Present 一定落进下一轮合成，一帧对一个刷新周期。
                    //
                    // 这是唯一一条在这台机器上真能把相位钉死的路：
                    //   · Present(1) 实测不锁相（合成 swapchain 上会提前返回，
                    //     表现为 16.4 / 33 两档乱跳，每 2 秒二十多次慢帧）；
                    //   · 帧延迟等待对象压根不阻塞（跑出 2300fps）；
                    //   · 纯时间轴平均很漂亮，但相对合成周期的相位会漂，漂出门就是一次 33ms。
                    // DwmFlush 直接问系统"这一轮合成什么时候结束"，没有相位漂移可言。
                    double nowFlush = _clock.Elapsed.TotalSeconds;
                    Native.DwmFlush();

                    // 诊断：单独记录等合成的耗时。它应该稳定在"到下一个合成边界的剩余时间"，
                    // 一旦接近 33ms，就说明那一帧整整多等了一轮。
                    if (_fpsLog)
                    {
                        double flushMs = (_clock.Elapsed.TotalSeconds - nowFlush) * 1000.0;
                        _fpsWaitSum += flushMs;
                        if (flushMs > _fpsWaitMax) _fpsWaitMax = flushMs;
                    }
                }
                else
                {
                        // 空闲期也要「一变就刷」。
                        //
                        // 旧做法是每轮固定间隔采一次（空闲 250ms、追帧档 12Hz），
                        // 而且整轮还被压到 4Hz —— 于是切换窗口、换壁纸、播视频的时候，
                        // 玻璃最多要等 250ms 才换色。这就是"透明状态有延迟"的来源，
                        // 不是渲染慢，是**根本还没去读桌面**。
                        //
                        // 现在改用 AcquireNextFrame 的阻塞等待当节拍：静止时它睡在核心里
                        // （不占 CPU），桌面一变立刻返回，采样 → 指纹比对 → 重绘一气呵成。
                        // 指针压在 Dock 上时不能阻塞（那段要按 60Hz 轮询光标），传 0。
                        bool needsPointer = NeedsPointerPolling();
                        bool boosting = _clock.Elapsed.TotalSeconds < _desktopBoostUntil;

                        bool blockedOnSampler = false;
                        if (_sampler is not null)
                        {
                            int blockMs = needsPointer ? 0 : IdleSampleBlockMs;
                            _lastIdleSampleAt = _clock.Elapsed.TotalSeconds;

                            if (SampleBackground(blockMs)) _dirty = true;

                            // 胶囊那块像素也变了 → 立刻让它重画，不许等它自己那几秒兜底。
                            // 用 SecondaryChanged 而不是 got：主区和第二区是两个独立的判据。
                            //
                            // 但只用 SecondaryChanged 会漏：第二采样区只有 312x208，
                            // 指纹取的是其中四条横带，切换页面时这块有可能整片"判成没变"
                            // （实测屏幕上已经全白，判据仍是 false），胶囊于是继续画上一页。
                            // 主区判据是同一帧同一套比对，只要它说变了，胶囊也该重画 ——
                            // 反正胶囊只有 180x80，多画一帧的代价远小于"背景卡在上一页"。
                            if (_sampler.SecondaryChanged || _sampler.MainChanged) _statusPill?.NotifyBackgroundChanged();

                            // 阻塞等待本身就充当了节拍，不必再 WaitUntil 睡一次
                            blockedOnSampler = blockMs > 0 && !_sessionLocked &&
                                _samplerRebuildAt <= 0 && _sampler.Available;
                        }

                        // 空闲期降档到 IdleCap，避免桌面每次重合成都被当成"新内容"。
                        // 追帧档用 BoostCap：窗口动画尾巴还在屏幕上，画太慢会看出跳格。
                        int cap = Math.Min(boosting ? BoostCap : IdleCap, _effectiveCap);

                        // 悬停预览在等时机 / 已经开着：这段必须按满帧率轮询光标。
                        // 空闲档是 4Hz —— 也就是每 250ms 才看一次鼠标。用户的手抬高 3 厘米
                        // 离开卡片，卡片还挂在那儿四分之一秒，看起来就是"迟钝、甩不掉"；
                        // 反过来从指针停稳到卡片弹出也会白等最多 250ms，2 秒被拖成 2.2 秒。
                        // 这里只是让主循环转得快，不重绘（Tick 里没动画没脏标记就早退），
                        // 单轮成本就是一次 GetCursorPos。
                        if (needsPointer) cap = Math.Max(cap, PointerPollCap);
                        if (cap > 0 && !blockedOnSampler)
                        {
                            double interval = 1.0 / cap;

                            // 先对齐再推进。原来的写法是
                            //   _nextFrameAt = Math.Max(_nextFrameAt + interval, now)
                            // 一旦 _nextFrameAt 落后于 now（动画期根本不走这条时间轴，
                            // _nextFrameAt 会一直停在很久以前），
                            // Max 会把它夹成 now，于是 waitMs=0、完全不等 ——
                            // 空闲期变成每 40 微秒空转一轮，实测刷到 24000 轮/秒。
                            double nowIdle = _clock.Elapsed.TotalSeconds;
                            if (_nextFrameAt <= 0) _nextFrameAt = nowIdle;
                            _nextFrameAt += interval;

                            // 落后一整个周期才重新起步。之前写成"先把目标夹到 now、再推进"，
                            // 等于每轮都以"现在"为基准 —— 于是每帧的处理耗时（约 1.7ms）
                            // 被永久累加进周期里，30fps 实测跑成 35ms（28.6fps）。
                            // 目标时间轴必须自己往前累加，才不会被单帧开销拖慢。
                            if (_nextFrameAt < nowIdle) _nextFrameAt = nowIdle + interval;

                            // 空闲期用 precise:false —— 这里不需要亚毫秒节拍，
                            // 而自旋收尾是实测出来的耗电主因（见 WaitUntil 里的注释）。
                            WaitUntil(_nextFrameAt, precise: false);
                        }
                }

                // 慢帧现场：只在真的超预算时记一条分解，正常情况下零开销。
                // 定位"鼠标一压在 Dock 上就偶发 33ms"这种问题，平均数字帮不上忙，
                // 必须知道那一帧的预算被哪一段吃掉了。
                if (_fpsLog && _lastAnimating)
                {
                    double totalMs = (_clock.Elapsed.TotalSeconds - _frameStartAt) * 1000.0;
                    if (totalMs > 21.0 && _slowFrames.Count < 40)
                        _slowFrames.Add($"{totalMs:F1}ms msg={_lastMsgMs:F1} " +
                                        $"tick={_lastTickCost:F1} sample={_lastSampleMs:F1}");
                }
            }

            DumpPerf();
            if (_slowFrames.Count > 0)
            {
                Logger.Info($"慢帧现场 {_slowFrames.Count} 例（总长 msg=消息 tick=绘制 sample=采样）:");
                foreach (string line in _slowFrames) Logger.Info("  " + line);
            }
            WriteMotionLog();
            SaveConfig();
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("致命错误: " + ex);
            return 1;
        }
        finally
        {
            if (_waitHandles != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_waitHandles);
                _waitHandles = IntPtr.Zero;
            }

            if (_latencyHandles != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_latencyHandles);
                _latencyHandles = IntPtr.Zero;
            }

            if (_waitTimer != IntPtr.Zero)
            {
                Native.CloseHandle(_waitTimer);
                _waitTimer = IntPtr.Zero;
            }
            Native.TimeEndPeriod(1);

            // 先挂上"我是正常退出"的牌子，再还任务栏。
            // 顺序反过来的话，守护看到进程没了、又还没看到牌子，会误判成崩溃：
            // 结果是刚恢复的任务栏被又一次重新隐藏 + Dock 被无谓地重启一遍。
            if (_watchdogSpawned) DockWatchdog.MarkCleanExit();

            // 无论正常退出、到达时长、还是抛异常，只要托管过任务栏就必须还回去。
            // 这是"不把用户的任务栏弄丢"的底线。
            if (_hideTaskbar)
            {
                int shown = TaskbarGuard.Show();
                _hideTaskbar = false;

                if (TaskbarGuard.IsStealthed())
                    Logger.Error($"任务栏恢复不完整（还原来 {shown} 个窗口），恢复标记已保留，下次启动会继续尝试");
                else
                    Logger.Info($"任务栏已恢复: {shown} 个窗口");
            }
        }
    }

    /// <summary>
    /// 低频守护：explorer 重启、显示设置变化、主题切换都会重建任务栏窗口或重置它的样式，
    /// 新建出来的窗口不认识我们，需要重新隐形一遍。
    /// 250ms 一次：一次 EnumWindows + 若干次 GetWindowLongPtr 是微秒级开销，
    /// 比一个重绘帧便宜好几个数量级，所以宁愿勤一点。
    /// </summary>
    private void GuardTaskbar()
    {
        if (!_hideTaskbar) return;

        double now = _clock.Elapsed.TotalSeconds;
        if (now < _taskbarCheckAt) return;
        _taskbarCheckAt = now + TaskbarCheckInterval;

        int again = TaskbarGuard.EnsureHidden();
        if (again > 0)
        {
            Logger.Warn($"任务栏样式被重置（explorer 重启 / 主题切换），已重新隐形 {again} 个窗口");
            // explorer 重启的瞬间，桌面正在重绘，这时候采下来的一帧很可能是半成品；
            // 而 Duplication 在桌面随后静止时不会再给新帧，玻璃就会永久停在那一帧
            // （用户看到的是「Dock 变成一条彩色大色块」）。这里主动要求硬刷新。
            _sampler?.RequestHardRefresh();
            _dirty = true;
        }
    }

    // ---------------------------------------------------------------- 窗口

    private void CreateWindow()
    {
        _wndProc = WndProc;
        IntPtr instance = Native.GetModuleHandle(null);

        var wc = new Native.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = ClassName,
        };
        Native.RegisterClassEx(ref wc);

        MeasureForMonitor(IntPtr.Zero);

        int exStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE |
                      Native.WS_EX_NOREDIRECTIONBITMAP;

        _hwnd = Native.CreateWindowEx(exStyle, ClassName, "Liquid Glass Desktop", Native.WS_POPUP,
            _winX, _winY, _winW, _winH, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx 失败");

        // 关键：把自己从所有截屏/采集结果里摘掉，否则自采样会形成反馈回路
        bool affinity = Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        Logger.Info("SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) = " + affinity +
                    (affinity ? "" : $" err={Marshal.GetLastWin32Error()}"));

        int round = Native.DWMWCP_DONOTROUND;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int border = unchecked((int)Native.DWMWA_COLOR_NONE);
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));

        // 这里**故意不调 ShowWindow**。
        //
        // 窗口一旦可见，而合成 swapchain 里还没有任何内容，屏幕上的它就是一块
        // 1030x171 的纯黑矩形 —— 开机时那一下"短暂黑屏"就是它，不是显卡的问题。
        // 先把位置摆好但保持隐藏，等主循环画出第一帧再露面（见 RevealWindow）。
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _winX, _winY, _winW, _winH,
            Native.SWP_NOACTIVATE);

        // 订阅会话锁 / 解锁通知。没有这一步，「锁屏回来后 Dock 背景变蓝」只能靠
        // 1.5 秒一次的退避重试去撞时机 —— 撞上之前用户看到的就是一块降级色块。
        bool sessionNotify = Native.WTSRegisterSessionNotification(_hwnd, Native.NOTIFY_FOR_THIS_SESSION);
        // 挂起 / 唤醒：PBT_APMSUSPEND 与 PBT_APMRESUMEAUTOMATIC 只发给注册过的进程，
        // 不注册就只能碰运气（PBT_APMRESUMESUSPEND 才是全局广播）。
        _powerNotify = Native.RegisterSuspendResumeNotification(_hwnd, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
        Logger.Info($"会话通知={sessionNotify} 电源通知={_powerNotify != IntPtr.Zero}");
    }

    /// <summary>
    /// 把窗口正式交给用户看：这一帧已经画好了，不会有黑块。
    /// 紧接着隐形任务栏，让"任务栏消失"和"Dock 出现"发生在同一瞬间。
    /// </summary>
    private void RevealWindow()
    {
        if (_windowShown) return;
        _windowShown = true;

        if (!_hideWindow)
        {
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _winX, _winY, _winW, _winH,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        if (_hideTaskbar)
        {
            int hidden = TaskbarGuard.Hide();
            if (hidden > 0)
                Logger.Info($"任务栏接管: 已隐形 {hidden} 个窗口（整窗 alpha=0 + 点击穿透；退出自动恢复）");
            else
                Logger.Warn("任务栏接管: 这次没找到任务栏窗口，守护会在 250ms 后自动重试");

            // 从这里开始由主循环的 GuardTaskbar 负责（250ms 一次），
            // 抢跑线程的 20ms 高频轮询该退场了 —— 两处同时改任务栏没有意义。
            TaskbarGuard.StopEarlyStealth();
        }
    }

    private void MeasureForMonitor(IntPtr hwnd)
    {
        IntPtr monitor = hwnd == IntPtr.Zero
            ? Native.MonitorFromPoint(new Native.POINT { X = 0, Y = 0 }, Native.MONITOR_DEFAULTTONEAREST)
            : Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);

        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);

        uint dpi = hwnd == IntPtr.Zero ? 96 : Native.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        _scale = dpi / 96f;

        // 尺子只能有一把。启动时窗口还不存在，只能按 96 DPI 起算；如果这里改用窗口的
        // 真实 DPI，同一个 Dock 在"启动"和"运行时重排（比如切任务栏）"两个时刻会算出
        // 两套尺寸 —— 150% 缩放的屏幕上直接大 1.5 倍，看起来就像 Dock 忽然炸开。
        // 所以一旦量过，就把 DPI 缩放锁死，之后的重量只更新显示器矩形。
        if (_scaleLocked > 0) _scale = _scaleLocked;
        else _scaleLocked = _scale;

        DockMetricsConfig m = _config.Metrics;
        _iconSize = m.IconSize * _scale;
        _gap = m.Gap * _scale;
        _padX = m.PaddingX * _scale;
        _padY = m.PaddingY * _scale;
        _barH = _iconSize + _padY * 2f;
        _radius = m.Radius * _barH;
        _shadowExtent = 26f * _scale;
        _captureMargin = (int)MathF.Round(48f * _scale);

        // 分隔竖线只占一条窄缝：线本身 1px 级别，两侧靠正常间距呼吸。
        // 这里只能留线宽那么多 —— 槽位再宽一点，两边加上正常间距，
        // 启动台和开始按钮之间就会空出两三个图标的距离。
        _sepWidth = 2f * _scale;
        _dotSize = MathF.Max(3f, 3.6f * _scale);
        _dotGap = 4f * _scale;

        // 接管任务栏时必须用显示器矩形而非工作区：ShowWindow(SW_HIDE) 不会
        // 触发工作区重算，rcWork 仍然把任务栏那条边占着，Dock 会悬在半空，
        // 底部留一条死带。隐藏任务栏的目的就是让 Dock 贴到真正的屏幕底边。
        Native.RECT area = _hideTaskbar ? info.rcMonitor : info.rcWork;
        _monLeft = area.Left;
        _monRight = area.Right;
        _monBottom = area.Bottom;

        ApplyGeometry();
    }

    /// <summary>
    /// 由当前槽位推出窗口与面板几何。运行中应用增减也会走这里，
    /// 所以它必须能在初始化之后被再次调用。
    /// </summary>
    private void ApplyGeometry()
    {
        DockMetricsConfig m = _config.Metrics;
        int n = _slots.Count;

        if (_left.Length != n)
        {
            _left = new float[n];
            _scaleArr = new float[n];
            _widths = new float[n];
            _gaps = new float[Math.Max(0, n - 1)];
        }

        for (int i = 0; i < n; i++) _widths[i] = SlotWidth(_slots[i]);

        // 分隔线两侧合起来仍是一个正常间距（4/6 + 2/6），但**故意不居中**：
        // 线贴近左边那组，右侧多留一点。原因是左边的启动台是浅色小方块，
        // 同样的空白在它旁边看着比在蓝色开始徽标旁边更空 —— 几何居中会读成
        // "左边太宽、右边太挤"。往左挪 1.8px 之后两边的视觉重量才对得上。
        for (int i = 0; i < n - 1; i++)
        {
            if (_slots[i].Kind == DockSlotKind.Separator) _gaps[i] = _gap * 0.6f;
            else if (_slots[i + 1].Kind == DockSlotKind.Separator) _gaps[i] = _gap * 0.4f;
            else _gaps[i] = _gap;
        }

        float total = 0f;
        for (int i = 0; i < n; i++)
        {
            total += _widths[i];
            if (i < n - 1) total += _gaps[i];
        }
        _baseTotal = total;

        // 底板两边量到"图形的边缘"，不是槽位的边缘。
        // 首个槽位画的是内置图标（启动台 3×3 / 默认徽标 2×2），纹理里自带一圈留白；
        // 右端是 Shell 应用图标，几乎铺满槽位。所以左端看起来天生比右端宽一圈，
        // 补到右边去，两端才一样宽。
        _padLeft = _padX;
        // 时钟占右端一段固定宽度：它不参与图标放大，所以只加进玻璃内边距，
        // 不放进槽位数组 —— 放进槽位的话指针扫过去时钟会跟着放大成一团糊字。
        _clockW = _config.ShowClock ? 80f * _scale : 0f;
        _padRight = _padX + FirstArtInset() +
                    (_clockW > 0f ? _clockW + _gap * 0.6f : 0f);

        // 面板按"最大放大态"的宽度一次算死。运行期指针怎么扫，面板都不再变宽变窄 ——
        // 之前拿每帧实际总宽去改面板，指针扫过去面板会反复伸缩 ±11px，
        // 而面板是居中的，于是左右两端同时抖，肉眼就是"整条在抖"。
        //
        // 面板宽度不再按"最大放大态 + 两侧各留一份"算死 —— 那正是空闲时
        // 两端各空掉 64px 的原因（左右合计 128px，比两个图标还宽）。
        // 现在只记下"最宽会有多少"，交给 LayoutGlass 在运行期跟着内容走。
        float radius = _iconSize * m.InfluenceRadius;
        float maxTotal = DockLayout.MaxTotal(_widths, _gaps, m.Magnification, radius);

        // 空闲态只占图标本身；激活态最宽也就是「内容被放大推到最右」那一下。
        // 注意不能用 _baseTotal + 2*growSpan —— 那是"玻璃居中、两侧都留余量"的旧算法。
        // 现在玻璃左边缘紧贴内容左端，右侧只需要 MaxTotal − _baseTotal 这一份增量。
        _idleInnerW = _baseTotal;
        _activeInnerW = maxTotal;
        _maxGlassW = maxTotal + _padLeft + _padRight;

        // 顶部留够放大后的高度 + 阴影外扩，否则顶出玻璃的图标和投影会被窗口裁掉。
        float topPad = _shadowExtent * 0.85f + (m.Magnification - 1f) * _iconSize + 6f * _scale;
        float bottomPad = _shadowExtent * 0.9f + 6f * _scale;
        _sidePad = _shadowExtent + 10f * _scale;

        // 窗口按**激活态**的最大宽度算死，运行期永不 resize。
        // 玻璃宽度在窗口内部随展开量变化，图标坐标不动，所以不需要动窗口。
        // 窗口宽度 = 玻璃最大宽 + 两侧外扩 + **一整份放大增量**。
        // 最后那一份不能省：图标居中于窗口、玻璃却向右生长，
        // 放大到最大态时玻璃整体右移 growSpan/2，两侧必须各补 growSpan/2 才不至于把阴影裁掉。
        int desiredWinW = (int)MathF.Round(_maxGlassW + (_activeInnerW - _idleInnerW) + _sidePad * 2f);

        // 但不能无限宽：图标一多，这个值会超过屏幕，两头直接落到屏幕外，
        // 窗口本身也会被系统裁掉（图标被切一半还画不出去）。
        // 超过"屏幕宽 - 两侧阴影"就进入溢流模式：窗口封顶、内容改横向滑动。
        int screenW = Math.Max(320, _monRight - _monLeft);
        int maxWinW = (int)MathF.Round(screenW - _sidePad * 2f);
        _winW = Math.Min(desiredWinW, maxWinW);

        // 玻璃最多能长到多宽（窗口减去两侧阴影外扩）
        _viewportW = _winW - _sidePad * 2f;

        // 溢流判据只看"图标本身排不排得下"，**不看放大预留**。
        //
        // 放大预留（_activeInnerW - _idleInnerW）是给"图标弹出玻璃"用的视觉余量，
        // 拿它当溢出判据会把"其实塞得下"也变成可滚动 ——
        // 13 个 160px 图标铺满 2412、视口 2416，明明刚好，却因为放大余量多算了 186
        // 就判成溢出，于是永远"能滚 22px"，白白让人以为功能坏了。
        _overflow = _baseTotal + _padLeft + _padRight > _viewportW + 0.5f;

        _winH = (int)MathF.Round(_barH + topPad + bottomPad);

        _winX = (_monLeft + _monRight) / 2 - _winW / 2;
        _winY = _monBottom - _winH - (int)MathF.Round(m.BottomMargin * _scale);

        _panelY = topPad;
        _panelH = _barH;

        // 面板位置变了，窗口形状必须重算一次（缓存的是"上次裁到哪一行"）
        _regionTop = int.MinValue;

        if (_overflow)
        {
            // 溢流：内容从玻璃左端开始排，视口固定，多出来的部分靠滚动看。
            _originX = _sidePad + _padLeft;
            _scrollMax = MathF.Max(0f, _baseTotal + _padLeft + _padRight - _viewportW);
        }
        else
        {
            // 玻璃左右外边距不等（见 _padLeft），内容要往左边挪半个差值，
            // 面板才仍然居于窗口正中 —— 否则整条会朝右偏一两像素。
            _originX = _winW * 0.5f - _baseTotal * 0.5f + (_padLeft - _padRight) * 0.5f;
            _scrollMax = 0f;
        }

        _scrollOffset = Math.Clamp(_scrollOffset, 0f, _scrollMax);
        _scrollTarget = Math.Clamp(_scrollTarget, 0f, _scrollMax);
        if (_scrollMax <= 0f) { _scrollOffset = 0f; _scrollTarget = 0f; }

        _iconBaseline = _panelY + _panelH - _padY;

        // 初值直接吸附到空闲态，否则启动瞬间玻璃会从零宽滑进来。
        _panelRightSpring.Snap(_originX + _baseTotal + _padRight);
        LayoutGlass(0f);
    }

    /// <summary>
    /// 摆放玻璃。核心约定：**图标组的坐标与玻璃无关，是玻璃去追图标**。
    ///
    /// 左边缘是常量 —— 排布方式决定了图标只会向右扩（左端固定、向右累积），
    /// 所以「内容左端 − padX」也就不动。右边缘用弹簧追最后一个图标的右端：
    /// 指针扫过去时跟着长，离开时平滑收回。
    ///
    /// 这样任何时刻玻璃都紧贴内容，不存在「预留了却用不上」的空档。
    /// 之前为了避免放大越界，空闲时两侧各留了整整一份放大冗余（本机是 64px×2），
    /// 看起来就是玻璃里飘着一排小图标。
    ///
    /// 为什么不会抖：图标坐标恒为「窗口中心 − 空闲总宽/2」，玻璃怎么伸缩都不动它们。
    /// 全场只有一个元素在动（右边缘），而且只朝一个方向单调变化。
    /// </summary>
    private void LayoutGlass(float dt)
    {
        // 溢流时玻璃不再"追内容"，而是固定占满整条视口 ——
        // 内容是滑动的，玻璃要是还跟着内容长，就会随着滚动一伸一缩，像在呼吸。
        if (_overflow)
        {
            float fixedRight = _winW - _sidePad;
            _panelRightSpring.Snap(fixedRight);
            _panelX = _sidePad;
            _panelW = MathF.Max(fixedRight - _panelX, 1f);
            return;
        }

        int n = _slots.Count;
        float contentRight = _originX + _baseTotal;
        if (n > 0 && _left.Length == n && _scaleArr.Length == n)
            contentRight = _originX + _left[n - 1] + _widths[n - 1] * _scaleArr[n - 1];

        float targetRight = contentRight + _padRight;

        _panelRightSpring.StepTo(targetRight, dt, 300f, 34f);

        // 取较大值：弹簧只允许「缩得比内容慢」，绝不允许「长得比内容慢」。
        // 后者会让正在放大的图标探到玻璃外面去，哪怕一个像素都很显眼。
        float right = MathF.Max(_panelRightSpring.Value, targetRight);
        if (MathF.Abs(right - targetRight) < 0.3f)
        {
            _panelRightSpring.Snap(targetRight);
            right = targetRight;
        }

        _panelX = _originX - _padLeft;
        _panelW = right - _panelX;
    }

    private float SlotWidth(DockSlot slot)
        => slot.Kind == DockSlotKind.Separator ? _sepWidth : _iconSize;

    /// <summary>
    /// 第一个槽位的图形在槽内留了多宽的空白（像素）。
    /// 只有内置纹理会留边：启动台是程序画的 3×3 阵列，默认徽标是 2×2；
    /// 应用图标来自 Shell，基本铺满槽位。自定义开始图标同样按铺满处理。
    /// 这段空白本来就计入"左侧视觉空隙"，右侧没有，于是拿它去补右端。
    /// </summary>
    private float FirstArtInset()
    {
        if (_slots.Count == 0) return 0f;

        return _slots[0].Kind switch
        {
            DockSlotKind.Launchpad => GlassRenderer.LaunchpadInset * _iconSize,
            DockSlotKind.Start when _config.StartIconPath.Length == 0
                => GlassRenderer.StartGlyphInset * _iconSize,
            _ => 0f,
        };
    }

    // ---------------------------------------------------------------- 图形

    private void InitGraphics()
    {
        _ctx = D3DContext.Create();

        string shaderPath = Path.Combine(AppContext.BaseDirectory, "Shaders", "Glass.hlsl");
        _shaderSource = File.Exists(shaderPath)
            ? File.ReadAllText(shaderPath, System.Text.Encoding.ASCII)
            : throw new FileNotFoundException("找不到着色器: " + shaderPath);

        _comp = new CompositionHost(_ctx, _hwnd, _winW, _winH);
        _renderer = new GlassRenderer(_ctx, _shaderSource);

        IntPtr monitor = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (_safeMode)
        {
            _sampler = null;
            Logger.Warn("安全模式：跳过桌面采样与折射，仅渲染静态玻璃");
        }
        else
        {
            _sampler = new DesktopSampler(_ctx, monitor);
            Logger.Info("桌面采样: " + _sampler.Status);
        }

        _fallback = new FallbackBackdrop(_ctx);

        _iconTextureSize = (int)MathF.Round(MathF.Max(96f, _iconSize * 2.4f));
        _dotPx = Math.Clamp((int)MathF.Round(_dotSize), 3, 32);
        _dotTex = _renderer.GetDot(_dotPx);
        _sepTex = _renderer.GetSeparator();
        EnsureSlotIcons();

        // 启动台第一次打开要现取 30+ 个 Shell 图标，冷缓存会把 UI 线程卡几秒。
        // 后台线程先补好，用户点的时候直接命中缓存。
        if (_config.ShowLaunchpad) LaunchpadWindow.PrewarmIcons();
    }

    // ---------------------------------------------------------------- 槽位

    /// <summary>
    /// 合成"这一版应该显示哪些固定项"。
    ///
    /// 顺序：Windows 任务栏的固定项（按任务栏上的真实顺序）→ 用户在 Dock 里手动加的。
    /// Windows 上删掉的固定项会自动从 Dock 消失（它不再出现在同步结果里），
    /// 用户在 Dock 里取消固定的项记进 HiddenPins，不会被下一轮同步贴回来。
    /// </summary>
    private void SyncPins()
    {
        _pins.Clear();

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_config.SyncTaskbarPins)
        {
            var hidden = new HashSet<string>(_config.HiddenPins, StringComparer.OrdinalIgnoreCase);

            foreach (TaskbarPin pin in TaskbarPins.Enumerate())
            {
                if (hidden.Contains(pin.Key)) continue;

                _pins.Add(new DockItemConfig
                {
                    Name = pin.Name,
                    // 启动目标：桌面程序是 .lnk，UWP 是 shell:AppsFolder\{AUMID}
                    Path = pin.LaunchPath,
                    // 图标走解析出来的 exe：直接拿 .lnk 取图标会被 shell 盖上
                    // 一个"快捷方式箭头"角标（Windows 任务栏上是不带的）。
                    // UWP 没有 exe 路径，留空让 IconPath 回落到 shell 路径，那种能直接出图标。
                    IconPath = pin.TargetPath.Length > 0 ? pin.TargetPath : null,
                    MatchPath = pin.TargetPath.Length > 0 ? pin.TargetPath : null,
                    Source = "windows",
                    PinKey = pin.Key,
                });

                seenKeys.Add(pin.Key);
                if (pin.TargetPath.Length > 0) seenTargets.Add(pin.TargetPath);
            }
        }

        // 用户手动固定的（含旧配置迁移前留下的）：避开与同步项重复的
        foreach (DockItemConfig item in _config.Items)
        {
            // 兜底项（Source=default）不是用户的固定选择，永远不该进固定区
            if (item.Source == "default") continue;
            if (item.PinKey is not null && seenKeys.Contains(item.PinKey)) continue;
            if (item.MatchPath is not null && seenTargets.Contains(item.MatchPath)) continue;
            if (item.Path.Length > 0 && seenTargets.Contains(item.Path)) continue;
            _pins.Add(item);
        }

        // 一个固定项都没有（同步关了、或者读不到任务栏）：用内置清单兜底，
        // 免得 Dock 变成一条空玻璃
        if (_pins.Count == 0)
            _pins.AddRange(DockConfig.FallbackItems());

        Logger.Info($"[pins] 固定区 {_pins.Count} 项（同步={_config.SyncTaskbarPins} " +
                    $"隐藏={_config.HiddenPins.Count} 手加={_config.Items.Count}）");
    }

    /// <summary>
    /// 重新合成槽位序列（固定区 + 分隔符 + 运行区）。不碰几何。
    /// </summary>
    private void RebuildSlots()
    {
        string before = _slotSignature;
        // 资源管理器系统位和"任务栏固定项里本来就有 explorer"会撞车：有固定项就不再插，
        // 否则 Dock 上会出现两个一模一样的文件夹图标。
        bool explorerPinned = false;
        foreach (DockItemConfig item in _pins)
        {
            if (!IsExplorerPath(item.Path) && !IsExplorerPath(item.MatchPath)) continue;
            explorerPinned = true;
            break;
        }

        _slotSignature = DockSlotBuilder.Build(_pins, _config.ShowRunningApps, _config.ShowStartButton,
            _config.ShowExplorer && !explorerPinned, _config.ShowLaunchpad,
            _tracker?.Apps ?? Array.Empty<TrackedApp>(), _slots);

        // 槽位集合变化是低频事件（开关应用时），值得留痕：
        // "图标没出现/不该出现的出现了"这类问题，只有这张表能直接给出答案。
        if (_slotSignature != before)
        {
            var parts = new List<string>(_slots.Count);
            foreach (DockSlot slot in _slots)
            {
                string tag = slot.Kind switch
                {
                    DockSlotKind.Separator => "|",
                    DockSlotKind.Running => "R",
                    DockSlotKind.Start => "S",
                    DockSlotKind.Explorer => "E",
                    DockSlotKind.Launchpad => "L",
                    DockSlotKind.Status => "T",
                    _ => "P",
                };
                parts.Add(tag + slot.Name + (slot.Running ? "*" : ""));
            }

            Logger.Info($"[slots] {_slots.Count} 项: {string.Join(" ", parts)}");
        }
    }

    /// <summary>给还没有图标的槽位补上图标。已有图标的直接复用缓存，不会重复读盘。</summary>
    private void EnsureSlotIcons()
    {
        if (_renderer is null) return;

        foreach (DockSlot slot in _slots)
        {
            if (slot.Kind == DockSlotKind.Separator) continue;
            if (slot.Icon is not null) continue;

            // 开始按钮和启动台没有路径，纹理由程序生成
            if (slot.Kind == DockSlotKind.Start)
            {
                // 只在真的用了自定义图时记一笔，默认徽标不刷日志
                if (_config.StartIconPath.Length > 0)
                    Logger.Info("[icon] 开始按钮使用自定义图标: " + _config.StartIconPath);

                slot.Icon = _renderer.GetStart(_iconTextureSize, _config.StartIconPath);
                continue;
            }

            if (slot.Kind == DockSlotKind.Launchpad)
            {
                slot.Icon = _renderer.GetLaunchpad(_iconTextureSize);
                continue;
            }

            // 状态位也没有路径：三条滑块是程序画的（见 GlassRenderer.GetStatusGlyph）
            if (slot.Kind == DockSlotKind.Status)
            {
                slot.Icon = _renderer.GetStatusGlyph(_iconTextureSize);
                continue;
            }

            slot.Icon = _renderer.GetIcon(slot.IconPath, slot.IconPath + "#" + _iconTextureSize, _iconTextureSize);
        }
    }

    /// <summary>
    /// 运行中应用变了：重算几何、必要时改窗口与交换链尺寸。
    /// 这是唯一会让 Dock 变宽/变窄的地方。
    /// </summary>
    private void OnSlotsChanged()
    {
        int oldW = _winW, oldH = _winH;

        // 槽位表重建 = 图标会换位置/消失，卡片此刻指向的位置已经没有意义了。
        // 不在这里收掉，卡片会继续挂在一个旧图标的坐标上，看起来像"卡住了"。
        HidePreview();

        ApplyGeometry();
        EnsureSlotIcons();

        if (_hwnd != IntPtr.Zero && (oldW != _winW || oldH != _winH))
        {
            ResizeSurface(oldW, oldH);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _winX, _winY, _winW, _winH,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        // Dock 变宽/变窄时窗口形状要跟着重算，否则会出现"图标点不到 / 空白处却被拦"的错位。
        UpdateWindowRegion();

        _dirty = true;
    }

    /// <summary>
    /// 调整交换链尺寸。ResizeBuffers 有"引用必须全部释放"的前置条件，
    /// 一旦满足不了就会抛 DXGI_ERROR_INVALID_CALL —— 而这条路径在用户开着应用时
    /// 随时会被触发，绝不能让它把整个 Dock 带走。失败就整块重建合成器。
    /// </summary>
    private void ResizeSurface(int oldW, int oldH)
    {
        if (_comp is null || _ctx is null) return;

        try
        {
            _comp.Resize(_ctx, _winW, _winH);
            return;
        }
        catch (Exception ex)
        {
            Logger.Warn($"交换链调整尺寸失败，改为重建合成器: {ex.Message}");
        }

        try
        {
            _comp.Dispose();
            _comp = new CompositionHost(_ctx, _hwnd, _winW, _winH);
        }
        catch (Exception ex)
        {
            // 重建也失败了：退回原尺寸，宁可布局挤一点也不能没有窗口
            Logger.Error("重建合成器失败，退回原尺寸: " + ex.Message);
            _winW = oldW;
            _winH = oldH;
            ApplyGeometry();
        }
    }

    /// <summary>
    /// 每帧问一次窗口追踪器。绝大多数帧只做几次数值比较；
    /// 真正的枚举只在钩子触发或 2 秒安全轮询到点时发生。
    /// </summary>
    private void UpdateAppList()
    {
        if (_tracker is null) return;

        TrackChange change = _tracker.Poll(allowSafetyPoll: !_lastAnimating, allowSync: !_lastAnimating);
        if (change == TrackChange.None) return;

        if (change == TrackChange.Layout)
        {
            RebuildSlots();
            OnSlotsChanged();
        }
        else
        {
            // 只是前台换人：指示点亮度变了，重绘一帧就够
            _dirty = true;
        }
    }

    // ---------------------------------------------------------------- 主循环

    /// <summary>
    /// 低频复查 Windows 任务栏的固定项。用户可能在 Dock 运行期间又钉了一个/取消了一个，
    /// 不复查就要重启才生效。签名只含文件名与修改时间，不解析 .lnk，成本可以忽略。
    /// </summary>
    private void MaybeRescanPins()
    {
        if (!_config.SyncTaskbarPins) return;

        double now = _clock.Elapsed.TotalSeconds;
        if (now < _pinRescanAt) return;
        // 原来是 3 秒。用户要的是"改了 Windows 任务栏，Dock 自己就跟上"，
        // 手动刷新那一项撤掉之后，这个间隔就是唯一的响应时间了 —— 压到 1.5 秒。
        // 成本是每秒一次目录签名（几个文件名的 stat + 读一次 Taskband 二进制值），
        // 比一帧渲染便宜两个数量级，完全可以接受。
        _pinRescanAt = now + 1.5;

        string sig = TaskbarPins.DirectorySignature();
        if (sig == _pinDirSignature) return;
        if (_pinDirSignature.Length == 0)
        {
            _pinDirSignature = sig;
            return;
        }

        _pinDirSignature = sig;
        Logger.Info("[pins] 检测到 Windows 任务栏固定项有变化，重新同步");
        SyncPins();
        RebuildSlots();
        OnSlotsChanged();
    }

    /// <summary>每 2 秒统计一次真实出帧率。窗口期内所有帧都算，不做插值美化。</summary>
    private void FpsTick(bool rendered)
    {
        if (!_fpsLog) return;

        double now = _clock.Elapsed.TotalSeconds;
        _fpsFrames++;
        _fpsTickSum += _lastTickCost;
        if (_lastTickCost > _fpsTickMax) _fpsTickMax = _lastTickCost;

        if (_fpsPrevAt >= 0)
        {
            double gap = (now - _fpsPrevAt) * 1000.0;
            _fpsGapSum += gap;
            if (gap > _fpsGapMax) _fpsGapMax = gap;

            // 超过 25ms 就是肉眼能看出来的一顿。只计数、不打日志 ——
            // 每条 Warn 都要拼字符串 + 同步写文件，本身就是几毫秒 I/O，
            // 一旦每 2 秒打七八条，诊断手段变成了病因（观察者效应）。
            //
            // 必须限定在动画期：空闲期本来就是 30fps（33.3ms 一帧），
            // 把它算进"慢帧"会让这个数字永远下不去，看的人以为一直在掉帧。
            if (gap > 25.0 && _lastAnimating) _fpsSlow++;
        }
        _fpsPrevAt = now;

        double span = now - _fpsWinStart;
        if (span < 2.0) return;

        double avgGap = _fpsFrames > 1 ? _fpsGapSum / (_fpsFrames - 1) : 0;
        double avgSample = _fpsFrames > 0 ? _fpsSampleSum / _fpsFrames : 0;
        double avgWait = _fpsFrames > 0 ? _fpsWaitSum / _fpsFrames : 0;
        double avgTick = _fpsFrames > 0 ? _fpsTickSum / _fpsFrames : 0;
        double avgPoll = _fpsFrames > 0 ? _fpsPollSum / _fpsFrames : 0;

        // GC 计数：一次 gen0 回收只有 1~2ms，但落在 16.7ms 的帧里就足以让它赶不上合成。
        // 之前一直怀疑是"系统抢占"，加上这三个数就能排除（或者坐实）。
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        string gcText = $"GC {gc0 - _fpsGc0}/{gc1 - _fpsGc1}/{gc2 - _fpsGc2}";
        _fpsGc0 = gc0;
        _fpsGc1 = gc1;
        _fpsGc2 = gc2;

        Logger.Info($"[fps] {span:F1}s 帧={_fpsFrames} fps={_fpsFrames / span:F1} " +
                    $"间隔 avg={avgGap:F2} max={_fpsGapMax:F2}ms 慢帧={_fpsSlow} " +
                    $"等合成 avg={avgWait:F2} max={_fpsWaitMax:F2}ms " +
                    $"采样 avg={avgSample:F2} max={_fpsSampleMax:F2}ms " +
                    $"tick avg={avgTick:F2} max={_fpsTickMax:F2}ms " +
                    $"追踪 avg={avgPoll:F2} max={_fpsPollMax:F2}ms " +
                    $"{gcText} 动画中={(_lastAnimating ? "是" : "否")}");
        _fpsWinStart = now;
        _fpsFrames = 0;
        _fpsGapSum = 0;
        _fpsGapMax = 0;
        _fpsSlow = 0;
        _fpsSampleSum = 0;
        _fpsSampleMax = 0;
        _fpsTickSum = 0;
        _fpsTickMax = 0;
        _fpsPollSum = 0;
        _fpsPollMax = 0;
        _fpsWaitSum = 0;
        _fpsWaitMax = 0;
    }

    /// <summary>
    /// 等到绝对时刻 <paramref name="target"/>（秒）。期间持续处理消息，保证输入不积压；
    /// 最后 2ms 自旋收尾，把误差压到几十微秒。
    ///
    /// 这里必须"醒来就重算剩余时间、没到点继续睡"。踩过的坑：单次
    /// MsgWaitForMultipleObjectsEx(QS_ALLINPUT) 会被下一条鼠标消息立刻唤醒，
    /// 鼠标以 200Hz 涌进来时等于根本没睡 —— 一轮几微秒就转回循环顶部，
    /// 实测帧计数刷到每秒上百万次，Dock 直接卡死。
    /// </summary>
    private void WaitUntil(double target, bool precise = true)
    {
        while (!_quit)
        {
            double remaining = target - _clock.Elapsed.TotalSeconds;
            if (remaining <= 0.0002) return;

            // 先把积压消息处理掉，否则 MsgWait 会被同一条消息反复唤醒。
            PumpMessages(2.0);

            // 桌面构图变了就别等了 —— 玻璃要在下一帧就把新背景吃进去。
            // 不设这个出口的话，事件虽然被处理了，主循环仍在按空闲节奏睡到本拍结束，
            // 用户看到的依然是"慢半拍"，只是从 1 秒缩到 250ms 而已。
            if (_wakeRequested)
            {
                _wakeRequested = false;
                return;
            }

            remaining = target - _clock.Elapsed.TotalSeconds;
            if (remaining <= 0.0002) return;

            if (_waitTimer != IntPtr.Zero)
            {
                // 相对时间用负数表示，单位 100ns。高精度定时器实测误差 < 0.5ms，
                // 而 MsgWait 自带的毫秒超时恒定慢 1.4ms —— 那就是之前"每帧都晚一点、
                // 60Hz 目标只跑到 55fps"的根源。
                long due = -(long)(remaining * 10_000_000.0);
                if (due < -1) due = -1;

                if (Native.SetWaitableTimer(_waitTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    // 同时等"定时器到点"和"有新消息"：有消息就立刻回来处理，
                    // 手指不会因为等待而拖手；没消息就踏实睡到点。
                    Native.MsgWaitForMultipleObjectsEx(1, _waitHandles, Native.INFINITE,
                        Native.QS_ALLINPUT, 0);

                    // 收尾自旋：定时器再准也有零点几毫秒的抖动，最后这段用自旋压平。
                    //
                    // **只在 precise 时做**。这是本机实测出来的耗电大户：
                    // 系统只要往队列里丢一条消息（WM_TIMER、WM_PAINT、任何东西），
                    // MsgWait 就立刻返回，然后这个 while 会把**剩下的一整个周期**自旋掉。
                    // 空闲期 30Hz × 每轮自旋约 30ms ≈ 每秒 900ms 纯空转，实测单核 96%，
                    // 笔记本风扇就是被它吹起来的。
                    // 空闲期没人看亚毫秒精度：醒来重算剩余、没到点继续睡就够了。
                    if (precise)
                    {
                        while (!_quit && _clock.Elapsed.TotalSeconds < target - 0.0002)
                            Thread.SpinWait(40);
                    }

                    continue;
                }
            }

            // 退路（老系统没有高精度定时器）：粗睡 + 余量留大一点，靠自旋收尾。
            uint ms = (uint)Math.Max(1.0, Math.Round((remaining - 0.003) * 1000.0));
            Native.MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, ms, Native.QS_ALLINPUT, 0);

            if (precise)
            {
                while (!_quit && _clock.Elapsed.TotalSeconds < target - 0.0015)
                    Thread.SpinWait(60);
            }
        }
    }

    /// <summary>
    /// 取并处理一轮消息，带两个限制：
    ///
    /// 1. **合并鼠标移动**。高回报率鼠标（200Hz+）在一帧里能攒下十几条 WM_MOUSEMOVE，
    ///    每条都要 Dispatch 进 WndProc、做一次命中测试。位置这个东西是"最新的说了算"，
    ///    中间那些位置没有任何意义（每帧还会用 GetCursorPos 复核一次），
    ///    所以只处理队列里最后一条。
    /// 2. **时间预算**。无论还剩多少消息，超过预算就收工，剩下的下一轮再处理。
    ///    否则一次消息洪峰就能把这一帧拉长到 30~40ms —— 实测滑动时每 2 秒有 7 次这种长帧，
    ///    而同一份代码在"指针已离开、没有鼠标消息"时能做到 60.0fps / 慢帧 0。
    /// </summary>
    private void PumpMessages(double budgetMs)
    {
        double limit = _clock.Elapsed.TotalSeconds + budgetMs / 1000.0;

        while (Native.PeekMessage(out Native.MSG m, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
        {
            if (m.message == Native.WM_DESTROY)
            {
                _quit = true;
                return;
            }

            // 后面还排着鼠标移动就先丢掉这一条，反正下一步会拿到更新的
            if (m.message == 0x0200 /* WM_MOUSEMOVE */ &&
                Native.PeekMessage(out Native.MSG next, IntPtr.Zero, 0, 0, 0 /* PM_NOREMOVE */) &&
                next.message == 0x0200)
            {
                continue;
            }

            Native.TranslateMessage(ref m);
            Native.DispatchMessage(ref m);

            if (_clock.Elapsed.TotalSeconds > limit) return;
        }
    }

    private bool Tick()
    {
        double tickStart = _clock.Elapsed.TotalSeconds;
        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)(now - _lastTick);
        _lastTick = now;
        if (dt > 0.25f) dt = 0.25f;

        // 锁屏期间整条 Dock 被锁屏盖住，什么都不用画。
        //
        // 解锁后**不能**继续不画：DWM 有可能在锁屏/解锁的切换里丢掉合成内容，
        // 那时窗口就是一块空白。所以要靠冻结帧重新画出来（见 SampleBackground
        // 与 Render 的 frozen 分支）—— 画的是锁屏前那一帧桌面，观感没有变化。
        if (_sessionLocked)
        {
            _lastAnimating = false;
            return false;
        }

        UpdateAppList();
        // 固定项目录扫描（读目录 + 每个 .lnk 的时间戳）同样属于"可以等一等"的活，
        // 动画期跳过一次不影响任何观感，但能保住那一帧的预算。
        if (!_lastAnimating) MaybeRescanPins();
        ApplyDevPointer();

        // 物理光标兜底。只靠消息驱动不可靠：鼠标快速划出、拖拽中丢失捕获、
        // 或 TME_LEAVE 注册被系统丢掉时，WM_MOUSELEAVE 不会到达，
        // 结果是鼠标早就走了、图标却一直挂着放大。每帧用真实光标位置复核一次，
        // 代价只有一次 GetCursorPos（微秒级），远小于一次重绘。
        //
        // 这里**刻意不再要求 PointerInside 已经为真**。原来只在"已进入"时复核，
        // 留下一个很隐蔽的死角：光标在我们启动之前就停在 Dock 上（开机自启、
        // 睡眠唤醒回来、或者刚把任务栏交出去），系统不会补发 WM_MOUSEMOVE，
        // PointerInside 永远起不来 —— 用户看到的就是"悬停在 Dock 上没反应，
        // 得先动一下鼠标"。现在每轮都问一次系统：光标到底在哪。
        if (float.IsNaN(_forcedPointerX) && !_sweep)
            SyncPointerWithCursor();

        // 悬停到时间就弹窗口预览（卡片自己是个独立的分层窗口，不参与 Dock 的玻璃渲染）
        UpdateHoverPreview(now);

        // 开发用：用一个真实的 WM_LBUTTONDOWN 走完整命中测试 + 启动链路
        if (_clickIndex >= 0 && !_clickSent && _clock.Elapsed.TotalSeconds > 1.6)
        {
            _clickSent = true;
            int hit = _clickIndex;
            if (hit < _slots.Count && hit < _scaleArr.Length)
            {
                float size = _iconSize * _scaleArr[hit];
                int cx = (int)(_originX + _left[hit] - _scrollOffset + size * 0.5f);
                int cy = (int)(_iconBaseline - size * 0.5f);
                IntPtr lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
                Native.PostMessageW(_hwnd, Native.WM_LBUTTONDOWN, IntPtr.Zero, lp);
                Logger.Info($"[测试] 模拟点击槽位 {hit} ({_slots[hit].Name}) at client({cx},{cy})");
            }
        }

        // 先算出这一帧要不要动
        bool animating = UpdateAnimation(dt);
        _lastAnimating = animating;

        // 右端的电量/网络/时间胶囊。它是个独立的分层窗口，不参与下面的玻璃渲染 ——
        // 位置依赖玻璃弹簧这一帧的值，所以必须排在 UpdateAnimation 之后。
        UpdateStatusPill(now);

        // 时钟一到下一分钟就强制画一帧。否则空闲时（4Hz 且背景不动）
        // 时间会停在上一分钟，看起来像时钟坏了。
        if (_clockW > 0f)
        {
            int minute = DateTime.Now.Minute;
            if (minute != _clockMinute)
            {
                _clockMinute = minute;
                _dirty = true;
            }
        }

        // 采样不在这里做。桌面采样（AcquireNextFrame + 区域拷贝）平均只要 0.3ms，
        // 但偶尔会抽到 11~12ms 的尖峰 —— 那一下就吃掉 60Hz 帧预算的 2/3，
        // 位置又恰好卡在渲染之前，直接导致这一帧错过合成周期，掉到 33ms。
        // 现在改由主循环在 DwmFlush 之后再采（见下方），用整个周期去采背景，
        // 渲染路径上只剩 ~0.3ms 的纯绘制。
        if (!animating && !_dirty) return false;

        Render();
        _dirty = false;

        double tickCost = (_clock.Elapsed.TotalSeconds - tickStart) * 1000.0;
        _lastTickCost = tickCost;
        if (_fpsLog && tickCost > 6.0)
            Logger.Warn($"[slow] Tick 耗时 {tickCost:F1}ms（60Hz 预算 16.7ms）");

        return true;
    }

    /// <summary>
    /// 采一帧桌面背景，供之后的绘制使用。
    /// blockMs &gt; 0 才会阻塞等待桌面变化（空闲期靠它省 CPU）；
    /// 动画期必须传 0，并且要放在 DwmFlush 之后调用，让尖峰落在空闲预算里。
    /// </summary>
    /// <summary>
    /// 窗口事件回调：桌面构图变了。
    ///
    /// 跑在 SetWinEventHook 的回调里（主线程消息泵中间），所以这里**只置标志位**：
    /// 抓屏一次要 10ms，塞在消息派发里等于让别人的消息陪着等。
    /// 真正的采样和重绘交给下一轮主循环，并且顺手打断空闲期的定长等待。
    /// </summary>
    private void OnDesktopChanged()
    {
        double now = _clock.Elapsed.TotalSeconds;

        // 事件簇拥时合并。开一个程序会连发十几个 CREATE/SHOW/DESTROY，
        // 每个都立刻抓一帧纯属浪费（一次抓屏 10ms），40ms 内只认第一个。
        // 第一个才是"画面已经变了"的那个时刻，后面的都是它的尾巴。
        if (now - _lastWakeAt < 0.04) return;
        _lastWakeAt = now;

        _dirty = true;
        _lastIdleSampleAt = 0;   // 清掉节流基准，下一轮立刻抓，不等 250ms
        _desktopBoostUntil = now + DesktopBoostSeconds;
        _wakeRequested = true;
        if (_boostAt <= 0) _boostAt = now;
    }

    private bool SampleBackground(int blockMs)
    {
        int capX = _winX - _captureMargin;
        int capY = _winY - _captureMargin;
        int capW = RoundTo(_winW + _captureMargin * 2, 8);
        int capH = RoundTo(_winH + _captureMargin * 2, 8);

        bool got = false;

        bool captureAllowed = !_sessionLocked && _samplerRebuildAt <= 0;
        if (captureAllowed && _sampler is not null)
        {
            double tSample = _clock.Elapsed.TotalSeconds;
            got = _sampler.TryUpdate(capX, capY, capW, capH, blockMs);
            double sampleMs = (_clock.Elapsed.TotalSeconds - tSample) * 1000.0;
            _lastSampleMs = sampleMs;

            _sampleMs += sampleMs;
            _sampleCalls++;
            if (_fpsLog)
            {
                _fpsSampleSum += sampleMs;
                if (sampleMs > _fpsSampleMax) _fpsSampleMax = sampleMs;
            }

            // 启动台开着的时候，面板那一大块也是在同一次 AcquireNextFrame 里拷好的。
            // 主区不能让给启动台：它铺满整屏，但 Dock 仍然浮在它上面、还在实时显示。
            if (_launchpad is { IsVisible: true } pad) pad.NotifyBackdrop(_sampler.TertiaryChanged);

            // 只在状态变化时记日志。采集在锁屏/唤醒后可能来回抖几次，
            // 每次都要留痕，但绝不能每轮都写 —— 4Hz 一晚上能把日志写成几百 MB。
            if (_sampler.Status != _samplerStatus)
            {
                string prev = _samplerStatus;
                _samplerStatus = _sampler.Status;
                if (_sampler.Available)
                    Logger.Info("桌面采样: " + _sampler.Status + (prev.Length > 0 ? $"（原: {prev}）" : ""));
                else
                    Logger.Warn("桌面采样不可用，玻璃改用 CPU 抓屏兜底: " + _sampler.Status +
                                (prev.Length > 0 ? $"（原: {prev}）" : ""));
            }

            // 追帧延迟实测（LGD_LAG_LOG=1 才输出）：从"窗口事件到达"到"真的拿到新的一帧"。
            // 这是判断"玻璃跟不跟得上"的唯一硬指标 —— 拿不到新帧，渲染再快也是旧画面。
            // 从启动台点进别的任务之后 3 秒内，无论有没有开环境变量都记（见 CloseLaunchpad），
            // 因为这条路径上"背景停在原页面"的耗时只能靠日志定位。
            if (got && (_lagLog || _clock.Elapsed.TotalSeconds < _lagDiagUntil) && _boostAt > 0)
            {
                Logger.Info($"追帧: 事件→新帧 {(_clock.Elapsed.TotalSeconds - _boostAt) * 1000:F0}ms");
                _boostAt = 0;
            }

            // 采样没能带回首帧时留痕（诊断窗口内，100ms 最多一条）：
            // 用来区分"根本没在采"（循环没转/被限频）和"采了但被脏区过滤丢掉"。
            if (_lagLog && _clock.Elapsed.TotalSeconds - _lagMissLogAt > 0.1)
            {
                _lagMissLogAt = _clock.Elapsed.TotalSeconds;
                Logger.Info($"[lag] got={got} block={blockMs}ms 动画={_lastAnimating} 次区={_sampler.SecondaryChanged} " +
                            $"主区拷贝={_sampler.MainCopies} 次区拷贝={_sampler.SecondaryCopies} " +
                            $"次区亮度={_sampler.SecondaryFpLuma:F2} miss={_sampler.MissCount}");
            }
        }

        // 采集不可用（会话丢失 / 锁屏 / 受保护内容 / 全黑帧）→ CPU 抓一次真实桌面顶上。
        //
        // 这一步是"蓝灰色板子"和"黑板子"的终结者：过去这里什么都不做，
        // 着色器只能走降级分支画中性灰，叠上玻璃的冷色偏移就是一块静止的蓝灰色；
        // 唤醒后那几秒采到全黑帧时则是一块黑板。现在永远至少有真实壁纸可看。
        bool live = captureAllowed && _sampler is not null && _sampler.IsLive;
        double tNow = _clock.Elapsed.TotalSeconds;
        if (_desktopPassthroughUntil > 0 && tNow >= _desktopPassthroughUntil && captureAllowed)
        {
            bool captured = _sampler is { Available: true, HasFrame: true, RegionW: > 0, ProtectedMasked: false };
            // Dark content is legitimate. After the settling period, an unmasked
            // acquisition from the new session can be used even if it is black.
            if (captured && (live || tNow >= _desktopPassthroughUntil + 0.45))
            {
                _desktopPassthroughUntil = 0;
                _backdropLive = true;
                _backdropFlipAt = 0;
                if (!LaunchpadHoldsBackdrop) _sampler!.ReleaseFreeze();
                got = true;
                Logger.Info("解锁玻璃：有效桌面帧已就绪，恢复 F 材质");
            }
        }

        // 冻结帧：锁屏/唤醒过渡期就是画"锁屏前那一帧桌面"。
        //
        // 只有真的采到活的桌面（会话在 + 有帧 + 像素不是全黑）才解冻 ——
        // 这里刻意不用"解锁后等 N 秒"这种时间判据：解锁动画有时比 0.9 秒长，
        // 时间一到采集还没恢复，就又会去抓屏，抓回来的正是过渡画面（灰 → 冷蓝）。
        bool frozen = _sampler is { FrozenActive: true };
        // 解冻要同时满足三条，缺一条都会把过渡画面放出来：
        //   1. 不在锁屏中 —— 锁屏消息到达后那几百毫秒，采集还在返回锁屏前的旧帧，
        //      单看"live"会误判成已经恢复，把冻结帧提前扔掉；
        //   2. 采到活的桌面（会话在 + 有帧 + 不是全黑）；
        //   3. 这一帧没有被标记为受保护 —— 解锁淡出那几帧正是受保护/遮罩的，
        //      它们不是黑也不算超时，但画出来就是灰/冷蓝色。
        if (frozen && _desktopPassthroughUntil <= 0 && !_sessionLocked && !LaunchpadHoldsBackdrop && live && !_sampler!.ProtectedMasked)
        {
            _sampler.ReleaseFreeze();
            frozen = false;
            // 冻结期间桌面已经被确认可用，直接采纳，不再套迟滞
            _backdropLive = true;
            _backdropFlipAt = 0;

            // 解冻这一拍必须立刻重绘。
            //
            // 冻结期间 Dock 画的是"打开启动台之前"那一帧。解冻后如果这一轮 TryUpdate
            // 没带回新像素（切前台那几帧桌面还在重绘，脏区/指纹都没变），got 就是 false，
            // 主循环就不会重画 —— 玻璃里仍旧是那张旧画面，要等下一次真采到变化。
            // 实测这段空窗约 190ms，正是"点 Dock 图标切到别的任务，背景慢半拍"的来源。
            // 主区纹理一直在实时更新（冻结只影响 Dock 画哪张，不影响采样），
            // 所以这一拍直接拿主区重画一次就行，不必等新帧。
            if (_sampler.HasFrame) got = true;

            // 解冻之后的 2 秒里，不再相信"metadata 为空 = 桌面没变"。
            //
            // 切换前台/关闭启动台这一下，新画面是被 DWM 直接合成上去的，
            // desktop duplication 经常不给 dirty rect。我们按"没变"把它丢掉，
            // 玻璃就一直挂着切换前那张背景，要等 heartbeat 兜底才自愈 ——
            // 用户原话："我已经到另一个页面了，但有一小会还停留在原来的页面背景。"
            _sampler.ForceCompareFor(ForceCompareAfterThawMs);

            // 胶囊同理：它一直画着冻结的那一帧，解冻后必须立刻跟上，别等指纹。
            _statusPill?.ForceRepaintFor(PillThawMs);

            Logger.Info("玻璃背景: 采到活的桌面，结束冻结");
        }
        else if (frozen && !_sessionLocked && _sampler!.FrozenExpired)
        {
            // 兜底收尾：一直等不到一帧"干净"的活帧，不能永远挂着旧桌面。
            // 如果整体还是 live 的（不是黑帧），说明区域里已经有真桌面，
            // 只是受保护标记在乱翻 —— 那就直接用；否则退回 CPU 抓屏。
            _sampler.ReleaseFreeze();
            frozen = false;
            _backdropLive = live;
            _backdropFlipAt = 0;
            // 同解冻正常路径：这一拍就重画，别让旧帧多挂一轮。
            if (live && _sampler.HasFrame) got = true;
            // 桌面刚被换过，接下来 2 秒不信"metadata 为空 = 没变"，逐帧比像素。
            _sampler.ForceCompareFor(ForceCompareAfterThawMs);
            _statusPill?.ForceRepaintFor(PillThawMs);
            Logger.Warn("玻璃背景: 冻结帧超时，结束冻结（" +
                        (live ? "改用当前采集帧" : "退回 CPU 抓屏兜底") + "）");
        }

        // 迟滞：判据里有"黑帧探测"这种一秒粒度、偶尔会抖的量，
        // 不留缓冲的话背景会在实时/兜底之间每几百毫秒翻一次 —— 那比一直兜底还难看。
        // 新状态必须连续观察到 HysteresisSeconds 才真的切换。
        const double HysteresisSeconds = 1.2;
        if (!frozen)
        {
            if (_backdropMode < 0)
            {
                _backdropLive = live;   // 第一次直接采纳，不延迟
            }
            else if (live != _backdropLive)
            {
                if (_backdropFlipAt <= 0) _backdropFlipAt = tNow + HysteresisSeconds;
                else if (tNow >= _backdropFlipAt)
                {
                    _backdropLive = live;
                    _backdropFlipAt = 0;
                }
            }
            else
            {
                _backdropFlipAt = 0;
            }
        }

        // 冻结期不抓屏。BitBlt 在这段时间抓到的就是锁屏画面或解锁淡出动画，
        // 喂给玻璃就等于把 Dock 染成灰/冷蓝色 —— 画冻结帧才是对的。
        bool holding = !captureAllowed || frozen || _desktopPassthroughUntil > 0;

        if (!_backdropLive && !holding)
        {
            if (_fallback is not null && _fallback.Refresh(capX, capY, capW, capH, FallbackRefreshMs))
                got = true;
        }

        int mode = frozen ? 3 : _backdropLive ? 0 : _fallback is { Ready: true } ? 1 : 2;
        if (mode != _backdropMode && (mode == 3 || !holding))
        {
            _backdropMode = mode;
            string why = _sampler is null ? "无采样器"
                : $"dup={_sampler.Available} 帧={_sampler.HasFrame} 受保护={_sampler.ProtectedMasked} " +
                  $"黑帧={_sampler.LooksBlack()} 状态={_sampler.Status}";
            if (mode == 0) Logger.Info("玻璃背景: DXGI 实时桌面");
            else if (mode == 1)
                Logger.Warn("玻璃背景: 改用 CPU 抓屏兜底（" + why + "）");
            else if (mode == 3)
                Logger.Info(LaunchpadHoldsBackdrop
                    ? "玻璃背景: 启动台打开中，Dock 保持打开前那一帧底图"
                    : "玻璃背景: 保持锁屏前最后一帧（等待采集恢复）");
            else Logger.Warn("玻璃背景: 无可用背景，暂时退化为中性磨砂（" + why + "）");
        }

        return got;
    }

    /// <summary>
    /// 玻璃空白穿透解决不了"按钮正好在图标正上方"的情形 ——
    /// 那时唯一不被挡的办法就是 Dock 整条离开。
    /// 点击后滑出屏幕底边，5 秒后自动滑回来；期间不接收任何鼠标。
    /// 用 SetWindowPos 瞬移而不是动画：这是用户主动要求的避让，
    /// 瞬间消失比慢慢滑走更"听话"，也不用为 5 秒一次的功能建动画状态机。
    /// </summary>
    private void DuckDock()
    {
        _ducked = true;
        _duckUntil = _clock.Elapsed.TotalSeconds + 5;
        PointerInside = false;
        _hoverIndex = -1;
        _dirty = true;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, _winX, _winY + _winH + 40, _winW, _winH,
            Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);
        Logger.Info("菜单：暂时让开（5 秒后自动回来）");
    }

    private void Undock()
    {
        _ducked = false;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, _winX, _winY, _winW, _winH,
            Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_NOZORDER);
        _dirty = true;
    }

    /// <summary>开发档位：固定指针位置 / 让虚拟指针往复扫过，均用于真实测量。</summary>
    private void ApplyDevPointer()
    {
        if (!float.IsNaN(_forcedPointerX))
        {
            _pointerX = _forcedPointerX;
            _pointerReason = "--pointer 固定";
            PointerInside = true;
            return;
        }

        if (!_sweep) return;

        double t = (_clock.Elapsed.TotalSeconds - _sweepStart) % 2.4;
        double phase = t < 1.2 ? t / 1.2 : (2.4 - t) / 1.2;   // 0..1..0
        double eased = phase * phase * (3 - 2 * phase);
        _pointerX = _originX - _iconSize + (float)eased * (_baseTotal + _iconSize * 2f);
        _pointerReason = "--sweep 扫掠";
        PointerInside = true;
    }

    private static int RoundTo(int value, int multiple)
        => (value + multiple - 1) / multiple * multiple;

    private bool UpdateAnimation(float dt)
    {
        float targetAmp = PointerInside ? 1f : 0f;
        bool before = _amp.IsSettled(targetAmp);
        _amp.StepTo(targetAmp, dt, 200f, 24f);

        bool changed = !before || !_amp.IsSettled(targetAmp, 0.001f);

        // 指针本身在动，也是"动画中"。这一条以前漏了，后果不小：
        // 鼠标已经在 Dock 上、放大系数早就到 1 并稳定下来，此时左右滑动指针，
        // 弹簧一帧都没动 → animating=false → 掉进空闲档（30fps + 不做垂直同步），
        // 鼠标划得再顺也是"一卡一卡"的。滑动期的帧率必须给满。
        bool pointerMoved = !float.IsNaN(_pointerX) && MathF.Abs(_pointerX - _lastPointerX) > 0.01f;
        _lastPointerX = _pointerX;
        if (pointerMoved) changed = true;

        // 溢流滚动：指针压在玻璃两端的热区里就持续推进。
        // 必须排在 pointerLayout 之前 —— 滚动会改变内容的横向位置，
        // 放大效果是按"指针相对内容"算的，不先滚就会指着错的图标放大。
        if (UpdateOverflowScroll(dt)) changed = true;

        // 布局
        float pointerLayout = PointerInside ? _pointerX - _originX + _scrollOffset : float.NaN;
        float radius = _iconSize * _config.Metrics.InfluenceRadius;
        DockLayout.Compute(_widths, _gaps, pointerLayout, _amp.Value,
            _config.Metrics.Magnification, radius, _left, _scaleArr);

        // 玻璃右边缘追着内容长。指针扫动时它跟着变，离开时平滑收回 ——
        // 动的只有右边缘一个量，图标坐标全程不动，所以不会把抖动带回来。
        float prevPanelW = _panelW;
        LayoutGlass(dt);
        if (MathF.Abs(prevPanelW - _panelW) > 0.01f) changed = true;

        // 窗口形状跟着**实际放大值**走：放大中的图标探出玻璃多少，区域就要跟着长多少。
        // 只在 PointerInside 翻转时更新是上一版的写法，那一刻正好是"点一下图标"的瞬间，
        // 区域会先缩回玻璃上沿、图标再慢慢长大 —— 屏幕上看到的就是一刀切。
        UpdateWindowRegion();

        return changed;
    }

    /// <summary>
    /// 溢流滚动。返回"这一帧滚动位置有没有变"（变了就要按动画档继续出帧）。
    ///
    /// 触发方式是指针压到玻璃两端的热区里：越靠边推得越快（二次曲线），
    /// 一离开热区立刻停住 —— 不用按住、也没有惯性。Dock 上滑动本身就该这么直接，
    /// 加了惯性以后"想停在某个图标上"反而变得很难。
    /// 滚轮写的是同一个目标值，两种输入共用一套平滑。
    ///
    /// 往回滚（指针压左端）只在已经滚出去之后才响应：内容没滚过的时候，
    /// 左端热区就是第一个图标，压上去还想放大，不能同时又往左推。
    /// </summary>
    private bool UpdateOverflowScroll(float dt)
    {
        if (!_overflow || _scrollMax <= 0.5f)
        {
            if (_scrollOffset == 0f && _scrollTarget == 0f) return false;
            _scrollOffset = 0f;
            _scrollTarget = 0f;
            return true;
        }

        // 热区：越宽越好按，但也别宽到"想点最后一个图标却在滚动"。
        // 2560 的屏上 92px 实在太窄（得把指针精确压到边缘才动），放到 130。
        float edge = Math.Clamp(_viewportW * 0.16f, 26f * _scale, 130f * _scale);

        // 诊断：滚动手感出问题时，光看画面分不清是"指针没进来""热区判断错了"
        // 还是"滚了但没重绘"。一秒一条，够定位，又不至于把日志写爆。
        if (Environment.TickCount64 - _scrollLogAt > 1000)
        {
            _scrollLogAt = Environment.TickCount64;
            Logger.Info($"[scroll] inside={PointerInside} px={_pointerX:F0} panelX={_panelX:F0} " +
                        $"panelW={_panelW:F0} edge={edge:F0} target={_scrollTarget:F0} " +
                        $"off={_scrollOffset:F0} max={_scrollMax:F0}");
        }

        if (PointerInside && !float.IsNaN(_pointerX))
        {
            float rel = _pointerX - _panelX;
            float speed = 0f;

            if (rel > _panelW - edge)
            {
                float t = Math.Clamp((rel - (_panelW - edge)) / edge, 0f, 1f);
                // 1.6 次方而不是平方：刚进热区就该有明确反馈，
                // 平方前 20% 只有 4% 速度，按上去像没反应。
                speed = OverflowScrollSpeed * _scale * MathF.Pow(t, 1.6f);
            }
            else if (rel < edge && _scrollTarget > 0.5f)
            {
                float t = Math.Clamp((edge - rel) / edge, 0f, 1f);
                speed = -OverflowScrollSpeed * _scale * MathF.Pow(t, 1.6f);
            }

            if (speed != 0f)
            {
                float next = Math.Clamp(_scrollTarget + speed * dt, 0f, _scrollMax);
                if (MathF.Abs(next - _scrollTarget) > 0.001f) _scrollTarget = next;
            }
        }

        float before = _scrollOffset;
        _scrollOffset += (_scrollTarget - _scrollOffset) * MathF.Min(1f, dt * 22f);
        if (MathF.Abs(_scrollOffset - _scrollTarget) < 0.4f) _scrollOffset = _scrollTarget;
        return MathF.Abs(_scrollOffset - before) > 0.01f;
    }

    private void Render()
    {
        if (_ctx is null || _comp is null || _renderer is null) return;

        double t0 = _clock.Elapsed.TotalSeconds;

        GlassConfig g = _config.Glass;

        // 采集"活着"的判定交给采样那一侧（带黑帧探测和迟滞，见 SampleBackground）。
        // 这里只读结果：渲染每秒要跑几十次，不能每次都去做一次探测/判定。
        //
        // 三个条件缺一不可：
        //   HasFrame      —— 这张区域纹理真的拷进过画面；
        //   RegionW > 0   —— Reset 之后区域尺寸会被标成 -1、坐标作废；
        //   IsLive        —— 像素不是全黑（解锁头几帧经常是受保护的纯黑帧）。
        // 少任何一个，渲染就会把一块没写过 / 全黑的纹理当成背景，也就是那块"灰/黑板子"。
        // 冻结期间一律画冻结帧 —— 不管采集此刻看起来多"活"。
        // 锁屏消息到达后的几百毫秒里，采集还在返回锁屏前的旧帧 / 解锁淡出帧，
        // 单看 IsLive 会把它们当成真桌面，画出来就是灰/冷蓝色。
        bool frozenBg = _sampler is { FrozenActive: true };
        bool hasCapture = !frozenBg && _backdropLive
                          && _sampler is { HasFrame: true, Srv: not null, RegionW: > 0 }
                          && _sampler.IsLive;

        int capX = _winX - _captureMargin;
        int capY = _winY - _captureMargin;
        int capW = RoundTo(_winW + _captureMargin * 2, 8);
        int capH = RoundTo(_winH + _captureMargin * 2, 8);

        // 采集不可用 → 优先用冻结帧（锁屏前最后一张真桌面），
        // 其次 CPU 兜底图（真实壁纸），都不行才让着色器画降级色。
        bool useFrozen = !hasCapture && frozenBg && _sampler!.FrozenSrv is not null;
        bool useFallback = !hasCapture && !useFrozen && _fallback is { Ready: true };

        ID3D11ShaderResourceView? capSrv = null;
        int srcW = capW, srcH = capH, srcX = capX, srcY = capY;
        if (hasCapture)
        {
            capSrv = _sampler!.Srv;
            srcW = _sampler.RegionW;
            srcH = _sampler.RegionH;
            srcX = _sampler.RegionX;
            srcY = _sampler.RegionY;
        }
        else if (useFrozen)
        {
            capSrv = _sampler!.FrozenSrv;
            srcW = _sampler.FrozenW;
            srcH = _sampler.FrozenH;
            srcX = _sampler.FrozenX;
            srcY = _sampler.FrozenY;
        }
        else if (useFallback)
        {
            capSrv = _fallback!.Srv;
            srcW = _fallback.W;
            srcH = _fallback.H;
            srcX = _fallback.X;
            srcY = _fallback.Y;
        }

        // 一条"没有背景"的帧都不许画。
        //
        // 着色器在 Extra.z = 0（没有采集纹理）时画的是中性灰 + 冷偏，也就是用户
        // 描述的那块"发冷蓝色加灰色"。它本来只在极端情况下出现，但打开/关闭启动台
        // 的那一瞬间，DWM 重合成 + 黑帧探测正好会凑齐条件：hasCapture 为 false，
        // 而 _backdropLive 还在 1.2 秒的迟滞期里没翻过去，于是走了这条空样本分支。
        // 直接不画 —— 交换链里留着上一帧，观感就是"什么都没发生"。
        bool desktopPassthrough = _sessionLocked || _desktopPassthroughUntil > 0;
        if (capSrv is null && !desktopPassthrough)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _noSrvLogAt >= 1000)
            {
                _noSrvLogAt = nowMs;
                Logger.Warn($"[dock] 本帧无背景样本，保留上一帧 live={_backdropLive} " +
                            $"hasFrame={_sampler?.HasFrame} srv={_sampler?.Srv is not null} " +
                            $"regionW={_sampler?.RegionW} isLive={_sampler?.IsLive} fallbackReady={_fallback?.Ready}");
            }

            return;
        }

        _quads.Clear();
        for (int i = 0; i < _slots.Count && i < _scaleArr.Length; i++)
        {
            DockSlot slot = _slots[i];
            float grow = _scaleArr[i];
            float x = _originX + _left[i] - _scrollOffset;
            float size = _iconSize * grow;

            // 溢流时把彻底滑出玻璃的图标丢掉，边缘那几个按距离淡出。
            //
            // 不做这一步的话，滑出去的图标会画在玻璃外的透明区上 ——
            // 屏幕上就是"图标飘在 Dock 外面"，比截断难看得多。
            // 窗口有 SetWindowRgn 只裁到窗口边界，玻璃面板的圆角它管不着。
            float alpha = 1f;
            if (_overflow)
            {
                float fade = 22f * _scale;
                float leftEdge = _panelX;
                float rightEdge = _panelX + _panelW;
                if (x + size <= leftEdge + 1f || x >= rightEdge - 1f) continue;
                alpha = Math.Clamp(MathF.Min(x + size - leftEdge, rightEdge - x) / fade, 0f, 1f);
            }

            if (slot.Kind == DockSlotKind.Separator)
            {
                // 分隔符不是图标：高度固定、跟着放大只变宽一点点，
                // 不然放大时它会跟着图标一起鼓起来，像一根柱子
                DrawSeparator(x, grow, alpha);
                continue;
            }

            if (slot.Icon is not null)
            {
                _quads.Add(new IconQuad
                {
                    Texture = slot.Icon,
                    X = x,
                    Y = _iconBaseline - size,
                    W = size,
                    H = size,
                    Alpha = alpha,
                });
            }

            if (slot.Running) DrawRunDot(slot, x, size, alpha);
        }

        // 右端时钟：贴着玻璃右边缘，固定尺寸，不参与放大。
        // 溢流时先不画 —— 那一块被滚动内容占用，叠上去会和图标撞在一起。
        if (_clockW > 0f && !_overflow)
        {
            float ch = _barH * 0.64f;
            float cx = _panelX + _panelW - _padX - _clockW;
            float cy = _panelY + (_panelH - ch) * 0.5f;

            ID3D11ShaderResourceView? clock = _renderer.GetClock(
                DateTime.Now, (int)MathF.Round(_clockW), (int)MathF.Round(ch));
            if (clock is not null)
            {
                _quads.Add(new IconQuad
                {
                    Texture = clock,
                    X = cx,
                    Y = cy,
                    W = _clockW,
                    H = ch,
                    Alpha = 1f,
                });
            }
        }

        var req = new RenderRequest
        {
            TargetRtv = _comp.Rtv,
            DesktopPassthrough = desktopPassthrough,
            CaptureSrv = desktopPassthrough ? null : capSrv,
            CaptureWidth = srcW,
            CaptureHeight = srcH,
            CaptureOffsetX = _winX - srcX,
            CaptureOffsetY = _winY - srcY,
            WindowW = _winW,
            WindowH = _winH,
            PanelX = _panelX,
            PanelY = _panelY,
            PanelW = _panelW,
            PanelH = _panelH,
            Radius = _radius,
            ShadowExtent = _shadowExtent,
            // 外阴影关掉（用户要求）。窗口四周那圈留白仍然留着 —— 它是图标放大
            // 动画和玻璃抬升的通透空间，不是阴影用的；只是这里不再往里画黑。
            // 着色器里 sd>0 的区域 alpha = 0 * Rim.w = 0，等于完全透明。
            Shadow = 0f,
            RefractBand = g.EnableRefraction ? g.RefractBand * _scale : 0f,
            RefractAmount = g.EnableRefraction ? g.RefractAmount * _scale : 0f,
            Dispersion = g.EnableRefraction ? g.Dispersion : 0f,
            Bulge = g.Bulge,
            RimIntensity = g.RimIntensity,
            RimWidth = g.RimWidth * _scale,
            Gloss = g.Gloss,
            Tint = g.Tint,
            Exposure = g.Exposure,
            Saturation = g.Saturation,
            Contrast = g.Contrast,
            Scatter = g.Scatter,
            Milk = g.Milk,
            Rolloff = g.Rolloff,
            EdgeSpec = g.EdgeSpec,
            Smoke = g.Smoke,
            // 越透越少糊：磨砂感和"看得清"是同一根轴上的两端，
            // 只降 alpha 不降模糊会变成"清晰的壁纸蒙了一层灰"。
            // 0.22 → 0.34：高通透档要能看出背景的结构，不只是平均色。
            BlurStep = g.BlurStep * (1f - 0.34f * EffectiveTransmission(g)) * _scale,
            Transmission = EffectiveTransmission(g),
            BodyR = g.BodyR,
            BodyG = g.BodyG,
            BodyB = g.BodyB,
            BodyAlpha = g.BodyAlpha,
            DpiScale = _scale,
            Time = (float)t0,
            Icons = _quads,
        };

        // 一次性打印实际参数，和 [pill] 那条逐项对账（见 docs/03-验证记录.md）
        if (!_glassParamsLogged)
        {
            _glassParamsLogged = true;
            Logger.Info($"[dock] 玻璃参数 h={_panelH:F1} "
                + $"trans={req.Transmission:F2} body={req.BodyAlpha:F3} milk={req.Milk:F3} scatter={req.Scatter:F2} "
                + $"band={req.RefractBand:F1} amount={req.RefractAmount:F1} rim={req.RimWidth:F1}/{req.RimIntensity:F2} "
                + $"gloss={req.Gloss:F2} edge={req.EdgeSpec:F2} blur={req.BlurStep:F2} "
                + $"radius={req.Radius:F1} shadow={req.Shadow:F2} panel={req.PanelW:F0}x{req.PanelH:F0}");
        }

        _renderer.Render(req);

        // 提交耗时（画到后台缓冲为止）：这才是 Dock 自己的渲染成本。
        double t1 = _clock.Elapsed.TotalSeconds;
        _stats.MarkFrame((t1 - t0) * 1000.0);

        // 一律 Present(0)：把帧交给 DWM 就立刻返回，节拍交给主循环里的 DwmFlush。
        //
        // 试过 Present(1)（让驱动等垂直同步），在这条 composition swapchain 链路上
        // 它并不锁相：帧间隔在 16.4 / 33 两档之间乱跳，滑动时每 2 秒二十多次慢帧。
        // 原因是我们这个窗口既不进 DWM 的普通合成树（WS_EX_NOREDIRECTIONBITMAP +
        // WDA_EXCLUDEFROMCAPTURE），Present 的 vblank 语义在这里不可靠。
        // 交给 DwmFlush 直接对齐"合成完成"这个真实事件才稳。
        _comp.Present(0u);

        double nowAt = _clock.Elapsed.TotalSeconds;
        _presentTotal += (nowAt - t1) * 1000.0;
        _presentCalls++;

        if (_lastFrameAt >= 0 && _frameGaps.Count < 6000)
            _frameGaps.Add((nowAt - _lastFrameAt) * 1000.0);
        _lastFrameAt = nowAt;

        if (_motionPath is not null)
        {
            // t, pointerX, 某个槽位的 left, 它的 scale
            int i = Math.Clamp(_motionSlot, 0, Math.Max(0, _left.Length - 1));
            if (_left.Length > i)
            {
                _motion.Add((float)nowAt);
                _motion.Add(float.IsNaN(_pointerX) ? -1f : _pointerX);
                _motion.Add(_left[i]);
                _motion.Add(_scaleArr[i]);
            }
        }
    }

    /// <summary>
    /// 用 Dock 自己这条玻璃管线烘一张卡片大小的玻璃图（含外阴影），给悬停预览卡片当背景。
    ///
    /// 参数和 <see cref="Render"/> 完全同源，只是把"渲染目标"从交换链换成一张离屏纹理、
    /// 把"窗口"换成卡片矩形 —— 所以卡片上的模糊、奶白、折射、边光、阴影和 Dock 本体的
    /// 玻璃是同一套像素，不是调参凑出来的近似。
    /// 频率：每次卡片浮出烘一次，一次 GPU 渲染 + 一次回读，可以忽略。
    /// </summary>
    public System.Drawing.Bitmap? RenderGlassCard(int screenX, int screenY, int w, int h,
        float radius, float shadow)
    {
        if (_ctx is null || _renderer is null || w < 48 || h < 48) return null;

        GlassConfig g = _config.Glass;
        bool frozenBg = _sampler is { FrozenActive: true };
        bool hasCapture = !frozenBg && _backdropLive
                          && _sampler is { HasFrame: true, Srv: not null, RegionW: > 0 }
                          && _sampler.IsLive;
        bool useFrozen = !hasCapture && frozenBg && _sampler!.FrozenSrv is not null;
        bool useFallback = !hasCapture && !useFrozen && _fallback is { Ready: true };

        ID3D11ShaderResourceView? capSrv = null;
        int srcW = w, srcH = h, srcX = 0, srcY = 0;
        if (hasCapture)
        {
            capSrv = _sampler!.Srv;
            srcW = _sampler.RegionW;
            srcH = _sampler.RegionH;
            srcX = _sampler.RegionX;
            srcY = _sampler.RegionY;
        }
        else if (useFrozen)
        {
            capSrv = _sampler!.FrozenSrv;
            srcW = _sampler.FrozenW;
            srcH = _sampler.FrozenH;
            srcX = _sampler.FrozenX;
            srcY = _sampler.FrozenY;
        }
        else if (useFallback)
        {
            capSrv = _fallback!.Srv;
            srcW = _fallback.W;
            srcH = _fallback.H;
            srcX = _fallback.X;
            srcY = _fallback.Y;
        }

        // 没有背景样本就别烘这张卡：烘出来的一定是那块中性灰。
        // 返回 null，调用方会退回"纯色底"，总比一张冷灰蓝的假玻璃好。
        if (capSrv is null) return null;


        var req = new RenderRequest
        {
            CaptureSrv = capSrv,
            CaptureWidth = srcW,
            CaptureHeight = srcH,
            // 玻璃采样的是"整屏（或采样区）里对应位置"，偏移必须算成卡片自己的屏幕坐标
            CaptureOffsetX = screenX - srcX,
            CaptureOffsetY = screenY - srcY,
            WindowW = w,
            WindowH = h,
            PanelX = shadow,
            PanelY = shadow,
            PanelW = w - shadow * 2f,
            PanelH = h - shadow * 2f,
            Radius = radius,
            ShadowExtent = shadow,
            Shadow = g.Shadow,
            RefractBand = g.EnableRefraction ? g.RefractBand * _scale : 0f,
            RefractAmount = g.EnableRefraction ? g.RefractAmount * _scale : 0f,
            Dispersion = g.EnableRefraction ? g.Dispersion : 0f,
            Bulge = g.Bulge,
            RimIntensity = g.RimIntensity,
            RimWidth = g.RimWidth * _scale,
            Gloss = g.Gloss,
            Tint = g.Tint,
            Exposure = g.Exposure,
            Saturation = g.Saturation,
            Contrast = g.Contrast,
            Scatter = g.Scatter,
            Milk = g.Milk,
            Rolloff = g.Rolloff,
            EdgeSpec = g.EdgeSpec,
            Smoke = g.Smoke,
            BlurStep = g.BlurStep * (1f - 0.34f * EffectiveTransmission(g)) * _scale,
            Transmission = EffectiveTransmission(g),
            BodyR = g.BodyR,
            BodyG = g.BodyG,
            BodyB = g.BodyB,
            BodyAlpha = g.BodyAlpha,
            DpiScale = _scale,
            Time = (float)_clock.Elapsed.TotalSeconds,
            Icons = CardGlassBaker.NoIcons,
        };

        return CardGlassBaker.Bake(_ctx, _renderer, req);
    }

    /// <summary>把逐帧位移诊断写盘（退出时一次写完，不干扰帧节拍）。</summary>
    private void WriteMotionLog()
    {
        if (_motionPath is null || _motion.Count == 0) return;
        try
        {
            var sb = new System.Text.StringBuilder(_motion.Count * 8);
            sb.Append("t,pointer,left,scale\n");
            for (int k = 0; k + 3 < _motion.Count; k += 4)
            {
                sb.Append(_motion[k].ToString("F4")).Append(',')
                  .Append(_motion[k + 1].ToString("F2")).Append(',')
                  .Append(_motion[k + 2].ToString("F3")).Append(',')
                  .Append(_motion[k + 3].ToString("F5")).Append('\n');
            }
            File.WriteAllText(_motionPath, sb.ToString(), System.Text.Encoding.UTF8);
            Logger.Info($"[motion] 已写出 {_motion.Count / 4} 帧位移记录 -> {_motionPath}");
        }
        catch (Exception ex)
        {
            Logger.Warn("位移记录写盘失败: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 消息

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_LIQUIDGLASS_OPEN_SETTINGS:
                Logger.Info("收到「打开设置」请求（来自第二个进程）");
                OpenSettings();
                return IntPtr.Zero;

            case Native.WM_LIQUIDGLASS_OPEN_LAUNCHPAD:
                Logger.Info("收到「打开启动台」请求（来自第二个进程）");
                OpenLaunchpad();
                return IntPtr.Zero;

            case Native.WM_ERASEBKGND:
                return 1;

            case Native.WM_NCHITTEST:
            {
                int sx = unchecked((short)(long)lParam);
                int sy = unchecked((short)((long)lParam >> 16));
                Native.POINT pt = new() { X = sx, Y = sy };
                // 只有玻璃面板和图标区域接收鼠标，其余全部穿透到桌面
                return HitTestScreen(pt) ? 1 : Native.HTTRANSPARENT;
            }

            case Native.WM_MOUSEMOVE:
            {
                int x = unchecked((short)(long)lParam);
                int y = unchecked((short)((long)lParam >> 16));
                _pointerX = x;
                _pointerReason = "WM_MOUSEMOVE";
                PointerInside = true;
                _hoverIndex = HitTestClient(x, y);
                TrackLeave();
                _dirty = true;
                return IntPtr.Zero;
            }

            // 滚轮横向滚动：溢流时最自然的补充分配方式（触摸板双指横滑就是它）。
            case (uint)0x020A: // WM_MOUSEWHEEL
            {
                if (!_overflow || _scrollMax <= 0.5f) return IntPtr.Zero;
                int delta = unchecked((short)((long)wParam >> 16));   // 一格 120
                // 向下滚 = 往右看。每格一个图标宽，手感跟列表滚动一致。
                _scrollTarget = Math.Clamp(
                    _scrollTarget - delta / 120f * _iconSize, 0f, _scrollMax);
                _dirty = true;
                return IntPtr.Zero;
            }

            case Native.WM_LBUTTONDOWN:
            {
                int x = unchecked((short)(long)lParam);
                int y = unchecked((short)((long)lParam >> 16));
                ActivateSlot(HitTestClient(x, y));
                return IntPtr.Zero;
            }

            // 右键 -> 弹出菜单。菜单项按点的位置决定：点在图标上和点在空白处不是一回事。
            case Native.WM_RBUTTONUP:
            {
                int x = unchecked((short)(long)lParam);
                int y = unchecked((short)((long)lParam >> 16));
                ShowContextMenu(HitTestClient(x, y));
                return IntPtr.Zero;
            }

            // 外部（--restore-taskbar）要求交出任务栏：立刻恢复并停止守护，
            // 但 Dock 本身继续运行，不连带把用户正在用的东西关掉。
            case Native.WM_APP_RELEASE_TASKBAR:
                if (_hideTaskbar)
                {
                    _hideTaskbar = false;
                    int shown = TaskbarGuard.Show();
                    Logger.Info($"收到恢复请求：已恢复 {shown} 个任务栏窗口，停止托管");
                }

                // 用户明确要回任务栏，那就别再自作主张藏它。
                // 必须落盘：只改内存的话，下次启动配置又把任务栏藏回去。
                _config.HideTaskbar = false;
                SaveConfig();
                return IntPtr.Zero;

            // 外部（--switch-taskbar）要求切换任务栏显隐：Dock 自己继续跑，
            // 这样"藏任务栏 / 放任务栏"和"Dock 开 / 关"就解耦了。
            case Native.WM_APP_TOGGLE_TASKBAR:
                ToggleTaskbar();
                return IntPtr.Zero;

            // 快捷键（鼠标钩子 / 键盘钩子）投过来的显隐请求。钩子不能直接动窗口，只能绕这一圈。
            case Native.WM_LIQUIDGLASS_TOGGLE_DOCK:
                ToggleDockByShortcut();
                return IntPtr.Zero;

            case (uint)0x02A3: // WM_MOUSELEAVE                // TME_LEAVE 是一次性的：系统发出 WM_MOUSELEAVE 后会自动注销这次注册。
                // 不在这里把标志复位，下一次悬停就不会重新注册，之后永远收不到离开消息
                // ——鼠标离开后图标一直保持放大的根因就在这一行。
                _trackingLeave = false;
                _pointerReason = "WM_MOUSELEAVE";
                PointerInside = false;
                _hoverIndex = -1;
                _pointerX = float.NaN;
                _dirty = true;
                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
                // 分辨率/显示器变化：重新量矩形，把 Dock 挪回底边。
                // 顺带把采集会话重建掉 —— 分辨率一变，旧的 DuplicateOutput
                // 给的纹理尺寸就对不上了，继续用只会画出拉伸的错帧。
                RelayoutAll();
                _sampler?.ForceRegionRebuild();
                _sampler?.TryInit();
                _dirty = true;
                return IntPtr.Zero;

            // 睡眠 / 唤醒。
            //
            // 这一条是「合上盖子耗电极快」的对症药：进程在 Modern Standby 下不会被挂起，
            // 只要定时器还在走，每一拍都会把 CPU / GPU 从低功耗里叫醒一次去做一次 Present。
            // 收到挂起通知后主循环切进无超时等待（见 Run 里的分支），
            // 睡眠期间这个进程一次都不跑，醒来再重建采集会话。
            case Native.WM_POWERBROADCAST:
            {
                int evt = (int)(long)wParam;
                if (evt == Native.PBT_APMSUSPEND)
                {
                    if (!_suspended)
                    {
                        _suspended = true;
                        _pointerX = float.NaN;
                        PointerInside = false;
                        _hoverIndex = -1;
                        HidePreview();
                        // 睡前把当前桌面冻起来：醒来那一瞬 DWM 还没合成完，
                        // 采集和抓屏拿到的都不可信，只有这张是真的。
                        _sampler?.Freeze();
                        Logger.Info("系统挂起：渲染循环停摆（睡眠期间不占用 CPU/GPU）");
                    }
                }
                else if (evt == Native.PBT_APMRESUMEAUTOMATIC || evt == Native.PBT_APMRESUMESUSPEND)
                {
                    if (_suspended)
                    {
                        _suspended = false;
                        // 不要在这里清 _sessionLocked：合盖前如果锁了屏，解锁消息会在恢复后
                        // 由消息泵补发；提前清掉会让"还停在锁屏"的这段时间重新开始重绘。
                        //
                        // 睡一觉回来，采集会话一定失效了，但**不能**立刻抢新会话：
                        // 刚唤醒那几百毫秒 DWM 还没把桌面合成出来，这时抢到的是一整块黑帧。
                        // 所以先扔掉旧会话，0.4 秒后重建；这期间画冻结帧。
                        _sampler?.Reset();
                        _sampler?.ArmFreezeTimeout(FreezeTimeoutMs);
                        _samplerRebuildAt = _clock.Elapsed.TotalSeconds + 0.4;
                        _desktopPassthroughUntil = _samplerRebuildAt + 0.30;
                        _desktopBoostUntil = _desktopPassthroughUntil;
                        _dirty = true;
                        Logger.Info("系统唤醒：渲染循环恢复，画冻结帧，0.4s 后重建采集会话");
                    }
                }

                return 1;
            }

            // 锁屏 / 解锁。
            //
            // 锁屏时用户桌面在安全桌面上，DXGI Desktop Duplication 一律返回
            // E_ACCESSDENIED —— 不是"偶发失败"，是**必然失败**，重试多少次都没用。
            // 过去没有这条消息，只能靠 1.5 秒一次的退避去撞解锁时机；撞不上就一直
            // 停在降级色块上。日志里 07:45 到 09:13 那两个半小时的"采集会话丢失"
            // 就是这么来的：整段时间玻璃都是一块静止的蓝灰色板子。
            case Native.WM_WTSSESSION_CHANGE:
            {
                int evt = (int)(long)wParam;
                if (evt == Native.WTS_SESSION_LOCK)
                {
                    if (!_sessionLocked)
                    {
                        _sessionLocked = true;
                        // 锁屏期间的重试没有任何意义，把间隔放到 5 秒，少折腾 GPU。
                        if (_sampler is not null) _sampler.RetryIntervalMs = 5000;
                        // 此刻 _region 里正是用户锁屏前的桌面，趁现在拷一份，
                        // 解锁后无论采集会话怎么重建，都有干净的帧可画。
                        _sampler?.Freeze();
                        Logger.Info("会话锁定：桌面采集暂停重试（锁屏期间 Duplication 必然拒绝）");
                    }
                }
                else if (evt == Native.WTS_SESSION_UNLOCK)
                {
                    _sessionLocked = false;
                    if (_sampler is not null) _sampler.RetryIntervalMs = 1500;

                    // 解锁瞬间屏幕上是锁屏淡出的画面，采集/抓屏拿到的都还是锁屏色。
                    // 这期间一直画冻结帧，直到真的采到活桌面（判据在 SampleBackground），
                    // 不按时间猜 —— 解锁动画比 0.9 秒长的时候，时间一到就会抓回灰/蓝色。
                    _sampler?.Reset();
                    _sampler?.ArmFreezeTimeout(FreezeTimeoutMs);
                    _samplerRebuildAt = _clock.Elapsed.TotalSeconds + 0.4;
                    _desktopPassthroughUntil = _samplerRebuildAt + 0.30;
                    _desktopBoostUntil = _desktopPassthroughUntil;
                    // 解锁后 DWM 可能丢掉过合成内容，马上用冻结帧重画一次
                    _dirty = true;
                    Logger.Info("会话解锁：画冻结帧，0.4s 后重建采集，采到活桌面才解冻");
                }

                return IntPtr.Zero;
            }

            case Native.WM_SETTINGCHANGE:
                _dirty = true;
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                if (_powerNotify != IntPtr.Zero)
                {
                    Native.UnregisterSuspendResumeNotification(_powerNotify);
                    _powerNotify = IntPtr.Zero;
                }
                Native.WTSUnRegisterSessionNotification(hwnd);
                _quit = true;
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public int cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT tme);

    private bool _trackingLeave;

    private void TrackLeave()
    {
        if (_trackingLeave) return;
        var tme = new TRACKMOUSEEVENT
        {
            cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(),
            dwFlags = 0x00000002, // TME_LEAVE
            hwndTrack = _hwnd,
        };
        _trackingLeave = TrackMouseEvent(ref tme);
    }

    private bool HitTestScreen(Native.POINT screen)
    {
        int cx = screen.X - _winX;
        int cy = screen.Y - _winY;
        return HitTestClient(cx, cy) >= 0 || InPanel(cx, cy);
    }

    /// <summary>
    /// 用物理光标位置纠正悬停状态。只处理"已经在悬停"的情形：
    /// 进入靠 WM_MOUSEMOVE 触发（窗口空闲时不重绘，必须由消息唤醒），
    /// 退出不能只靠消息（TME_LEAVE 一次性、可能丢），所以每帧对一次真值。
    /// </summary>
    private void SyncPointerWithCursor()
    {
        if (!Native.GetCursorPos(out Native.POINT cur)) return;

        if (!HitTestScreen(cur))
        {
            _pointerReason = $"光标兜底 cursor=({cur.X},{cur.Y})";
            PointerInside = false;
            _hoverIndex = -1;
            _pointerX = float.NaN;
            _trackingLeave = false;
            _dirty = true;
            return;
        }

        // 还停在面板上：补上可能漏掉的横向位置（光标不动就不产生重绘）
        float px = cur.X - _winX;
        if (!PointerInside)
        {
            // 启动前光标就在这上面 → 从来没有"进入"事件。这里补上。
            PointerInside = true;
            _pointerReason = "光标兜底（首次进入）";
            _dirty = true;
        }

        if (float.IsNaN(_pointerX) || MathF.Abs(px - _pointerX) > 0.25f)
        {
            _pointerX = px;
            _hoverIndex = HitTestClient(px, cur.Y - _winY);
            _dirty = true;
        }
    }

    private bool InPanel(float x, float y)
    {
        // 命中区往上多给一段：图标放大后是以底边为基准往上长的，
        // 最高会探出玻璃上沿 (magnification-1)*iconSize。只把命中区框在玻璃矩形里，
        // 鼠标一抬到放大图标的上半部分就会判成"离开" —— 图标瞬间缩回去，再压回来又长大，
        // 看起来就是"一跳一跳"。这段留白必须算进来。
        //
        // 横向跟着**当前**玻璃矩形走，不按最大态。玻璃会随内容伸缩，
        // 如果命中区永远按最大宽度算，鼠标离玻璃还有 64px 就会触发放大，
        // 那是一个看不见的"空气热区"。
        //
        // 跟着动不会自激：玻璃左边缘是常量，右侧只会在"图标正在变小时"收缩，
        // 而图标变小本身就是离开的后果 —— 会一路收敛到静止，不会来回弹。
        //
        // 上边界有两种取法（用户实测反馈：WorkBuddy 的输入框工具行正好压着
        // 玻璃上沿，"默认权限/快速"那些按钮永远点不到）：
        //
        // · 未悬停 → 只认**图标条带**（图标顶再放一点余量）。玻璃上半部是空白，
        //   整块拦的话压在它下面的窗口按钮就永远点不到 —— 而那段空白对 Dock
        //   毫无用处（没有图标，也不需要 hover）。条带必须是**整条**而不是
        //   逐图标的框：横向滑动穿过图标之间的缝隙时不能闪断，否则图标会
        //   "放大→判离开→缩小"地抖。
        //
        // · 已悬停 → 认整个玻璃 + loft。图标放大后顶部探出条带，
        //   鼠标跟上去不能被判成"离开"，否则又是一跳一跳。
        float slack = 6f * _scale;
        float stripTop = _iconBaseline - _iconSize - slack;
        float top = PointerInside
            ? _panelY - ((_config.Metrics.Magnification - 1f) * _iconSize + 4f * _scale)
            : stripTop;
        return x >= _panelX - slack && x <= _panelX + _panelW + slack &&
               y >= top && y <= _panelY + _panelH + 2;
    }

    private int HitTestClient(float x, float y)
    {
        for (int i = 0; i < _slots.Count && i < _scaleArr.Length; i++)
        {
            // 分隔符不参与命中：点它等于点空白
            if (_slots[i].Kind == DockSlotKind.Separator) continue;

            float size = _iconSize * _scaleArr[i];
            float ix = _originX + _left[i] - _scrollOffset;
            // 已经滑出玻璃的图标不可点：它们要么被淡出到看不见，要么根本没画出来，
            // 还能点中的话就变成"点空白启动了某个应用"。
            if (_overflow && (ix + size <= _panelX + 1f || ix >= _panelX + _panelW - 1f)) continue;
            float iy = _iconBaseline - size;
            if (x >= ix - _gap * 0.35f && x <= ix + size + _gap * 0.35f &&
                y >= iy - 6 * _scale && y <= iy + size + 6 * _scale)
                return i;
        }

        return -1;
    }

    // ---------------------------------------------------------------- 绘制细节

    /// <summary>
    /// 启动台与开始按钮之间的固定竖线。用 1x1 白纹理拉成细长矩形，
    /// 走的是同一条图标管线，不需要额外的着色器或 draw call。
    /// </summary>
    private void DrawSeparator(float x, float grow, float alpha = 1f)
    {
        if (_sepTex is null) return;

        float slotW = _sepWidth * grow;
        float w = MathF.Max(1.1f * _scale, 1f);
        float h = _iconSize * 0.62f;

        _quads.Add(new IconQuad
        {
            Texture = _sepTex,
            X = x + (slotW - w) * 0.5f,
            Y = _iconBaseline - _iconSize * 0.5f - h * 0.5f,
            W = w,
            H = h,
            Alpha = 0.26f * alpha,
        });
    }

    /// <summary>
    /// 运行指示点。前台应用亮、后台应用暗 —— 这是"哪些还开着"的唯一线索，
    /// 没有它整个运行区看起来就只是又一排启动图标。
    /// </summary>
    private void DrawRunDot(DockSlot slot, float x, float size, float alpha = 1f)
    {
        if (_dotTex is null) return;

        // 取整对齐：纹理和屏幕像素一比一才不会被插值糊掉
        float dot = _dotPx;
        _quads.Add(new IconQuad
        {
            Texture = _dotTex,
            X = MathF.Round(x + (size - dot) * 0.5f),
            Y = MathF.Round(_iconBaseline + _dotGap),
            W = dot,
            H = dot,
            Alpha = (slot.HasForeground ? 0.90f : 0.38f) * alpha,
        });
    }

    // ---------------------------------------------------------------- 悬停预览

    /// <summary>
    /// 现在需要"盯着光标"吗？—— 卡片开着（随时可能被移开，要当场收起），
    /// 或者指针正停在 Dock 上、2 秒倒计时正在走（到点得准时弹出）。
    /// 这两种状态下主循环按 PointerPollCap 轮询，其余时间仍旧 4Hz。
    /// </summary>
    private bool NeedsPointerPolling()
        => (_preview is not null && _preview.Visible)
           || (PointerInside && _config.HoverPreviewDelayMs > 0);

    /// <summary>
    /// 悬停到时间就浮出窗口预览卡片。
    ///
    /// 这里**没有**用 WM_MOUSELEAVE 决定收不收：鼠标要从图标移到卡片上才点得到
    /// 右上角的 ×，而那一移就出 Dock 了，靠离开消息会当场把卡片收掉，按钮永远点不着。
    /// 改成每帧读一次物理光标，判断"在图标上 / 在卡片上"，两者都不是就立刻收起 ——
    /// 不留宽限期，鼠标一离开卡片当场消失（留 0.5 秒反而让用户觉得"迟钝、甩不掉"）。
    ///
    /// 但"立刻"不能是"字面意义上的零宽限"：卡片下沿离放大后的图标上沿还有 6~16px，
    /// 鼠标往上走的那一两帧会掉进这条缝里，判定不到任何一边，卡片当场消失 ——
    /// × 就永远点不着。所以把"图标那一列"从卡片下沿一直连到图标底边，整条算保留区。
    /// </summary>
    private void UpdateHoverPreview(double now)
    {
        if (_config.HoverPreviewDelayMs <= 0)
        {
            HidePreview();
            return;
        }

        int idx = PointerInside ? _hoverIndex : -1;
        if (idx != _hoverSlot)
        {
            _hoverSlot = idx;
            _hoverSlotSince = now;
        }

        bool haveCursor = Native.GetCursorPos(out Native.POINT cursor);

        if (_preview is not null && _preview.Visible)
        {
            if (haveCursor && PreviewKeepsCursor(cursor))
            {
                if (now >= _previewRefreshAt)
                {
                    _previewRefreshAt = now + 1.0;
                    _preview.RefreshThumbnail();
                }

                return;
            }

            HidePreview();
            return;
        }

        if (idx < 0 || idx >= _slots.Count || idx >= _scaleArr.Length) return;

        DockSlot slot = _slots[idx];
        if (!slot.Running || slot.Hwnd == IntPtr.Zero) return;
        if (now - _hoverSlotSince < _config.HoverPreviewDelayMs / 1000.0) return;

        ShowPreview(idx, slot);
    }

    /// <summary>
    /// 光标是否还在"这张卡片的地盘"上。三块：卡片本身、预览对应的那个图标、
    /// 以及两者之间那条竖直缝（鼠标从图标走向卡片时只经过这里）。
    /// </summary>
    private bool PreviewKeepsCursor(Native.POINT cursor)
    {
        if (_preview is null) return false;
        if (_preview.ContainsCursor(cursor.X, cursor.Y)) return true;

        // 图标位置按当前实际尺寸算，不用命中测试的结果 ——
        // 卡片开着时鼠标已经不在 Dock 上，_hoverIndex 是 -1，靠不住。
        if (_previewSlot < 0 || _previewSlot >= _slots.Count ||
            _previewSlot >= _scaleArr.Length || _previewSlot >= _left.Length) return false;

        float size = _iconSize * _scaleArr[_previewSlot];
        float iconLeft = _winX + _originX + _left[_previewSlot] - _scrollOffset;
        float iconTop = _winY + _iconBaseline - size;
        float iconBottom = _winY + _iconBaseline;

        // 左右各让 6px：图标是圆角方形，四角那点空白不该被算成"离开了"
        float pad = 6f * _scale;

        // 图标那一列
        if (cursor.X >= iconLeft - pad && cursor.X < iconLeft + size + pad &&
            cursor.Y >= iconTop - pad && cursor.Y < iconBottom + pad) return true;

        // 图标上沿到卡片下沿那条缝：左右放宽到整个卡片宽度。
        //
        // 卡片是按"放大后的图标中心"摆的，指针一离开 Dock 图标就缩回原位、中心横移
        // 三十来像素 —— 只认图标那一列的话，顺着卡片中线往下走会当场掉进缝里，
        // 卡片在手指还压着的时候消失。缝的下沿多给 12px 盖住图标头顶那一小截。
        if (_preview.TryGetPanelBounds(out int panelX, out int panelY, out int panelW, out int panelH))
        {
            float gapTop = panelY + panelH;
            float gapBottom = iconTop + 12f * _scale;

            if (cursor.Y >= gapTop - pad && cursor.Y < gapBottom &&
                cursor.X >= panelX - pad && cursor.X < panelX + panelW + pad) return true;
        }

        return false;
    }

    /// <summary>
    /// 摆好右端的系统状态胶囊（电量 / 网络 / 时间）。
    ///
    /// 位置跟着**玻璃右边缘**走：指针压上 Dock 时玻璃会向右长开（弹簧驱动），
    /// 胶囊就顺势被推开一点 —— 比钉死在某个屏幕坐标上自然，也永远不会被玻璃压住。
    /// 但移动过程中不重烘玻璃底：一次烘图要 5~9ms，每帧都做会吃掉整帧预算。
    /// 停稳 250ms 之后再补一次，让折射重新对准背后的桌面。
    /// </summary>
    private void UpdateStatusPill(double now)
    {
        // 胶囊已经并进 Dock 右端的状态图标（DockSlotKind.Status），这里不再开窗口。
        //
        // 撤掉它的收益不只是"少一块玻璃"：胶囊要单独摆位、单独采背景、Dock 展开时
        // 要跟着让位、还要自己探底色换墨 —— 全是"第二块玻璃"才有的麻烦。并成一颗
        // 图标之后，它天然跟着 Dock 的弹簧、放大和同一份玻璃走，也就不会再出现
        // "两块玻璃颜色对不上"这种问题。
        //
        // 回滚方式：删掉下面这行 return，再让 DockSlotBuilder 不生成 Status 槽。
        // StatusPill.cs 一直留着，没删。
        _statusPill?.Hide();
    }

    /// <summary>
    /// 打开系统自带的快速设置面板（Win + A）。
    ///
    /// 为什么不自己做一套开关界面：网络切换、蓝牙、亮度、音量这些都要跟系统服务打交道，
    /// 自己实现等于把 Windows 的活重写一遍，还得跟着版本更新维护。
    /// 原生面板一步到位，而且用户本来就熟悉那个界面。
    ///
    /// 已知副作用（2026-10-29 定案，别再当 bug 重查）：
    /// 键盘唤出时 WinUI 会给第一个磁贴（Wi-Fi）画一圈键盘焦点框（2px 黑 + 1px 白）。
    /// 已逐一验证无效：打开后移动光标、悬停面板空白、点击面板空白、打开前预注入指针。
    /// 焦点框只认"指针按下"造成的输入模式切换，而面板里能被按下的只有磁贴/滑块/齿轮，
    /// 按哪个都会改状态或跳页 —— 没有安全位置。用户已知情并选择接受现状。
    /// </summary>
    private static void OpenQuickSettings()
    {
        const byte VkA = 0x41;

        uint scanWin = Native.MapVirtualKeyW(Native.VK_LWIN, 0);
        uint scanA = Native.MapVirtualKeyW(VkA, 0);

        Native.keybd_event(Native.VK_LWIN, (byte)scanWin, 0, UIntPtr.Zero);
        Thread.Sleep(20);
        Native.keybd_event(VkA, (byte)scanA, 0, UIntPtr.Zero);
        Thread.Sleep(20);
        Native.keybd_event(VkA, (byte)scanA, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(20);
        Native.keybd_event(Native.VK_LWIN, (byte)scanWin, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);

        Logger.Info("状态胶囊：已发送 Win+A，打开系统快速设置");
    }

    private void ShowPreview(int index, DockSlot slot)
    {
        if (_preview is null)
        {
            _preview = new WindowPreviewCard(_scale);
            _preview.GlassProvider = RenderGlassCard;
            _preview.ActivateRequested = ActivateWindowHandle;
            _preview.CloseRequested = ClosePreviewedApp;
            _preview.PinToggleRequested = () =>
            {
                if (_previewSlot >= 0 && _previewSlot < _slots.Count)
                    TogglePin(_slots[_previewSlot]);
            };
        }

        float size = _iconSize * _scaleArr[index];
        float left = _originX + _left[index] - _scrollOffset;

        // 卡片下沿要落在**放大后图标的上沿之上**，不是玻璃上沿之上。
        // 悬停时图标本来就会顶出玻璃（这是刻意做的效果），按玻璃上沿摆卡片
        // 正好把那个图标盖住一半 —— 用户看到的就是"卡片挡住了图标"。
        float iconTop = _winY + _iconBaseline - size;
        int bottomY = (int)MathF.Round(iconTop - 16f * _scale);

        _previewSlot = index;
        _previewRefreshAt = _clock.Elapsed.TotalSeconds + 1.0;

        _preview.Present(
            slot.Hwnd,
            slot.Name,
            slot.IconPath,
            slot.Kind == DockSlotKind.Pinned,
            (int)MathF.Round(_winX + left + size * 0.5f),
            bottomY);
    }

    private void HidePreview()
    {
        if (_previewSlot >= 0) _previewSlot = -1;
        _preview?.Hide();
    }

    /// <summary>关掉预览里那个应用的所有窗口。发 WM_CLOSE，不杀进程 ——
    /// 有未保存内容的程序会自己弹保存框，直接 TerminateProcess 是数据丢失。</summary>
    private void ClosePreviewedApp()
    {
        if (_previewSlot < 0 || _previewSlot >= _slots.Count) return;

        DockSlot slot = _slots[_previewSlot];
        if (slot.App is null) return;

        IntPtr[] windows = slot.App.Windows.ToArray();
        Logger.Info($"[preview] 关闭 {slot.Name} 的 {windows.Length} 个窗口");
        foreach (IntPtr hwnd in windows)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) continue;
            Native.PostMessageW(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>把某个窗口切到前台（预览卡片点缩略图走这条）。</summary>
    private void ActivateWindowHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

        if (WindowActivator.IsForeground(hwnd))
        {
            WindowActivator.Minimize(hwnd);
            return;
        }

        WindowActivator.Activate(hwnd);
    }


    /// <summary>
    /// 点击槽位。行为对齐 Windows 任务栏：
    /// 没运行就启动；运行中但不在前台就切过去；已经在前台就最小化。
    /// </summary>
    private void ActivateSlot(int index)
    {
        if (index < 0 || index >= _slots.Count) return;
        DockSlot slot = _slots[index];
        if (slot.Kind == DockSlotKind.Separator) return;

        HidePreview();

        // 启动台开着的时候点 Dock 上任何别的图标 = 用户要离开面板。
        //
        // 这一步必须在拦截点之外做：Dock 是 WS_EX_NOACTIVATE，点它不会把启动台
        // 踩下前台，所以启动台那边的"失去前台自动收起"收不到通知。而面板是全屏
        // TOPMOST 的玻璃，不主动关它，它会一直盖在刚激活的窗口上面；同时 Dock
        // 的背景还停在打开面板时冻住的那一帧 —— 用户看到的就是"Dock 和胶囊
        // 一直保留着之前那张壁纸，背景再也不跟着变"。
        if (slot.Kind != DockSlotKind.Launchpad) CloseLaunchpad();

        // 开始按钮 = 按 Win 键，弹系统开始菜单。
        //
        // 之前这里接到 OpenLaunchpad()，理由是"系统开始菜单是 shell 的 XAML 岛窗口，
        // 改不成我们的玻璃"。但那让用户失去了开始菜单本身：任务栏被藏起来了，
        // 点开始却弹出桌面条目，找不到设置、搜索和"所有应用"。
        // 启动台有自己的入口（最左边那颗 3×3 图标），两块面板各管各的。
        if (slot.Kind == DockSlotKind.Start)
        {
            PressStartKey();
            return;
        }

        if (slot.Kind == DockSlotKind.Launchpad)
        {
            OpenLaunchpad();
            return;
        }

        // 状态位 = 原来的胶囊：点一下开系统快速设置（Win+A）。
        // 胶囊撤掉之后这里是快速设置唯一的入口，所以不能顺手删。
        if (slot.Kind == DockSlotKind.Status)
        {
            Logger.Info("点击 快速设置：发 Win+A");
            OpenQuickSettings();
            return;
        }

        if (slot.Kind == DockSlotKind.Explorer)
        {
            Logger.Info("点击 资源管理器：打开文件资源管理器");
            Launcher.Launch(DockSlotBuilder.ExplorerPath, null);
            return;
        }

        if (slot.Running && slot.Hwnd != IntPtr.Zero)
        {
            if (WindowActivator.IsForeground(slot.Hwnd))
            {
                IntPtr fg = Native.GetForegroundWindow();
                WindowActivator.Minimize(fg == IntPtr.Zero ? slot.Hwnd : fg);
                Logger.Info($"点击 {slot.Name}：已在前台，最小化");
                return;
            }

            bool ok = WindowActivator.Activate(slot.Hwnd);
            Logger.Info(ok
                ? $"点击 {slot.Name}：已切到前台"
                : $"点击 {slot.Name}：激活失败（系统拒绝前台切换）");
            return;
        }

        Logger.Info($"点击 {slot.Name}：启动 {slot.LaunchTarget}");
        Launcher.Launch(slot.LaunchTarget, slot.Args);
    }

    /// <summary>
    /// 模拟按一下 Win 键，唤出开始菜单。
    /// 用 keybd_event 而不是 SendInput：这里只是按一个修饰键，不需要注入
    /// 鼠标/键盘的结构体数组，系统对它的处理也更宽容（不会被 UIPI 拦）。
    /// </summary>
    private void PressStartKey()
    {
        Native.keybd_event(Native.VK_LWIN, 0, 0, UIntPtr.Zero);
        Thread.Sleep(28);                     // 按下与抬起之间留一点时间，太急壳层会当成误触忽略
        Native.keybd_event(Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Logger.Info("点击开始：已发送 Win 键");
    }

    // ---------------------------------------------------------------- 右键菜单

    /// <summary>
    /// 原生弹出菜单。为什么不自绘：Dock 窗口只有一条窄带高度，菜单必须画到窗口外，
    /// 等于要临时扩窗口、重做命中测试和"点外面关闭"的逻辑；而菜单本身是系统组件，
    /// 自己画既费力又做不出深浅色跟随。这里只借它的骨头，交互全走回调。
    /// </summary>
    private void ShowContextMenu(int slotIndex)
    {
        if (!Native.GetCursorPos(out Native.POINT cursor)) return;

        // 弹菜单就把预览收掉：两者叠在同一块区域，视觉上会打架
        HidePreview();

        IntPtr menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var actions = new List<(uint Id, Action Run)>(8);
        uint nextId = 1;

        void Item(string text, Action run)
        {
            uint menuId = nextId++;
            Native.AppendMenuW(menu, Native.MF_STRING, (IntPtr)menuId, text);
            actions.Add((menuId, run));
        }

        void Separator() => Native.AppendMenuW(menu, Native.MF_SEPARATOR, IntPtr.Zero, null);

        DockSlot? slot = null;
        if (slotIndex >= 0 && slotIndex < _slots.Count &&
            _slots[slotIndex].Kind != DockSlotKind.Separator)
            slot = _slots[slotIndex];

        if (slot is not null)
        {
            string title = slot.Name.Length > 20 ? slot.Name[..20] + "…" : slot.Name;

            if (slot.Running && slot.Hwnd != IntPtr.Zero)
            {
                Item($"关闭「{title}」的窗口", () => CloseSlotWindow(slot));

                // 对齐 Windows 11 任务栏的「结束任务」：关窗口关不掉的后台程序，
                // 只有结束进程这一条路。放在关闭窗口下面并加了括号说明，
                // 因为它会丢未保存的数据 —— 不做确认框，但必须让用户看得见后果。
                Item($"结束任务（强制结束「{title}」进程）", () => KillSlotProcess(slot));
            }

            // 开始按钮不属于固定区，没有"固定/取消固定"这回事
            if (slot.Kind is DockSlotKind.Pinned or DockSlotKind.Running)
                Item(slot.Kind == DockSlotKind.Pinned ? "从 Dock 取消固定" : "固定到 Dock",
                    () => TogglePin(slot));

            // 状态位是唯一能开到快速设置的地方，右键也给一条 —— 免得用户只能靠点图标去猜
            if (slot.Kind == DockSlotKind.Status)
                Item("打开快速设置（Win+A）", OpenQuickSettings);

            Separator();
        }

        Item(_hideTaskbar ? "显示 Windows 任务栏" : "隐藏 Windows 任务栏", ToggleTaskbar);

        Item("设置…", OpenSettings);

        Item("暂时让开（5 秒后自动回来）", DuckDock);

        // 这里原来还有四项：「打开启动台」「更换开始按钮图标…」「开始按钮用我的账户头像」
        // 「开始按钮恢复默认图标」，以及一项手动的「重新读取任务栏固定项」。
        //
        // 全部按用户要求撤掉：菜单开出来一长条，常用的（关闭窗口 / 结束任务 / 设置 / 退出）
        // 反而要在一堆一次性配置项里找。固定项本来就每 3 秒自动对账 Windows 任务栏，
        // 手动那一项纯属冗余。功能代码都还在（--launchpad 启动参数、开始图标仍走配置），
        // 只是不再占菜单位置。
        Separator();
        Item("退出 Dock", () =>
        {
            Logger.Info("菜单：退出 Dock");
            _quit = true;
        });

        // 菜单要能"点外面自己关"：先把自己设为前台（NOACTIVATE 窗口多半会被拒，
        // 被拒也不影响菜单本身弹出），结束后补一个 WM_NULL 打断菜单的消息队列。
        Native.SetForegroundWindow(_hwnd);

        int cmd = Native.TrackPopupMenuEx(menu,
            Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN,
            cursor.X, cursor.Y, _hwnd, IntPtr.Zero);

        Native.DestroyMenu(menu);
        Native.PostMessageW(_hwnd, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (cmd <= 0) return;

        foreach ((uint menuId, Action run) in actions)
        {
            if (menuId != (uint)cmd) continue;
            run();
            break;
        }

        _dirty = true;
    }

    /// <summary>
    /// 强制结束进程。这是「把后台也关掉」的那把钥匙：
    /// 很多程序关掉窗口只是退到托盘 / 后台驻留（微信、网易云、各种 Electron 应用），
    /// WM_CLOSE 关不干净，只有结束进程才算真的关了。
    ///
    /// 代价是未保存的数据会直接丢 —— 所以它**不在**预览卡片上，只放在右键菜单里，
    /// 并且菜单文字写清了「强制结束」。
    /// </summary>
    private void KillSlotProcess(DockSlot slot)
    {
        if (slot.App is null) return;

        var pids = new HashSet<uint>();
        foreach (IntPtr hwnd in slot.App.Windows)
        {
            if (hwnd == IntPtr.Zero) continue;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0) pids.Add(pid);
        }

        if (pids.Count == 0) return;

        foreach (uint pid in pids)
        {
            try
            {
                using Process process = Process.GetProcessById((int)pid);
                process.Kill();
                Logger.Info($"菜单：结束进程 {slot.Name} pid={pid}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"菜单：结束进程失败 {slot.Name} pid={pid} — {ex.Message}");
            }
        }
    }

    /// <summary>关闭该槽位代表窗口。语义对齐任务栏右键的"关闭窗口"。可能弹保存确认框。</summary>
    private void CloseSlotWindow(DockSlot slot)
    {
        IntPtr hwnd = slot.Hwnd;
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

        Logger.Info($"关闭窗口: {slot.Name} 0x{hwnd.ToInt64():X}");
        Native.PostMessageW(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 固定 / 取消固定。
    /// 取消固定只影响 Dock：Windows 任务栏上那个图标原样保留（同步项进 HiddenPins 黑名单）。
    /// 这是刻意的 —— 用户要的是"Dock 上不显示"，不是"帮我把系统任务栏也改了"。
    /// </summary>
    private void TogglePin(DockSlot slot)
    {
        DockItemConfig? item = slot.Item;

        if (slot.Kind == DockSlotKind.Pinned && item is not null)
        {
            if (item.Source == "windows" && !string.IsNullOrEmpty(item.PinKey))
            {
                if (!_config.HiddenPins.Contains(item.PinKey, StringComparer.OrdinalIgnoreCase))
                    _config.HiddenPins.Add(item.PinKey!);
                Logger.Info($"[pins] 取消固定（Dock 侧隐藏）: {item.Name} key={item.PinKey}");
            }
            else
            {
                _config.Items.RemoveAll(i => ReferenceEquals(i, item) ||
                                             (i.Path == item.Path && i.Name == item.Name));
                Logger.Info($"[pins] 取消固定（移除手动添加项）: {item.Name}");
            }
        }
        else
        {
            // 运行区图标 -> 固定到 Dock
            string path = slot.App?.ExePath is { Length: > 0 } exe ? exe : slot.IconPath;
            if (path.Length == 0) return;

            // 如果它本来就有一个被隐藏的 Windows 固定项，解除隐藏比再堆一个副本更合直觉
            if (slot.App is not null && _config.HiddenPins.Count > 0 &&
                RestoreHiddenPinFor(slot.App.ExePath))
            {
                Logger.Info($"[pins] 固定到 Dock：恢复被隐藏的任务栏固定项 {slot.Name}");
            }
            else
            {
                _config.Items.Add(new DockItemConfig
                {
                    Name = slot.Name,
                    Path = path,
                    MatchPath = slot.App?.ExePath,
                    Source = "user",
                });
                Logger.Info($"[pins] 固定到 Dock: {slot.Name} ({path})");
            }
        }

        _config.Save();
        SyncPins();
        RebuildSlots();
        OnSlotsChanged();
    }

    /// <summary>把某个 exe 对应的隐藏固定项从黑名单里移除。返回是否真的移除了一项。</summary>
    private bool RestoreHiddenPinFor(string exePath)
    {
        foreach (TaskbarPin pin in TaskbarPins.Enumerate())
        {
            if (pin.TargetPath.Length == 0) continue;
            if (!string.Equals(pin.TargetPath, exePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (_config.HiddenPins.RemoveAll(k => string.Equals(k, pin.Key,
                    StringComparison.OrdinalIgnoreCase)) > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 运行时切换 Windows 任务栏的显示 / 隐藏。
    /// 隐藏后鼠标压到屏幕底边也不会弹出来 —— 靠的是 TaskbarGuard 的整窗透明 + 守护，
    /// 不是简单的 ShowWindow(SW_HIDE)。
    /// </summary>
    private void ToggleTaskbar()
    {
        _hideTaskbar = !_hideTaskbar;

        if (_hideTaskbar)
        {
            int hidden = TaskbarGuard.Hide();
            _taskbarCheckAt = _clock.Elapsed.TotalSeconds + TaskbarCheckInterval;
            Logger.Info($"菜单：隐藏任务栏（{hidden} 个窗口）");
        }
        else
        {
            int shown = TaskbarGuard.Show();
            if (TaskbarGuard.IsStealthed())
            {
                // 还原失败就别假装已经还回去了：开关留在"隐藏"状态，
                // 让用户再点一次，守护也仍在，下一轮会继续补。
                _hideTaskbar = true;
                Logger.Warn($"菜单：任务栏还原来 {shown} 个窗口，仍有窗口隐形，开关保持开启");
            }
            else
            {
                Logger.Info($"菜单：显示任务栏（{shown} 个窗口）");
            }
        }

        // 任务栏在不在，决定了 Dock 该贴屏幕底边还是贴工作区底边，必须整体重排
        _config.HideTaskbar = _hideTaskbar;
        _config.Save();
        RelayoutAll();
    }

    // ---------------------------------------------------------------- 显示 / 隐藏快捷键

    /// <summary>录制时试探组合键是否空闲用的临时 ID，注册后立刻注销。</summary>
    private const int ProbeHotkeyId = 0x4C48;

    /// <summary>
    /// 按当前配置装好（或拆掉）快捷键。
    ///
    /// 键盘组合和鼠标键都走低级钩子，规则统一为「指针在 Dock 上才动手」：
    /// · 鼠标中键/侧键 → WH_MOUSE_LL；
    /// · 键盘组合 → WH_KEYBOARD_LL 只观察不吞键，指针不在 Dock 上就原样放行。
    ///
    /// 键盘**故意不用 RegisterHotKey**：注册过的组合会被系统全局吞掉，
    /// 用户想用 Ctrl+C 复制文本时，正在编辑的软件根本收不到这个键。
    ///
    /// 鼠标键同理不全局生效：中键在浏览器里是"新标签页打开链接"，全局抢走
    /// 等于把浏览器搞坏。Dock 被藏起来时那块位置仍然算数 —— 否则藏起来就再也
    /// 点不回来了。
    /// </summary>
    private bool ApplyToggleShortcut()
    {
        // 鼠标钩子常驻，跟当前是不是鼠标快捷键无关：
        // 设置里"录制快捷键"需要它在任意位置都能接住中键 / 侧键。
        // 它只在按键与配置匹配、且指针压在 Dock 上时才动手，其余情况原样放行。
        if (_mouseHook == IntPtr.Zero)
        {
            _mouseHookProc = MouseShortcutHook;
            _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseHookProc,
                Native.GetModuleHandle(null), 0);

            if (_mouseHook == IntPtr.Zero)
            {
                _mouseHookProc = null;
                Logger.Warn($"[shortcut] 鼠标钩子安装失败（错误 {Marshal.GetLastWin32Error()}）");
            }
        }

        // 键盘钩子同样常驻：录制快捷键时设置面板不一定拿得到焦点，
        // 只有系统级钩子能保证"按了什么都能收到"。
        if (_keyboardHook == IntPtr.Zero)
        {
            _keyboardHookProc = KeyboardShortcutHook;
            _keyboardHook = Native.SetWindowsHookExWKeyboard(Native.WH_KEYBOARD_LL, _keyboardHookProc,
                Native.GetModuleHandle(null), 0);

            if (_keyboardHook == IntPtr.Zero)
            {
                _keyboardHookProc = null;
                Logger.Warn($"[shortcut] 键盘钩子安装失败（错误 {Marshal.GetLastWin32Error()}）");
            }
        }

        string token = _config.ToggleShortcut ?? "";

        _kbShortcutArmed = false;
        _kbShortcutFired = false;
        _dockClicks = 0;

        if (token.Length == 0)
        {
            Logger.Info("[shortcut] 显示/隐藏快捷键：已关闭");
            return true;
        }

        if (ShortcutSpec.IsMouse(token))
        {
            if (_mouseHook == IntPtr.Zero) return false;

            Logger.Info($"[shortcut] 显示/隐藏快捷键：{ShortcutSpec.DisplayName(token)}（指针在 Dock 上时生效）");
            return true;
        }

        if (!ShortcutSpec.TryParseKeyboard(token, out uint modifiers, out uint vk))
        {
            Logger.Warn($"[shortcut] 无法识别的快捷键 \"{token}\"，已忽略");
            return false;
        }

        if (_keyboardHook == IntPtr.Zero) return false;

        // 不再 RegisterHotKey：注册过的组合会被系统全局吞掉，用户想用 Ctrl+C 复制
        // 文本时，应用根本收不到这个键。改成低级键盘钩子只"观察"，指针不在 Dock 上
        // 就原样放行，应用照常复制。
        _kbShortcutMods = ((modifiers & Native.MOD_CONTROL) != 0 ? 1 : 0)
                        | ((modifiers & Native.MOD_ALT) != 0 ? 2 : 0)
                        | ((modifiers & Native.MOD_SHIFT) != 0 ? 4 : 0)
                        | ((modifiers & Native.MOD_WIN) != 0 ? 8 : 0);
        _kbShortcutVk = vk;
        _kbShortcutArmed = true;

        Logger.Info($"[shortcut] 显示/隐藏快捷键：{token}（指针在 Dock 上时才生效，不抢其他程序的按键）");
        return true;
    }

    private IntPtr MouseShortcutHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        int message = (int)wParam;

        // 录制期间左键点到面板外面 = 放弃录制。否则录制状态一直挂着，
        // 键盘钩子会持续吞键，用户会觉得"键盘坏了"。
        if (_settings is { IsCapturingShortcut: true } cap && message == (int)Native.WM_LBUTTONDOWN)
        {
            var click = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (!Native.GetWindowRect(cap.Handle, out Native.RECT rc) ||
                click.pt.X < rc.Left || click.pt.X > rc.Right ||
                click.pt.Y < rc.Top || click.pt.Y > rc.Bottom)
            {
                Native.PostMessageW(cap.Handle, Native.WM_LIQUIDGLASS_CANCEL_CAPTURE, IntPtr.Zero, IntPtr.Zero);
            }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        int button = 0; // 1=中键 2=侧键后退 3=侧键前进

        // 「双击 / 三击 Dock」：在玻璃空白处连点，最后一下立刻显隐，不再等。
        // 点击必须落在 Dock 玻璃上（不压图标，避免把启动应用算进来）。
        string gesture = _config.ToggleShortcut ?? "";
        if (message == (int)Native.WM_LBUTTONDOWN && gesture is "MouseDouble" or "MouseTriple")
        {
            var click = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            int cx = click.pt.X - _winX;
            int cy = click.pt.Y - _winY;

            bool inDock;
            bool onIcon = false;

            if (_dockHiddenByShortcut)
            {
                float slack = 8f * _scale;
                inDock = cx >= _panelX - slack && cx <= _panelX + _panelW + slack &&
                         cy >= _panelY - slack && cy <= _panelY + _panelH + slack;
            }
            else
            {
                inDock = cx >= _panelX - 2f && cx <= _panelX + _panelW + 2f &&
                         cy >= _panelY - 2f && cy <= _panelY + _panelH + 2f;
                onIcon = HitTestClient(cx, cy) >= 0;
            }

            if (inDock && !onIcon)
            {
                long now = Environment.TickCount64;
                int window = gesture == "MouseDouble" ? 500 : 600;
                int needed = gesture == "MouseDouble" ? 2 : 3;

                _dockClicks = now - _lastDockClickMs <= window ? _dockClicks + 1 : 1;
                _lastDockClickMs = now;

                if (_dockClicks >= needed)
                {
                    _dockClicks = 0;
                    Native.PostMessageW(_hwnd, Native.WM_LIQUIDGLASS_TOGGLE_DOCK, IntPtr.Zero, IntPtr.Zero);
                    return (IntPtr)1;
                }
            }
            else
            {
                _dockClicks = 0;
            }

            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }
        if (message == Native.WM_MBUTTONDOWN) button = 1;
        else if (message == Native.WM_XBUTTONDOWN)
        {
            var xinfo = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            button = ((xinfo.mouseData >> 16) & 0xFFFF) == 2 ? 3 : 2;
        }

        if (button == 0)
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        // 录制期间：鼠标在哪都算。用户最自然的动作是点一下 Dock 试试，
        // 那一下如果只放行，既不录制也不触发显隐，看起来就是"怎么按都没反应"。
        // 钩子里不能直接改设置（会在这里拆钩子），投给设置窗口处理。
        if (_settings is { IsCapturingShortcut: true } capturing)
        {
            Native.PostMessageW(capturing.Handle, Native.WM_LIQUIDGLASS_CAPTURE_MOUSE,
                (IntPtr)button, IntPtr.Zero);
            return (IntPtr)1;
        }

        string token = button switch
        {
            1 => "MouseMiddle",
            2 => "MouseX1",
            _ => "MouseX2",
        };

        if (token != _config.ToggleShortcut)
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
        if (!PointInToggleZone(info.pt.X, info.pt.Y))
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        // 钩子回调里不能直接动窗口（正在输入处理链里），投回自己的消息队列。
        Native.PostMessageW(_hwnd, Native.WM_LIQUIDGLASS_TOGGLE_DOCK, IntPtr.Zero, IntPtr.Zero);
        return (IntPtr)1;
    }

    /// <summary>
    /// 键盘钩子：跟踪修饰键状态、把录制期间按下的键投给设置面板、
    /// 在指针位于 Dock 上时匹配键盘快捷键。其余按键一律原样放行。
    /// </summary>
    private IntPtr KeyboardShortcutHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        int message = (int)wParam;
        bool down = message is (int)Native.WM_KEYDOWN or (int)Native.WM_SYSKEYDOWN;

        // 修饰键永远放行：拦掉 keyup 会让状态卡在"按住"，下一次录制平白多一个修饰键。
        switch (info.vkCode)
        {
            case 0x11: case 0xA2: case 0xA3: _capCtrl = down; return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            case 0x12: case 0xA4: case 0xA5: _capAlt = down; return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            case 0x10: case 0xA0: case 0xA1: _capShift = down; return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            case 0x5B: case 0x5C: _capWin = down; return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        if (_settings is { IsCapturingShortcut: true } capturing)
        {
            if (!down)
                return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            int mods = (_capCtrl ? 1 : 0) | (_capAlt ? 2 : 0) | (_capShift ? 4 : 0) | (_capWin ? 8 : 0);
            Native.PostMessageW(capturing.Handle, Native.WM_LIQUIDGLASS_CAPTURE_KEY,
                (IntPtr)(int)info.vkCode, (IntPtr)mods);

            // 只吃掉"能当快捷键"的键和 Esc/Backspace。单按字母放行 ——
            // 万一用户忘了在录制，普通打字至少还能用。
            byte key = (byte)info.vkCode;
            bool special = key is Native.VK_ESCAPE or Native.VK_BACK;
            bool accepted = ShortcutSpec.FromKey(key, _capCtrl, _capAlt, _capShift, _capWin).Length > 0;
            return special || accepted
                ? (IntPtr)1
                : Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        // 键盘快捷键：只在指针位于 Dock 上时才拦。指针不在 Dock 上就原样放行，
        // 所以 Ctrl+C 在编辑器里仍然是复制，不会把 Dock 收起来。
        if (_kbShortcutArmed && info.vkCode == _kbShortcutVk)
        {
            if (!down)
            {
                bool fired = _kbShortcutFired;
                _kbShortcutFired = false;
                return fired
                    ? (IntPtr)1
                    : Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            int mods = (_capCtrl ? 1 : 0) | (_capAlt ? 2 : 0) | (_capShift ? 4 : 0) | (_capWin ? 8 : 0);
            if (mods == _kbShortcutMods && !_kbShortcutFired &&
                Native.GetCursorPos(out Native.POINT pt) && PointInToggleZone(pt.X, pt.Y))
            {
                _kbShortcutFired = true;
                Native.PostMessageW(_hwnd, Native.WM_LIQUIDGLASS_TOGGLE_DOCK, IntPtr.Zero, IntPtr.Zero);
                return (IntPtr)1;
            }
        }

        return Native.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// 指针是否压在 Dock 的玻璃 / 图标上（用屏幕坐标）。Dock 藏起来时也照常算，
    /// 而且这时放宽到整块玻璃 —— 藏起来之后没有任何视觉可以对照，
    /// 只认图标条带的话用户很难正好点回原来那 16px 高的位置。
    /// </summary>
    private bool PointInToggleZone(int screenX, int screenY)
    {
        if (_hwnd == IntPtr.Zero) return false;

        if (_dockHiddenByShortcut)
        {
            float slack = 8f * _scale;
            float cx = screenX - _winX;
            float cy = screenY - _winY;
            return cx >= _panelX - slack && cx <= _panelX + _panelW + slack &&
                   cy >= _panelY - slack && cy <= _panelY + _panelH + slack;
        }

        return HitTestScreen(new Native.POINT { X = screenX, Y = screenY });
    }

    private void ToggleDockByShortcut()
    {
        if (_dockHiddenByShortcut) ShowDockAfterShortcut();
        else HideDockByShortcut();
    }

    /// <summary>
    /// 快捷键藏 Dock。任务栏状态**不动** —— 快捷键只管 Dock 自己，
    /// 藏任务栏是另一个开关；这样误触也不会把整条任务栏带走。
    /// 恢复入口始终在：再按一下快捷键，或桌面上的「恢复任务栏.bat」。
    /// </summary>
    private void HideDockByShortcut()
    {
        if (_dockHiddenByShortcut) return;

        if (_launchpad is { IsVisible: true }) _launchpad.Hide();
        _settings?.Hide();
        HidePreview();

        _pointerX = float.NaN;
        PointerInside = false;
        _hoverIndex = -1;
        _hoverSlot = -1;
        _dockHiddenByShortcut = true;

        Native.ShowWindow(_hwnd, 0 /* SW_HIDE */);
        Logger.Info($"[shortcut] Dock 已隐藏（{_config.ToggleShortcut}），再按一次显示");
    }

    private void ShowDockAfterShortcut()
    {
        if (!_dockHiddenByShortcut) return;

        _dockHiddenByShortcut = false;
        Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _winX, _winY, _winW, _winH,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        _dirty = true;
        Render();
        Logger.Info("[shortcut] Dock 已显示");
    }

    /// <summary>
    /// 设置面板录制完成后落到配置里，并立刻重装快捷键。
    /// 返回 null = 已生效；返回文字 = 这个键不能用，面板会留在录制状态等用户换一个。
    /// </summary>
    private string? SetToggleShortcut(string token)
    {
        token ??= "";

        if (token.Length > 0 && !ShortcutSpec.IsMouse(token) &&
            !ShortcutSpec.TryParseKeyboard(token, out _, out _))
        {
            Logger.Warn($"[shortcut] 拒绝无法识别的快捷键 \"{token}\"");
            return "这个键不支持 —— 换一个组合键";
        }

        string previous = _config.ToggleShortcut ?? "";
        if (token == previous)
        {
            if (ApplyToggleShortcut()) return null;
            return $"{ShortcutSpec.DisplayName(token)} 启用失败 —— 换一个组合键";
        }

        // 先探测键盘组合是否已被别的程序占用：临时注册再注销。
        // 被占用的直接不收，用户不用等"保存失败再退回去"。
        if (token.Length > 0 && !ShortcutSpec.IsMouse(token) &&
            ShortcutSpec.TryParseKeyboard(token, out uint modifiers, out uint vk))
        {
            if (Native.RegisterHotKey(_hwnd, ProbeHotkeyId, modifiers | Native.MOD_NOREPEAT, vk))
            {
                Native.UnregisterHotKey(_hwnd, ProbeHotkeyId);
            }
            else
            {
                Logger.Info($"[shortcut] {token} 已被其他程序占用（错误 {Marshal.GetLastWin32Error()}），不采用");
                return $"{ShortcutSpec.DisplayName(token)} 已被其他程序占用 —— 换一个组合键";
            }
        }

        _config.ToggleShortcut = token;

        if (!ApplyToggleShortcut())
        {
            _config.ToggleShortcut = previous;
            ApplyToggleShortcut();
            Logger.Warn($"[shortcut] {ShortcutSpec.DisplayName(token)} 无法启用，已恢复为 {ShortcutSpec.DisplayName(previous)}");
            return $"{ShortcutSpec.DisplayName(token)} 启用失败 —— 换一个组合键";
        }

        SaveConfig();
        Logger.Info($"[shortcut] 快捷键已更新：{ShortcutSpec.DisplayName(token)}");
        return null;
    }

    /// <summary>重新量一遍显示器并把窗口挪到新位置（任务栏显隐、分辨率变化都要走这里）。</summary>
    private void RelayoutAll()
    {
        if (_hwnd == IntPtr.Zero) return;

        int oldW = _winW, oldH = _winH;
        MeasureForMonitor(_hwnd);

        if (oldW != _winW || oldH != _winH) ResizeSurface(oldW, oldH);

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _winX, _winY, _winW, _winH,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        UpdateWindowRegion();
        _dirty = true;
    }

    /// <summary>
    /// 把**窗口形状**限制成真正需要接收鼠标的那一块。
    ///
    /// 为什么必须是形状、而不是 WM_NCHITTEST 返回 HTTRANSPARENT：
    /// 文档写得很清楚，HTTRANSPARENT 只在**同一线程**的窗口之间向下转发。
    /// 玻璃下面全是别的进程（WorkBuddy、桌面、浏览器），那个 -1 等于没写。
    /// SetWindowRgn 改的是窗口在系统里的形状，命中测试在系统层面走，跨进程有效。
    /// （WS_EX_TRANSPARENT 也一样只对同线程兄弟窗口生效，实测点下去仍然是我们自己。）
    ///
    /// **这条裁剪线同时管点击和显示**，所以它的位置不能随便定：
    /// 线画在玻璃上方 → 玻璃顶部连同圆角被一起削掉，屏幕上就是一条硬切边
    /// （v1.2 那条线在 "图标顶端 - 6px"，比玻璃顶低 7px，正好切掉玻璃上沿 ——
    ///  用户看到的"上边被切割"就是它）。
    ///
    /// v1.9 之前线正好压在玻璃上沿（_panelY），换来的是"上边没有阴影"和
    /// "放大的图标被切一条"—— 因为阴影和放大后的图标都画在 _panelY 以上。
    ///
    /// 现在按**实际画到哪**来定，两个量取大：
    /// <list type="number">
    /// <item>玻璃自己的投影：_shadowExtent*0.85 就是 MeasureForMonitor 里
    ///       给面板上方留的那一段。少一个像素，上半个阴影就没了。</item>
    /// <item>当前最大放大高度 (scale-1)*iconSize：必须跟着**实际弹簧值**走，
    ///       而不是跟着"鼠标在不在"的布尔量走。跟着布尔量会出现
    ///       "刚点一下 → 指针状态翻转 → 区域瞬间缩回 → 正在放大的图标被切一刀"，
    ///       也就是用户说的"左击图标直接就被切割了"。</item>
    /// </list>
    ///
    /// 代价：玻璃顶到图标顶那条缝（含阴影带）现在归 Dock 所有，压在那条带上的
    /// 窗口按钮点不到。这是"要阴影"必须付的账 —— 形状是二值的，
    /// 一个像素要么属于窗口（挡住鼠标）要么被裁掉（阴影消失），没有中间态。
    /// 这条带只有 22px 高，比原来"整块拦"的一次只多这一点。
    /// </summary>
    private void UpdateWindowRegion()
    {
        if (_hwnd == IntPtr.Zero || _winW <= 2 || _winH <= 2) return;

        // 当前真正画出来的最大放大高度
        float lift = 0f;
        for (int i = 0; i < _scaleArr.Length; i++)
        {
            float d = (_scaleArr[i] - 1f) * _iconSize;
            if (d > lift) lift = d;
        }

        // 弹簧还没停下时多留 3px，动画尾巴上不会擦到边
        if (lift > 0.01f) lift += 3f * _scale;

        float need = MathF.Max(lift, _shadowExtent * 0.85f);
        int top = (int)MathF.Floor(_panelY - need);
        if (top < 0) top = 0;
        if (top > _winH - 2) top = _winH - 2;

        // 每帧都调，但真正重建区域只在整像素变化时发生。
        // SetWindowRgn 是内核 + DWM 的往返，60Hz 无条件调用会让滑动变涩。
        if (top == _regionTop) return;
        _regionTop = top;

        IntPtr rgn = Native.CreateRectRgn(0, top, _winW, _winH);
        if (rgn == IntPtr.Zero) return;

        // SetWindowRgn 成功后区域归系统所有，不能再删；失败时才是我们的责任。
        if (Native.SetWindowRgn(_hwnd, rgn, true) == 0)
            Native.DeleteObject(rgn);
    }

    // ---------------------------------------------------------------- 收尾

    /// <summary>临时关闭采集排除 → 真正拍到"桌面 + Dock"的画面 → 再恢复排除。</summary>
    private void CaptureShot(string path)
    {
        Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_NONE);
        // 胶囊自己也带采集排除（它就躺在自己的第二采样区里）。不关掉的话，
        // 截出来的图上 Dock 玻璃在、胶囊那块是个洞 —— 看着像胶囊没画出来。
        IntPtr pillHwnd = _statusPill?.Handle ?? IntPtr.Zero;
        if (pillHwnd != IntPtr.Zero)
            Native.SetWindowDisplayAffinity(pillHwnd, Native.WDA_NONE);

        // 启动台打开时它铺满整屏，而它也带采集排除。不关掉就只拍得到它背后那块桌面。
        bool panelOpen = _launchpad is { IsVisible: true };
        if (panelOpen) Native.SetWindowDisplayAffinity(_launchpad!.Handle, Native.WDA_NONE);

        // 弹簧需要几帧才收敛，截图前先把动画跑到位，否则拍到的是半截放大
        for (int i = 0; i < 48; i++)
        {
            ApplyDevPointer();
            UpdateAnimation(1f / 60f);
        }

        _dirty = true;
        Render();
        Thread.Sleep(160);
        Render();
        // 面板也要重画一帧：它平时只在背景变化时才刷，截图这一刻未必正好画过
        if (panelOpen) _launchpad!.Invalidate();

        int pad = 26;
        // 截图范围要把右端的胶囊一起框进来：它才是这次要对比的东西，
        // 只截 Dock 窗口的话胶囊永远在画框外面。
        int x0 = _winX - pad;
        int y0 = _winY - pad;
        int x1 = _winX + _winW + pad;
        int y1 = _winY + _winH + pad;
        if (_statusPill is { } pill && pill.TryGetScreenBounds(out int px, out int py, out int pw, out int ph))
        {
            x0 = Math.Min(x0, px - 8);
            y0 = Math.Min(y0, py - 8);
            x1 = Math.Max(x1, px + pw + 8);
            y1 = Math.Max(y1, py + ph + 8);
        }

        // 面板是全屏的，只截 Dock 那一小块就什么都看不到
        if (panelOpen)
        {
            x0 = 0;
            y0 = 0;
            x1 = Math.Max(1, Native.GetSystemMetrics(0));
            y1 = Math.Max(1, Native.GetSystemMetrics(1));
        }

        bool ok = ScreenCapture.SaveBmp(x0, y0, x1 - x0, y1 - y0, path);

        if (_dumpFramePath is not null && _comp is not null && _ctx is not null)
        {
            try { _comp.DumpFrame(_ctx, _dumpFramePath); Logger.Info("后台缓冲已导出: " + _dumpFramePath); }
            catch (Exception ex) { Logger.Warn("后台缓冲导出失败: " + ex.Message); }
        }

        if (panelOpen) Native.SetWindowDisplayAffinity(_launchpad!.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        if (_statusPill is { } pillShot) Native.SetWindowDisplayAffinity(pillShot.Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        Logger.Info((ok ? "截图成功: " : "截图失败: ") + path);
    }

    private void DumpPerf()
    {
        double seconds = _clock.Elapsed.TotalSeconds;
        string json = _stats.ToJson(seconds, _ctx?.AdapterName ?? "n/a", _sampler?.Status ?? "n/a");
        try
        {
            Directory.CreateDirectory(DockConfig.Directory);
            File.WriteAllText(Path.Combine(DockConfig.Directory, "perf.json"), json);
        }
        catch { /* 忽略 */ }

        var (p50, p95, p99, max, avg) = _stats.Summary();
        Logger.Info($"性能: frames={_stats.Count} avg={avg:F2}ms p50={p50:F2} p95={p95:F2} p99={p99:F2} max={max:F2}");

        // 帧间隔才是"顺不顺"的判据：单帧画得再快，间隔忽长忽短一样是卡。
        if (_frameGaps.Count > 4)
        {
            var sorted = new List<double>(_frameGaps);
            sorted.Sort();
            double g50 = sorted[sorted.Count / 2];
            double g95 = sorted[(int)(sorted.Count * 0.95)];
            double g99 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))];
            double gmax = sorted[^1];
            double mean = 0;
            foreach (double g in sorted) mean += g;
            mean /= sorted.Count;

            double jitter = 0;
            foreach (double g in sorted) jitter += Math.Abs(g - mean);
            jitter /= sorted.Count;

            Logger.Info($"帧间隔: n={sorted.Count} avg={mean:F2}ms p50={g50:F2} p95={g95:F2} " +
                        $"p99={g99:F2} max={gmax:F2} 抖动(平均绝对偏差)={jitter:F2}ms");
        }
        Logger.Info($"采样耗时: 调用={_sampleCalls} 平均={(_sampleCalls > 0 ? _sampleMs / _sampleCalls : 0):F3}ms 合计={_sampleMs:F0}ms");
        if (_tracker is not null)
        {
            double sec = Math.Max(0.001, _clock.Elapsed.TotalSeconds);
            Logger.Info($"窗口追踪: 钩子事件={_tracker.EventCount} ({_tracker.EventCount / sec:F1}/秒) " +
                        $"枚举={_tracker.SyncCount} 次 合计={_tracker.SyncMs:F1}ms " +
                        $"平均={(_tracker.SyncCount > 0 ? _tracker.SyncMs / _tracker.SyncCount : 0):F2}ms");
        }
        if (_sampler is not null)
            Logger.Info($"采样: 新帧={_sampler.HitCount} 无变化={_sampler.MissCount} " +
                        $"自反馈过滤={_sampler.SelfLoopCount} 丢失={_sampler.LostCount}");

        if (_presentCalls > 0)
            Logger.Info($"垂直同步等待: 调用={_presentCalls} 平均={_presentTotal / _presentCalls:F2}ms " +
                        $"合计={_presentTotal:F0}ms");
    }

    // ---------------------------------------------------------------- 设置面板

    private const string AppVersion = "v1.64";

    private void OpenSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsPanel(BuildSettingCategories(), _scale, AppVersion);
            _settings.Closed += () => Logger.Info("设置窗口：已关闭");
        }

        _settings.ToggleNearDock();
    }

    /// <summary>
    /// 通透度档位。没做连续滑块：设置面板目前只有开关/按钮/信息三种行，
    /// 而"磨砂"到"最透"之间人眼能分辨的也就这么几档，写死比半吊子滑杆好用。
    /// </summary>
    private static readonly (float Value, string Name)[] GlassTransmissionLevels =
    {
        (0.00f, "磨砂"),
        (0.35f, "标准"),
        (0.60f, "较透"),
        (0.85f, "很透"),
        (1.00f, "最透"),
    };

    /// <summary>渲染实际用到的通透度：--transmission 覆盖优先，否则读配置。</summary>
    private float EffectiveTransmission(GlassConfig g) =>
        _transmissionOverride >= 0f ? _transmissionOverride : Math.Clamp(g.Transmission, 0f, 1f);

    private static string GlassTransmissionName(float v)
    {
        float bestDelta = float.MaxValue;
        string best = "自定义";
        foreach ((float value, string name) in GlassTransmissionLevels)
        {
            float d = MathF.Abs(value - v);
            if (d < bestDelta) { bestDelta = d; best = name; }
        }
        // 手工改过 config.json 的落在两档之间时，别硬说是某一档
        return bestDelta < 0.06f ? best : "自定义";
    }

    private void CycleGlassTransmission()
    {
        float cur = EffectiveTransmission(_config.Glass);
        int idx = 0;
        float best = float.MaxValue;
        for (int i = 0; i < GlassTransmissionLevels.Length; i++)
        {
            float d = MathF.Abs(GlassTransmissionLevels[i].Value - cur);
            if (d < best) { best = d; idx = i; }
        }

        (float Value, string Name) next = GlassTransmissionLevels[(idx + 1) % GlassTransmissionLevels.Length];
        _config.Glass.Transmission = next.Value;
        SaveConfig();
        _dirty = true;
        Logger.Info($"玻璃通透度 → {next.Name}（{next.Value:0.00}）");
    }

    /// <summary>悬停预览的可选延迟。0 表示彻底关掉（不建窗口、不抓图）。</summary>
    private static readonly (int Ms, string Name)[] HoverPreviewDelays =
    {
        (0, "关闭"),
        (600, "0.6 秒"),
        (1500, "1.5 秒"),
        (2000, "2 秒"),
        (3000, "3 秒"),
        (5000, "5 秒"),
    };

    private static string HoverPreviewDelayName(int ms)
    {
        foreach ((int value, string name) in HoverPreviewDelays)
            if (value == ms) return name;
        return $"{ms / 1000.0:0.#} 秒";
    }

    private void CycleHoverPreviewDelay()
    {
        int cur = _config.HoverPreviewDelayMs;
        int idx = 0, best = int.MaxValue;
        for (int i = 0; i < HoverPreviewDelays.Length; i++)
        {
            int d = Math.Abs(HoverPreviewDelays[i].Ms - cur);
            if (d < best) { best = d; idx = i; }
        }

        (int Ms, string Name) next = HoverPreviewDelays[(idx + 1) % HoverPreviewDelays.Length];
        _config.HoverPreviewDelayMs = next.Ms;
        SaveConfig();
        HidePreview();
        Logger.Info($"悬停预览 → {next.Name}");

        if (next.Ms == 0)
        {
            // 关掉的时候顺手把卡片窗口销毁：它是个常驻 TOPMOST 窗口，
            // 留着虽然不可见，但没必要让用户为"关掉的功能"付一个窗口的账。
            _preview?.Dispose();
            _preview = null;
        }
    }

    /// <summary>
    /// 设置窗口的内容。加一个开关 = 这里加一行；加一个效果分类 = 这里加一个 Category，
    /// 侧边栏会自动多出一项，窗口本身不用改。
    /// </summary>
    private List<SettingsPanel.Category> BuildSettingCategories() => new()
    {
        new SettingsPanel.Category
        {
            Title = "停靠栏",
            Note = "Dock 本身的行为、显示内容与图标",
            Rows =
            {
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "显示 / 隐藏 Dock 快捷键",
                    Note = "双击 / 三击 / 中键 / 侧键都指在 Dock 上操作；键盘组合也要指针在 Dock 上才生效",
                    ValueOf = () => ShortcutSpec.DisplayName(_config.ToggleShortcut),
                    CaptureShortcut = true,
                    ShortcutCaptured = SetToggleShortcut,
                },
                new SettingsPanel.Row
                {
                    Title = "接管系统任务栏",
                    Note = "开启 = 藏掉 Windows 任务栏；关闭 = 立刻把任务栏放回来",
                    Read = () => _hideTaskbar,
                    Write = SetTaskbarHidden,
                },
                new SettingsPanel.Row
                {
                    Title = "显示开始按钮",
                    Note = "最左边的徽标，点它等于按 Win 键",
                    Read = () => _config.ShowStartButton,
                    Write = v => ApplySlotFlag(() => _config.ShowStartButton = v),
                },
                new SettingsPanel.Row
                {
                    Title = "显示运行中的程序",
                    Note = "正在运行的应用出现在 Dock 上，点击切换或最小化",
                    Read = () => _config.ShowRunningApps,
                    Write = v => ApplySlotFlag(() => _config.ShowRunningApps = v),
                },
                new SettingsPanel.Row
                {
                    Title = "跟随任务栏固定项",
                    Note = "Windows 任务栏钉了什么，Dock 上就有什么",
                    Read = () => _config.SyncTaskbarPins,
                    Write = SetSyncPins,
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "悬停预览",
                    Note = "鼠标停住多久后浮出窗口缩略图（0 = 关闭）",
                    ValueOf = () => HoverPreviewDelayName(_config.HoverPreviewDelayMs),
                    Run = CycleHoverPreviewDelay,
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "更换开始按钮图标…",
                    Note = "选一张 png / jpg / bmp / ico，自动裁圆角",
                    Run = PickStartIcon,
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "用我的账户头像当开始按钮",
                    Note = "直接取 Windows 账户头像",
                    Run = UseAccountAvatar,
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "恢复开始按钮默认图标",
                    Note = "回到内置的 Windows 徽标",
                    Run = () => SetStartIcon(""),
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "重新启动 Dock",
                    Note = "退出后自动重新打开，任务栏设置保持不变",
                    Run = RestartDock,
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "关闭 Dock 并恢复 Windows 任务栏",
                    Note = "和右键菜单里的「退出 Dock」一样，任务栏会先回来",
                    Danger = true,
                    Run = () => _quit = true,
                },
            },
        },
        new SettingsPanel.Category
        {
            Title = "小工具",
            Note = "Dock 上挂的附加面板与开机行为",
            Rows =
            {
                new SettingsPanel.Row
                {
                    Title = "启动台",
                    Note = "全屏浏览所有已安装应用，可输入筛选",
                    Read = () => _config.ShowLaunchpad,
                    Write = v => ApplySlotFlag(() => _config.ShowLaunchpad = v),
                },
                new SettingsPanel.Row
                {
                    Title = "文件资源管理器",
                    Note = "在 Dock 左端放一个资源管理器入口",
                    Read = () => _config.ShowExplorer,
                    Write = v => ApplySlotFlag(() => _config.ShowExplorer = v),
                },
                new SettingsPanel.Row
                {
                    Title = "显示时钟",
                    Note = "在 Dock 右端显示时间和日期（任务栏藏起来后也能看时间）",
                    Read = () => _config.ShowClock,
                    Write = SetClockVisible,
                },
                new SettingsPanel.Row
                {
                    Title = "开机自启",
                    Note = "登录 Windows 后自动启动 Dock 并接管任务栏",
                    Read = () => Autostart.IsEnabled,
                    Write = SetAutostart,
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "现在打开启动台",
                    Note = "等于点 Dock 上的九宫格",
                    Run = OpenLaunchpad,
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "打开系统快速设置",
                    Note = "Windows 原生的网络 / 蓝牙 / 亮度 / 音量面板",
                    Run = OpenQuickSettings,
                },
            },
        },
        new SettingsPanel.Category
        {
            Title = "外观",
            Note = "玻璃质感与动效。后续效果都会加在这一页",
            Rows =
            {
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "玻璃通透度",
                    Note = "越透越能看清桌面；太透时壁纸的亮边会透进玻璃里",
                    ValueOf = () => GlassTransmissionName(_config.Glass.Transmission),
                    Run = CycleGlassTransmission,
                },
                new SettingsPanel.Row
                {
                    Title = "玻璃折射",
                    Note = "关闭后边缘不再扭曲背景，可省一点电",
                    Read = () => _config.Glass.EnableRefraction,
                    Write = v =>
                    {
                        _config.Glass.EnableRefraction = v;
                        SaveConfig();
                        _dirty = true;
                    },
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Info,
                    Title = "更多外观项",
                    Note = "模糊强度、圆角、底色、图标风格",
                    Value = "排期中",
                },
            },
        },
        new SettingsPanel.Category
        {
            Title = "关于",
            Note = "版本信息与退出入口",
            Rows =
            {
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Info,
                    Title = "液态玻璃 Dock",
                    Note = "自绘玻璃 Dock，接管 Windows 任务栏",
                    Value = AppVersion,
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Info,
                    Title = "配置文件",
                    Note = "所有开关都存在这个 json 里",
                    Value = "config.json",
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Action,
                    Title = "打开配置文件夹",
                    Note = "改坏了可以手动改回来",
                    Run = OpenConfigFolder,
                },
                new SettingsPanel.Row
                {
                    Kind = SettingsPanel.RowKind.Info,
                    Title = "退出 / 重启",
                    Note = "入口已移到「停靠栏」页，方便和任务栏开关放在一起",
                    Value = "见停靠栏",
                },
            },
        },
    };

    private void OpenConfigFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = DockConfig.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("打开配置文件夹失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 设置里的"重新启动 Dock"。
    ///
    /// 不能在当前进程里直接启动自己：单实例锁还在手上，新进程会撞锁退出，
    /// 用户看到的就是"点了重启，Dock 没了"。所以先拉起一个 --relaunch-after 助手，
    /// 由它等本进程完全退出后再启动。本进程走正常退出流程，任务栏照常先还原。
    /// </summary>
    private void RestartDock()
    {
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (self.Length == 0)
            {
                Logger.Warn("重新启动 Dock: 拿不到自身可执行文件路径");
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                self, "--relaunch-after " + Environment.ProcessId)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });

            Logger.Info("设置：已安排重新启动 Dock（退出后由助手拉起新实例）");
            _quit = true;
        }
        catch (Exception ex)
        {
            Logger.Warn("重新启动 Dock 失败: " + ex.Message);
        }
    }

    /// <summary>开关改的是"槽位集合"：写配置、重建槽位、重新量几何，三步缺一不可。</summary>
    private void ApplySlotFlag(Action apply)
    {
        apply();
        SaveConfig();
        RebuildSlots();
        OnSlotsChanged();
    }

    private void SetTaskbarHidden(bool hidden)
    {
        if (_hideTaskbar == hidden) return;
        ToggleTaskbar();
    }

    /// <summary>时钟开关改的是玻璃右端的内边距，不是槽位，所以只要重排一次几何。</summary>
    private void SetClockVisible(bool on)
    {
        if (_config.ShowClock == on) return;
        _config.ShowClock = on;
        SaveConfig();

        // 关闭时把旧纹理留着没意义；下次打开会按新尺寸重建
        _clockMinute = -1;
        OnSlotsChanged();
        _dirty = true;
        Logger.Info($"设置：显示时钟 = {on}");
    }

    private void SetSyncPins(bool on)
    {
        _config.SyncTaskbarPins = on;
        SaveConfig();

        // 两个方向都要重读：关掉同步时要立刻退回"只有用户自己钉的项"
        _pinDirSignature = "";
        _pinRescanAt = 0;
        SyncPins();
        RebuildSlots();
        OnSlotsChanged();
    }

    // ------------------------------------------------------------ 开始按钮图标

    /// <summary>换开始按钮图标：写配置、丢掉旧纹理、重建、重画。空路径 = 恢复内置徽标。</summary>
    private void SetStartIcon(string path)
    {
        _config.StartIconPath = path;
        SaveConfig();

        // 槽位里缓存的是纹理句柄，不清掉的话换了图也还是旧的样子
        foreach (DockSlot slot in _slots)
            if (slot.Kind == DockSlotKind.Start) slot.Icon = null;

        EnsureSlotIcons();
        _dirty = true;
        Logger.Info("开始按钮图标 = " + (path.Length == 0 ? "内置 Windows 徽标" : path));
    }

    /// <summary>弹系统文件对话框挑一张图，选中即生效。</summary>
    private void PickStartIcon()
    {
        string? picked = Native.PickImageFile(_hwnd, "选择开始按钮图标");
        if (picked is null)
        {
            Logger.Info("选择图标：已取消");
            return;
        }

        SetStartIcon(picked);
    }

    /// <summary>
    /// 用 Windows 账户头像当开始图标。
    ///
    /// 头像文件不在一处：用户自己设置过的在 AppData 的 AccountPictures 下（按 SID 命名），
    /// 没设置过的只有 ProgramData 里那张通用人像。两个都找不到就如实说一句。
    /// </summary>
    private void UseAccountAvatar()
    {
        foreach (string candidate in AccountAvatarCandidates())
        {
            if (!File.Exists(candidate)) continue;
            SetStartIcon(candidate);
            return;
        }

        Logger.Warn("没找到 Windows 账户头像文件，请用「更换开始按钮图标…」手动选一张");
    }

    private static IEnumerable<string> AccountAvatarCandidates()
    {
        // 用户自己设的头像（按 SID 命名）
        string pics = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "AccountPictures");
        if (Directory.Exists(pics))
        {
            foreach (string f in Directory.EnumerateFiles(pics, "*.png")
                         .Concat(Directory.EnumerateFiles(pics, "*.jpg")))
                yield return f;
        }

        // 系统通用头像
        string common = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "User Account Pictures");
        foreach (string name in new[] { "user.png", "user.bmp", "guest.png" })
        {
            string p = Path.Combine(common, name);
            if (File.Exists(p)) yield return p;
        }
    }

    private void SetAutostart(bool on)    {
        bool ok = on ? Autostart.Install(out string message) : Autostart.Remove(out message);
        Logger.Info($"[settings] 开机自启={(on ? "开" : "关")}：{message}");
        if (!ok) Logger.Warn("开机自启没有写成功: " + message);
    }

    // ---------------------------------------------------------------- 启动台

    /// <summary>面板开着就收起来（点 Dock 上别的图标时先走这一步）。</summary>
    private void CloseLaunchpad()
    {
        if (_launchpad is not { IsVisible: true } pad) return;
        Logger.Info("点击 Dock 图标：先收起启动台");
        pad.Hide();
        _dirty = true;

        // 面板一收，桌面马上就要换成"点进去的那个任务"。这一下 DWM 常常不报脏区，
        // 采样端会把新帧当成"没变化"丢掉，于是玻璃继续显示原页面 —— 直到 heartbeat 兜底。
        // 所以从这一刻起 2 秒内强制逐帧比像素（见 DesktopSampler.ForceCompareFor）。
        _sampler?.ForceCompareFor(ForceCompareAfterThawMs);

        // 接下来 3 秒把"事件→新帧"的耗时写进日志（不必开环境变量），
        // 用来确认这条路径到底还剩多少延迟、以及脏区是不是又被漏报了。
        _lagDiagUntil = _clock.Elapsed.TotalSeconds + 3.0;

        // 临时诊断：接下来 3 秒记录胶囊每一帧的来源与追帧耗时。
        if (_statusPill is { } lagPill) lagPill.LagDiagUntilMs = Environment.TickCount64 + 3000;

        // 胶囊在接下来这一秒里逐帧重画（见 StatusPill.ForceRepaintFor）。
        // 实测这条路径上 Dock 本体 6ms 就换好了，胶囊却因为第二采样区的指纹漏判
        // 整整 3 秒没有任何一帧 —— 用户看到的就是"点进别的任务，胶囊还停在上一页"。
        _statusPill?.ForceRepaintFor(PillThawMs);
    }


    private void OpenLaunchpad()
    {
        if (_launchpad is null)
        {
            _launchpad = new LaunchpadWindow(_scale);
            // 跟 Dock 本体共享同一套玻璃环境：设备、着色器、配置、通透度。
            // 少给任何一样，它就退回成一块半透明底板，跟旁边那条玻璃对不上。
            _launchpad.D3D = _ctx;
            _launchpad.ShaderSource = _shaderSource;
            _launchpad.GlassConfigProvider = () => _config.Glass;
            _launchpad.TransmissionProvider = () => EffectiveTransmission(_config.Glass);
            // 面板无论从哪个入口关掉，都要把 Dock 的背景解冻（见 OpenLaunchpad 里的说明）
            // 面板不管从哪个入口关（Esc / 点空白 / × / 点 Dock 图标），都在这里收口：
            // 解冻 + 接下来 2 秒强制逐帧比像素（见 DesktopSampler.ForceCompareFor）。
            _launchpad.Closed = () =>
            {
                _sampler?.ReleaseFreeze();
                _sampler?.ForceCompareFor(ForceCompareAfterThawMs);
                // 面板从任何入口关掉（点空白 / Esc / 右上角 ×），胶囊也要立刻跟上新桌面，
                // 否则它会一直画着打开启动台之前那张背景（见 StatusPill.ForceRepaintFor）。
                _statusPill?.ForceRepaintFor(PillThawMs);

                // 面板是激活窗口，收起后它那块 TOPMOST 位置空出来，Dock 会被排到后面去
                // （用户报的"点完启动台，Dock 沉到壁纸后面、点都点不到"）。下一拍再把
                // Dock 和胶囊提回最前 —— 不能同一帧提，DWM 还没处理完隐藏。
                _topmostReassertAt = _clock.Elapsed.TotalSeconds + 0.30;
            };
        }

        // 每次打开都重新绑一次：采样器可能在显示器变化时被重建过。
        // 启动台不能自己开一个 Duplication（一个输出只允许一份），只能借这份。
        _launchpad.Sampler = _sampler;

        bool wasVisible = _launchpad.IsVisible;

        // 打开之前先把桌面背景冻住，关闭之后解冻。
        //
        // 为什么必须冻：启动台是一块铺满整屏的玻璃，它的窗口虽然设了
        // WDA_EXCLUDEFROMCAPTURE，但在"刚显示/刚隐藏"的那一两帧里，DWM 的排除
        // 还没生效，采集端抓到的是启动台自己的奶白玻璃；同时黑帧探测又会把
        // 这一帧判成"桌面不可用"。两条路最后都指向同一个结果 ——
        // Dock 拿一张错的底图重画，用户看到的就是"点一下，Dock 闪成冷白/冷蓝灰"。
        //
        // 冻住之后 Dock 的 capSrv 一直是打开前那一帧（桌面内容此刻并没有变），
        // 观感就是"什么都不动"。关闭时解冻，实时采样照旧。
        if (!wasVisible) _sampler?.Freeze();

        _launchpad.Toggle();

        // 启动台铺满整屏、而且是后创建的 TOPMOST 窗口，会把 Dock 和胶囊压在下面 ——
        // 面板是"桌面摊开"，Dock 应该还浮在它上面（macOS 也是这个层级），
        // 否则打开启动台后 Dock 直接消失，想点图标反而要先关面板。
        if (!wasVisible && _launchpad.IsVisible) BringOverlaysToTop();
    }

    /// <summary>把 Dock 与状态胶囊重新提到 TOPMOST 层最前，不抢焦点。</summary>
    private void BringOverlaysToTop()
    {
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;

        if (_hwnd != IntPtr.Zero)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);

        if (_statusPill is { } pill && pill.Handle != IntPtr.Zero)
            Native.SetWindowPos(pill.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
    }

    private static bool IsExplorerPath(string? path)
        => !string.IsNullOrEmpty(path) &&
           path.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 启动台开着的时候，Dock 的背景必须是冻住的。
    ///
    /// 屏幕此刻被一块全屏玻璃盖住，采集端看到的东西跟"桌面"已经不是一回事了
    /// （排除生效前是启动台自己的奶白玻璃，生效后是一块黑），任何一帧都不该
    /// 被 Dock 当成新的底图。
    /// </summary>
    private bool LaunchpadHoldsBackdrop => _launchpad is { IsVisible: true };

    private void SaveConfig()
    {
        _config.Save();
        Logger.Info("配置已保存: " + DockConfig.FilePath);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseHookProc = null;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
            _keyboardHookProc = null;
        }

        _settings?.Dispose();
        _settings = null;
        _launchpad?.Dispose();
        _launchpad = null;

        _preview?.Dispose();
        _preview = null;
        _previewSlot = -1;

        _statusPill?.Dispose();
        _statusPill = null;

        _tracker?.Dispose();
        _tracker = null;

        _renderer?.Dispose();
        _comp?.Dispose();
        _sampler?.Dispose();
        _fallback?.Dispose();
        _ctx?.Dispose();
        if (_hwnd != IntPtr.Zero) Native.DestroyWindow(_hwnd);
    }
}
