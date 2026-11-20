using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

using LiquidGlass.Desktop.App;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 独立守护进程。Dock 是"接管任务栏"的那个进程：它活着时 Windows 任务栏是全透明不可见的，
/// 它一旦死掉（崩溃、被任务管理器结束、被别的工具杀掉），任务栏就永远藏在那儿 ——
/// 屏幕上既没有 Dock 也没有任务栏，只剩壁纸；反过来如果它是正常退出的，任务栏该回来就回来。
///
/// 这两种结局只有进程外的人分得清，所以守护必须是**另一个进程**：
/// 它拿着 Dock 的进程句柄等退出，然后看有没有「我是自己走的」标记。
/// <list type="bullet">
/// <item>有标记 → 用户主动退出（设置中心「退出 Dock」/ --quit），什么都不做；</item>
/// <item>没标记 → 意外死亡，先把任务栏还回去，再按配置把 Dock 重新拉起来。</item>
/// </list>
///
/// 为什么不用「下次启动时 RecoverStale」兜底：那只在用户下次打开程序时才生效，
/// 中间这段时间屏幕底部是真的什么都没有，用户只会以为"软件坏了"。
/// </summary>
internal static class DockWatchdog
{
    private const string MutexName = @"Local\LiquidGlassDesktop.Watchdog";

    /// <summary>意外退出后等多久再重启。太短会跟正在收尾的旧进程抢单实例锁。</summary>
    private const int RestartDelayMs = 1200;

    /// <summary>重启频率闸门：10 分钟内最多重启 5 次，防止"启动即崩"变成死循环。</summary>
    private const int RestartWindowMinutes = 10;
    private const int MaxRestartsInWindow = 5;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LiquidGlassDesktop");

    /// <summary>正常退出标记。存在 = 这次退出是用户要求的，守护不要重启。</summary>
    private static readonly string CleanExitPath = Path.Combine(Dir, "clean-exit.flag");

    private static readonly string RestartLogPath = Path.Combine(Dir, "watchdog-restarts.json");

    /// <summary>Dock 主进程启动时调用：拉起一个和自己没有任何父子依赖的守护者。</summary>
    public static void Spawn()
    {
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (self.Length == 0)
            {
                Logger.Warn("守护进程: 拿不到自身路径，跳过");
                return;
            }

            Directory.CreateDirectory(Dir);
            try { if (File.Exists(CleanExitPath)) File.Delete(CleanExitPath); } catch { /* 删不掉就留着，下次 MarkCleanExit 会覆盖 */ }

            var psi = new ProcessStartInfo(self, "--watchdog " + Environment.ProcessId)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            // 守护者不需要窗口、不需要抢占前台，也不应该被快捷方式统计当成一次启动
            using Process? child = Process.Start(psi);
            if (child is null)
                Logger.Warn("守护进程: 启动返回空");
            else
                Logger.Info($"守护进程已启动（pid={child.Id}）：Dock 若意外退出会自动恢复任务栏并重启");
        }
        catch (Exception ex)
        {
            Logger.Warn("守护进程启动失败: " + ex.Message);
        }
    }

    /// <summary>Dock 正常退出前调用，告诉守护"不用管我"。</summary>
    public static void MarkCleanExit()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(CleanExitPath, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch
        {
            // 写不进去的后果只是"正常退出被误判成崩溃"，守护会多恢复一次任务栏并重启一次，
            // 不会造成数据损坏。所以这里刻意不抛。
        }
    }

    /// <summary>守护进程主体。<paramref name="pid"/> 是它要盯的 Dock 进程。</summary>
    public static int Run(int pid)
    {
        // 只允许一个守护者：Dock 每次启动都会 spawn 一个，撞车时后来的直接走人
        IntPtr mutex = Native.CreateMutex(IntPtr.Zero, true, MutexName);
        if (Marshal.GetLastWin32Error() == Native.ERROR_ALREADY_EXISTS) return 0;
        GC.KeepAlive(mutex);

        IntPtr target = pid > 0 ? Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)pid) : IntPtr.Zero;
        if (target == IntPtr.Zero)
        {
            // 打不开 = 进程已经没了（或权限不够，同用户同权限下不会发生）
            Logger.Warn($"守护进程: 无法打开 pid={pid}，按意外退出处理");
        }
        else
        {
            Native.WaitForSingleObject(target, Native.INFINITE);
            Native.CloseHandle(target);
        }

        // 给正常退出路径留出"恢复任务栏 + 写标记"的时间
        Thread.Sleep(700);

        if (File.Exists(CleanExitPath))
        {
            try { File.Delete(CleanExitPath); } catch { }
            Logger.Info("守护进程: Dock 正常退出，任务栏交给它自己恢复，本次不介入");
            return 0;
        }

        // 走到这里说明 Dock 是意外死的
        Logger.Warn("守护进程: 检测到 Dock 意外退出，开始善后");

        // 趁恢复之前先问一句：它死的时候是不是正在接管任务栏？
        // 这个信息决定重启时要不要带 --hide-taskbar（见下面的注释）。
        int owner = TaskbarGuard.HiddenOwnerPid();
        bool wasHiding = owner == pid;

        try
        {
            // 恢复失败时任务栏会永久留在屏幕外，所以这里必须带重试和实况校验：
            // Show 内部会重试 4 次、必要时重新拉起 explorer，只有确认没有窗口
            // 还带着隐形样式才算成功。
            int shown = TaskbarGuard.Show(restartExplorerIfMissing: true);
            bool stillHidden = TaskbarGuard.IsStealthed();
            if (stillHidden)
                Logger.Error($"守护进程: 任务栏恢复不完整（还原来 {shown} 个），已保留恢复标记，下次启动会继续尝试");
            else
                Logger.Info($"守护进程: 已恢复 {shown} 个任务栏窗口（崩溃前接管={wasHiding}）");
        }
        catch (Exception ex)
        {
            Logger.Error("守护进程: 恢复任务栏失败 " + ex.Message);
        }

        if (!TryConsumeRestartQuota())
        {
            Logger.Warn($"守护进程: {RestartWindowMinutes} 分钟内已重启 {MaxRestartsInWindow} 次，判定为启动即崩，停止自动重启");
            return 0;
        }

        Thread.Sleep(RestartDelayMs);
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (self.Length == 0) return 0;

            // 两个参数都不能省：
            //   --hide-taskbar  崩溃前它在接管任务栏，重启后必须继续接管，
            //                   否则任务栏和 Dock 会同时出现（用户看到的就是"特别大的任务栏又来了"）
            //   --autostart     等 Shell 就绪 + 撞上已有实例时静默退出，不弹框打扰人
            string args = wasHiding ? "--hide-taskbar --autostart" : "--autostart";

            using Process? again = Process.Start(new ProcessStartInfo(self, args)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            Logger.Info(again is null ? "守护进程: 重启 Dock 失败（返回空）" : $"守护进程: 已重启 Dock（pid={again.Id}）");
        }
        catch (Exception ex)
        {
            Logger.Error("守护进程: 重启 Dock 失败 " + ex.Message);
        }

        return 0;
    }

    /// <summary>重启闸门：把最近一次重启时间落盘，超过阈值就拒绝重启。</summary>
    private static bool TryConsumeRestartQuota()
    {
        List<DateTime> stamps = new();
        try
        {
            if (File.Exists(RestartLogPath))
            {
                stamps = JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(RestartLogPath)) ?? new();
            }
        }
        catch { /* 文件坏了当空表处理 */ }

        DateTime cutoff = DateTime.UtcNow.AddMinutes(-RestartWindowMinutes);
        stamps.RemoveAll(t => t < cutoff);
        if (stamps.Count >= MaxRestartsInWindow) return false;

        stamps.Add(DateTime.UtcNow);
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(RestartLogPath, JsonSerializer.Serialize(stamps));
        }
        catch { /* 写不进去就退化成"每次都能重启"，比反过来安全 */ }

        return true;
    }
}
