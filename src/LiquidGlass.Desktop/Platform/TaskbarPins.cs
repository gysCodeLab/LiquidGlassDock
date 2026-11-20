using System.Runtime.InteropServices;
using System.Text;

namespace LiquidGlass.Desktop.Platform;

/// <summary>
/// Windows 任务栏上的一个固定项（就是"用户自己钉在任务栏上的图标"）。
/// Dock 的固定区直接由它驱动：Windows 上有什么，Dock 上就有什么。
/// </summary>
internal sealed class TaskbarPin
{
    /// <summary>显示名。桌面程序取 .lnk 文件名（Windows 自己也这么显示），
    /// UWP 取 shell 里的显示名（"Microsoft Store" 这种）。</summary>
    public string Name { get; init; } = "";

    /// <summary>.lnk 全路径。UWP 固定项没有 .lnk，这里为空串。</summary>
    public string LnkPath { get; init; } = "";

    /// <summary>启动目标。桌面程序是 .lnk（交给 ShellExecute，参数/工作目录/单实例都由它处理），
    /// UWP 是 <c>shell:AppsFolder\&lt;AUMID&gt;</c>。</summary>
    public string LaunchPath { get; init; } = "";

    /// <summary>指向的 exe。只用来和"运行中的应用"对对账；解析不出来/UWP 就是空串。</summary>
    public string TargetPath { get; init; } = "";

    /// <summary>在 Windows 任务栏里的位置，用来还原顺序。未知的是 int.MaxValue。</summary>
    public int Rank { get; init; } = int.MaxValue;

    /// <summary>这条是不是 UWP（商店应用）固定项。</summary>
    public bool IsUwp { get; init; }

    /// <summary>稳定的去重键。桌面程序用 .lnk 文件名，UWP 用 AUMID。
    /// 用户"取消固定"记的就是它。</summary>
    public string Key { get; init; } = "";

    public static string KeyOf(string lnkPath)
    {
        try { return Path.GetFileNameWithoutExtension(lnkPath); }
        catch { return lnkPath; }
    }
}

/// <summary>
/// 读 Windows 任务栏的固定项清单。
///
/// 数据来源全是只读的：
/// <list type="number">
/// <item>目录 <c>%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar</c>
/// 里的 .lnk —— 传统桌面程序（Chrome、Edge、各种客户端）的固定项都落在这里；</item>
/// <item>注册表 <c>HKCU\...\Explorer\Taskband\Favorites</c> 这个二进制值 —— 两件事：
/// 还原 .lnk 的排列顺序，以及找出**没有 .lnk 的 UWP 固定项**（商店、Codex 这类）。
/// 后者的应用 ID（AUMID，形如 <c>Microsoft.WindowsStore_8wekyb3d8bbwe!App</c>）就存在这个
/// 值里，扫出来再用 <c>shell:AppsFolder\{AUMID}</c> 校验一遍，能解析出显示名才算数。</item>
/// </list>
///
/// 不解析 ITEMIDLIST 本身：那东西的内部布局随 shell 版本变，硬解不如上面两条稳。
/// </summary>
internal static class TaskbarPins
{
    private static string PinnedDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>按 Windows 任务栏上的实际顺序返回固定项。</summary>
    public static List<TaskbarPin> Enumerate()
    {
        var list = new List<TaskbarPin>(16);
        string dir = PinnedDir;

        byte[]? blob = ReadTaskbandBlob();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(dir))
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*.lnk"); }
            catch { files = Array.Empty<string>(); }

            foreach (string lnk in files)
            {
                string name = TaskbarPin.KeyOf(lnk);
                if (name.Length == 0) continue;

                int rank = blob is null ? int.MaxValue : FindUtf16(blob, name + ".lnk");

                list.Add(new TaskbarPin
                {
                    Name = name,
                    LnkPath = lnk,
                    LaunchPath = lnk,
                    TargetPath = ResolveTarget(lnk) is { Length: > 0 } target ? target : KnownShellTarget(name),
                    Rank = rank < 0 ? int.MaxValue : rank,
                    Key = name,
                });
                seen.Add(name);
            }
        }

        if (blob is not null) AddUwpPins(blob, list, seen);

        list.Sort((a, b) =>
        {
            int c = a.Rank.CompareTo(b.Rank);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        return list;
    }

    // ---------------------------------------------------------------- UWP 固定项

    /// <summary>
    /// 从 Taskband 的二进制值里捞出 UWP 固定项。
    ///
    /// 这个值不是纯 ITEMIDLIST：里面混着 shell 序列化的属性块，AUMID 以 UTF-16 明文出现。
    /// 所以策略是"找形如 AUMID 的字符串 → 用 shell 校验它真的存在 → 取显示名"。
    /// 校验这一步是必须的：随便一段长得像 AUMID 的字节不能当固定项用。
    /// </summary>
    private static void AddUwpPins(byte[] blob, List<TaskbarPin> into, HashSet<string> seen)
    {
        string text = Encoding.Unicode.GetString(blob);

        foreach (System.Text.RegularExpressions.Match m in AumidPattern.Matches(text))
        {
            string aumid = m.Value;
            if (!seen.Add(aumid)) continue;

            string launch = @"shell:AppsFolder\" + aumid;
            if (!TryResolveAppsFolder(launch, out string displayName)) continue;

            into.Add(new TaskbarPin
            {
                Name = displayName.Length > 0 ? displayName : aumid,
                LnkPath = "",
                LaunchPath = launch,
                TargetPath = "",
                // 命中位置换算成字节偏移：和 .lnk 的 Rank 同一个坐标系，排序才有意义
                Rank = m.Index * 2,
                IsUwp = true,
                Key = aumid,
            });
        }
    }

    /// <summary>
    /// AUMID 的形态：<c>包名_13位家族哈希!入口</c>。也接受没有家族哈希的老式写法。
    /// 前后都要求是字符串边界，避免从一长串路径中间截出一段假的。
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AumidPattern = new(
        @"(?<![A-Za-z0-9._\-\\])[A-Za-z][A-Za-z0-9.\-]*(?:_[a-z0-9]{13})?![A-Za-z0-9._{}]+",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SIZE
    {
        public int cx, cy;
        public SIZE(int x, int y) { cx = x; cy = y; }
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr pszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint attrs);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    private const uint SIGDN_NORMALDISPLAY = 0x00000000;

    /// <summary>
    /// 用 shell 解析 <c>shell:AppsFolder\{AUMID}</c>。能解析出显示名，说明这个应用
    /// 确实装着、而且这个 AUMID 是真的；解析不了就当没看见这条固定项。
    /// </summary>
    private static bool TryResolveAppsFolder(string parsingName, out string displayName)
    {
        displayName = "";
        IShellItem? item = null;
        try
        {
            Guid iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out item);
            if (item is null) return false;

            if (item.GetDisplayName(SIGDN_NORMALDISPLAY, out IntPtr ptr) < 0 || ptr == IntPtr.Zero)
                return false;

            try { displayName = Marshal.PtrToStringUni(ptr) ?? ""; }
            finally { Marshal.FreeCoTaskMem(ptr); }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (item is not null) Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>导出每个固定项的完整属性表，供 --pins 诊断核对"目标路径到底读到了没有"。</summary>
    public static string DumpProperties(string lnkPath)
    {
        var sb = new StringBuilder();
        foreach ((string key, string value) in ReadAllStringProperties(lnkPath))
            sb.Append("    ").Append(key).Append(" = ").Append(value).AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// 固定项目录的轻量签名（文件名 + 修改时间）。只读目录项、不解析 .lnk，
    /// 单次成本在微秒级，可以每几秒跑一次用来发现"用户在 Windows 上又钉了一个"。
    /// </summary>
    public static string DirectorySignature()
    {
        try
        {
            var sb = new StringBuilder(256);

            string dir = PinnedDir;
            if (Directory.Exists(dir))
            {
                string[] files = Directory.GetFiles(dir, "*.lnk");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (string f in files)
                {
                    sb.Append(Path.GetFileName(f)).Append(':');
                    try { sb.Append(File.GetLastWriteTimeUtc(f).Ticks); } catch { sb.Append('?'); }
                    sb.Append(';');
                }
            }

            // UWP 固定项没有 .lnk，只有注册表里那个二进制值变了才能发现。
            // 7KB 走一遍 FNV-1a 是几十微秒，3 秒一次完全可以忽略。
            byte[]? blob = ReadTaskbandBlob();
            sb.Append("|blob:").Append(blob?.Length ?? 0).Append(':')
              .Append(blob is null ? 0u : Fnv1a(blob));

            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static uint Fnv1a(byte[] data)
    {
        uint hash = 2166136261u;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }

    // ---------------------------------------------------------------- .lnk 解析

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    /// <summary>
    /// PROPVARIANT 的托管镜像。只需要前 16 字节：vt 在 0，联合体从 8 开始。
    /// 多出来的字段不影响 PropVariantClear（它按 vt 解释，不按托管大小）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p1;
        public IntPtr p2;
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr bindCtx,
        uint flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pv);

    private const ushort VT_LPWSTR = 31;
    private const ushort VT_BSTR = 8;
    private const ushort VT_LPSTR = 30;

    /// <summary>
    /// 解析 .lnk 指向的 exe。做法是枚举快捷方式的所有字符串属性，
    /// 取第一个"看着像个存在的可执行文件"的值 —— 这样不用猜具体是哪个
    /// PKEY（不同 Windows 版本里目标路径出现的属性未必是同一个），
    /// 而且解析失败也只是退化成"匹配不到运行中应用"，不会影响显示与启动。
    /// </summary>
    private static string ResolveTarget(string lnkPath)
    {
        foreach ((string _, string value) in ReadAllStringProperties(lnkPath))
        {
            if (!LooksLikeExecutable(value)) continue;
            try { if (File.Exists(value)) return value; } catch { /* 忽略 */ }
        }

        return "";
    }

    /// <summary>
    /// 个别固定项不指向 exe，而是指向 shell 命名空间（"文件资源管理器"指向"此电脑"，
    /// 属性表里根本没有可执行路径）。这类只能按名字兜底映射到真正承载它的进程，
    /// 否则资源管理器一开窗，Dock 上就会同时出现"固定项"和"运行项"两个图标。
    /// </summary>
    private static string KnownShellTarget(string lnkName)
    {
        if (lnkName.Equals("File Explorer", StringComparison.OrdinalIgnoreCase) ||
            lnkName.Equals("文件资源管理器", StringComparison.OrdinalIgnoreCase) ||
            lnkName.Equals("Windows Explorer", StringComparison.OrdinalIgnoreCase))
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "explorer.exe");
            return File.Exists(explorer) ? explorer : "";
        }

        return "";
    }

    private static bool LooksLikeExecutable(string value)
    {
        if (value.Length < 5 || value.Contains('\0')) return false;
        if (!value.Contains('\\')) return false;

        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".com", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
               value.EndsWith(".msc", StringComparison.OrdinalIgnoreCase);
    }

    private static List<(string Key, string Value)> ReadAllStringProperties(string lnkPath)
    {
        var result = new List<(string, string)>(16);

        IPropertyStore? store = null;
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            SHGetPropertyStoreFromParsingName(lnkPath, IntPtr.Zero, 0 /* GPS_DEFAULT */, ref iid, out store);
            if (store is null) return result;

            store.GetCount(out uint count);
            for (uint i = 0; i < count && i < 64; i++)
            {
                if (store.GetAt(i, out PROPERTYKEY pk) < 0) continue;
                if (store.GetValue(ref pk, out PROPVARIANT pv) < 0) continue;

                try
                {
                    string? text = pv.vt switch
                    {
                        VT_LPWSTR => pv.p1 == IntPtr.Zero ? null : Marshal.PtrToStringUni(pv.p1),
                        VT_BSTR => pv.p1 == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(pv.p1),
                        VT_LPSTR => pv.p1 == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(pv.p1),
                        _ => null,
                    };

                    if (!string.IsNullOrEmpty(text))
                        result.Add(($"{{{pk.fmtid}}} pid={pk.pid}", text));
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
        }
        catch
        {
            // 坏掉的 .lnk、被占用、权限不足：当作"解析不出来"，不影响固定项本身
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }

        return result;
    }

    // ---------------------------------------------------------------- 顺序

    /// <summary>
    /// 读任务栏固定项的原始顺序。这个二进制值是 shell 的 ITEMIDLIST 序列，
    /// 完整解析代价很高、而且格式随版本变；我们只需要"谁排在谁前面"，
    /// 所以直接在字节流里找每个快捷方式文件名第一次出现的位置，按位置排序即可。
    /// </summary>
    private static byte[]? ReadTaskbandBlob()
    {
        const string subKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
        if (Native.RegOpenKeyExW(Native.HKEY_CURRENT_USER, subKey, 0, Native.KEY_QUERY_VALUE,
                out IntPtr key) != 0)
            return null;

        try
        {
            int size = 0;
            if (Native.RegQueryValueExW(key, "Favorites", IntPtr.Zero, out int type, null, ref size) != 0 ||
                size <= 0 || type != Native.REG_BINARY || size > 1024 * 1024)
                return null;

            var buffer = new byte[size];
            if (Native.RegQueryValueExW(key, "Favorites", IntPtr.Zero, out type, buffer, ref size) != 0)
                return null;

            return buffer;
        }
        catch
        {
            return null;
        }
        finally
        {
            Native.RegCloseKey(key);
        }
    }

    /// <summary>在字节流里找 UTF-16LE 子串，返回第一次出现的字节偏移，找不到返回 -1。</summary>
    private static int FindUtf16(byte[] haystack, string needle)
    {
        if (needle.Length == 0) return -1;
        byte[] pattern = Encoding.Unicode.GetBytes(needle);
        int limit = haystack.Length - pattern.Length;

        for (int i = 0; i <= limit; i++)
        {
            int j = 0;
            while (j < pattern.Length && haystack[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }

        return -1;
    }
}
