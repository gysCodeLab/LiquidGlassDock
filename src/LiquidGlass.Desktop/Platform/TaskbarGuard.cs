using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using LiquidGlass.Desktop.App;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 任务栏接管。做法是给任务栏窗口加 <c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c>
/// 并把整窗 alpha 设为 0：窗口仍在、AppBar 注册不变、点击穿透，但用户完全看不见。
///
/// 为什么不直接 <c>ShowWindow(SW_HIDE)</c>（第一版的做法）：
/// 系统任务栏开着自己的"自动隐藏"逻辑，它不是靠窗口可见性判断的，
/// 而是靠鼠标是否压到屏幕边缘的触发带。SW_HIDE 只是藏了窗口，
/// 鼠标一碰底边 explorer 就自己把窗口显示回来，用户会看到任务栏"弹出来"。
/// 改成整窗透明后，explorer 想怎么显示都行——显示出来也是全透明的，
/// 用户看到的仍然是 Dock，不会再有第二次接管失败。
///
/// 仍然不做的事：不结束 explorer.exe、不替换 Shell、不改注册表、不需要管理员权限。
///
/// 恢复机制（这是普通桌面程序能做到的最大努力，边界必须说清楚）：
/// <list type="bullet">
/// <item>正常退出 / 捕获到异常 → 立即恢复；</item>
/// <item>进程被强杀 → 下次启动凭 shell-state.json 判定并恢复；</item>
/// <item>explorer 重启 → 任务栏窗口重建，样式自动消失；</item>
/// <item>蓝屏、断电、强制关机 → 无法自动恢复，但重新登录必然恢复；</item>
/// <item>始终保留 <c>--restore-taskbar</c> 这条不依赖 Dock 运行的独立出口。</item>
/// </list>
/// </summary>
internal static class TaskbarGuard
{
    private const string PrimaryClass = "Shell_TrayWnd";              // 主任务栏
    private const string SecondaryClass = "Shell_SecondaryTrayWnd";    // 多显示器的副任务栏

    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiquidGlassDesktop");

    private static readonly string StatePath = Path.Combine(StateDir, "shell-state.json");

    /// <summary>接管前的原始扩展样式，退出时按它精确还原（只在同进程内有效）。</summary>
    private static readonly Dictionary<IntPtr, long> OriginalExStyle = new();

    private sealed class State
    {
        public int Pid { get; set; }
        public string StartedUtc { get; set; } = "";
        public bool Hidden { get; set; }
        public string HiddenAtUtc { get; set; } = "";
        public int WindowCount { get; set; }
    }

    /// <summary>
    /// 枚举所有任务栏窗口（主 + 副）。按类名匹配，并排除本进程自己的窗口——
    /// 隐藏任务栏类工具在多屏或自带同名窗口时会"自己打自己"，这个判断就是防它。
    ///
    /// explorer 的 PID 带缓存：这个函数现在每秒被调用 4 次，而
    /// <c>Process.GetProcessById().ProcessName</c> 单次要 1~2ms，不能放在热路径里。
    /// explorer 重启后 PID 变了，缓存会匹配不到，此时刷新一次重试。
    /// </summary>
    public static List<IntPtr> FindTaskbars()
    {
        uint self = (uint)Environment.ProcessId;
        var candidates = new List<(IntPtr Hwnd, uint Pid)>(4);
        var sb = new StringBuilder(64);

        Native.EnumWindows((hwnd, _) =>
        {
            sb.Clear();
            if (Native.GetClassName(hwnd, sb, sb.Capacity) == 0) return true;
            string cls = sb.ToString();
            if (!cls.Equals(PrimaryClass, StringComparison.Ordinal) &&
                !cls.Equals(SecondaryClass, StringComparison.Ordinal))
                return true;

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) return true;

            candidates.Add((hwnd, pid));
            return true;
        }, IntPtr.Zero);

        if (candidates.Count == 0) return new List<IntPtr>(0);

        HashSet<uint> exp = DetectExplorerPids();
        var found = new List<IntPtr>(candidates.Count);
        foreach ((IntPtr hwnd, uint pid) in candidates)
            if (exp.Contains(pid)) found.Add(hwnd);

        // PID 对不上 = explorer 重启过（或缓存过期），刷新后重判一次
        if (found.Count == 0)
        {
            _explorerPids = null;
            exp = DetectExplorerPids();
            foreach ((IntPtr hwnd, uint pid) in candidates)
                if (exp.Contains(pid)) found.Add(hwnd);
        }

        return found;
    }

    private static readonly bool _skipKidTransparent =
        Environment.GetEnvironmentVariable("LGD_NO_KID_TRANSPARENT") == "1";

    private static HashSet<uint>? _explorerPids;

    // ------------------------------------------------------------ 开机抢跑

    /// <summary>抢跑线程本体。null = 还没启动过。</summary>
    private static Thread? _earlyThread;

    /// <summary>置 true 后抢跑线程下一轮就退出（主循环的任务栏守护已接管）。</summary>
    private static volatile bool _earlyStop;

    /// <summary>
    /// 开机抢跑：**在 D3D 初始化、读固定项、建窗口之前**就开一个后台线程盯着 Shell_TrayWnd，
    /// 一出现立刻隐形。
    ///
    /// 解决的是"登录后先看到 Windows 任务栏好几秒，然后才换成 Dock"：
    /// 原来的顺序是 等 Shell 就绪 → 读固定项 → 初始化 D3D → 画首帧 → 才隐藏任务栏，
    /// 这几步串起来短则 1 秒、慢则十几秒，这段时间用户看到的就是那条又宽又大的系统任务栏。
    ///
    /// 现在隐藏任务栏和初始化并行跑：任务栏窗口一被 explorer 建出来，
    /// 20ms 内的下一轮就会把它压掉，用户看到的是"壁纸 → Dock"，中间没有任务栏。
    ///
    /// 只在 <c>--autostart</c> 时启动。手动双击启动本身就是一两秒的事，
    /// 而且"先建好窗口再隐藏"能保证万一初始化失败也还有任务栏可用。
    /// </summary>
    public static void StartEarlyStealth()
    {
        if (_earlyThread is not null) return;
        _earlyStop = false;
        _earlyThread = new Thread(EarlyStealthLoop)
        {
            IsBackground = true,
            Name = "TaskbarEarlyStealth",
        };
        _earlyThread.Priority = ThreadPriority.AboveNormal;
        _earlyThread.Start();
        Logger.Info("开机抢跑: 已开始盯任务栏窗口（出现即隐形，与初始化并行）");
    }

    /// <summary>主循环的任务栏守护接管后调用，让抢跑线程退出，避免两处同时改任务栏。</summary>
    public static void StopEarlyStealth() => _earlyStop = true;

    private static void EarlyStealthLoop()
    {
        Stopwatch sw = Stopwatch.StartNew();
        int firstAt = -1;

        // 30 秒足够覆盖一次完整登录（SSD 上 Shell 通常 2~5 秒就绪）。
        // 超时也不报错：主循环的 GuardTaskbar 是长期守护，这里只是抢头几秒。
        while (!_earlyStop && sw.ElapsedMilliseconds < 30_000)
        {
            try
            {
                if (FindTaskbars().Count > 0 && AnyNotStealthed())
                {
                    int n = Hide();
                    if (n > 0)
                    {
                        if (firstAt < 0) firstAt = (int)sw.ElapsedMilliseconds;
                        Logger.Info($"开机抢跑: 任务栏出现后 {(int)sw.ElapsedMilliseconds}ms 内已隐形 {n} 个窗口");
                    }
                }
            }
            catch (Exception ex)
            {
                // 抢跑失败不能影响启动，主循环还会再补
                Logger.Warn("开机抢跑异常: " + ex.Message);
            }

            // 头 5 秒高频（20ms 一轮，几乎赶上 explorer 建窗口的瞬间），
            // 之后降频 —— 这段只是防"任务栏被重建"，没必要一直高频轮询。
            Thread.Sleep(sw.ElapsedMilliseconds < 5_000 ? 20 : 150);
        }
    }

    /// <summary>
    /// 收集所有 explorer 进程的 PID（不是"取第一个"）。本机实测会有多个：
    /// 一个正常运行的任务栏宿主，外加一个几十 KB 的空壳残留进程。
    /// 只取第一个会取到空壳，然后判定"找不到任务栏"——这个坑踩过一次。
    /// </summary>
    private static HashSet<uint> DetectExplorerPids()
    {
        if (_explorerPids is { Count: > 0 }) return _explorerPids;

        var set = new HashSet<uint>();
        try
        {
            foreach (Process p in Process.GetProcessesByName("explorer"))
                set.Add((uint)p.Id);
        }
        catch
        {
            // 拿不到就退化成空集，FindTaskbars 会返回"找不到"而不是误伤别的窗口
        }

        if (set.Count > 0) _explorerPids = set;
        return set;
    }

    /// <summary>让所有任务栏窗口隐形并落盘状态。返回实际生效的数量。</summary>
    public static int Hide()
    {
        int n = 0;
        foreach (IntPtr h in FindTaskbars())
        {
            if (!Native.IsWindow(h)) continue;
            if (MakeInvisible(h)) n++;
        }

        if (n > 0) WriteState(n);
        return n;
    }

    /// <summary>
    /// 单窗口隐形。顺序很重要：先改扩展样式 → 必须跟一次 SWP_FRAMECHANGED
    /// 让 DWM 重建窗口帧 → 最后才设 alpha，否则 alpha 会被忽略。
    ///
    /// 子窗口单独处理：整窗 alpha 已经让整棵窗口树不可见，但
    /// <c>WS_EX_TRANSPARENT</c> 只管"设置它的那个窗口"，子窗口仍然会拦鼠标——
    /// 不递归给子窗口加，用户就会点到一条看不见的任务栏（开始按钮、托盘图标都在那儿）。
    /// 子窗口只加 TRANSPARENT，不加 LAYERED：只设 LAYERED 而不设 alpha 会让它变黑。
    /// </summary>
    private static bool MakeInvisible(IntPtr h)
    {
        long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        if (!OriginalExStyle.ContainsKey(h)) OriginalExStyle[h] = ex;

        long want = ex | Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT;
        if (ex != want)
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, (IntPtr)want);

        Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
            Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        bool alphaOk = Native.SetLayeredWindowAttributes(h, 0, 0, Native.LWA_ALPHA);

        // 子窗口穿透是"被 explorer 重新显示的那一瞬间"才有用的保险。
        // 它是本类里最贵的一步（每个子窗口一次跨进程 SetWindowLongPtr），
        // 用环境变量留一个关闭开关，方便量化它到底值多少 CPU。
        int kids = _skipKidTransparent ? 0 : SetChildTransparent(h, true);

        // 关键一步：把窗口也藏起来。
        // 只要窗口还在合成队列里，DWM 每帧都要合成这个全透明的大窗口，
        // 实测代价是本进程 CPU 从 1.4% 涨到 4.7%（帧率完全没变，纯粹是白烧）。
        // 隐藏的窗口不参与合成，开销归零；而上面加的透明样式继续留在这个窗口上，
        // 万一 explorer 的自动隐藏逻辑又把它 ShowWindow 出来，用户看到的仍然是全透明的，
        // 250ms 后守护再把它藏回去。
        Native.ShowWindow(h, Native.SW_HIDE);

        // 读回校验：样式真的写进去了、alpha 调用真的成功了，才算接管成功
        long after = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        bool layered = (after & Native.WS_EX_LAYERED) != 0;
        bool transparent = (after & Native.WS_EX_TRANSPARENT) != 0;
        return alphaOk && layered && transparent && kids >= 0;
    }

    /// <summary>递归给任务栏的所有子窗口加上/去掉点击穿透。返回处理数量。</summary>
    private static int SetChildTransparent(IntPtr root, bool on)
    {
        int n = 0;
        Native.EnumChildWindows(root, (c, _) =>
        {
            long ex = (long)Native.GetWindowLongPtr(c, Native.GWL_EXSTYLE);

            long want;
            if (!on && OriginalExStyle.TryGetValue(c, out long orig))
                want = orig;                                   // 恢复成接管前的原样
            else
            {
                if (!OriginalExStyle.ContainsKey(c)) OriginalExStyle[c] = ex;
                want = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~(long)Native.WS_EX_TRANSPARENT;
            }

            if (ex != want)
            {
                Native.SetWindowLongPtr(c, Native.GWL_EXSTYLE, (IntPtr)want);
                n++;
            }

            return true;
        }, IntPtr.Zero);

        return n;
    }

    /// <summary>恢复所有任务栏窗口（不激活、不抢焦点）。单次失败会自动重试。</summary>
    public static int Show() => Show(false);

    /// <param name="restartExplorerIfMissing">
    /// 一个任务栏窗口都找不到、连 explorer 进程也没有时，是否尝试把它拉起来。
    /// 只有守护进程和急救入口该传 true；正常退出时传 false，
    /// 避免在 explorer 短暂重建窗口的瞬间误开一个资源管理器窗口。
    /// </param>
    public static int Show(bool restartExplorerIfMissing)
    {
        const int Attempts = 4;
        int best = 0;
        bool sawWindow = false;

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            List<IntPtr> bars = FindTaskbars();
            if (bars.Count > 0) sawWindow = true;

            // explorer 整个没了（不是正在重建窗口）：任务栏不会自己回来，
            // 这是唯一能救它的动作。先确认没有任何 explorer 进程，避免误开资源管理器。
            if (bars.Count == 0 && restartExplorerIfMissing && attempt == 1 &&
                DetectExplorerPids().Count == 0)
            {
                if (TryStartExplorer())
                    Logger.Warn("任务栏恢复: explorer 未运行，已尝试重新拉起 shell");
                Thread.Sleep(600);
                _explorerPids = null;
                bars = FindTaskbars();
                if (bars.Count > 0) sawWindow = true;
            }

            int shown = 0;
            foreach (IntPtr h in bars)
            {
                if (!Native.IsWindow(h)) continue;
                if (MakeVisible(h)) shown++;
            }

            if (shown > best) best = shown;

            // 已经没有任何窗口还带着我们的隐形样式 = 恢复完成。
            if (bars.Count > 0 && !IsStealthed()) break;

            if (attempt < Attempts - 1)
            {
                // explorer 刚重启时任务栏窗口会在几百毫秒后被重建，旧 PID 缓存也要丢掉。
                Thread.Sleep(120 * (attempt + 1));
                _explorerPids = null;
            }
        }

        OriginalExStyle.Clear();

        // 关键：恢复没做全时必须把 shell-state.json 留给下一次启动。
        // 以前这里无条件删除 —— explorer 正在重建窗口的那一下恢复 0 个，
        // 标记却被清掉，于是这个任务栏再也没人负责变回来。
        if (!IsStealthed()) TryDeleteState();
        else Logger.Warn("任务栏恢复: 仍有窗口处于隐形状态，保留恢复标记以便下次启动重试");

        if (best == 0 && sawWindow) Logger.Warn("任务栏恢复: 找到了任务栏窗口，但一个都没能还原");
        return best;
    }

    private static bool TryStartExplorer()
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
            });
            return p is not null;
        }
        catch (Exception ex)
        {
            Logger.Warn("任务栏恢复: 拉起 explorer.exe 失败 " + ex.Message);
            return false;
        }
    }

    private static bool MakeVisible(IntPtr h)
    {
        // 先恢复不透明度，再去样式，最后显示：任何一步失败都还剩可见的窗口
        Native.SetLayeredWindowAttributes(h, 0, 255, Native.LWA_ALPHA);

        long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
        long want = OriginalExStyle.TryGetValue(h, out long orig)
            ? orig
            : ex & ~(long)(Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT);

        if (ex != want)
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, (IntPtr)want);

        Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER |
            Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        SetChildTransparent(h, false);
        Native.ShowWindow(h, Native.SW_SHOWNA);
        return Native.IsWindowVisible(h);
    }

    /// <summary>
    /// 启动时调用：如果上一次没有正常恢复就退出了，这里补上。
    /// 返回非空字符串表示做过恢复动作（供调用方写日志）。
    /// </summary>
    public static string? RecoverStale()
    {
        if (!File.Exists(StatePath)) return null;

        State? st = null;
        try
        {
            st = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath));
        }
        catch
        {
            // 状态文件坏了也要能恢复，不能因为读不出 JSON 就放弃
            st = null;
        }

        if (st is null || !st.Hidden)
        {
            TryDeleteState();
            return null;
        }

        // 状态文件的主人还活着，交给它自己管，别去动它的任务栏
        if (IsOwnerAlive(st)) return null;

        int restored = Show();
        return $"检测到上次未正常退出（pid={st.Pid}），已恢复 {restored} 个任务栏窗口";
    }

    /// <summary>
    /// 守护：explorer 重启或显示设置变化会把任务栏窗口重建出来（新窗口没有我们的样式），
    /// 自动隐藏逻辑也可能在鼠标碰底边时把它重新 ShowWindow 出来。
    /// 这里两件事都管：样式丢了就补样式，藏好的窗口被放出来就再藏回去。
    /// 返回本次处理的数量。
    /// </summary>
    public static int EnsureHidden()
    {
        int n = 0;
        foreach (IntPtr h in FindTaskbars())
        {
            if (!Native.IsWindow(h)) continue;

            long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            bool stillStealth = (ex & Native.WS_EX_LAYERED) != 0 &&
                                (ex & Native.WS_EX_TRANSPARENT) != 0;
            bool stillHidden = !Native.IsWindowVisible(h);

            if (stillStealth && stillHidden) continue;

            if (MakeInvisible(h)) n++;
        }

        return n;
    }

    /// <summary>是否还有任务栏窗口没被接管（样式缺失 = explorer 重建过，或已恢复原状）。</summary>
    public static bool AnyNotStealthed()
    {
        foreach (IntPtr h in FindTaskbars())
        {
            if (!Native.IsWindow(h)) continue;
            long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_LAYERED) == 0) return true;
        }

        return false;
    }

    /// <summary>
    /// 任务栏现在是不是"已经被设成隐形"（LAYERED + TRANSPARENT 都在）。
    ///
    /// 启动自愈要用它：配置文件说不需要接管，可实测任务栏已经隐形了 ——
    /// 说明上一次进程没把样式还回去（被强杀、或者状态文件和配置不同步）。
    /// 这种时候如果还不接管，任务栏就会永久消失，而且**没有任何进程认为该由自己恢复它**。
    /// </summary>
    public static bool IsStealthed()
    {
        foreach (IntPtr h in FindTaskbars())
        {
            if (!Native.IsWindow(h)) continue;
            long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_LAYERED) != 0 && (ex & Native.WS_EX_TRANSPARENT) != 0)
                return true;
        }

        return false;
    }

    /// <summary>崩溃恢复标记是否还留着（= 上次没正常还回去）。</summary>
    public static bool HasStaleState() => File.Exists(StatePath);

    /// <summary>
    /// 读残留状态里记录的"隐藏者 PID"（没有状态文件 / 不是隐藏状态时返回 0）。
    ///
    /// 守护进程用它判断"死掉的那个 Dock 当时到底有没有在接管任务栏"：
    /// 接管过就必须带着 --hide-taskbar 重启，否则重启出来的 Dock 会让
    /// Windows 任务栏和玻璃 Dock 同时出现在屏幕底部 —— 那正是用户最烦的那个画面。
    /// </summary>
    public static int HiddenOwnerPid()
    {
        if (!File.Exists(StatePath)) return 0;
        try
        {
            State? st = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath));
            return st is { Hidden: true } ? st.Pid : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsOwnerAlive(State st)
    {
        try
        {
            using Process p = Process.GetProcessById(st.Pid);
            if (p.HasExited) return false;

            if (!DateTime.TryParse(st.StartedUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime recorded))
                return false;

            // PID 复用检测：启动时间偏差超过 2 秒，说明这个 PID 已经被别的进程占了
            double delta = Math.Abs((p.StartTime.ToUniversalTime() - recorded.ToUniversalTime()).TotalSeconds);
            return delta < 2.0;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteState(int hiddenCount)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            using Process self = Process.GetCurrentProcess();
            var st = new State
            {
                Pid = self.Id,
                StartedUtc = self.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Hidden = true,
                HiddenAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                WindowCount = hiddenCount,
            };

            string tmp = StatePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StatePath, true);
        }
        catch
        {
            // 状态写不进去不影响隐藏本身，只是失去崩溃恢复能力
        }
    }

    private static void TryDeleteState()
    {
        try
        {
            if (File.Exists(StatePath)) File.Delete(StatePath);
        }
        catch
        {
            // 删不掉就留着，下次启动的 RecoverStale 会重新判断
        }
    }
}
