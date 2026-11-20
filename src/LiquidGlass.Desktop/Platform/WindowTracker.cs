using System.Diagnostics;
using System.Text;
using LiquidGlass.Desktop.App;

namespace LiquidGlass.Desktop.Platform;

internal enum TrackChange
{
    /// <summary>没有任何变化。</summary>
    None = 0,

    /// <summary>只有"谁在前台"变了：需要重绘运行指示点，布局不用动。</summary>
    Highlight = 1,

    /// <summary>应用集合或窗口归属变了：需要重建 Dock 槽位并重算几何。</summary>
    Layout = 2,
}

/// <summary>一个"在运行的应用程序"：同一个 exe 的多个窗口合并成一条。</summary>
internal sealed class TrackedApp
{
    public string ExePath = "";
    public string Name = "";
    /// <summary>该应用的所有顶层窗口，按"最适合被激活"排序（未最小化的在前）。</summary>
    public readonly List<IntPtr> Windows = new(2);
    public bool HasForeground;

    public IntPtr Representative => Windows.Count > 0 ? Windows[0] : IntPtr.Zero;
}

/// <summary>
/// 追踪"当前有哪些应用在运行"。这是把任务栏搬进 Dock 的基础。
///
/// 设计取舍：
/// - **事件驱动为主**：SetWinEventHook 只在窗口出现/消失/切换时唤醒，静止时不花任何 CPU。
/// - **2 秒安全轮询兜底**：钩子会漏（跨进程、时序、explorer 重启），完全信任钩子迟早出错。
/// - **按 exe 分组**：一个应用的多个窗口只占一个图标位，和任务栏一致。
/// - **顺序稳定**：新出现追加到末尾，消失就移除。不按前台重排，否则每切一次窗口
///   Dock 上的图标就换位置，用户点第二次就点空了。
/// </summary>
internal sealed class WindowTracker : IDisposable
{
    /// <summary>事件合并窗口：一次启动会连发十几个窗口事件，等它们落定再枚举。</summary>
    private const double DebounceSeconds = 0.18;

    /// <summary>安全轮询间隔：钩子漏了最多 2 秒也能自愈。</summary>
    private const double SafetySeconds = 2.0;

    private readonly List<TrackedApp> _apps = new(16);
    private readonly List<IntPtr> _hooks = new(2);
    private readonly StringBuilder _cls = new(128);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>必须持有委托引用：被 GC 回收后系统仍会回调，进程会直接崩。</summary>
    private readonly Native.WinEventProc _proc;

    private bool _pending = true;
    private double _nextAllowedAt;
    private double _nextSafetyAt;

    public IReadOnlyList<TrackedApp> Apps => _apps;

    /// <summary>
    /// 桌面构图可能变了：窗口出现 / 消失 / 最小化 / 切前台。
    ///
    /// 钩子本来就在监听这些事件，但过去只用来决定"图标列表要不要重算"。
    /// 而玻璃的颜色完全来自桌面抓屏 —— 用户关掉一个全屏黑窗口后，
    /// 抓屏还按空闲节奏（数秒一次）在跑，玻璃就会黑着不动，看着像卡死。
    /// 挂在事件上就能做到"页面一变，玻璃下一帧就变"。
    ///
    /// **回调跑在主线程的消息泵里**，只能置标志位，绝不能在这里枚举或抓屏。
    /// </summary>
    public Action? DesktopChanged;

    // ---- 自测计数：用来回答"这套追踪到底花多少钱" ----
    /// <summary>钩子回调次数。这是"系统每创建一个 UI 对象就打我们一次"的真实压力。</summary>
    public long EventCount { get; private set; }
    public long SyncCount { get; private set; }
    public double SyncMs { get; private set; }

    public WindowTracker()
    {
        _proc = OnWinEvent;
        // 0x8000..0x8003 = CREATE / DESTROY / SHOW / HIDE，窗口生命周期全覆盖
        Hook(Native.EVENT_OBJECT_CREATE, Native.EVENT_OBJECT_HIDE, Native.WINEVENT_SKIPOWNPROCESS);
        // 前台切换单独挂：它只影响指示点高亮，不值得连带整表重算
        Hook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, 0);
    }

    private void Hook(uint min, uint max, uint flags)
    {
        IntPtr h = Native.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | flags);
        if (h != IntPtr.Zero) _hooks.Add(h);
        else Logger.Warn($"SetWinEventHook(0x{min:X4}-0x{max:X4}) 失败，只能靠轮询");
    }

    /// <summary>
    /// 钩子回调。跑在本线程的消息泵里（OUTOFCONTEXT），
    /// 所以这里**绝不能**做枚举/COM 之类的重活，只能置标志位。
    /// </summary>
    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild,
        uint threadId, uint timeMs)
    {
        // idObject == 0 是 OBJID_WINDOW；其它是菜单、光标、控件之类的子对象
        if (idObject != 0 || idChild != 0) return;
        if (hwnd == IntPtr.Zero) return;
        EventCount++;
        _pending = true;
        DesktopChanged?.Invoke();
    }

    /// <summary>
    /// 每帧调用。绝大多数帧只走两个数值比较，开销可忽略。
    /// </summary>
    /// <param name="allowSafetyPoll">
    /// 是否允许触发"安全轮询"。一次枚举约 4ms，在鼠标扫过 Dock 的动画中间做
    /// 会直接吃掉一帧，所以动画期间把这个保险往后推 —— 事件驱动的同步不受影响。
    /// </param>
    public TrackChange Poll(bool allowSafetyPoll, bool allowSync = true)
    {
        double now = _clock.Elapsed.TotalSeconds;

        if (now >= _nextSafetyAt && allowSafetyPoll)
        {
            _nextSafetyAt = now + SafetySeconds;
            _pending = true;
        }

        if (!_pending || now < _nextAllowedAt) return TrackChange.None;

        // 动画期间不枚举：一次 EnumWindows 加逐窗口读 exe 路径要 3ms 左右，
        // 插在 16.7ms 的一帧里就是一次可见的顿挫。挂起标记，等手指停下来再补上 ——
        // 代价只是"程序刚打开时图标晚 0.2 秒出现"，换来的是连续滑动不掉帧。
        if (!allowSync) return TrackChange.None;

        _pending = false;
        _nextAllowedAt = now + DebounceSeconds;
        return Sync();
    }

    /// <summary>立刻枚举一次（启动时、探针用）。</summary>
    public TrackChange Sync()
    {
        double t0 = _clock.Elapsed.TotalSeconds;
        var found = new List<TrackedApp>(16);
        var byPath = new Dictionary<string, TrackedApp>(StringComparer.OrdinalIgnoreCase);
        uint self = (uint)Environment.ProcessId;
        IntPtr fg = Native.GetForegroundWindow();

        Native.EnumWindows((hwnd, _) =>
        {
            if (!TryResolve(hwnd, self, out string exe)) return true;

            if (!byPath.TryGetValue(exe, out TrackedApp? app))
            {
                app = new TrackedApp { ExePath = exe, Name = DisplayName(exe) };
                byPath[exe] = app;
                found.Add(app);
            }

            app.Windows.Add(hwnd);
            if (hwnd == fg) app.HasForeground = true;
            return true;
        }, IntPtr.Zero);

        // 未最小化的窗口排在前面：点图标时要激活的是"看得见的那个"，不是最小化的那个
        foreach (TrackedApp app in found)
        {
            if (app.Windows.Count > 1)
            {
                List<IntPtr> sorted = app.Windows.OrderBy(h => Native.IsIconic(h) ? 1 : 0).ToList();
                app.Windows.Clear();
                app.Windows.AddRange(sorted);
            }
        }

        // 沿用旧顺序：老应用保持原位，新应用追加到末尾
        var remaining = new Dictionary<string, TrackedApp>(byPath, StringComparer.OrdinalIgnoreCase);
        var merged = new List<TrackedApp>(found.Count);
        foreach (TrackedApp old in _apps)
        {
            if (remaining.Remove(old.ExePath, out TrackedApp? live)) merged.Add(live);
        }
        foreach (TrackedApp app in found)
        {
            if (remaining.ContainsKey(app.ExePath)) merged.Add(app);
        }

        bool layout = merged.Count != _apps.Count;
        bool highlight = false;

        for (int i = 0; i < merged.Count && !layout; i++)
        {
            TrackedApp a = merged[i];
            TrackedApp b = _apps[i];

            if (!string.Equals(a.ExePath, b.ExePath, StringComparison.OrdinalIgnoreCase) ||
                a.Representative != b.Representative ||
                a.Windows.Count != b.Windows.Count ||
                Fingerprint(a.Windows) != Fingerprint(b.Windows))
            {
                layout = true;
            }
            else if (a.HasForeground != b.HasForeground)
            {
                highlight = true;
            }
        }

        _apps.Clear();
        _apps.AddRange(merged);

        SyncCount++;
        SyncMs += (_clock.Elapsed.TotalSeconds - t0) * 1000.0;

        if (layout) return TrackChange.Layout;
        return highlight ? TrackChange.Highlight : TrackChange.None;
    }

    private static long Fingerprint(List<IntPtr> windows)
    {
        long h = 0;
        foreach (IntPtr w in windows) h ^= (long)w;
        return h;
    }

    // ---------------------------------------------------------------- 过滤

    /// <summary>
    /// 这个窗口算不算"一个正在运行的应用"。
    /// 规则基本对齐 Windows 自己的任务栏判定，只是不做分组策略。
    /// </summary>
    private bool TryResolve(IntPtr hwnd, uint self, out string exe)
    {
        exe = "";

        if (!Native.IsWindowVisible(hwnd)) return false;

        // 有 owner 的是对话框/弹窗，不是应用主窗口
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;

        long ex = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (!appWindow && (ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0)
            return false;

        _cls.Clear();
        if (Native.GetClassName(hwnd, _cls, _cls.Capacity) == 0) return false;
        string cls = _cls.ToString();
        if (IsShellClass(cls)) return false;

        if (Native.GetWindowTextLengthW(hwnd) == 0) return false;

        // UWP 挂起的窗口仍在枚举结果里，但被 DWM cloak 掉了 —— 那不是"在运行"
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 &&
            cloaked != 0) return false;

        if (!Native.GetWindowRect(hwnd, out Native.RECT rect)) return false;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == self) return false;

        // UWP：顶层永远是 ApplicationFrameHost 的 ApplicationFrameWindow，
        // 真正跑业务的是它下面的 CoreWindow 子窗口，进程也属于真正的应用。
        if (cls.Equals("ApplicationFrameWindow", StringComparison.Ordinal))
        {
            IntPtr core = Native.FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core == IntPtr.Zero) return false;   // 没有 CoreWindow = 挂起，等同于没在跑
            Native.GetWindowThreadProcessId(core, out uint corePid);
            if (corePid != 0) pid = corePid;
        }

        if (!TryGetExePath(pid, out exe)) return false;
        if (exe.Length == 0) return false;
        if (exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    /// <summary>
    /// 系统壳窗口。这些要么是桌面本身，要么是开始菜单/搜索/任务视图这类系统 UI，
    /// 它们有自己的唤出方式，塞进 Dock 里既没用又会被误点。
    /// </summary>
    private static readonly string[] ShellClasses =
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "Shell_InputSwitchTopLevelWindow",
        "Windows.UI.Core.CoreWindow",              // 顶层 CoreWindow = 开始菜单/搜索/操作中心
        "Windows.Internal.Shell.TabProxyWindow",
        "ApplicationManager_DesktopShellWindow",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.Internal.UI.Shell.WindowTabManager",
        "TaskListThumbnailWnd",
        "EdgeUiInputTopWndClass",
        "ThumbnailDeviceHelperWnd",
        "LiquidGlassDesktopDock",
    };

    private static bool IsShellClass(string cls)
    {
        foreach (string s in ShellClasses)
            if (cls.Equals(s, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 取进程的 exe 全路径。提权进程（任务管理器等）可能拿不到，
    /// 那就返回 false 而不是硬闯 —— 拿不到图标的应用不该拖慢或拖挂整个 Dock。
    /// </summary>
    private static bool TryGetExePath(uint pid, out string path)
    {
        path = "";
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!Native.QueryFullProcessImageNameW(h, 0, sb, ref size)) return false;
            if (size <= 0) return false;
            path = sb.ToString(0, size);
            return path.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 显示名：优先用文件版本信息里的 FileDescription（"记事本"而不是 "notepad"），
    /// 拿不到就退回文件名。结果缓存，同一路径只查一次盘。
    /// </summary>
    private static string DisplayName(string exe)
    {
        lock (NameCache)
        {
            if (NameCache.TryGetValue(exe, out string? cached)) return cached;
        }

        string name = "";
        try
        {
            name = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim() ?? "";
        }
        catch
        {
            // 无版本资源或文件被独占，退回文件名
        }

        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(exe);
        // 有些系统组件的 FileDescription 就是原样文件名（画图是 "mspaint.exe"），
        // 直接显示会很难看，这里统一去掉扩展名
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) name = exe;

        lock (NameCache) NameCache[exe] = name;
        return name;
    }

    public void Dispose()
    {
        foreach (IntPtr h in _hooks) Native.UnhookWinEvent(h);
        _hooks.Clear();
        GC.KeepAlive(_proc);
    }
}
