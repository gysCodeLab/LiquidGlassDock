using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 极简日志：同时写 %LOCALAPPDATA%\LiquidGlassDesktop\app.log，
/// 因为 WinExe 没有控制台，出问题时只能靠文件。
/// </summary>
internal static class Logger
{
    private static readonly object Gate = new();
    private static readonly string Path =
        System.IO.Path.Combine(DockConfig.Directory, "app.log");

    static Logger()
    {
        try { System.IO.Directory.CreateDirectory(DockConfig.Directory); } catch { /* 忽略 */ }
        try
        {
            var info = new FileInfo(Path);
            if (info.Exists && info.Length > 512 * 1024)
                File.Move(Path, Path + ".1", true);
        }
        catch { /* 忽略 */ }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {level} {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try { File.AppendAllText(Path, line + Environment.NewLine, Encoding.UTF8); }
            catch { /* 日志失败绝不能影响主流程 */ }
        }
    }
}

internal static class Launcher
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);

    private const uint SEE_MASK_NOASYNC = 0x00000100;
    private const int SW_SHOWNORMAL = 1;

    public static bool Launch(string path, string? args)
    {
        try
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_NOASYNC,
                lpVerb = "open",
                lpFile = path,
                lpParameters = string.IsNullOrWhiteSpace(args) ? null : args,
                nShow = SW_SHOWNORMAL,
            };

            if (ShellExecuteEx(ref info))
            {
                Logger.Info($"启动: {path} {args}");
                return true;
            }

            int err = Marshal.GetLastWin32Error();
            Logger.Warn($"启动失败 ({err}): {path}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn("启动异常: " + ex.Message);
            return false;
        }
    }
}

internal sealed class FrameStats
{
    private readonly struct Sample
    {
        public readonly double FrameMs;
        public readonly double SinceDirtyMs;
        public Sample(double frameMs, double sinceDirtyMs) { FrameMs = frameMs; SinceDirtyMs = sinceDirtyMs; }
    }

    private readonly List<double> _frames = new(8192);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastFrameMs;
    private bool _dirtySinceLast;

    // CPU 百分比必须扣掉启动期（D3D 设备创建 + 着色器编译约 0.6s 满载），
    // 否则 12 秒的测试里 5% 全是开机成本，会把稳态数据完全盖掉。
    private TimeSpan _cpuBase = TimeSpan.Zero;
    private double _cpuBaseAt;
    private bool _warmedUp;

    public int FramesThisSecond { get; private set; }
    private double _lastSecond;
    public int Fps { get; private set; }

    public void MarkFrame(double frameMs)
    {
        _frames.Add(frameMs);
        if (_frames.Count > 20000) _frames.RemoveRange(0, 10000);
        _lastFrameMs = frameMs;
        FramesThisSecond++;

        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastSecond >= 1.0)
        {
            Fps = (int)(FramesThisSecond / (now - _lastSecond));
            FramesThisSecond = 0;
            _lastSecond = now;
        }
    }

    public void MarkWarmup(double nowSeconds)
    {
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        // 必须先 Refresh。Process 对象缓存了创建时刻的性能快照，
        // 不刷新读到的 TotalProcessorTime 接近 0 —— 基准变成 0 之后，
        // 启动期那一秒多的开销会被平摊进整个测量窗口，把稳态数据彻底盖掉
        // （实测会把 1.0% 报成 10.2%，前后差十倍）。
        proc.Refresh();
        _cpuBase = proc.TotalProcessorTime;
        _cpuBaseAt = nowSeconds;
        _warmedUp = true;
    }

    public (double P50, double P95, double P99, double Max, double Avg) Summary()
    {
        if (_frames.Count == 0) return (0, 0, 0, 0, 0);
        var sorted = _frames.OrderBy(v => v).ToArray();
        double Pick(double q) => sorted[Math.Clamp((int)(q * (sorted.Length - 1)), 0, sorted.Length - 1)];
        return (Pick(0.50), Pick(0.95), Pick(0.99), sorted[^1], sorted.Average());
    }

    public int Count => _frames.Count;

    public string ToJson(double totalSeconds, string adapter, string samplerStatus)
    {
        var (p50, p95, p99, max, avg) = Summary();
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        double cpuSec = proc.TotalProcessorTime.TotalSeconds;
        double cpuWindow = totalSeconds;
        if (_warmedUp)
        {
            cpuSec -= _cpuBase.TotalSeconds;
            cpuWindow = Math.Max(0.001, totalSeconds - _cpuBaseAt);
        }
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"seconds\": {totalSeconds:F1},");
        sb.AppendLine($"  \"frames\": {_frames.Count},");
        sb.AppendLine($"  \"avgFps\": {(_frames.Count / Math.Max(totalSeconds, 0.001)):F1},");
        sb.AppendLine($"  \"frameMs\": {{ \"p50\": {p50:F2}, \"p95\": {p95:F2}, \"p99\": {p99:F2}, \"max\": {max:F2}, \"avg\": {avg:F2} }},");
        // CPU 占用 = 进程累计 CPU 时间 / 墙钟时间（单核归一化，1.0 = 占满一个核）
        sb.AppendLine($"  \"cpuPercent\": {(cpuSec / Math.Max(cpuWindow, 0.001) * 100.0):F1},");
        sb.AppendLine($"  \"cpuWindowSeconds\": {cpuWindow:F1},");
        sb.AppendLine($"  \"workingSetMb\": {(proc.WorkingSet64 / 1048576.0):F1},");
        sb.AppendLine($"  \"privateMb\": {(proc.PrivateMemorySize64 / 1048576.0):F1},");
        sb.AppendLine($"  \"gcHeapMb\": {(GC.GetTotalMemory(false) / 1048576.0):F1},");
        sb.AppendLine($"  \"adapter\": \"{adapter.Replace("\"", "")}\",");
        sb.AppendLine($"  \"sampler\": \"{samplerStatus.Replace("\"", "")}\"");
        sb.AppendLine("}");
        return sb.ToString();
    }
}
