using LiquidGlass.Desktop.Platform;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 「显示 / 隐藏 Dock」的快捷键。
///
/// 配置里存一个字符串：键盘组合写成 "Ctrl+Alt+D"，鼠标按键写成
/// "MouseMiddle" / "MouseX1" / "MouseX2"。空字符串 = 关闭快捷键。
///
/// 为什么不用一个结构体存进 JSON：配置是给人看的，坏了的字段也能一眼改回来；
/// 解析失败时这里统一退回默认值，不会因为一个手滑的字符串让 Dock 起不来。
/// </summary>
internal static class ShortcutSpec
{
    public const string Default = "MouseDouble";

    public static bool IsMouse(string token) =>
        token is "MouseMiddle" or "MouseX1" or "MouseX2" or "MouseDouble" or "MouseTriple";

    /// <summary>设置面板里显示的名字。用户不该看到 MouseMiddle 这种内部写法。</summary>
    public static string DisplayName(string token) => token switch
    {
        "" => "已关闭",
        "MouseMiddle" => "鼠标中键",
        "MouseX1" => "鼠标侧键（后退）",
        "MouseX2" => "鼠标侧键（前进）",
        "MouseDouble" => "鼠标双击",
        "MouseTriple" => "鼠标三击",
        _ => token,
    };

    /// <summary>把键盘组合解析成 RegisterHotKey 的参数。鼠标键和非法字符串返回 false。</summary>
    public static bool TryParseKeyboard(string token, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (token.Length == 0 || IsMouse(token)) return false;

        string[] parts = token.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win": modifiers |= Native.MOD_WIN; break;
                default: return false;
            }
        }

        string key = parts[^1].Trim();
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; return true; }
            if (c is >= '0' and <= '9') { vk = c; return true; }
            return false;
        }

        switch (key.ToLowerInvariant())
        {
            case "space": vk = 0x20; return true;
            case "tab": vk = 0x09; return true;
            case "enter": vk = 0x0D; return true;
        }

        if (key[0] == 'F' && int.TryParse(key[1..], out int fn) && fn >= 1 && fn <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 录制时把按下的键变成配置字符串。
    /// 普通字母/数字必须带修饰键（否则等于把整个键盘的某个键抢走），
    /// F1-F24 允许单按。返回空串表示"这个键不能当快捷键"。
    /// </summary>
    public static string FromKey(byte vk, bool ctrl, bool alt, bool shift, bool win)
    {
        string? key = vk switch
        {
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x70 + 1),
            0x20 => "Space",
            0x09 => "Tab",
            0x0D => "Enter",
            _ => null,
        };
        if (key is null) return "";

        bool functionKey = key.StartsWith('F');
        if (!ctrl && !alt && !shift && !win && !functionKey) return "";

        var parts = new List<string>(5);
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(key);
        return string.Join("+", parts);
    }
}
