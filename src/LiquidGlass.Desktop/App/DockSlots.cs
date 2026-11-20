using System.Text;
using LiquidGlass.Desktop.Platform;
using Vortice.Direct3D11;

namespace LiquidGlass.Desktop.App;

internal enum DockSlotKind
{
    /// <summary>最左边的 Windows 徽标。任务栏藏起来以后，这是唯一的开始菜单入口。</summary>
    Start,

    /// <summary>文件资源管理器入口（设置里可开关）。</summary>
    Explorer,

    /// <summary>启动台：点开全屏应用网格。</summary>
    Launchpad,

    /// <summary>固定在 Dock 上的应用。即使没运行也常驻。</summary>
    Pinned,

    /// <summary>启动台与开始按钮之间的固定竖线，不可点。位置不随运行中的应用变化。</summary>
    Separator,

    /// <summary>正在运行、但不对应任何固定项的应用。关掉就消失。</summary>
    Running,

    /// <summary>
    /// 系统状态位：Dock 最右端的一颗图标，点一下开 Windows 快速设置（Win+A）。
    ///
    /// 它接替的是原来挂在 Dock 右边那颗独立胶囊（电量 / 网络 / 时间）。胶囊是一整块
    /// 额外的玻璃窗口，要单独摆位、单独采背景、Dock 展开时还得跟着让位；并成一颗
    /// 图标之后，它天然跟着 Dock 的放大、弹簧和玻璃走，不会再出现"两块玻璃颜色
    /// 对不上"这类问题。
    /// </summary>
    Status,
}

/// <summary>Dock 上的一个位置。固定项、分隔符、运行中应用共用同一套布局逻辑。</summary>
internal sealed class DockSlot
{
    public DockSlotKind Kind { get; init; }
    public string Name { get; init; } = "";

    /// <summary>取图标用的路径（exe 全路径、.lnk 全路径，或 ms-settings: 这类 URI）。</summary>
    public string IconPath { get; init; } = "";

    /// <summary>点击时启动的目标。只有"没在运行"时才会用到。</summary>
    public string LaunchTarget { get; init; } = "";
    public string? Args { get; init; }

    /// <summary>该应用"最适合被激活"的窗口，0 表示没在运行。</summary>
    public IntPtr Hwnd { get; init; }

    /// <summary>运行中的应用（含固定项里正在运行的那些）。</summary>
    public TrackedApp? App { get; init; }

    /// <summary>固定项的来源配置。运行区槽位为 null；右键菜单靠它做固定/取消固定。</summary>
    public DockItemConfig? Item { get; init; }

    public bool Running => App is not null;

    /// <summary>该应用此刻是否在前台，决定指示点亮度。</summary>
    public bool HasForeground => App?.HasForeground ?? false;

    public ID3D11ShaderResourceView? Icon { get; set; }
}

internal static class DockSlotBuilder
{
    /// <summary>资源管理器入口的目标。固定在这个路径，Windows 上必然存在。</summary>
    public const string ExplorerPath = @"C:\Windows\explorer.exe";

    /// <summary>
    /// 把"固定项"和"当前运行的应用"合成为一条槽位序列。
    /// 返回签名串：签名不变就说明布局不用重算，避免无谓的重排与抖闪。
    /// </summary>
    /// <param name="pinnedItems">已经合成好的固定项（Windows 同步来的 + 用户手加的）。</param>
    public static string Build(IReadOnlyList<DockItemConfig> pinnedItems, bool showRunningApps,
        bool showStart, bool showExplorer, bool showLaunchpad,
        IReadOnlyList<TrackedApp> running, List<DockSlot> into)
    {
        into.Clear();

        // 池子里是"还没被任何固定项认领"的运行中应用
        var pool = new Dictionary<string, TrackedApp>(StringComparer.OrdinalIgnoreCase);
        foreach (TrackedApp app in running)
            if (app.ExePath.Length > 0) pool[app.ExePath] = app;

        // 这三个是"系统位"：不属于固定区，也不参与同步与取消固定。
        // 顺序固定在最左端，用户的固定项永远从它们右边开始，加减应用不会让它们左右跳。
        //
        // 启动台排第一，紧跟一条**固定**分隔线，再往后才是开始按钮和应用。
        // 以前这条线插在"固定区 / 运行区"之间：运行区里没有程序时它不出现，
        // 有程序时又夹在应用中间，位置一直在变。现在它只跟系统位绑定，
        // 不管有没有运行中的应用，都在同一个位置。
        if (showLaunchpad)
            into.Add(new DockSlot { Kind = DockSlotKind.Launchpad, Name = "启动台" });

        if (showLaunchpad)
            into.Add(new DockSlot { Kind = DockSlotKind.Separator });

        if (showStart)
            into.Add(new DockSlot { Kind = DockSlotKind.Start, Name = "开始" });

        if (showExplorer)
            into.Add(new DockSlot
            {
                Kind = DockSlotKind.Explorer,
                Name = "资源管理器",
                IconPath = ExplorerPath,
                LaunchTarget = ExplorerPath,
            });

        var pinned = new List<DockSlot>(pinnedItems.Count);
        foreach (DockItemConfig item in pinnedItems)
        {
            TrackedApp? live = Claim(pool, item);
            pinned.Add(new DockSlot
            {
                Kind = DockSlotKind.Pinned,
                Name = item.Name,
                IconPath = string.IsNullOrEmpty(item.IconPath) ? item.Path : item.IconPath!,
                LaunchTarget = item.Path,
                Args = item.Args,
                App = live,
                Hwnd = live?.Representative ?? IntPtr.Zero,
                Item = item,
            });
        }

        var extra = new List<DockSlot>();
        if (showRunningApps)
        {
            foreach (TrackedApp app in running)
            {
                if (!pool.Remove(app.ExePath, out TrackedApp? live)) continue;   // 已被固定项认领
                extra.Add(new DockSlot
                {
                    Kind = DockSlotKind.Running,
                    Name = live.Name,
                    IconPath = live.ExePath,
                    LaunchTarget = live.ExePath,
                    App = live,
                    Hwnd = live.Representative,
                });
            }
        }

        into.AddRange(pinned);
        into.AddRange(extra);

        // 状态位永远排在最后：它代替的是 Windows 的托盘区，位置就该在右端。
        // 固定项和运行中的应用增减时，它只是继续贴在最右边，不会夹在应用中间。
        into.Add(new DockSlot { Kind = DockSlotKind.Status, Name = "快速设置" });

        var sb = new StringBuilder(into.Count * 28);
        foreach (DockSlot slot in into)
        {
            sb.Append((int)slot.Kind).Append('|').Append(slot.IconPath).Append('|')
              .Append(slot.Hwnd.ToInt64()).Append(';');
        }

        return sb.ToString();
    }

    /// <summary>
    /// 找出某个固定项对应的运行中应用。
    /// 优先用解析出来的真实 exe 路径（Windows 同步项都是 .lnk，Path 是快捷方式本身，
    /// 拿它对窗口永远对不上）；没有再退回全路径精确匹配，最后退回文件名 ——
    /// 后者是必须的：calc.exe / WindowsTerminal.exe 这类是"应用执行别名"存根，
    /// 真实进程在 WindowsApps 或别的目录下，全路径永远对不上。
    /// </summary>
    private static TrackedApp? Claim(Dictionary<string, TrackedApp> pool, DockItemConfig item)
    {
        if (!string.IsNullOrEmpty(item.MatchPath))
        {
            if (pool.Remove(item.MatchPath, out TrackedApp? byTarget)) return byTarget;

            string target = SafeFileName(item.MatchPath);
            if (target.Length > 0 && TryTakeByName(pool, target, out TrackedApp? fuzzy)) return fuzzy;
        }

        string path = item.Path;
        if (path.Length == 0) return null;

        if (pool.Remove(path, out TrackedApp? exact)) return exact;

        // ms-settings: 这类 URI 没有文件名，不参与模糊匹配，否则会乱认
        if (!path.Contains('\\') && !path.Contains('/')) return null;

        string name = SafeFileName(path);
        if (name.Length == 0) return null;

        return TryTakeByName(pool, name, out TrackedApp? app) ? app : null;
    }

    private static bool TryTakeByName(Dictionary<string, TrackedApp> pool, string fileName,
        out TrackedApp? app)
    {
        foreach (KeyValuePair<string, TrackedApp> kv in pool)
        {
            if (!SafeFileName(kv.Key).Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
            pool.Remove(kv.Key);
            app = kv.Value;
            return true;
        }

        app = null;
        return false;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return "";
        }
    }
}
