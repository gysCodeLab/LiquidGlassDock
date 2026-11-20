using Microsoft.Win32;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// 开机自启：把「登录后启动 Dock」登记在 HKCU 的 Run 项里。
///
/// 为什么用 Run 项而不是「启动」文件夹里的快捷方式：
///  - 不需要 COM 建 .lnk（本机安全策略会拦 COM 实例化），纯注册表字符串，零依赖；
///  - 换版本只要重写这一个值，不用重新生成快捷方式；
///  - 卸载就是删一个值，不会留下死链。
///
/// 值名固定为 LiquidGlassDock，命令行固定带 --autostart：
/// 那个参数负责「等 explorer 把任务栏建出来再动手」和「撞车时静默退出」，
/// 少了它，登录瞬间启动会读不到固定项，Dock 会起成半残的样子。
/// </summary>
internal static class Autostart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LiquidGlassDock";

    /// <summary>当前应登记的命令行（带引号，路径含空格也不会断）。</summary>
    public static string CommandLine
    {
        get
        {
            string exe = Environment.ProcessPath ?? "";
            return exe.Length == 0 ? "" : $"\"{exe}\" --autostart";
        }
    }

    /// <summary>注册表里已经登记的命令行；没登记返回 null。</summary>
    public static string? Registered
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) as string;
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(Registered);

    /// <summary>
    /// 登记项是不是**死链**：它指向的那个 exe 已经不存在了。
    ///
    /// 这里刻意不看「登记项是不是等于当前 exe」。旧版本用的是「谁在跑就登记谁」，
    /// 结果多版本目录并存时，只要旧版被启动过一次（比如自启项还指着 release-v11），
    /// 旧版就把登记项抢回自己，用户重启后永远回到旧版 —— 表现就是
    /// 「明明说修好了，怎么还是老样子」。现在只修死链，活着的路径一律不动。
    /// </summary>
    public static bool IsStale
    {
        get
        {
            string? cur = Registered;
            if (string.IsNullOrWhiteSpace(cur)) return false;
            string path = ExtractPath(cur);
            return path.Length == 0 || !File.Exists(path);
        }
    }

    /// <summary>从命令行里取出 exe 路径（支持带引号和不带引号两种写法）。</summary>
    private static string ExtractPath(string commandLine)
    {
        string s = commandLine.Trim();
        if (s.Length == 0) return "";
        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s.Substring(1, end - 1) : "";
        }

        int space = s.IndexOf(' ');
        return space > 0 ? s.Substring(0, space) : s;
    }

    public static bool Install(out string message)
    {
        string cmd = CommandLine;
        if (cmd.Length == 0)
        {
            message = "开机自启失败：拿不到当前 exe 路径";
            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            key.SetValue(ValueName, cmd, RegistryValueKind.String);
            message = "已开启开机自启: " + cmd;
            return true;
        }
        catch (Exception ex)
        {
            message = "开机自启写入失败: " + ex.Message;
            return false;
        }
    }

    public static bool Remove(out string message)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key?.GetValue(ValueName) is null)
            {
                message = "开机自启本来就没开";
                return true;
            }

            key.DeleteValue(ValueName, false);
            message = "已关闭开机自启";
            return true;
        }
        catch (Exception ex)
        {
            message = "关闭开机自启失败: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 路径自愈：只修**死链**（登记项指向的 exe 已经不存在）。
    /// 指向一个活着的 exe 时一律不动 —— 否则从旧版本目录启动一次，它就会把
    /// 登记项改成自己，用户重启后又回到旧版。换版本现在靠固定运行目录解决。
    /// </summary>
    public static string? RefreshIfStale()
    {
        if (!IsStale) return null;
        string old = Registered ?? "";
        return Install(out _) ? $"开机自启已更新到当前版本\n  旧: {old}\n  新: {CommandLine}" : null;
    }
}
