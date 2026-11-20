using System.Runtime.InteropServices;
using LiquidGlass.Desktop.App;
using LiquidGlass.Desktop.Platform;

namespace LiquidGlass.Desktop;

internal static class Program
{
    private const string MutexName = @"Local\LiquidGlassDesktop.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);

        // 守护进程：盯住 Dock，意外死亡时恢复任务栏并重启。
        // 必须在单实例锁之前处理 —— 它本来就不是"一份 UI"，不该跟 Dock 抢锁。
        if (options.WatchdogPid > 0)
            return DockWatchdog.Run(options.WatchdogPid);

        if (options.IconDumpDir is not null)
            return IconProbe.Run(options.IconDumpDir, 130);

        // 设置里的"重新启动 Dock"：先等旧进程退干净，再拉起新实例。
        // 不能由旧进程自己直接 Process.Start(self)：单实例锁还没释放，
        // 新进程会撞锁，用户看到的是"点了重启，结果 Dock 直接没了"。
        if (options.RelaunchAfterPid > 0)
            return RelaunchAfter(options.RelaunchAfterPid);

        if (options.LabDir is not null)
        {
            DockConfig labConfig = DockConfig.Load();
            if (options.Transmission >= 0f) labConfig.Glass.Transmission = options.Transmission;
            string shader = Path.Combine(AppContext.BaseDirectory, "Shaders", "Glass.hlsl");
            string outDir = options.LabOut ?? Path.Combine(AppContext.BaseDirectory, "lab-output");
            return MaterialLab.Run(options.LabDir, outDir, shader, labConfig.Glass,
                options.LabPanelW, options.LabPanelH);
        }

        // 独立恢复入口：不依赖 Dock 是否在运行，也不看配置。
        // 这是"我再也看不到任务栏了"时唯一的硬出口，必须最先处理。
        if (options.RestoreTaskbar)
        {
            IntPtr running = Native.FindWindow(DockApp.ClassName, null);
            if (running != IntPtr.Zero)
            {
                // 先让在跑的实例交出托管权，否则它的守护会立刻把任务栏再藏回去
                Native.PostMessageW(running, Native.WM_APP + 10, IntPtr.Zero, IntPtr.Zero);
                Thread.Sleep(120);
            }

            // 急救入口连 explorer 一起救：用户点这个的时候任务栏已经没了，
            // 多开一次 explorer 的代价远小于"点了没反应"。
            int shown = TaskbarGuard.Show(restartExplorerIfMissing: true);
            bool stillHidden = TaskbarGuard.IsStealthed();

            // 记住"用户不想要隐藏"，否则下次启动又会被藏回去
            DockConfig restoreCfg = DockConfig.Load();
            if (restoreCfg.HideTaskbar)
            {
                restoreCfg.HideTaskbar = false;
                restoreCfg.Save();
            }

            Logger.Info($"手动恢复任务栏：{shown} 个窗口 仍有窗口隐形={stillHidden}");

            // 成功时不弹框：任务栏重新出现本身就是最直接的反馈，
            // 弹个框反而会挡住刚回来的任务栏。只有"没救到"才需要解释。
            if (stillHidden || shown == 0)
            {
                Native.MessageBoxW(IntPtr.Zero,
                    stillHidden
                        ? "还有任务栏窗口没能还原。请再运行一次「恢复任务栏.bat」；\n" +
                          "仍然不行时，用任务管理器重启 explorer.exe。"
                        : "没有找到被隐藏的任务栏，Windows 任务栏应该本来就是正常的。",
                    "Liquid Glass Dock",
                    Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_TOPMOST);
            }

            return 0;
        }

        // 诊断：验证崩溃恢复判定（上次进程已死 / PID 被复用时应恢复任务栏）
        if (options.RecoverProbe)
        {
            bool hadState = TaskbarGuard.HasStaleState();
            string? msg = TaskbarGuard.RecoverStale();
            Logger.Info($"[recover] 有残留状态={hadState} 结果={(msg ?? "未动作")} " +
                        $"残留仍在={TaskbarGuard.HasStaleState()} 任务栏可见={TaskbarGuard.AnyNotStealthed()}");
            return 0;
        }

        // 诊断：只枚举一次窗口，把追踪结果写进日志。
        // 悬停/渲染这类问题可以截图，但"哪些窗口被算成应用"只能靠清单核对。
        if (options.WindowsProbe)
        {
            using var tracker = new WindowTracker();
            TrackChange change = tracker.Sync();
            Logger.Info($"[windows] 变化={change} 应用数={tracker.Apps.Count}");

            foreach (TrackedApp entry in tracker.Apps)
            {
                Logger.Info($"[windows]   {(entry.HasForeground ? "*" : " ")} {entry.Name} " +
                            $"| {entry.ExePath} | 窗口={entry.Windows.Count} " +
                            $"| 代表窗口=0x{entry.Representative.ToInt64():X}");
            }

            Logger.Info($"[windows] 配置固定项={DockConfig.Load().Items.Count}");
            return 0;
        }

        // 诊断：把 Windows 任务栏的固定项清单连顺序一起导出来，并附一个 .lnk 的完整属性表。
        // "固定区图标不对/顺序不对"这类问题只有这张表能核对。
        if (options.PinsProbe)
        {
            List<TaskbarPin> pins = TaskbarPins.Enumerate();
            Logger.Info($"[pins] 任务栏固定项 {pins.Count} 个");
            foreach (TaskbarPin pin in pins)
            {
                Logger.Info($"[pins]   rank={pin.Rank,-6} {pin.Name} | {pin.LnkPath} | 目标={pin.TargetPath}");
            }

            if (pins.Count > 0)
            {
                Logger.Info($"[pins] 属性表: {pins[0].LnkPath}");
                Logger.Info(TaskbarPins.DumpProperties(pins[0].LnkPath));
            }

            DockConfig cfg = DockConfig.Load();
            Logger.Info($"[pins] 配置: 同步={cfg.SyncTaskbarPins} 隐藏={cfg.HiddenPins.Count} 手加={cfg.Items.Count}");
            return 0;
        }

        // 一键切换：Dock 在跑 -> 关掉它（任务栏自动恢复）；没跑 -> 启动它并隐藏任务栏。
        // 这是给"我想隐藏就隐藏、想恢复就恢复"准备的那个开关，双击 bat 即可。
        if (options.ToggleDock)
            return ToggleDock();

        // 只切任务栏，Dock 继续运行。给"我想把任务栏放回来一下"用，不用关 Dock。
        if (options.SwitchTaskbar)
        {
            IntPtr dock = Native.FindWindow(DockApp.ClassName, null);
            if (dock == IntPtr.Zero)
            {
                Logger.Warn("--switch-taskbar: Dock 没在运行，无法单独切换任务栏");
                Native.MessageBoxW(IntPtr.Zero,
                    "Dock 没有在运行，这时没法只切换任务栏。\n\n" +
                    "想连 Dock 一起开关，请双击「切换任务栏.bat」。\n" +
                    "只想要任务栏回来，请双击「恢复任务栏.bat」。",
                    "Liquid Glass Dock",
                    Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_TOPMOST);
                return 1;
            }

            Native.PostMessageW(dock, Native.WM_APP_TOGGLE_TASKBAR, IntPtr.Zero, IntPtr.Zero);
            Logger.Info("--switch-taskbar: 已请求运行中的 Dock 切换任务栏显隐");
            return 0;
        }

        // 诊断：只跑一遍"隐藏 -> 检查 -> 恢复"，不建窗口、不占单实例锁，
        // 因此可以在用户正在运行 Dock 的同时安全验证任务栏接管逻辑。
        if (options.TaskbarProbe)
        {
            int total = TaskbarGuard.FindTaskbars().Count;
            bool before = TaskbarGuard.AnyNotStealthed();
            int hidden = TaskbarGuard.Hide();
            Thread.Sleep(700);
            bool during = TaskbarGuard.AnyNotStealthed();
            bool stateWritten = TaskbarGuard.HasStaleState();
            int shown = TaskbarGuard.Show();
            Thread.Sleep(250);
            bool after = TaskbarGuard.AnyNotStealthed();
            bool stateCleared = !TaskbarGuard.HasStaleState();

            Logger.Info($"[probe] 任务栏窗口={total} 隐形前未接管={before} 隐形成功={hidden} " +
                        $"隐形后未接管={during} 状态文件={stateWritten} 恢复={shown} " +
                        $"恢复后未接管={after} 状态已清={stateCleared}");
            return 0;
        }

        // 诊断：只做"隐形 -> 等待 -> 恢复"，不建窗口、不占单实例锁。
        // 等待期间可以用外部截图确认任务栏是否真的完全不可见（这是最容易做错的一步，
        // 只靠读回样式位不能证明用户看不见）。
        if (options.StealthProbeSeconds > 0)
        {
            int total = TaskbarGuard.FindTaskbars().Count;
            int hidden = TaskbarGuard.Hide();
            Logger.Info($"[stealth] 任务栏窗口={total} 隐形成功={hidden}，保持 {options.StealthProbeSeconds}s 供外部截图");
            System.Threading.Thread.Sleep(options.StealthProbeSeconds * 1000);
            int shown = TaskbarGuard.Show();
            Logger.Info($"[stealth] 已恢复 {shown} 个窗口 任务栏已还原={!TaskbarGuard.AnyNotStealthed()}");
            return 0;
        }

        // 关闭已在运行的实例（不启动新窗口）。给不熟悉右键菜单的人一条明确出口。
        if (options.QuitRequested)
        {
            IntPtr existing = Native.FindWindow(DockApp.ClassName, null);
            if (existing == IntPtr.Zero)
            {
                // 没有实例在跑时静默返回：这个参数会被启动脚本当作"先清理旧的"来调，
                // 弹框会把脚本卡住。
                Logger.Info("--quit: 没有正在运行的实例");
                return 0;
            }

            Native.PostMessageW(existing, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Logger.Info("已向运行中的实例发送退出请求");
            return 0;
        }

        // 安装 / 卸载开机自启。放在单实例锁之前：这两个动作不需要 Dock 在跑。
        if (options.InstallAutostart)
            return Autostart.Install(out string installMsg) ? Log(installMsg, 0) : Log(installMsg, 1);
        if (options.RemoveAutostart)
            return Autostart.Remove(out string removeMsg) ? Log(removeMsg, 0) : Log(removeMsg, 1);

        // 单实例：避免重复启动后两个 Dock 互相压着
        IntPtr mutex = Native.CreateMutex(IntPtr.Zero, true, MutexName);
        if (!options.NoMutex && Marshal.GetLastWin32Error() == Native.ERROR_ALREADY_EXISTS)
        {
            // 带着「打开设置 / 打开启动台」来的第二个进程：不启动自己，
            // 把请求投给正在运行的那个实例，由它弹面板。
            // 没有这一步，桌面上的「Dock 设置」快捷方式永远只会撞锁退出。
            if (options.OpenSettings || options.OpenLaunchpad)
            {
                IntPtr dock = Native.FindWindow(DockApp.ClassName, null);
                if (dock != IntPtr.Zero)
                {
                    Native.PostMessageW(dock,
                        options.OpenSettings
                            ? Native.WM_LIQUIDGLASS_OPEN_SETTINGS
                            : Native.WM_LIQUIDGLASS_OPEN_LAUNCHPAD,
                        IntPtr.Zero, IntPtr.Zero);
                    Logger.Info("已有实例在运行，已把「打开面板」请求转交给它");
                    return 0;
                }
            }

            Logger.Warn("已有实例在运行，本次启动退出");

            // 开机自启撞上已有实例（用户先手动开过了）必须静默走人：
            // 登录时弹一个"已经在运行了"的框，比不启动还烦人。
            if (options.Autostart) return 0;

            // 静默退出会让用户以为程序坏了，这里必须给可见反馈
            Native.MessageBoxW(IntPtr.Zero,
                "液态玻璃 Dock 已经在运行了。\n\n" +
                "关闭方法：在 Dock 上点鼠标右键 → 退出 Dock；\n" +
                "或者双击「切换任务栏.bat」再点一次。",
                "Liquid Glass Dock",
                Native.MB_OK | Native.MB_ICONINFORMATION | Native.MB_TOPMOST | Native.MB_SETFOREGROUND);
            return 0;
        }

        GC.KeepAlive(mutex);

        // 配置要先读：抢跑线程需不需要开，取决于"这次到底要不要接管任务栏"。
        DockConfig config = DockConfig.Load();
        Logger.Info("==== 启动 ==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Logger.Info($"配置: {DockConfig.FilePath} items={config.Items.Count} safeMode={options.SafeMode} " +
                    $"hideTaskbar={options.HideTaskbar || config.HideTaskbar}");

        // 安全模式一律不接管任务栏：能起来比好看重要
        bool hideTaskbar = (options.HideTaskbar || config.HideTaskbar) && !options.SafeMode;

        // 上次如果是被强杀的，任务栏还藏在那儿，这里补恢复。
        // 必须在抢跑之前：抢跑一旦接管，状态文件的 PID 就是我们自己，
        // RecoverStale 会认为"主人还活着"，那次恢复就永远补不上了。
        string? recovered = TaskbarGuard.RecoverStale();
        if (recovered is not null) Logger.Warn(recovered);

        // 自愈：配置说"不接管"，但实测任务栏已经隐形了。
        //
        // 这种情况真的发生过：配置被写回 false，而任务栏还是透明+不可见的状态，
        // 于是新旧两个进程都认为"不该由我管"，任务栏就一直不见 ——
        // 用户看到的是一条永远消失的任务栏，而且没人会去恢复它。
        // 只要实测到隐形，本次就接管；退出时自然会还回去。
        if (!hideTaskbar && !options.SafeMode && TaskbarGuard.IsStealthed())
        {
            hideTaskbar = true;
            Logger.Warn("任务栏已处于隐形状态但配置未要求接管 → 本次接管（否则它会一直不见且无人恢复）");
        }

        // 开机抢跑：和下面"等 Shell 就绪 / 初始化 D3D"并行，任务栏一出现就压掉。
        // 这是"登录后先看到好幾秒 Windows 任务栏"那个问题的解法 ——
        // 串行做的顺序是 等 Shell → 初始化 → 画首帧 → 才隐藏，中间全是任务栏在露脸。
        if (options.Autostart && hideTaskbar) TaskbarGuard.StartEarlyStealth();

        // 开机自启：登录后 explorer 还没把任务栏建出来，这时候读固定项是空的。
        // 等，但不再死等 60 秒 —— 抢跑线程已经在处理任务栏了，
        // 这里只需要保证固定项清单别是空的（否则 Dock 会少一截图标）。
        if (options.Autostart) WaitForShell(8_000);

        // 换版本后（release-v6 → release-v7）自启还指着旧 exe，这里顺手改过来。
        // 只认 release* 目录：在 build/ 里调试时绝不能把自启指到调试产物上。
        string dirName = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (dirName.StartsWith("release", StringComparison.OrdinalIgnoreCase))
        {
            string? refreshed = Autostart.RefreshIfStale();
            if (refreshed is not null) Logger.Info(refreshed);
        }

        using var app = new DockApp(config, options.RunSeconds, options.ShotPath, options.SafeMode,
            options.PointerX, options.Sweep, options.DumpFramePath, options.FrameCap,
            options.HideWindow, options.ClickIndex, hideTaskbar,
            options.OpenSettings, options.OpenLaunchpad, options.Transmission, options.ShotAt);
        return app.Run();
    }

    /// <summary>
    /// 等 Windows Shell 就绪：任务栏窗口出现、而且固定项读得出来。
    ///
    /// 开机登录瞬间 explorer 还在初始化，这时候读固定项是空清单 ——
    /// 结果就是"Dock 起来了，但图标少一截"。
    ///
    /// v1.9 改了两处，都是为了缩短"登录后到看见 Dock"的时间：
    /// <list type="bullet">
    /// <item>轮询从 250ms 收紧到 60ms：任务栏窗口一出现就能发现，不必多睡半拍；</item>
    /// <item>固定项最多只再等 1.5 秒。以前是"读不到就一直等到超时"，
    ///       最坏拖满 60 秒 —— 那段时间屏幕上一直挂着 Windows 任务栏。
    ///       固定项晚一点由主循环的 SyncPins 补，少几个图标总好过卡着不启动。</item>
    /// </list>
    /// </summary>
    private static void WaitForShell(int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int attempt = 0;
        long traySeenAt = -1;

        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            attempt++;
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                if (traySeenAt < 0) traySeenAt = sw.ElapsedMilliseconds;

                int pins = 0;
                try { pins = TaskbarPins.Enumerate().Count; } catch { /* 注册表还没写好，下一轮再试 */ }
                if (pins > 0)
                {
                    Logger.Info($"Shell 就绪（等待 {sw.ElapsedMilliseconds}ms，轮次 {attempt}，固定项 {pins} 个）");
                    return;
                }

                // 任务栏已经在了，固定项还没写出来：再给 1.5 秒，然后照常启动。
                if (sw.ElapsedMilliseconds - traySeenAt > 1_500)
                {
                    Logger.Warn($"任务栏已就绪但固定项仍为空（等待 {sw.ElapsedMilliseconds}ms），先启动，稍后补同步");
                    return;
                }
            }

            Thread.Sleep(60);
        }

        Logger.Warn($"等待 Shell 就绪超时（{timeoutMs}ms），照常启动；任务栏守护会在稍后补上");
    }

    private static int Log(string message, int code)
    {
        if (code == 0) Logger.Info(message);
        else Logger.Error(message);
        return code;
    }

    /// <summary>
    /// 重启助手：等旧 Dock 进程退出（同时让它的单实例锁和任务栏托管彻底收尾），
    /// 再按配置启动新实例。窗口与托盘都从这个进程起，跟用户手动双击没有区别。
    /// </summary>
    private static int RelaunchAfter(int pid)
    {
        bool exited = true;
        try
        {
            using System.Diagnostics.Process? old = System.Diagnostics.Process.GetProcessById(pid);
            if (old is not null) exited = old.WaitForExit(15_000);
        }
        catch
        {
            // 进程已经没了（最常见）：直接继续
        }

        if (!exited)
        {
            Logger.Warn("重启助手: 旧进程 15 秒内没有退出，放弃本次重启（任务栏未受影响）");
            return 1;
        }

        // 旧进程退出时会把任务栏恢复、写正常退出标记；稍等收尾完成再拉起新实例。
        Thread.Sleep(700);

        string self = Environment.ProcessPath ?? "";
        if (self.Length == 0)
        {
            Logger.Error("重启助手: 拿不到自身可执行文件路径");
            return 1;
        }

        DockConfig cfg = DockConfig.Load();
        string args = cfg.HideTaskbar ? "--hide-taskbar" : "";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(self, args)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            Logger.Info($"重启助手: 已按配置重新启动 Dock（隐藏任务栏={cfg.HideTaskbar}）");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("重启助手: 启动新实例失败 " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// 任务栏的"开关"。同一个入口两种结果，用户只需要记住一个动作：
    /// Dock 在跑 → 关掉它，任务栏回来了；Dock 没跑 → 起来并把任务栏藏了。
    ///
    /// 注意这里刻意不去改配置里的 HideTaskbar：双击 exe 直接启动永远不动任务栏，
    /// 只有通过这个开关（或"启动 Dock（隐藏任务栏）.bat"）才会隐藏。
    /// 想彻底不碰任务栏的人，双击 exe 就永远是安全的。
    /// </summary>
    private static int ToggleDock()
    {
        IntPtr running = Native.FindWindow(DockApp.ClassName, null);
        if (running != IntPtr.Zero)
        {
            Logger.Info("切换：Dock 正在运行 → 关闭它，任务栏会被恢复");
            Native.PostMessageW(running, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            // 等旧实例真的退干净：不等就启动新实例会撞单实例锁
            for (int i = 0; i < 100; i++)
            {
                Thread.Sleep(50);
                if (Native.FindWindow(DockApp.ClassName, null) == IntPtr.Zero) return 0;
            }

            Logger.Warn("切换：等待旧实例退出超时（2.5s），本次不再启动新实例");
            return 1;
        }

        string self = Environment.ProcessPath ?? "";
        if (self.Length == 0)
        {
            Logger.Error("切换：拿不到自身可执行文件路径");
            return 1;
        }

        Logger.Info("切换：Dock 未运行 → 启动它并隐藏任务栏");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(self, "--hide-taskbar")
            {
                // 用 shell 启动：新进程彻底脱离 bat 的控制台，bat 窗口关掉也不会带走 Dock
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("切换：启动失败 " + ex.Message);
            return 1;
        }
    }
}

internal sealed class LaunchOptions
{
    public string? LabDir { get; private set; }
    public string? LabOut { get; private set; }
    /// <summary>--lab-panel WxH：材质实验室里玻璃面板的尺寸，默认 820x92（Dock 条）。</summary>
    public int LabPanelW { get; private set; } = 820;
    public int LabPanelH { get; private set; } = 92;
    public string? IconDumpDir { get; private set; }
    public float PointerX { get; private set; } = float.NaN;
    /// <summary>--transmission &lt;0..1&gt;：临时覆盖玻璃通透度，专门用来调材质（不写回配置）</summary>
    public float Transmission { get; private set; } = -1f;
    public bool Sweep { get; private set; }
    public string? DumpFramePath { get; private set; }
    public int FrameCap { get; private set; }
    public bool HideWindow { get; private set; }
    public int ClickIndex { get; private set; } = -1;
    public bool SafeMode { get; private set; }
    public bool QuitRequested { get; private set; }
    public bool HideTaskbar { get; private set; }
    public bool RestoreTaskbar { get; private set; }
    public bool TaskbarProbe { get; private set; }
    public bool RecoverProbe { get; private set; }
    public bool WindowsProbe { get; private set; }
    public int StealthProbeSeconds { get; private set; }
    public bool NoMutex { get; private set; }
    public bool PinsProbe { get; private set; }
    /// <summary>由开机自启拉起：等 Shell 就绪，撞上已有实例时静默退出。</summary>
    public bool Autostart { get; private set; }
    public bool InstallAutostart { get; private set; }
    public bool RemoveAutostart { get; private set; }
    public bool OpenSettings { get; private set; }
    public bool OpenLaunchpad { get; private set; }
    public bool ToggleDock { get; private set; }
    public bool SwitchTaskbar { get; private set; }
    /// <summary>守护进程模式：要盯的 Dock 进程 PID（0 = 不是守护进程）。</summary>
    public int WatchdogPid { get; private set; }

    /// <summary>重启助手模式：等这个 PID 退出后重新启动 Dock（0 = 不是重启助手）。</summary>
    public int RelaunchAfterPid { get; private set; }
    public int RunSeconds { get; private set; }
    public string? ShotPath { get; private set; }

    /// <summary>
    /// 截图时刻（秒）。默认 1.0 —— 足够让首帧稳定下来。
    /// 测动画/滚动时得往后推：那些效果要指针到位之后才开始演化，
    /// 1 秒截到的永远是初始状态。
    /// </summary>
    public double ShotAt { get; private set; } = 1.0;

    public static LaunchOptions Parse(string[] args)
    {
        var o = new LaunchOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lab":
                    if (i + 1 < args.Length) o.LabDir = args[++i];
                    break;
                case "--lab-out":
                    if (i + 1 < args.Length) o.LabOut = args[++i];
                    break;
                case "--lab-panel":
                    // WxH，例如 1376x916。不给就用 Dock 条的 820x92。
                    if (i + 1 < args.Length)
                    {
                        string[] wh = args[++i].Split('x', 'X');
                        if (wh.Length == 2
                            && int.TryParse(wh[0], out int pw) && int.TryParse(wh[1], out int ph)
                            && pw > 40 && ph > 20)
                        {
                            o.LabPanelW = pw;
                            o.LabPanelH = ph;
                        }
                    }
                    break;
                case "--icons":
                    if (i + 1 < args.Length) o.IconDumpDir = args[++i];
                    break;
                // 开发用：固定鼠标位置，便于截图核对放大与相邻位移
                case "--pointer":
                    if (i + 1 < args.Length && float.TryParse(args[i + 1],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float px))
                        o.PointerX = px;
                    i++;
                    break;
                // 开发用：让虚拟鼠标左右往复扫过 Dock，测量连续交互帧时间
                case "--sweep":
                    o.Sweep = true;
                    break;
                // 开发用：临时覆盖玻璃通透度（0 = 全磨砂，1 = 最透），不写回配置
                case "--transmission":
                    if (i + 1 < args.Length && float.TryParse(args[i + 1],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float tr))
                        o.Transmission = Math.Clamp(tr, 0f, 1f);
                    i++;
                    break;
                // 开发用：限制渲染帧率，测出的 CPU 占用才有参考价值
                case "--cap":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int cap)) o.FrameCap = cap;
                    i++;
                    break;
                // 开发用：启动后模拟点击第 N 个图标，验证"点击 -> 启动应用"链路
                case "--click":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int ci)) o.ClickIndex = ci;
                    i++;
                    break;
                // 开发用：窗口不进合成，用来判定"新帧"是不是我们自己的 Present 造成的
                case "--hide":
                    o.HideWindow = true;
                    break;
                case "--dump-frame":
                    if (i + 1 < args.Length) o.DumpFramePath = args[++i];
                    break;
                case "--safe-mode":
                    o.SafeMode = true;
                    break;
                case "--quit":
                    o.QuitRequested = true;
                    break;
                // 接管任务栏：启动时隐藏，正常退出/异常退出时恢复
                case "--hide-taskbar":
                    o.HideTaskbar = true;
                    break;
                // 独立恢复入口：不管 Dock 在不在跑，把任务栏放回来
                case "--restore-taskbar":
                    o.RestoreTaskbar = true;
                    break;
                case "--taskbar-probe":
                    o.TaskbarProbe = true;
                    break;
                // 开发用：跳过单实例锁，用于在用户已有一个实例时验证完整启动链路
                case "--no-mutex":
                    o.NoMutex = true;
                    break;
                case "--recover-probe":
                    o.RecoverProbe = true;
                    break;
                // 诊断：导出"当前被判定为运行中应用"的窗口清单，不建窗口、不占单实例锁
                case "--windows":
                    o.WindowsProbe = true;
                    break;
                // 诊断：导出 Windows 任务栏的固定项清单（含顺序与目标路径）
                case "--pins":
                    o.PinsProbe = true;
                    break;
                // 开机自启拉起时用：等 Shell 就绪，撞车时静默退出
                case "--autostart":
                    o.Autostart = true;
                    break;
                // 把"登录后自动启动"写进注册表 Run 项（指向当前 exe）
                case "--install-autostart":
                    o.InstallAutostart = true;
                    break;
                case "--remove-autostart":
                    o.RemoveAutostart = true;
                    break;
                // 开关：Dock 在跑就关它（任务栏恢复），没跑就起来并隐藏任务栏
                case "--settings":
                    o.OpenSettings = true;
                    break;
                case "--launchpad":
                    o.OpenLaunchpad = true;
                    break;
                case "--toggle":
                    o.ToggleDock = true;
                    break;
                // 只切换任务栏显隐，Dock 继续运行
                case "--switch-taskbar":
                    o.SwitchTaskbar = true;
                    break;
                // 诊断：隐形任务栏并保持 N 秒，供外部截图核对是否真的不可见
                case "--taskbar-stealth":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int sp)) o.StealthProbeSeconds = sp;
                    i++;
                    break;
                case "--seconds":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int s)) o.RunSeconds = s;
                    i++;
                    break;
                // 内部用：由 Dock 主进程拉起，盯住它退出（用户不该手动传这个参数）
                case "--watchdog":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int wp)) o.WatchdogPid = wp;
                    i++;
                    break;
                // 内部用：设置里点"重新启动 Dock"后，等旧进程退出再拉起新实例
                case "--relaunch-after":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int rp)) o.RelaunchAfterPid = rp;
                    i++;
                    break;
                case "--shot":
                    if (i + 1 < args.Length) o.ShotPath = args[++i];
                    break;
                case "--shot-at":
                    if (i + 1 < args.Length && double.TryParse(args[i + 1],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double sa) && sa > 0)
                        o.ShotAt = sa;
                    i++;
                    break;
            }
        }

        return o;
    }
}
