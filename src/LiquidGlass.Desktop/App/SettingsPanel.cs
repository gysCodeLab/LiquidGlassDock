using System.Drawing;
using System.Drawing.Drawing2D;
using LiquidGlass.Desktop.Platform;
using Gfx = System.Drawing.Graphics;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 设置窗口：左侧分类导航 + 右侧内容页，像一个真正的软件设置界面。
///
/// 为什么从"一列开关的小卡片"改成"带导航的页面"：
///  - 开关会一直加（以后还要加模糊强度、圆角、通知、日历……），一列排下去会变成一条长龙，
///    想找某一项只能靠肉眼扫；
///  - 分类是**扩展点**：以后加一个效果 = 加一个 Category，侧边栏自动多一项，不用改这个文件；
///  - 用户明确说过"以后还会加别的效果"，界面结构必须一开始就留出这个位置。
///
/// 设计取舍（沿用上一版，都是踩过的坑）：
///  - 每行是"状态 + 动作"两个委托，面板自己不认识任何业务字段；
///  - 整行可点，不只是那个小胶囊 —— 胶囊命中区太小，误点率高；
///  - 改完重新读真实状态再画，不本地记账：写注册表失败时界面必须显示真实结果。
/// </summary>
internal sealed class SettingsPanel : LayeredWindow
{
    internal enum RowKind
    {
        /// <summary>带胶囊开关的行。</summary>
        Toggle,

        /// <summary>点一下执行一个动作的行（右侧显示 ›）。</summary>
        Action,

        /// <summary>只显示信息，不可点。</summary>
        Info,
    }

    internal sealed class Row
    {
        public RowKind Kind = RowKind.Toggle;
        public string Title = "";
        public string Note = "";

        /// <summary>Info 行右侧的灰色文字；Action 行右侧可选的强调文字。</summary>
        public string Value = "";

        /// <summary>
        /// 动态版本的 Value。像"通透度: 更透"这种点一下就变的行，
        /// 文字必须每帧重新取，写静态字符串会在点完之后停在旧值上。
        /// </summary>
        public Func<string>? ValueOf;

        public Func<bool> Read = () => false;
        public Action<bool> Write = _ => { };
        public Action? Run;

        /// <summary>危险动作（比如退出 Dock），标题用警示色。</summary>
        public bool Danger;

        /// <summary>
        /// 这一行是快捷键录制器：点击后进入"按键中"状态，
        /// 下一次按下的键 / 鼠标键会通过 <see cref="ShortcutCaptured"/> 传出去。
        /// </summary>
        public bool CaptureShortcut;

        /// <summary>
        /// 录制完成（空串 = 清除，关闭快捷键）。返回 null = 已生效；
        /// 返回提示文字 = 这个键不能用（比如已被别的程序占用），面板留在录制状态等下一个。
        /// </summary>
        public Func<string, string?>? ShortcutCaptured;
    }

    internal sealed class Category
    {
        public string Title = "";
        public string Note = "";
        public List<Row> Rows = new();
    }

    // ---------------------------------------------------------------- 布局常量（逻辑像素）

    private const float WinW = 880f;
    private const float WinH = 636f;
    private const float Shadow = 20f;
    private const float SidebarW = 214f;
    private const float NavTop = 100f;
    private const float NavH = 42f;
    private const float NavGap = 4f;

    private const float ContentLeft = Shadow + SidebarW + 34f;
    private const float ContentRight = WinW - Shadow - 30f;
    private const float ContentTop = Shadow + 104f;      // 分类标题下面
    private const float ContentBottom = WinH - Shadow - 58f;

    private const float RowToggleH = 64f;
    private const float RowActionH = 52f;
    private const float RowInfoH = 46f;

    private const float ToggleW = 46f;
    private const float ToggleH = 26f;

    private readonly List<Category> _categories;
    private readonly string _version;

    private readonly List<(Rectangle Rect, Row Item)> _rows = new();
    private readonly List<(Rectangle Rect, int Index)> _nav = new();

    private Rectangle _closeRect;

    private int _current;          // 当前分类
    private int _hoverRow = -1;
    private int _hoverNav = -1;
    private bool _hoverClose;

    /// <summary>正在录制快捷键的行（-1 = 没在录制）。</summary>
    private int _captureRow = -1;
    /// <summary>录制时按了不接受键的临时提示（下一次按键或取消时清掉）。</summary>
    private string? _captureHint;

    /// <summary>快捷键下拉菜单（画在面板里，-1 = 收起）。</summary>
    private int _menuRow = -1;
    private int _menuHover = -1;
    private Rectangle _menuBox;
    private readonly List<(Rectangle Rect, string Label, string Token, bool Capture, bool Sep)> _menu = new();

    /// <summary>Dock 的鼠标钩子看到这个为 true 时会把中键放行，交给这里录制。</summary>
    public bool IsCapturingShortcut => _captureRow >= 0;

    private float _scroll;         // 内容滚动偏移（逻辑像素）
    private float _contentHeight;  // 当前分类内容总高
    private float _viewport;       // 可视高度

    public event Action? Closed;

    public SettingsPanel(List<Category> categories, float scale, string version) : base("LiquidGlassSettings", scale)
    {
        _categories = categories;
        _version = version;

        Create(WinW, WinH);
    }

    /// <summary>弹出（已显示则收起）。窗口居中，靠上一点，别压住 Dock。</summary>
    public void ToggleNearDock()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        ApplyScale(Scale, WinW, WinH);

        int sw = Native.GetSystemMetrics(0);
        int x = (sw - PixelWidth) / 2;
        int y = Math.Max(S(40), (int)((GetWorkBottom() - PixelHeight) / 2f) - S(40));

        ShowAt(x, y, activate: true);
        Logger.Info($"设置窗口: 已打开 ({PixelWidth}x{PixelHeight} @ {x},{y})");
    }

    public void Hide()
    {
        _captureRow = -1;
        _captureHint = null;
        CloseShortcutMenu();
        HideWindow();
        Closed?.Invoke();
    }

    private static float GetWorkBottom()
    {
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        IntPtr monitor = Native.MonitorFromWindow(IntPtr.Zero, Native.MONITOR_DEFAULTTONEAREST);
        if (Native.GetMonitorInfo(monitor, ref info)) return info.rcWork.Bottom;
        return Native.GetSystemMetrics(1);
    }

    // ---------------------------------------------------------------- 布局

    private Rectangle R(float x, float y, float w, float h)
        => new(S(x), S(y), Math.Max(1, S(w)), Math.Max(1, S(h)));

    private float ContentWidth => ContentRight - ContentLeft;

    /// <summary>把导航与行矩形算出来（设备像素），绘制与命中共用同一份结果。</summary>
    private void BuildLayout()
    {
        _nav.Clear();
        _rows.Clear();

        // ---- 侧边栏导航 ----
        float ny = NavTop;
        for (int i = 0; i < _categories.Count; i++)
        {
            _nav.Add((R(Shadow + 12f, ny, SidebarW - 24f, NavH), i));
            ny += NavH + NavGap;
        }

        _closeRect = R(ContentRight - 30f, Shadow + 24f, 30f, 30f);

        // ---- 内容行 ----
        Category cat = _categories[Math.Clamp(_current, 0, Math.Max(0, _categories.Count - 1))];
        float y = ContentTop;
        foreach (Row row in cat.Rows)
        {
            float h = Height(row);
            _rows.Add((R(ContentLeft - 10f, y, ContentWidth + 20f, h), row));
            y += h;
        }

        _contentHeight = Math.Max(0f, y - ContentTop);
        _viewport = ContentBottom - ContentTop;
        _scroll = Math.Clamp(_scroll, 0f, MathF.Max(0f, _contentHeight - _viewport));
    }

    private static float Height(Row row) => row.Kind switch
    {
        RowKind.Action => RowActionH,
        RowKind.Info => RowInfoH,
        _ => RowToggleH,
    };

    // ---------------------------------------------------------------- 绘制

    protected override void OnPaint(Gfx g, int width, int height)
    {
        BuildLayout();

        var card = new RectangleF(S(Shadow), S(Shadow), S(WinW - Shadow * 2f), S(WinH - Shadow * 2f));
        float radius = S(20f);

        UiTheme.DrawShadow(g, card, radius, S(12f) + 4);

        using (var fill = new LinearGradientBrush(card, UiTheme.CardTop, UiTheme.CardBottom, 90f))
            UiTheme.FillRound(g, card, radius, fill);

        // 侧边栏：在圆角卡片内部叠一层暗底，再补一条分隔线
        using (GraphicsPath clip = UiTheme.Round(card, radius))
        {
            GraphicsState state = g.Save();
            g.SetClip(clip, CombineMode.Intersect);

            var side = new RectangleF(card.Left, card.Top, S(SidebarW), card.Height);
            using (var sb = new SolidBrush(UiTheme.Sunken)) g.FillRectangle(sb, side);

            using (var pen = new Pen(UiTheme.Edge, 1f))
                g.DrawLine(pen, card.Left + S(SidebarW), card.Top + S(16f),
                    card.Left + S(SidebarW), card.Bottom - S(16f));

            g.Restore(state);
        }

        UiTheme.StrokeRound(g, card, radius, UiTheme.Edge, 1f);

        DrawSidebar(g);
        DrawContent(g);
        DrawCloseButton(g);
        DrawShortcutMenu(g);
    }

    private void DrawSidebar(Gfx g)
    {
        float x = Shadow + 24f;

        UiTheme.DrawText(g, "液态玻璃", UiTheme.Font(S(17f), true), UiTheme.Text, S(x), S(Shadow + 30f));
        UiTheme.DrawText(g, $"Dock 设置中心 · {_version}", UiTheme.Font(S(11f)), UiTheme.Dim,
            S(x), S(Shadow + 58f));

        foreach ((Rectangle rect, int index) in _nav)
        {
            bool selected = index == _current;
            bool hover = index == _hoverNav && !selected;

            if (selected || hover)
            {
                var box = new RectangleF(rect.X, rect.Y, rect.Width, rect.Height);
                using var brush = new SolidBrush(selected
                    ? Color.FromArgb(46, UiTheme.Accent)
                    : UiTheme.Hover);
                UiTheme.FillRound(g, box, S(10f), brush);
            }

            if (selected)
            {
                var bar = new RectangleF(rect.X + S(1f), rect.Y + S(11f), S(3f), rect.Height - S(22f));
                using var accent = new SolidBrush(UiTheme.Accent);
                UiTheme.FillRound(g, bar, S(2f), accent);
            }

            UiTheme.DrawText(g, _categories[index].Title,
                UiTheme.Font(S(14f), selected), selected ? UiTheme.Text : UiTheme.Dim,
                rect.X + S(18f), rect.Y + S(11f));
        }

        // 底部提示：给"以后还要加效果"留个位置说明
        string hint = "更多效果会作为新分类加在这里";
        Font hintFont = UiTheme.Font(S(10.5f));
        float hintW = UiTheme.Measure(g, hint, hintFont).Width;
        float hintX = Shadow + (SidebarW - hintW) / 2f;
        UiTheme.DrawText(g, hint, hintFont, UiTheme.Dim, S(hintX), S(WinH - Shadow - 34f));
    }

    private void DrawContent(Gfx g)
    {
        Category cat = _categories[Math.Clamp(_current, 0, _categories.Count - 1)];

        UiTheme.DrawText(g, cat.Title, UiTheme.Font(S(19f), true), UiTheme.Text,
            S(ContentLeft), S(Shadow + 30f));

        if (cat.Note.Length > 0)
        {
            Font noteFont = UiTheme.Font(S(11.5f));
            UiTheme.DrawText(g, UiTheme.Ellipsis(g, cat.Note, noteFont, S(ContentWidth - 40f)),
                noteFont, UiTheme.Dim, S(ContentLeft), S(Shadow + 62f));
        }

        var viewport = new RectangleF(S(ContentLeft - 10f), S(ContentTop),
            S(ContentWidth + 20f), S(_viewport));

        GraphicsState state = g.Save();
        g.SetClip(viewport, CombineMode.Intersect);

        for (int i = 0; i < _rows.Count; i++)
        {
            (Rectangle rect, Row row) = _rows[i];
            var moved = new Rectangle(rect.X, rect.Y - S(_scroll), rect.Width, rect.Height);
            if (moved.Bottom < viewport.Top - S(4f) || moved.Top > viewport.Bottom + S(4f)) continue;
            DrawRow(g, moved, row, i == _hoverRow, i == _captureRow);
        }

        g.Restore(state);

        DrawScrollBar(g, viewport);
        DrawFooterFade(g, viewport);

        // 底部状态行
        UiTheme.DrawText(g, "改动立即生效 · 自动保存", UiTheme.Font(S(10.5f)), UiTheme.Dim,
            S(ContentLeft), S(WinH - Shadow - 32f));
    }

    /// <summary>
    /// 内容滚到底时，最后一行会紧贴底栏。这里在底栏上方压一段渐变，
    /// 让内容"淡出"而不是被一条硬线切断 —— 不做这一步，看起来像文字被裁掉了。
    /// </summary>
    private void DrawFooterFade(Gfx g, RectangleF viewport)
    {
        float h = S(26f);
        var fade = new RectangleF(viewport.Left, viewport.Bottom - h + S(2f), viewport.Width, h);
        using var brush = new LinearGradientBrush(
            new RectangleF(fade.Left, fade.Top - 1f, fade.Width, fade.Height + 2f),
            Color.FromArgb(0, UiTheme.CardBottom), UiTheme.CardBottom, 90f);
        g.FillRectangle(brush, fade);
    }

    private void DrawRow(Gfx g, Rectangle rect, Row row, bool hover, bool capturing)
    {
        if ((hover || capturing) && row.Kind != RowKind.Info)
        {
            var box = new RectangleF(rect.X, rect.Y + S(2f), rect.Width, rect.Height - S(4f));
            using var brush = new SolidBrush(UiTheme.Hover);
            UiTheme.FillRound(g, box, S(10f), brush);
        }

        float textX = rect.X + S(14f);
        float textTop = rect.Y + S(row.Kind == RowKind.Toggle ? 12f : 15f);

        UiTheme.DrawText(g, row.Title, UiTheme.Font(S(14.5f)),
            row.Danger ? UiTheme.Danger : UiTheme.Text, textX, textTop);

        string note = _captureHint
            ?? (capturing
                ? "现在按：鼠标键（任意位置），或键盘组合（指针要在 Dock 上）· Esc 取消 · Backspace 清除"
                : row.Note);
        if (note.Length > 0)
        {
            Font noteFont = UiTheme.Font(S(11.5f));
            float maxW = rect.Width - S(14f * 2f) - S(ToggleW + 24f);
            UiTheme.DrawText(g, UiTheme.Ellipsis(g, note, noteFont, maxW), noteFont,
                capturing || _captureHint is not null ? UiTheme.Accent : UiTheme.Dim,
                textX, rect.Y + S(36f));
        }

        switch (row.Kind)
        {
            case RowKind.Toggle:
            {
                bool on = SafeRead(row);
                var track = new RectangleF(
                    rect.Right - S(14f) - S(ToggleW),
                    rect.Y + (rect.Height - S(ToggleH)) / 2f,
                    S(ToggleW), S(ToggleH));
                UiTheme.DrawToggle(g, track, on);
                break;
            }

            case RowKind.Action:
            {
                Font chevronFont = UiTheme.Font(S(17f));
                float cx = rect.Right - S(26f);
                UiTheme.DrawText(g, row.CaptureShortcut ? "▾" : "›", chevronFont, UiTheme.Dim,
                    cx, rect.Y + S(13f));

                string value = capturing ? "按键中…" : ResolveValue(row);
                if (value.Length > 0)
                {
                    Font valueFont = UiTheme.Font(S(12f));
                    float w = UiTheme.Measure(g, value, valueFont).Width;
                    UiTheme.DrawText(g, value, valueFont, UiTheme.Accent,
                        cx - S(12f) - w, rect.Y + S(17f));
                }
                break;
            }

            case RowKind.Info:
            {
                string value = ResolveValue(row);
                if (value.Length == 0) break;
                Font valueFont = UiTheme.Font(S(12f));
                float w = UiTheme.Measure(g, value, valueFont).Width;
                UiTheme.DrawText(g, value, valueFont, UiTheme.Dim,
                    rect.Right - S(18f) - w, rect.Y + S(15f));
                break;
            }
        }
    }

    private static string ResolveValue(Row row)    {
        if (row.ValueOf is null) return row.Value;
        try { return row.ValueOf() ?? ""; } catch { return row.Value; }
    }

    private void DrawScrollBar(Gfx g, RectangleF viewport)
    {
        if (_contentHeight <= _viewport + 1f) return;

        float trackX = viewport.Right + S(6f);
        float ratio = _viewport / _contentHeight;
        float thumbH = MathF.Max(S(36f), viewport.Height * ratio);
        float maxScroll = _contentHeight - _viewport;
        float t = maxScroll <= 0f ? 0f : _scroll / maxScroll;
        float thumbY = viewport.Top + (viewport.Height - thumbH) * t;

        var thumb = new RectangleF(trackX, thumbY, S(4f), thumbH);
        using var brush = new SolidBrush(Color.FromArgb(70, UiTheme.Text));
        UiTheme.FillRound(g, thumb, S(2f), brush);
    }

    private static bool SafeRead(Row row)
    {
        try { return row.Read(); }
        catch { return false; }
    }

    private void DrawCloseButton(Gfx g)
    {
        Rectangle r = _closeRect;
        if (_hoverClose) UiTheme.FillRound(g, r, S(8f), UiTheme.Hover);

        using var pen = new Pen(_hoverClose ? UiTheme.Text : UiTheme.Dim, MathF.Max(1.2f, S(1.4f)));
        float inset = r.Width * 0.32f;
        g.DrawLine(pen, r.Left + inset, r.Top + inset, r.Right - inset, r.Bottom - inset);
        g.DrawLine(pen, r.Right - inset, r.Top + inset, r.Left + inset, r.Bottom - inset);
    }

    // ---------------------------------------------------------------- 交互

    protected override bool OnMouseMove(int x, int y)
    {
        if (_menuRow >= 0)
        {
            int menu = _menuBox.Contains(x, y)
                ? _menu.FindIndex(t => t.Rect.Contains(x, y) && !t.Sep)
                : -1;

            SetCursorKind(menu >= 0);
            if (menu == _menuHover && _hoverRow < 0) return false;

            _menuHover = menu;
            _hoverRow = -1;
            _hoverNav = -1;
            _hoverClose = false;
            return true;
        }

        int nav = _nav.FindIndex(t => t.Rect.Contains(x, y));

        int row = -1;
        if (y >= S(ContentTop) && y <= S(ContentBottom))
            row = _rows.FindIndex(t => t.Rect.Contains(x, y + S(_scroll)));

        bool close = _closeRect.Contains(x, y);

        SetCursorKind(nav >= 0 || close || (row >= 0 && _rows[row].Item.Kind != RowKind.Info));

        if (nav == _hoverNav && close == _hoverClose && row == _hoverRow) return false;
        _hoverNav = nav;
        _hoverRow = row;
        _hoverClose = close;
        return true;
    }

    protected override bool OnMouseLeave()
    {
        if (_hoverRow < 0 && _hoverNav < 0 && !_hoverClose) return false;
        _hoverRow = -1;
        _hoverNav = -1;
        _hoverClose = false;
        SetCursorKind(false);
        return true;
    }

    protected override bool OnLeftUp(int x, int y)
    {
        if (_menuRow >= 0)
        {
            if (_menuBox.Contains(x, y))
            {
                int picked = _menu.FindIndex(t => t.Rect.Contains(x, y) && !t.Sep);
                if (picked >= 0)
                {
                    (_, string label, string token, bool capture, _) = _menu[picked];
                    Row menuRow = _rows[_menuRow].Item;
                    CloseShortcutMenu();

                    if (capture)
                    {
                        Logger.Info($"[settings] 快捷键下拉: {label}");
                        BeginCapture(menuRow);
                    }
                    else
                    {
                        ApplyShortcutChoice(menuRow, token);
                    }
                }
                return false;
            }

            // 点菜单外面只收起菜单，不顺手触发下面的行 —— 不然像误触开关。
            CloseShortcutMenu();
            Render();
            return false;
        }

        if (_closeRect.Contains(x, y))
        {
            Hide();
            return false;
        }

        // 录制中点别处 = 放弃录制，这次点击照常处理。
        if (_captureRow >= 0)
        {
            bool onCaptureRow = _captureRow < _rows.Count &&
                                _rows[_captureRow].Rect.Contains(x, y + S(_scroll));
            if (!onCaptureRow) { _captureRow = -1; Render(); }
        }

        foreach ((Rectangle rect, int index) in _nav)
        {
            if (!rect.Contains(x, y)) continue;
            if (index == _current) return false;

            _current = index;
            _scroll = 0f;
            _hoverRow = -1;
            Render();
            return false;
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            (Rectangle rect, Row row) = _rows[i];
            if (!rect.Contains(x, y + S(_scroll))) continue;
            if (row.Kind == RowKind.Info) return false;

            if (row.Kind == RowKind.Action)
            {
                if (row.CaptureShortcut)
                {
                    // 点这一行 = 打开选择菜单。不再直接进"按键中"——
                    // 那一步用户看不出还能选什么，只会觉得按了没反应。
                    if (_captureRow >= 0)
                    {
                        _captureRow = -1;
                        _captureHint = null;
                    }
                    OpenShortcutMenu(i);
                    return false;
                }

                try
                {
                    row.Run?.Invoke();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"设置动作「{row.Title}」失败: {ex.Message}");
                }

                Logger.Info($"[settings] 执行: {row.Title}");
                Render();
                return false;
            }

            bool next = !SafeRead(row);
            try
            {
                row.Write(next);
            }
            catch (Exception ex)
            {
                Logger.Warn($"设置项「{row.Title}」应用失败: {ex.Message}");
            }

            Logger.Info($"[settings] {row.Title} = {next}");
            Render();   // 重新读真实状态再画
            return false;
        }

        return false;
    }

    protected override bool OnWheel(int delta, int x, int y)
    {
        if (_menuRow >= 0)
        {
            CloseShortcutMenu();
            Render();
            return false;
        }

        if (_contentHeight <= _viewport + 1f) return false;

        float before = _scroll;
        _scroll = Math.Clamp(_scroll - delta / 120f * 56f, 0f, _contentHeight - _viewport);
        return !before.Equals(_scroll);
    }

    protected override IntPtr OnHitTest(int x, int y)
    {
        if (_closeRect.Contains(x, y)) return (IntPtr)Native.HTCLIENT;

        // 顶部一条当标题栏：能拖动窗口，双击不会最大化
        if (y < S(Shadow + 72f)) return (IntPtr)Native.HTCAPTION;

        return (IntPtr)Native.HTCLIENT;
    }

    protected override bool OnKeyDown(byte vk)
    {
        if (_captureRow < 0)
        {
            if (_menuRow >= 0 && vk == Native.VK_ESCAPE)
            {
                CloseShortcutMenu();
                Render();
                return false;
            }

            if (vk != Native.VK_ESCAPE) return false;
            Hide();
            return false;
        }

        return HandleCaptureKey(vk,
            Native.GetKeyState(Native.VK_CONTROL) < 0,
            Native.GetKeyState(Native.VK_MENU) < 0,
            Native.GetKeyState(Native.VK_SHIFT) < 0,
            Native.GetKeyState(Native.VK_LWIN) < 0 || Native.GetKeyState(Native.VK_RWIN) < 0);
    }

    /// <summary>
    /// 全局键盘钩子投回来的按键。面板没拿到焦点时（比如刚被 Dock 唤起，
    /// 前台还停在别的程序上）WM_KEYDOWN 根本不会到这里，录制只能靠这条路。
    /// </summary>
    protected override void OnCaptureKeyboardShortcut(int vk, int modifiers)
    {
        if (_captureRow < 0) return;

        if (HandleCaptureKey((byte)vk,
                (modifiers & 1) != 0,
                (modifiers & 2) != 0,
                (modifiers & 4) != 0,
                (modifiers & 8) != 0))
            Render();
    }

    protected override void OnCancelShortcutCapture()
    {
        if (_captureRow < 0) return;
        _captureRow = -1;
        _captureHint = null;
        Logger.Info("[settings] 点击面板外，取消录制快捷键");
        Render();
    }

    private bool HandleCaptureKey(byte vk, bool ctrl, bool alt, bool shift, bool win)
    {
        if (_captureRow < 0) return false;

        if (vk == Native.VK_ESCAPE)
        {
            _captureRow = -1;
            _captureHint = null;
            Logger.Info("[settings] 取消录制快捷键");
            return true;
        }

        // Backspace = 清除（关闭快捷键），而不是录一个退格键。
        if (vk == Native.VK_BACK)
        {
            _captureHint = null;
            FinishShortcutCapture("");
            return true;
        }

        // 只按了修饰键：等真正的键，不结束录制。
        if (vk is Native.VK_CONTROL or Native.VK_MENU or Native.VK_SHIFT or Native.VK_LWIN or Native.VK_RWIN)
        {
            if (_captureHint is not null)
            {
                _captureHint = null;
                Render();
            }
            return false;
        }

        string token = ShortcutSpec.FromKey(vk, ctrl, alt, shift, win);
        if (token.Length == 0)
        {
            // 单按字母/数字会把普通打字抢走，不接收；界面保持"按键中"等下一个。
            // 必须把原因画出来，否则用户只会看到"按了没反应"。
            Logger.Info($"[settings] 快捷键不接受 vk=0x{vk:X2}（普通键需要搭配 Ctrl/Alt/Shift/Win）");
            _captureHint = $"{KeyName(vk)} 不能单独用 —— 请按住 Ctrl / Alt / Shift / Win 再按一次";
            Render();
            return false;
        }

        _captureHint = null;
        FinishShortcutCapture(token);
        return true;
    }

    /// <summary>给"这个键不行"的提示用，别把内部 vk 编号直接甩给用户。</summary>
    private static string KeyName(byte vk) => vk switch
    {
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        0x20 => "空格",
        0x09 => "Tab",
        0x0D => "回车",
        _ => "这个键",
    };

    protected override bool OnMiddleDown(int x, int y)
    {
        if (_captureRow < 0) return false;
        FinishShortcutCapture("MouseMiddle");
        return true;
    }

    protected override bool OnXButtonDown(int x, int y, int button)
    {
        if (_captureRow < 0) return false;
        FinishShortcutCapture(button == 2 ? "MouseX2" : "MouseX1");
        return true;
    }

    /// <summary>
    /// 录制期间按鼠标键。鼠标在窗口外也要能录到 —— 用户最自然的动作是点一下 Dock 试试，
    /// 若只认窗口内的点击，那一下既不会录到、也不会触发显隐，看起来就是"怎么按都没反应"。
    /// </summary>
    protected override void OnCaptureMouseShortcut(int button)
    {
        if (_captureRow < 0) return;
        FinishShortcutCapture(button switch
        {
            2 => "MouseX1",
            3 => "MouseX2",
            _ => "MouseMiddle",
        });
    }

    /// <summary>
    /// 快捷键选择下拉：画在面板内部，右端对齐。选项固定，最后一个入口留给
    /// "自己按键录制…"，被占用的组合键仍然走行内提示。
    /// </summary>
    private void OpenShortcutMenu(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return;

        // 再点同一行 = 收起
        if (_menuRow == rowIndex)
        {
            CloseShortcutMenu();
            Render();
            return;
        }

        _menuRow = rowIndex;
        _menuHover = -1;
        _captureHint = null;
        _menu.Clear();

        var entries = new List<(string Label, string Token, bool Capture, bool Sep)>
        {
            ("鼠标双击（在 Dock 上连点两下）", "MouseDouble", false, false),
            ("鼠标三击（在 Dock 上连点三下）", "MouseTriple", false, false),
            ("鼠标中键", "MouseMiddle", false, false),
            ("鼠标侧键（后退）", "MouseX1", false, false),
            ("鼠标侧键（前进）", "MouseX2", false, false),
            ("", "", false, true),
            ("自己按键录制…（指针在 Dock 上才生效）", "", true, false),
            ("", "", false, true),
            ("关闭快捷键", "", false, false),
        };

        Rectangle rowRect = _rows[rowIndex].Rect;
        int rowTop = rowRect.Y - S(_scroll);
        int w = S(300);
        int itemH = S(34);
        int sepH = S(9);
        int pad = S(6);
        int total = pad * 2 + entries.Sum(e => e.Sep ? sepH : itemH);

        int left = Math.Min(rowRect.Right - S(14) - w, PixelWidth - S(Shadow) - w);
        left = Math.Max(S(Shadow) + S(6), left);
        int top = rowTop + rowRect.Height + S(2);

        // 下面放不下就往上翻
        if (top + total > PixelHeight - S(Shadow) - S(8))
            top = Math.Max(S(Shadow) + S(8), rowTop - total - S(2));

        _menuBox = new Rectangle(left, top, w, total);

        int y = top + pad;
        foreach ((string label, string token, bool capture, bool sep) in entries)
        {
            int h = sep ? sepH : itemH;
            _menu.Add((new Rectangle(left + pad, y, w - pad * 2, h), label, token, capture, sep));
            y += h;
        }

        Logger.Info("[settings] 打开快捷键选择菜单");
        Render();
    }

    private void CloseShortcutMenu()
    {
        _menuRow = -1;
        _menuHover = -1;
        _menu.Clear();
    }

    private void DrawShortcutMenu(Gfx g)
    {
        if (_menuRow < 0) return;

        // 先垫一层阴影，菜单才不会像是直接贴在行上面
        using (var shade = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
        {
            var shadow = new RectangleF(_menuBox.X + S(1.5f), _menuBox.Y + S(4f),
                _menuBox.Width, _menuBox.Height);
            UiTheme.FillRound(g, shadow, S(12f), shade);
        }

        using (var fill = new SolidBrush(UiTheme.CardBottom))
            UiTheme.FillRound(g, _menuBox, S(12f), fill);
        UiTheme.StrokeRound(g, _menuBox, S(12f), UiTheme.Edge, 1f);

        string current = "";
        try { current = _rows[_menuRow].Item.ValueOf?.Invoke() ?? ""; } catch { }

        for (int i = 0; i < _menu.Count; i++)
        {
            (Rectangle rect, string label, string token, bool capture, bool sep) = _menu[i];

            if (sep)
            {
                var line = new RectangleF(rect.X + S(8f), rect.Y + rect.Height / 2f,
                    rect.Width - S(16f), MathF.Max(1f, S(1f)));
                using var pen = new Pen(UiTheme.Edge, MathF.Max(1f, S(1f)));
                g.DrawLine(pen, line.Left, line.Top, line.Right, line.Top);
                continue;
            }

            bool hover = i == _menuHover;
            if (hover)
            {
                using var brush = new SolidBrush(UiTheme.Hover);
                UiTheme.FillRound(g, rect, S(8f), brush);
            }

            bool selected = !capture && displayMatches(current, token);
            Font font = UiTheme.Font(S(13f), selected);
            UiTheme.DrawText(g, label, font,
                selected ? UiTheme.Accent : UiTheme.Text,
                rect.X + S(10f), rect.Y + S(9f));

            if (selected)
                UiTheme.DrawText(g, "✓", UiTheme.Font(S(12f), true), UiTheme.Accent,
                    rect.Right - S(26f), rect.Y + S(9f));
        }
    }

    private static bool displayMatches(string current, string token) =>
        current == ShortcutSpec.DisplayName(token);

    private void BeginCapture(Row row)
    {
        _captureRow = _rows.FindIndex(t => ReferenceEquals(t.Item, row));
        _captureHint = null;
        Logger.Info($"[settings] 等待录制快捷键: {row.Title}");
        Render();
    }

    private void ApplyShortcutChoice(Row row, string token)
    {
        string? error = null;
        try
        {
            error = row.ShortcutCaptured?.Invoke(token);
        }
        catch (Exception ex)
        {
            error = "应用失败，请换一个";
            Logger.Warn($"设置快捷键「{row.Title}」应用失败: {ex.Message}");
        }

        _captureHint = error;
        Logger.Info(error is null
            ? $"[settings] 快捷键「{row.Title}」= {(token.Length == 0 ? "关闭" : token)}"
            : $"[settings] 快捷键「{row.Title}」未生效: {error}");
        Render();
    }

    private void FinishShortcutCapture(string token)
    {
        int index = _captureRow;
        if (index < 0 || index >= _rows.Count) { _captureRow = -1; _captureHint = null; return; }

        Row row = _rows[index].Item;
        string? error = null;
        try
        {
            error = row.ShortcutCaptured?.Invoke(token);
        }
        catch (Exception ex)
        {
            error = "应用失败，请换一个组合键";
            Logger.Warn($"设置快捷键「{row.Title}」应用失败: {ex.Message}");
        }

        // 没生效（占用 / 被系统保留）：不写进设置，留在录制态等用户换一个。
        if (error is not null)
        {
            _captureRow = index;
            _captureHint = error;
            Logger.Info($"[settings] 快捷键「{row.Title}」未生效: {error}");
            Render();
            return;
        }

        _captureRow = -1;
        _captureHint = null;
        Logger.Info($"[settings] 快捷键「{row.Title}」= {(token.Length == 0 ? "关闭" : token)}");
        Render();
    }
}
