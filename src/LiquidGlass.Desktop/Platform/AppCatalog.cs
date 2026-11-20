using LiquidGlass.Desktop.App;

namespace LiquidGlass.Desktop.Platform;

/// <summary>启动台里的一个应用条目。</summary>
internal sealed record AppEntry(string Name, string Path);

/// <summary>
/// 从开始菜单收集所有已安装应用。
///
/// 为什么不用 Shell 的应用清单（AppsFolder）：那需要 COM（本机安全策略会拦
/// CoCreateInstance），而且返回的是 UWP 的 AUMID，启动还得再翻译一次。
/// 开始菜单里的 .lnk 是最稳的一层 —— 所有能在开始菜单看到的东西都在这里，
/// 而且直接 ShellExecute 就能打开，和用户平时的点击行为完全一致。
/// </summary>
internal static class AppCatalog
{
    private static List<AppEntry>? _cache;

    public static IReadOnlyList<AppEntry> All => _cache ??= Scan();

    /// <summary>应用列表变了（装了/卸了软件）时调用，下次打开重新扫。</summary>
    public static void Invalidate() => _cache = null;

    private static List<AppEntry> Scan()
    {
        var byName = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in Roots())
        {
            if (!Directory.Exists(root)) continue;

            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!IsUseful(name)) continue;
                    if (byName.ContainsKey(name)) continue;

                    byName[name] = new AppEntry(name, file);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"扫描开始菜单失败 ({root}): {ex.Message}");
            }
        }

        List<AppEntry> list = byName.Values.ToList();
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        Logger.Info($"[launchpad] 应用清单: {list.Count} 个（来自开始菜单）");
        return list;
    }

    private static IEnumerable<string> Roots()
    {
        // 公共开始菜单优先：同一款软件在两边都有快捷方式时，以公共的那份为准
        string common = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        string user = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");

        yield return common;
        yield return user;
    }

    /// <summary>滤掉卸载器、说明文件、帮助链接这类"点了没意义"的条目。</summary>
    private static bool IsUseful(string name)
    {
        if (name.Length == 0) return false;

        string lower = name.ToLowerInvariant();
        string[] junk =
        {
            "uninstall", "卸载", "readme", "read me", "说明", "帮助", "help",
            "documentation", "文档", "license", "许可", "update", "更新程序",
            "官网", "网站", "website", "user manual", "release notes",
        };

        foreach (string word in junk)
            if (lower.Contains(word, StringComparison.Ordinal))
                return false;

        return true;
    }
}
