using LiquidGlass.Desktop.App;

namespace LiquidGlass.Desktop.Platform;

/// <summary>桌面条目的类别。既用于分类筛选，也决定「全部」视图里的排列顺序。</summary>
internal enum DesktopKind
{
    App,
    Folder,
    Image,
    Video,
    Audio,
    Document,
    Archive,
    Other,
}

internal static class DesktopKindInfo
{
    /// <summary>分类切换条上的文字。</summary>
    public static string Label(this DesktopKind kind) => kind switch
    {
        DesktopKind.App => "应用",
        DesktopKind.Folder => "文件夹",
        DesktopKind.Image => "图片",
        DesktopKind.Video => "视频",
        DesktopKind.Audio => "音乐",
        DesktopKind.Document => "文档",
        DesktopKind.Archive => "压缩包",
        _ => "其他",
    };

    /// <summary>枚举顺序就是「全部」视图里的分组顺序。</summary>
    public static IReadOnlyList<DesktopKind> Order { get; } = new[]
    {
        DesktopKind.App, DesktopKind.Folder, DesktopKind.Image, DesktopKind.Video,
        DesktopKind.Audio, DesktopKind.Document, DesktopKind.Archive, DesktopKind.Other,
    };
}

/// <summary>桌面上的一个条目。Name 是显示名（已去掉扩展名），Path 是完整路径。</summary>
internal sealed record DesktopItem(string Name, string Path, DesktopKind Kind);

/// <summary>
/// 只扫桌面。启动台要的是"我桌面上有什么"，不是"我装了什么软件" ——
/// 后者是开始菜单的职责，两者混在一起会让这张面板失去意义
/// （108 个装过的程序里，常用的其实都在 Dock 上钉着）。
///
/// 枚举方式选 DirectoryInfo 而不是 Shell API：桌面就是一个普通目录，
/// 用 Shell 反而要处理虚拟项（回收站、此电脑），而用户要的恰恰是**真实文件**。
/// 图标另由 IconLoader 走 Shell 取，所以外观仍然是系统级质量。
/// </summary>
internal static class DesktopCatalog
{
    private static List<DesktopItem>? _cache;

    public static IReadOnlyList<DesktopItem> All => _cache ??= Scan();

    /// <summary>桌面变了（新建 / 删除 / 改名）时调用。每次打开启动台都会调。</summary>
    public static void Invalidate() => _cache = null;

    private static List<DesktopItem> Scan()
    {
        var list = new List<DesktopItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in Roots())
        {
            if (!Directory.Exists(root)) continue;

            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(root))
                {
                    string raw = Path.GetFileName(path);
                    if (raw.Length == 0) continue;

                    // 桌面上的 desktop.ini 是系统用来记图标位置的，永远不该出现在面板里
                    if (raw.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    // 同一个文件的快捷方式在用户桌面和公共桌面都可能存在，按路径去重
                    if (!seen.Add(path)) continue;

                    FileAttributes attr;
                    bool isDir;
                    try
                    {
                        attr = File.GetAttributes(path);
                        isDir = (attr & FileAttributes.Directory) != 0;
                    }
                    catch
                    {
                        continue;   // 权限不够 / 刚被删掉，跳过就好
                    }

                    // 隐藏项和系统项（含 desktop.ini、Thumbs.db）不显示。
                    // 这和资源管理器的默认行为一致 —— 面板里飘出用户自己都没打开过的文件很怪。
                    if ((attr & FileAttributes.Hidden) != 0) continue;
                    if ((attr & FileAttributes.System) != 0) continue;

                    list.Add(new DesktopItem(DisplayName(raw), path, KindOf(path, isDir)));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"扫描桌面失败 ({root}): {ex.Message}");
            }
        }

        list.Sort(Compare);
        Logger.Info($"[launchpad] 桌面清单: {list.Count} 项（" +
                    string.Join(" ", DesktopKindInfo.Order
                        .Select(k => $"{k.Label()}{list.Count(i => i.Kind == k)}")) + "）");
        return list;
    }

    private static IEnumerable<string> Roots()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (user.Length > 0) yield return user;

        // 公共桌面：装软件时往这里丢的快捷方式，资源管理器也显示在桌面上
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (common.Length > 0 && !string.Equals(common, user, StringComparison.OrdinalIgnoreCase))
            yield return common;
    }

    /// <summary>显示名去掉扩展名：桌面上摆的几乎全是快捷方式，带着 .lnk 很难看。</summary>
    private static string DisplayName(string raw)
    {
        string stem = Path.GetFileNameWithoutExtension(raw);
        return stem.Length > 0 ? stem : raw;
    }

    private static DesktopKind KindOf(string path, bool isDir)
    {
        if (isDir) return DesktopKind.Folder;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".lnk" or ".url" or ".exe" or ".msi" or ".bat" or ".cmd" or ".ps1"
                or ".appref-ms" or ".jar" => DesktopKind.App,

            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp"
                or ".svg" or ".ico" or ".heic" or ".tif" or ".tiff" or ".raw" => DesktopKind.Image,

            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv"
                or ".webm" or ".m4v" or ".ts" or ".mpg" or ".mpeg" => DesktopKind.Video,

            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg"
                or ".wma" or ".lrc" or ".ape" or ".aiff" => DesktopKind.Audio,

            ".doc" or ".docx" or ".pdf" or ".txt" or ".md" or ".rtf"
                or ".xls" or ".xlsx" or ".csv" or ".ppt" or ".pptx"
                or ".onnx" or ".json" or ".xml" or ".yaml" or ".yml" => DesktopKind.Document,

            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" => DesktopKind.Archive,

            _ => DesktopKind.Other,
        };
    }

    /// <summary>先按类别分组，再按名字。同类挨在一起，扫一眼就能看出桌面主要由什么构成。</summary>
    private static int Compare(DesktopItem a, DesktopItem b)
    {
        int ka = (int)a.Kind, kb = (int)b.Kind;
        if (ka != kb) return ka.CompareTo(kb);
        return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
    }
}
