using System.Text.Json;
using System.Text.Json.Serialization;

namespace LiquidGlass.Desktop.App;

internal sealed class DockItemConfig
{
    public string Name { get; set; } = "";

    /// <summary>
    /// 图标来源 + 启动目标。Windows 固定项在这里存的是 .lnk 全路径：
    /// 交给 ShellExecute 打开快捷方式，UWP 和传统程序都能正确拉起，不用自己拼参数。
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// 用来和"运行中的应用"对账的 exe 全路径。
    /// 快捷方式实际指向哪个 exe，只有解析 .lnk 才知道；解析不出来就留空，
    /// 这时该固定项不会认领运行中的窗口（最多多显示一个运行区图标，不会出错）。
    /// </summary>
    public string? MatchPath { get; set; }

    public string? Args { get; set; }
    public bool Pinned { get; set; } = true;

    /// <summary>
    /// 图标来源（可空，默认用 Path）。Windows 同步项在这里放解析出来的 exe：
    /// 直接拿 .lnk 取图标会被 shell 盖上一个"快捷方式箭头"角标，而启动仍然走
    /// .lnk（参数、工作目录、单实例重定向都由它自己处理，比裸 exe 更稳）。
    /// </summary>
    public string? IconPath { get; set; }

    /// <summary>"windows" = 从 Windows 任务栏同步来的；"user" = 用户在 Dock 里手动固定的。</summary>
    public string Source { get; set; } = "user";

    /// <summary>同步项的稳定键（.lnk 文件名）。用户在 Dock 里取消固定靠它记住。</summary>
    public string? PinKey { get; set; }
}

internal sealed class GlassConfig
{
    public float RefractBand { get; set; } = 44f;      // 折射带宽度（逻辑像素）
    public float RefractAmount { get; set; } = 26f;     // 边缘最大位移
    public float Bulge { get; set; } = 0.18f;           // 玻璃厚度/鼓胀
    public float Dispersion { get; set; } = 0.48f;      // 色散
    public float BlurStep { get; set; } = 0.75f;        // 模糊步长（纹素）
    public float RimIntensity { get; set; } = 0.36f;    // 边光强度（0.62 时那圈白线在亮壁纸上明显抢眼）
    public float RimWidth { get; set; } = 10f;          // 边光宽度
    public float Gloss { get; set; } = 0.64f;           // 顶部光泽
    public float Shadow { get; set; } = 0.20f;          // 外阴影强度
    public float Tint { get; set; } = 0.12f;            // 背景自适应色调强度
    public float Exposure { get; set; } = 0.03f;
    public float Saturation { get; set; } = 1.18f;
    public float Contrast { get; set; } = 1.02f;
    public float BodyAlpha { get; set; } = 0.02f;
    public float BodyR { get; set; } = 1.0f;
    public float BodyG { get; set; } = 1.0f;
    public float BodyB { get; set; } = 1.0f;

    // ---- 材质第二组：决定"像不像真玻璃"的四个关键量 ----
    /// <summary>暗背景散射：越大玻璃越乳白通透，0 = 直接透出深色（像黑洞）</summary>
    public float Scatter { get; set; } = 0.70f;
    /// <summary>亮背景奶白化：越大越磨砂，避免亮处只剩一片白</summary>
    public float Milk { get; set; } = 0.05f;
    /// <summary>高光软限拐点：越低越早压缩高光，保住亮部层次不糊成白板</summary>
    public float Rolloff { get; set; } = 0.90f;
    /// <summary>边缘 1px 镜面高光强度</summary>
    public float EdgeSpec { get; set; } = 0.82f;

    /// <summary>
    /// 白底烟熏强度 0~1（只在接近纸色的背景上生效）。
    ///
    /// 白壁纸上是这套材质唯一没法用"加法"解决的情况：白 + 白还是白，面板主体
    /// 只能做到比背景暗 1%，于是白字看不见、边框也看不见。
    ///
    /// 0.92 会把面板压成暗塑料板，0 又会在白纸上假得只剩一圈灰边。
    /// 日常用轻度烟熏：本体保留白底的明暗，文字仍走深色墨。
    ///
    /// 0 = 白玻璃（几乎透明的一层白）；1 = 完整烟熏（本体落在 0.19 附近）。
    /// 暗底、彩色底不受影响 —— 那里本来就是暗玻璃，再加烟只会变脏。
    /// </summary>
    public float Smoke { get; set; } = 0f;

    /// <summary>
    /// 通透度 0~1。0 = 原来的全磨砂（几乎不透，只看到模糊色块）；
    /// 1 = 最透（能看清桌面壁纸的轮廓，代价是壁纸上的亮边会略微"透"进玻璃）。
    /// 这一个值同时驱动暗部乳白、亮部奶白、内部暗带、冷偏色和最终 alpha ——
    /// 分开调过，结果总是这里透了那里发灰，必须一起动才像一块玻璃。
    /// </summary>
    public float Transmission { get; set; } = 0.93f;

    /// <summary>材质实验室用的圆角比例（相对面板高度），Dock 运行时用 Metrics.Radius</summary>
    public float RadiusHint { get; set; } = 0.38f;

    /// <summary>0 = 关闭折射（性能降级档 L1）</summary>
    public bool EnableRefraction { get; set; } = true;

    /// <summary>
    /// 把折射带/折射量按面板高度换算。
    ///
    /// 这套材质是按 80px 高的 Dock 条标定的：44px 的带在着色器里还被钳到
    /// `halfSize.y * 0.55` = 22px，占高度 27.5%，所以整条 Dock 有小半面积在做折射。
    ///
    /// 大卡片**不能**按高度等比放大：820px 的卡片上有过 140~200px 的带子，
    /// 半面玻璃都在位移，用户看到的就是「透明的背景是错位的」—— 玻璃里那张图和
    /// 外面桌面压不到一起。真玻璃的倒角就那么薄，所以这里只做很轻的尺寸补偿，
    /// 让 Rim 附近有一圈克制的弯折，中间完全 1:1。
    /// </summary>
    public (float Band, float Amount) EdgeFor(float panelHeight)
    {
        float r = Math.Clamp(panelHeight / 80f, 1f, 14f);
        // 656 逻辑高（启动台）→ 带约 60px、位移约 20px，是 Dock 的 1.2~1.4 倍，
        // 不再出现整块玻璃被"推走"的观感。
        return (RefractBand * MathF.Pow(r, 0.15f), RefractAmount * MathF.Pow(r, 0.10f));
    }

    /// <summary>
    /// 大面板的"雾感"强度。
    ///
    /// 原来这里按面板高度把 Scatter 收掉一半（820px 高时 0.62 → 0.31），理由是
    /// "面积大了补光摊在整片暗背景上会发灰"。但 Scatter 在着色器里是一条
    /// **纯加法**（`c += 蓝灰 * Scatter`），收掉一半的直接后果是：同一面壁纸上
    /// Dock 比启动台亮 24/255 且明显偏蓝，两块玻璃并排就是两种颜色 ——
    /// 用户的原话是"dock 栏和启动台颜色不一致"。
    ///
    /// 尺寸不该改变材料的颜色：同一块料摊大，透光率和色偏都该一样。
    /// 所以这里恒返回 Scatter，大面板的发灰问题交给别的手段（模糊、alpha），
    /// 不能再靠"偷偷减一半补光"来掩盖 —— 那等于两块面板不是同一种材质。
    /// </summary>
    public float ScatterFor(float panelHeight) => Scatter;
}

internal sealed class DockMetricsConfig
{
    public float IconSize { get; set; } = 54f;
    public float Gap { get; set; } = 12f;
    public float PaddingX { get; set; } = 13f;
    public float PaddingY { get; set; } = 13f;
    public float Magnification { get; set; } = 1.62f;
    public float InfluenceRadius { get; set; } = 2.3f;  // 以图标宽度为单位
    public float BottomMargin { get; set; } = 10f;
    public float Radius { get; set; } = 0.38f;          // 相对 Dock 高度
}

internal sealed class DockConfig
{
    /// <summary>
    /// 配置结构版本。
    /// 1 = 固定项写死在 Items；2 = 固定区跟随 Windows 任务栏；3 = 剔除 v2 遗留的内置默认项；
    /// 4 = 玻璃通透度与阴影重新标定；5 = 再加一档通透 + 玻璃质感加强（折射/边光/色散）；
    /// 6 = 边光收一档；7 = 白底恢复白玻璃（Smoke 归 0，可读性改由字色承担）；
    /// 8 = 整体壁厚/方向性反光提升，白底改轻度烟熏磨砂。
    /// </summary>
    public int Version { get; set; } = 8;

    /// <summary>
    /// 是否接管 Windows 任务栏（启动时隐藏、退出时恢复）。
    /// 默认关闭：这是会改动系统可见状态的操作，必须由用户显式开启。
    /// </summary>
    public bool HideTaskbar { get; set; }

    /// <summary>
    /// 任务栏藏起来以后，开始菜单就没入口了。这里放一个 Windows 徽标在最左边，
    /// 点它等于按 Win 键。关掉就回到纯应用 Dock。
    /// </summary>
    public bool ShowStartButton { get; set; } = true;

    /// <summary>
    /// 是否把"正在运行的程序"显示到固定区右侧。默认开启 —— 这是把 Windows
    /// 任务栏搬进 Dock 的核心。关掉后 Dock 退回成纯启动器。
    /// </summary>
    public bool ShowRunningApps { get; set; } = true;

    /// <summary>
    /// 悬停多久之后从图标上方浮出窗口预览卡片（毫秒）。0 = 关闭预览。
    /// 做成可调是因为这个值主观性极强：有人希望碰一下就看见，
    /// 有人只是路过 Dock 就被卡片挡住，宁可关掉。
    /// </summary>
    public int HoverPreviewDelayMs { get; set; } = 2000;

    /// <summary>
    /// 固定区是否直接跟随 Windows 任务栏的固定项。默认开启：
    /// Windows 上钉了什么，Dock 上就有什么，顺序也一致。
    /// 关掉后固定区只显示 <see cref="Items"/> 里手动加的那些。
    /// </summary>
    public bool SyncTaskbarPins { get; set; } = true;

    /// <summary>
    /// 用户在 Dock 里取消固定、但 Windows 任务栏上还留着的项（存 .lnk 文件名）。
    /// 有这个黑名单，同步才不会把用户刚删掉的图标又贴回来。
    /// 注意：只影响 Dock，不动 Windows 任务栏本身。
    /// </summary>
    public List<string> HiddenPins { get; set; } = new();

    /// <summary>
    /// 是否在 Dock 左端放一个"启动台"入口：点开全屏应用网格。
    /// 默认开 —— 任务栏藏掉之后，光靠固定项找不到没钉过的应用。
    /// </summary>
    public bool ShowLaunchpad { get; set; } = true;

    /// <summary>
    /// 是否在 Dock 左端放一个"文件资源管理器"入口。
    /// 默认开：这是 Windows 上最常被点的一个入口；如果任务栏固定项里本来就有，
    /// RebuildSlots 会跳过，不会出现两个。
    /// </summary>
    public bool ShowExplorer { get; set; } = true;

    /// <summary>
    /// 是否在 Dock 最右端显示时间与日期。
    ///
    /// Windows 任务栏被藏掉之后，时钟也跟着一起消失 —— 这是 Windows 没有提供
    /// 单独救回托盘任一元素的办法。时钟是任务栏里最常被扫一眼的信息，
    /// 所以单独画在状态图标右侧，占的是固定宽度，不参与图标放大。
    ///
    /// 属性默认 true：老配置里没有这个字段时，反序列化会保留这里的默认值，
    /// 不需要再写一次版本迁移。
    /// </summary>
    public bool ShowClock { get; set; } = true;

    /// <summary>
    /// 显示 / 隐藏 Dock 的快捷键。
    ///
    /// 字符串格式："Ctrl+Alt+D"、"MouseMiddle"、"MouseX1"、"MouseX2"，空串 = 关闭。
    /// 鼠标键只在 Dock 自己那一块（或它消失后原来的位置）生效，不会抢走其他程序里的中键；
    /// 键盘组合是系统级全局热键。
    /// 默认鼠标中键：点一下 Dock 让整条消失，再在同一位置点一下回来。
    /// </summary>
    public string ToggleShortcut { get; set; } = ShortcutSpec.Default;

    /// <summary>
    /// 左端「开始」按钮用的自定义图标（图片绝对路径）。空串 = 内置 Windows 徽标。
    ///
    /// 允许用户换图是因为原来那个图标是画死的：四个蓝色方块 + 渐变，
    /// 想换成自己的头像、或某个 logo，没有任何入口。图片会被居中裁成正方形、
    /// 套 22% 圆角、预乘后当纹理用（见 GlassRenderer.GetStart）。
    /// </summary>
    public string StartIconPath { get; set; } = "";

    public DockMetricsConfig Metrics { get; set; } = new();
    public GlassConfig Glass { get; set; } = new();
    public List<DockItemConfig> Items { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Directory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiquidGlassDesktop");

    public static string FilePath => System.IO.Path.Combine(Directory, "config.json");

    public static DockConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                DockConfig? cfg = JsonSerializer.Deserialize<DockConfig>(File.ReadAllText(FilePath), Options);
                if (cfg is not null)
                {
                    // v1 -> v2：固定区改由 Windows 任务栏驱动，旧的那批自动生成的固定项
                    // 会和同步来的图标重复（一个是 exe 路径、一个是 .lnk 路径，去重对不上），
                    // 所以一次性清空。用户自己加的东西在 v1 里没有别的入口，不存在误删。
                    if (cfg.Version < 2)
                    {
                        Logger.Info($"配置升级 v{cfg.Version} -> v2：清空 {cfg.Items.Count} 个旧的固定项，改由 Windows 任务栏同步");
                        cfg.Items.Clear();
                        cfg.Version = 2;
                    }

                    // v2 -> v3：v2 的加载路径在 Items 为空时会补一批内置默认项，
                    // 这些从来不在 Windows 任务栏上，却被 Save 写进了配置，
                    // 结果 Dock 固定区多出记事本、计算器、设置这些"任务栏上根本没有"的图标。
                    // 用户自己钉的项一定带 MatchPath（右侧栏"固定到 Dock"时写入），据此区分。
                    // 这段放在版本判断之外，任何一次加载都顺手清理，避免历史脏数据复活。
                    int junk = cfg.Items.RemoveAll(i =>
                        i.Source != "windows" && string.IsNullOrEmpty(i.MatchPath) &&
                        string.IsNullOrEmpty(i.PinKey) && IsLegacyDefaultPath(i.Path));
                    if (junk > 0)
                        Logger.Info($"配置清理：剔除 {junk} 个内置默认固定项（固定区只跟随 Windows 任务栏）");
                    if (cfg.Version < 3) cfg.Version = 3;

                    // v3 -> v4：整体往"更透、更轻"调一档。
                    //
                    // 只改上面的默认值没用：用户机器上早就有 config.json 了，
                    // Load 出来的对象会把默认值全套盖掉，改了等于没改。
                    // 所以做成一次性迁移，把这几项直接落进已有配置。
                    // 只动这四个直接决定通透感的量，别的一概不碰 ——
                    // 用户自己调过的折射、边光、饱和度都还得是原样。
                    if (cfg.Version < 4)
                    {
                        cfg.Glass.Transmission = 0.85f;   // 「很透」档
                        cfg.Glass.BodyAlpha = 0.05f;      // 本体那层白雾再薄一半
                        cfg.Glass.Milk = 0.10f;           // 亮处不要糊成一块白板
                        cfg.Glass.Shadow = 0.26f;         // 外阴影减四成
                        cfg.Version = 4;
                        Logger.Info("配置升级 v3 -> v4：玻璃更通透（Transmission 0.85）、外阴影更轻（0.26）");
                    }

                    // v4 -> v5：再往"更像玻璃"推一档。
                    //
                    // 这一档同时动两组量，因为单动一组一定出问题：
                    //   通透组（Transmission↑ / BodyAlpha↓ / Milk↓）—— 让背景真正透出来；
                    //   质感组（折射/边光/色散/镜面高光↑）—— 补回因为变透而丢掉的"玻璃"信号。
                    // 只加通透会变成"蒙了一层灰的壁纸"，只加质感会变成"一块磨砂塑料"。
                    if (cfg.Version < 5)
                    {
                        cfg.Glass.Transmission = 0.93f;
                        cfg.Glass.BodyAlpha = 0.02f;
                        cfg.Glass.Milk = 0.05f;
                        cfg.Glass.RefractBand = 44f;
                        cfg.Glass.RefractAmount = 17f;
                        cfg.Glass.Dispersion = 0.62f;
                        cfg.Glass.RimIntensity = 0.62f;
                        cfg.Glass.Gloss = 0.72f;
                        cfg.Glass.EdgeSpec = 0.72f;
                        cfg.Glass.Shadow = 0.20f;
                        cfg.Version = 5;
                        Logger.Info("配置升级 v4 -> v5：更透（0.93）+ 玻璃质感加强（折射 17 / 边光 0.62 / 色散 0.62）");
                    }

                    // v5 -> v6：边光收一档。
                    //
                    // v5 为了补回"变透之后丢掉的玻璃信号"，把边光推到 0.62、贴壁高光
                    // 推到 0.72。这两条在 80px 的 Dock 条上刚好像一道反光，可一旦摊到
                    // 1280x820 的启动台上，就成了一圈描出来的白线 —— 用户原话是
                    // "这个边框是不是太亮了些"。只降这两个量，折射/色散/光泽一概不动，
                    // 玻璃的厚度和通透都不受影响，减掉的只是那圈抢眼的亮线。
                    if (cfg.Version < 6)
                    {
                        cfg.Glass.RimIntensity = 0.42f;
                        cfg.Glass.EdgeSpec = 0.55f;
                        cfg.Version = 6;
                        Logger.Info("配置升级 v5 -> v6：边光 0.62 -> 0.42、贴壁高光 0.72 -> 0.55");
                    }

                    // v6 -> v7：白底回白玻璃。
                    //
                    // v1.59 试过"白底压成烟熏暗玻璃"，Store.Smoke 被写进存档 0.92。
                    // v1.60 把代码默认值改回 0，可启动时读的是存档，0.92 又把默认值
                    // 盖回去了 —— 用户看到的现象是"怎么还是深色"。
                    // 这类"改了默认值但存档还在"的坑必须靠版本迁移兜住，
                    // 否则用户手上的配置永远停在旧值上，代码怎么改都不生效。
                    if (cfg.Version < 7)
                    {
                        cfg.Glass.Smoke = 0f;
                        cfg.Version = 7;
                        Logger.Info("配置升级 v6 -> v7：白底恢复白玻璃（Smoke 0.92 -> 0）");
                    }

                    if (cfg.Version < 8)
                    {
                        cfg.Glass.RefractAmount = 26f;
                        cfg.Glass.Bulge = 0.18f;
                        cfg.Glass.Dispersion = 0.48f;
                        cfg.Glass.BlurStep = 0.75f;
                        cfg.Glass.RimIntensity = 0.36f;
                        cfg.Glass.RimWidth = 10f;
                        cfg.Glass.Gloss = 0.64f;
                        cfg.Glass.EdgeSpec = 0.82f;
                        cfg.Glass.Rolloff = 0.90f;
                        cfg.Glass.Smoke = 0f;
                        cfg.Version = 8;
                        Logger.Info("配置升级 v7 -> v8：清晰主体、边缘液态折射与薄镜面反光，白底取消灰色填充");
                    }

                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("配置读取失败，使用默认值: " + ex.Message);
        }

        // 全新配置：固定项留空，由 Windows 任务栏的固定项填充（见 DockApp.SyncPins）
        return new DockConfig();
    }

    /// <summary>原子写：先写 .tmp 再替换，断电/崩溃不会留下半截 JSON。</summary>
    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null);
            else
                File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            Logger.Warn("配置保存失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 老版本内置的"默认固定项"。只用于两件事：清理历史配置里的脏数据、
    /// 以及"Windows 任务栏一个固定项都读不到"时的兜底展示。
    /// </summary>
    private static readonly (string Name, string Path)[] LegacyCandidates =
    {
        ("文件资源管理器", @"C:\Windows\explorer.exe"),
        ("终端", @"C:\Windows\System32\WindowsTerminal.exe"),
        ("记事本", @"C:\Windows\System32\notepad.exe"),
        ("计算器", @"C:\Windows\System32\calc.exe"),
        ("画图", @"C:\Windows\System32\mspaint.exe"),
        ("设置", "ms-settings:"),
        ("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
        ("VS Code", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Programs\Microsoft VS Code\Code.exe")),
    };

    private static readonly HashSet<string> LegacyDefaultPaths =
        new(LegacyCandidates.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);

    private static bool IsLegacyDefaultPath(string path)
        => path.Length > 0 && LegacyDefaultPaths.Contains(path);

    /// <summary>
    /// 兜底固定项。只在"Windows 任务栏一个固定项都读不到、用户也没手动加过"时使用，
    /// 保证 Dock 不会空着一条什么都没有的玻璃条。
    /// 注意 Source 标成 default：它们不是用户的固定选择，永远不该被写进配置当固定项。
    /// </summary>
    public static List<DockItemConfig> FallbackItems()
    {
        var list = new List<DockItemConfig>();
        foreach ((string name, string path) in LegacyCandidates)
        {
            bool ok = path.Contains(':') && !path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase)
                ? File.Exists(path)
                : true;
            if (ok) list.Add(new DockItemConfig { Name = name, Path = path, Source = "default" });
        }

        if (list.Count == 0)
            list.Add(new DockItemConfig
            {
                Name = "记事本",
                Path = @"C:\Windows\System32\notepad.exe",
                Source = "default",
            });

        return list;
    }
}
