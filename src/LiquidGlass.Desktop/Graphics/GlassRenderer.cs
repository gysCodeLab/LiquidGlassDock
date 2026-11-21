using System.Numerics;
using System.Runtime.InteropServices;
using LiquidGlass.Desktop.App;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiquidGlass.Desktop.Graphics;

/// <summary>与 Glass.hlsl 的 cbuffer 一一对应（10 × float4 = 160 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GlassParams
{
    public Vector4 PanelRect;   // xy 左上角(窗口像素) zw 尺寸
    public Vector4 ScreenMap;   // xy 桌面采样偏移(像素) zw 1/采样尺寸
    public Vector4 Material;    // x 模糊步长 y tint z 曝光 w 饱和度
    public Vector4 Optics;      // x 折射带 y 位移 z 色散 w 鼓胀
    public Vector4 Rim;         // x 边光 y 边光宽 z 光泽 w 阴影
    public Vector4 Shape;       // x 圆角 y 阴影外延 z 窗口宽 w 窗口高
    public Vector4 Extra;       // x 时间 y dpi z 有无纹理 w 对比度
    public Vector4 BodyColor;   // rgb 本体色 a 本体不透明度
    public Vector4 Material2;   // x 暗部散射 y 亮部奶白 z 高光软限 w 边缘镜面
    public Vector4 Transmission; // x 通透度 0=全磨砂 1=最透
    public Vector4 Fx;           // x 边缘羽化像素 y 壁厚权重
    public Vector4 Recovery;
}

internal sealed class GlassRenderer : IDisposable
{
    private readonly D3DContext _ctx;
    private readonly ShaderLibrary _lib;

    private readonly ID3D11VertexShader _vsQuad;
    private readonly ID3D11PixelShader _psCopy;
    private readonly ID3D11PixelShader _psBlurH;
    private readonly ID3D11PixelShader _psBlurV;
    private readonly ID3D11PixelShader _psGlass;
    private readonly ID3D11VertexShader _vsIcon;
    private readonly ID3D11PixelShader _psIcon;

    private readonly ID3D11Buffer _cb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _vb;
    private readonly int _vbCapacity;
    private readonly List<IconVertex> _vertices = new(256);

    private ID3D11Texture2D? _l1, _l2, _l3;
    private ID3D11ShaderResourceView? _l1Srv, _l2Srv, _l3Srv;
    private ID3D11RenderTargetView? _l1Rtv, _l2Rtv, _l3Rtv;
    private int _capW = -1, _capH = -1;

    private readonly Dictionary<string, ID3D11ShaderResourceView> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private ID3D11ShaderResourceView? _start;
    private ID3D11ShaderResourceView? _launchpad;
    private ID3D11ShaderResourceView? _statusGlyph;
    private ID3D11ShaderResourceView? _clock;
    private string _clockKey = "";

    [StructLayout(LayoutKind.Sequential)]
    private struct IconVertex
    {
        public Vector2 Pos;
        public Vector2 Uv;
        public Vector4 Color;
    }

    public GlassRenderer(D3DContext ctx, string shaderSource)
    {
        _ctx = ctx;
        _lib = new ShaderLibrary(ctx);

        _vsQuad = _lib.Vertex(shaderSource, "VS_Quad");
        _psCopy = _lib.Pixel(shaderSource, "PS_Copy");
        _psBlurH = _lib.Pixel(shaderSource, "PS_BlurH");
        _psBlurV = _lib.Pixel(shaderSource, "PS_BlurV");
        _psGlass = _lib.Pixel(shaderSource, "PS_Glass");
        byte[] vsIconBytecode = _lib.Compile(shaderSource, "VS_Icon", "vs_5_0");
        _vsIcon = ctx.Device.CreateVertexShader(vsIconBytecode);
        _psIcon = _lib.Pixel(shaderSource, "PS_Icon");

        _inputLayout = ctx.Device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0),
        }, vsIconBytecode);

        _cb = ctx.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)Marshal.SizeOf<GlassParams>(),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        }, (SubresourceData?)null);

        _sampler = ctx.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        });

        var rt = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };

        BlendDescription blendDesc = default;
        blendDesc.AlphaToCoverageEnable = false;
        blendDesc.IndependentBlendEnable = false;
        blendDesc.RenderTarget[0] = rt;
        _blend = ctx.Device.CreateBlendState(blendDesc);

        _raster = ctx.Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            DepthClipEnable = false,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false,
        });

        _vbCapacity = 4096;
        _vb = ctx.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = (uint)(_vbCapacity * Marshal.SizeOf<IconVertex>()),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.Write,
        }, (SubresourceData?)null);
    }

    private ID3D11ShaderResourceView? _sep;

    /// <summary>当前 <see cref="_start"/> 是用哪张外部图片生成的；空串表示内置徽标。</summary>
    private string _startSource = "";

    /// <summary>
    /// 左端「开始」按钮的图标。
    ///
    /// 默认是程序生成的 Windows 徽标（四块圆角方块 + 蓝色渐变），因为图标尺寸随 DPI 变，
    /// 抠系统位图会糊。但用户的诉求是「只能这样吗」——所以给一条覆盖路径：
    /// <paramref name="imagePath"/> 非空时优先用那张图片，做成圆角方形贴图。
    ///
    /// 缓存按「图片路径」失效：换图（或恢复默认）后再进来会重建纹理，
    /// 只判断 _start 是否为 null 的话换了图也不会变。
    /// </summary>
    public ID3D11ShaderResourceView GetStart(int size, string? imagePath = null)
    {
        string src = imagePath ?? "";
        if (_start is not null && string.Equals(_startSource, src, StringComparison.OrdinalIgnoreCase))
            return _start;

        _start?.Dispose();
        _start = null;

        int n = Math.Clamp(size, 32, 256);
        byte[] px = src.Length > 0 && TryLoadIconImage(src, n, out byte[] loaded)
            ? loaded
            : DrawStartGlyph(n);

        _startSource = src;
        _start = CreateSrv(px, n, n);
        return _start;
    }

    /// <summary>
    /// 把任意图片做成图标：居中裁正方形 → 缩放到 n×n → 圆角遮罩 → 预乘 BGRA。
    ///
    /// 圆角用 22% 边长（iOS/macOS 图标的观感），比直角好看，也比圆形更宽容 ——
    /// 用户拿一张 logo 当开始图标时，圆形会把四个角切掉。
    /// </summary>
    private static bool TryLoadIconImage(string path, int n, out byte[] px)
    {
        px = Array.Empty<byte>();
        try
        {
            if (!File.Exists(path)) return false;

            // 用流打开，避免 Image.FromFile 把文件锁住（用户可能想马上换下一张）
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var src = System.Drawing.Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);

            int side = Math.Min(src.Width, src.Height);
            int sx = (src.Width - side) / 2;
            int sy = (src.Height - side) / 2;

            using var bmp = new System.Drawing.Bitmap(n, n, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(src, new System.Drawing.Rectangle(0, 0, n, n),
                    new System.Drawing.Rectangle(sx, sy, side, side), System.Drawing.GraphicsUnit.Pixel);
            }

            var rect = new System.Drawing.Rectangle(0, 0, n, n);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            byte[] raw = new byte[n * n * 4];
            try
            {
                for (int y = 0; y < n; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, raw, y * n * 4, n * 4);
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            px = new byte[n * n * 4];
            float rad = n * 0.22f;
            float half = n * 0.5f;

            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // 圆角矩形有符号距离场（和外框用的同一套）
                float dx = MathF.Abs(x + 0.5f - half) - (half - rad);
                float dy = MathF.Abs(y + 0.5f - half) - (half - rad);
                float ox = MathF.Max(dx, 0f), oy = MathF.Max(dy, 0f);
                float d = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(dx, dy), 0f) - rad;
                float cov = Math.Clamp(0.5f - d, 0f, 1f);

                int i = (y * n + x) * 4;
                byte a = (byte)Math.Clamp((int)(raw[i + 3] * cov), 0, 255);
                // System.Drawing 的 Format32bppArgb 在内存里就是 B,G,R,A —— 正好是 D3D 要的序
                px[i] = (byte)(raw[i] * a / 255);
                px[i + 1] = (byte)(raw[i + 1] * a / 255);
                px[i + 2] = (byte)(raw[i + 2] * a / 255);
                px[i + 3] = a;
            }

            return true;
        }
        catch (Exception ex)
        {
            // 图片损坏 / 格式不支持 / 权限不足：退回内置徽标，不让 Dock 起不来。
            // 必须记日志 —— 吞掉异常的话，用户看到的就是"选了图但没反应"，没法查。
            Logger.Warn($"开始按钮图标加载失败（{path}）：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 内置图标在纹理内部留的边（占图标边长的比例）。
    /// 布局要用它：玻璃的外边距是按槽位算的，而应用图标几乎铺满槽位、
    /// 内置图标却留了边，于是最左端的空白会比最右端宽出一截。
    /// </summary>
    public const float LaunchpadInset = 0.15f;
    public const float StartGlyphInset = 0.13f;

    /// <summary>内置的 Windows 徽标（四块圆角方块，蓝色上下渐变）。</summary>
    private static byte[] DrawStartGlyph(int n)
    {
        byte[] px = new byte[n * n * 4];

        float margin = n * StartGlyphInset;
        float gap = n * 0.075f;
        float pane = (n - margin * 2f - gap) * 0.5f;
        float rad = pane * 0.24f;

        for (int row = 0; row < 2; row++)
        for (int col = 0; col < 2; col++)
        {
            float x0 = margin + col * (pane + gap);
            float y0 = margin + row * (pane + gap);
            float cx = x0 + pane * 0.5f;
            float cy = y0 + pane * 0.5f;

            int ix0 = (int)MathF.Floor(x0) - 1, ix1 = (int)MathF.Ceiling(x0 + pane) + 1;
            int iy0 = (int)MathF.Floor(y0) - 1, iy1 = (int)MathF.Ceiling(y0 + pane) + 1;

            for (int y = Math.Max(0, iy0); y < Math.Min(n, iy1); y++)
            for (int x = Math.Max(0, ix0); x < Math.Min(n, ix1); x++)
            {
                // 圆角矩形有符号距离场
                float dx = MathF.Abs(x + 0.5f - cx) - (pane * 0.5f - rad);
                float dy = MathF.Abs(y + 0.5f - cy) - (pane * 0.5f - rad);
                float ox = MathF.Max(dx, 0f), oy = MathF.Max(dy, 0f);
                float d = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(dx, dy), 0f) - rad;
                float cov = Math.Clamp(0.5f - d, 0f, 1f);
                if (cov <= 0f) continue;

                // 上浅下深的 Windows 蓝渐变（#4CC2FF → #0078D4）
                float t = (y + 0.5f) / n;
                float r = 0x4C + (0x00 - 0x4C) * t;
                float g = 0xC2 + (0x78 - 0xC2) * t;
                float b = 0xFF + (0xD4 - 0xFF) * t;

                byte a = (byte)Math.Clamp((int)(cov * 255f), 0, 255);
                int i = (y * n + x) * 4;
                px[i] = (byte)(b * a / 255f);       // 预乘
                px[i + 1] = (byte)(g * a / 255f);
                px[i + 2] = (byte)(r * a / 255f);
                px[i + 3] = a;
            }
        }

        return px;
    }

    /// <summary>
    /// 启动台图标：3×3 的小方块阵列。
    ///
    /// 为什么让程序画而不是找一个系统图标：启动台是概念图标，Windows 里没有对应的
    /// 现成图案；随手拿 shell32 的某个图标会和"开始"混淆。3×3 与开始的 2×2 一眼能分。
    /// </summary>
    public ID3D11ShaderResourceView GetLaunchpad(int size)
    {
        if (_launchpad is not null) return _launchpad;

        int n = Math.Clamp(size, 32, 256);
        byte[] px = new byte[n * n * 4];

        const int Grid = 3;
        float margin = n * LaunchpadInset;
        float gap = n * 0.055f;
        float pane = (n - margin * 2f - gap * (Grid - 1)) / Grid;
        float rad = pane * 0.30f;

        for (int row = 0; row < Grid; row++)
        for (int col = 0; col < Grid; col++)
        {
            float x0 = margin + col * (pane + gap);
            float y0 = margin + row * (pane + gap);
            float cx = x0 + pane * 0.5f;
            float cy = y0 + pane * 0.5f;

            int ix0 = (int)MathF.Floor(x0) - 1, ix1 = (int)MathF.Ceiling(x0 + pane) + 1;
            int iy0 = (int)MathF.Floor(y0) - 1, iy1 = (int)MathF.Ceiling(y0 + pane) + 1;

            for (int y = Math.Max(0, iy0); y < Math.Min(n, iy1); y++)
            for (int x = Math.Max(0, ix0); x < Math.Min(n, ix1); x++)
            {
                float dx = MathF.Abs(x + 0.5f - cx) - (pane * 0.5f - rad);
                float dy = MathF.Abs(y + 0.5f - cy) - (pane * 0.5f - rad);
                float ox = MathF.Max(dx, 0f), oy = MathF.Max(dy, 0f);
                float d = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(dx, dy), 0f) - rad;
                float cov = Math.Clamp(0.5f - d, 0f, 1f);
                if (cov <= 0f) continue;

                // 冷白渐变：亮背景上不会糊，暗背景上又比纯白柔和
                float t = (y + 0.5f) / n;
                float r = 255f + (196f - 255f) * t;
                float g = 255f + (206f - 255f) * t;
                float b = 255f + (226f - 255f) * t;

                byte a = (byte)Math.Clamp((int)(cov * 255f), 0, 255);
                int i = (y * n + x) * 4;
                px[i] = (byte)(b * a / 255f);       // 预乘
                px[i + 1] = (byte)(g * a / 255f);
                px[i + 2] = (byte)(r * a / 255f);
                px[i + 3] = a;
            }
        }

        _launchpad = CreateSrv(px, n, n);
        return _launchpad;
    }

    /// <summary>状态位图标的留边（占边长比例）。和启动台一样是"程序画的"纹理。</summary>
    public const float StatusGlyphInset = 0.17f;

    /// <summary>
    /// 系统状态位图标：三条滑块，点一下开 Windows 快速设置（Win+A）。
    ///
    /// 这个槽位接的是原来 DOCK 右边那颗独立胶囊（电量 / 网络 / 时间）。胶囊撤掉
    /// 之后，Dock 上得留一个去开快速设置的入口 —— 滑块就是系统里"设置 / 调节"
    /// 的通用符号，而且三条横线在 54px 的图标尺寸下不会糊成一团（齿轮的齿一到
    /// 这个尺寸就没了）。
    ///
    /// 同样由程序画：Windows 没有现成的"快速设置"图标可取，随便拿 shell32 里的
    /// 一个又会跟设置齿轮撞脸。
    /// </summary>
    public ID3D11ShaderResourceView GetStatusGlyph(int size)
    {
        if (_statusGlyph is not null) return _statusGlyph;

        int n = Math.Clamp(size, 32, 256);
        byte[] px = new byte[n * n * 4];

        float margin = n * StatusGlyphInset;
        float w = n - margin * 2f;
        float halfW = w * 0.5f;
        float trackR = MathF.Max(0.8f, n * 0.024f);
        float knobR = MathF.Max(2.2f, n * 0.072f);

        // 三个圆点的位置错开 —— 这是整个图标的读法所在。对齐的话它看起来
        // 只是三根横线，"错开 + 一个实心点"才读得出"这些可以拖"。
        float[] knobAt = { 0.64f, 0.26f, 0.72f };

        for (int row = 0; row < 3; row++)
        {
            float cy = margin + w * ((row + 0.5f) / 3f);
            float knobX = margin + w * knobAt[row];

            int y0 = (int)MathF.Floor(cy - knobR) - 1;
            int y1 = (int)MathF.Ceiling(cy + knobR) + 1;
            int x0 = (int)MathF.Floor(margin) - 1;
            int x1 = (int)MathF.Ceiling(margin + w) + 1;

            for (int y = Math.Max(0, y0); y < Math.Min(n, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(n, x1); x++)
            {
                float pxc = x + 0.5f, pyc = y + 0.5f;

                // 轨道：水平圆角长条（b = (halfW, trackR), r = trackR）
                float qx = MathF.Abs(pxc - (margin + halfW)) - halfW + trackR;
                float qy = MathF.Abs(pyc - cy);
                float ox = MathF.Max(qx, 0f), oy = MathF.Max(qy, 0f);
                float dTrack = MathF.Sqrt(ox * ox + oy * oy)
                             + MathF.Min(MathF.Max(qx, qy), 0f) - trackR;

                // 圆点压在轨道上，所以两者的覆盖取更大值即可
                float dKnob = MathF.Sqrt((pxc - knobX) * (pxc - knobX)
                                       + (pyc - cy) * (pyc - cy)) - knobR;

                float covKnob = Math.Clamp(0.5f - dKnob, 0f, 1f);
                float covTrack = Math.Clamp(0.5f - dTrack, 0f, 1f) * 0.66f;   // 轨道压一档，圆点才跳出来
                float cov = MathF.Max(covKnob, covTrack);
                if (cov <= 0f) continue;

                // 冷白渐变，和启动台 / 开始徽标同一套（亮底不糊，暗底比纯白柔和）
                float t = (y + 0.5f) / n;
                float r = 255f + (198f - 255f) * t;
                float g = 255f + (208f - 255f) * t;
                float b = 255f + (228f - 255f) * t;

                byte a = (byte)Math.Clamp((int)(cov * 255f), 0, 255);
                int i = (y * n + x) * 4;
                px[i] = (byte)(b * a / 255f);       // 预乘
                px[i + 1] = (byte)(g * a / 255f);
                px[i + 2] = (byte)(r * a / 255f);
                px[i + 3] = a;
            }
        }

        _statusGlyph = CreateSrv(px, n, n);
        return _statusGlyph;
    }

    /// <summary>
    /// 时钟纹理：上排 HH:mm，下排月日。
    ///
    /// 用 GDI 画而不是在着色器里排字：小字号下 GDI 的字形和抗锯齿明显好于 SDF/图集，
    /// 而且一分钟才重建一次，成本可以忽略。缓存键带上分钟与像素尺寸，
    /// 渲染循环每帧调用也只是字符串比较。
    ///
    /// 白色字 + 1px 深色投影：Dock 玻璃在深色壁纸上是暗底、亮壁纸上是亮底，
    /// 单靠白字在亮底上会消失，叠一层投影两种底都读得出来。
    /// </summary>
    public ID3D11ShaderResourceView? GetClock(DateTime now, int w, int h)
    {
        string time = now.ToString("HH:mm");
        string date = now.ToString("M月d日");
        string key = $"{time}|{date}|{w}x{h}";
        if (_clock is not null && _clockKey == key) return _clock;

        _clock?.Dispose();
        _clock = null;

        w = Math.Clamp(w, 24, 512);
        h = Math.Clamp(h, 12, 256);

        try
        {
            using var bmp = new System.Drawing.Bitmap(w, h,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                using var format = new System.Drawing.StringFormat
                {
                    Alignment = System.Drawing.StringAlignment.Far,
                    LineAlignment = System.Drawing.StringAlignment.Near,
                    FormatFlags = System.Drawing.StringFormatFlags.NoWrap,
                };

                // 宽了就把字号收一档，宁可小一点也不要被裁掉半截
                float timePx = FitWidth(g, time, "Segoe UI", MathF.Max(8f, h * 0.54f), w - 2f);
                float datePx = FitWidth(g, date, "Segoe UI", MathF.Max(7f, h * 0.29f), w - 2f);

                using var timeFit = new System.Drawing.Font("Segoe UI", timePx,
                    System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
                using var dateFit = new System.Drawing.Font("Segoe UI", datePx,
                    System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);

                using var shadow = new System.Drawing.SolidBrush(
                    System.Drawing.Color.FromArgb(120, 0, 0, 0));
                using var ink = new System.Drawing.SolidBrush(
                    System.Drawing.Color.FromArgb(240, 255, 255, 255));

                var timeRect = new System.Drawing.RectangleF(0, -1, w, h * 0.66f);
                var dateRect = new System.Drawing.RectangleF(0, h * 0.60f, w, h * 0.40f);

                g.DrawString(time, timeFit, shadow, Offset(timeRect), format);
                g.DrawString(date, dateFit, shadow, Offset(dateRect), format);
                g.DrawString(time, timeFit, ink, timeRect, format);
                g.DrawString(date, dateFit, ink, dateRect, format);
            }

            var rect = new System.Drawing.Rectangle(0, 0, w, h);
            System.Drawing.Imaging.BitmapData data = bmp.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            byte[] px = new byte[w * h * 4];
            try
            {
                for (int y = 0; y < h; y++)
                {
                    int row = w * 4;
                    Marshal.Copy(data.Scan0 + y * data.Stride, px, y * row, row);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            // Format32bppArgb 是直通 alpha，着色器要的是预乘
            for (int i = 0; i < px.Length; i += 4)
            {
                int a = px[i + 3];
                if (a == 255) continue;
                px[i] = (byte)(px[i] * a / 255);
                px[i + 1] = (byte)(px[i + 1] * a / 255);
                px[i + 2] = (byte)(px[i + 2] * a / 255);
            }

            _clockKey = key;
            _clock = CreateSrv(px, w, h);
            return _clock;
        }
        catch (Exception ex)
        {
            Logger.Warn("时钟纹理生成失败: " + ex.Message);
            return null;
        }
    }

    private static System.Drawing.RectangleF Offset(System.Drawing.RectangleF r)
        => new(r.X + 1f, r.Y + 1f, r.Width, r.Height);

    private static float FitWidth(System.Drawing.Graphics g, string text, string family,
        float sizePx, float maxWidth)
    {
        using var font = new System.Drawing.Font(family, sizePx,
            System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        float width = g.MeasureString(text, font).Width;
        if (width <= maxWidth || width <= 0f) return sizePx;
        return MathF.Max(7f, sizePx * maxWidth / width);
    }

    private readonly Dictionary<int, ID3D11ShaderResourceView> _dots = new(2);

    /// <summary>
    /// 圆形运行指示点。
    ///
    /// 这里有个坑：如果生成一张大图（比如 16×16）再缩到 4px，四个采样点会
    /// **全部落在圆心附近**，抗锯齿的边缘被整个跳过，结果还是一个方块。
    /// 所以必须按目标的实际像素数逐尺寸生成，做到 1 纹素 ≈ 1 像素。
    /// </summary>
    public ID3D11ShaderResourceView GetDot(int pixels)
    {
        if (_dots.TryGetValue(pixels, out ID3D11ShaderResourceView? cached)) return cached;

        int n = Math.Clamp(pixels, 3, 32);
        byte[] px = new byte[n * n * 4];
        float r = n * 0.5f;

        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float dx = x + 0.5f - r;
            float dy = y + 0.5f - r;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            float cov = Math.Clamp(r - d + 0.5f, 0f, 1f);
            byte a = (byte)Math.Clamp((int)(cov * 255f), 0, 255);
            int i = (y * n + x) * 4;
            px[i] = a; px[i + 1] = a; px[i + 2] = a; px[i + 3] = a;   // 预乘
        }

        ID3D11ShaderResourceView srv = CreateSrv(px, n, n);
        _dots[pixels] = srv;
        return srv;
    }

    /// <summary>
    /// 分隔竖线。上下两端做 alpha 衰减，否则一条等宽的实心线会在玻璃上
    /// 显得又硬又脏；渐隐之后它读起来更像"一道光缝"。
    /// </summary>
    public ID3D11ShaderResourceView GetSeparator()
    {
        if (_sep is not null) return _sep;

        const int n = 64;
        byte[] px = new byte[n * 4];
        for (int y = 0; y < n; y++)
        {
            float t = (y + 0.5f) / n;                    // 0 顶部 → 1 底部
            float edge = MathF.Min(t, 1f - t) * 2f;       // 两端 0，中段 1
            float a = Math.Clamp(edge * 1.6f, 0f, 1f);
            a = a * a * (3f - 2f * a);                    // smoothstep，收得更干净
            byte v = (byte)Math.Clamp((int)(a * 255f), 0, 255);
            int i = y * 4;
            px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = v;
        }

        _sep = CreateSrv(px, 1, n);
        return _sep;
    }

    public ID3D11ShaderResourceView GetIcon(string path, string cacheKey, int size)
    {
        if (_iconCache.TryGetValue(cacheKey, out ID3D11ShaderResourceView? cached)) return cached;

        byte[]? pixels = Platform.IconLoader.Load(path, size);
        if (pixels is null)
        {
            // 兜底：一块中性占位（避免空纹理导致整帧丢失）
            pixels = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                bool inside = x > size * 0.12f && x < size * 0.88f && y > size * 0.12f && y < size * 0.88f;
                byte a = inside ? (byte)200 : (byte)0;
                pixels[i] = (byte)(60 * a / 255);
                pixels[i + 1] = (byte)(70 * a / 255);
                pixels[i + 2] = (byte)(90 * a / 255);
                pixels[i + 3] = a;
            }
        }

        ID3D11ShaderResourceView srv = CreateSrv(pixels, size, size);
        _iconCache[cacheKey] = srv;
        return srv;
    }

    private ID3D11ShaderResourceView CreateSrv(byte[] pixels, int w, int h)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var data = new SubresourceData(handle.AddrOfPinnedObject(), (uint)(w * 4));
            ID3D11Texture2D tex = _ctx.Device.CreateTexture2D(desc, data);
            ID3D11ShaderResourceView srv = _ctx.Device.CreateShaderResourceView(tex, null);
            tex.Dispose();
            return srv;
        }
        finally
        {
            handle.Free();
        }
    }

    private void EnsureTargets(int capW, int capH)
    {
        if (_l1 is not null && _capW == capW && _capH == capH) return;

        _l1Srv?.Dispose(); _l1Rtv?.Dispose(); _l1?.Dispose();
        _l2Srv?.Dispose(); _l2Rtv?.Dispose(); _l2?.Dispose();
        _l3Srv?.Dispose(); _l3Rtv?.Dispose(); _l3?.Dispose();

        _capW = Math.Max(capW, 8);
        _capH = Math.Max(capH, 8);

        int w1 = Math.Max(_capW / 2, 4), h1 = Math.Max(_capH / 2, 4);
        int w2 = w1, h2 = h1;   // 模糊链全部同尺寸：uv 与纹素一一对应，不再有跨尺寸换算

        (_l1, _l1Srv, _l1Rtv) = CreateTarget(w1, h1);
        (_l2, _l2Srv, _l2Rtv) = CreateTarget(w2, h2);
        (_l3, _l3Srv, _l3Rtv) = CreateTarget(w2, h2);
    }

    private (ID3D11Texture2D, ID3D11ShaderResourceView, ID3D11RenderTargetView) CreateTarget(int w, int h)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        ID3D11Texture2D tex = _ctx.Device.CreateTexture2D(desc);
        ID3D11ShaderResourceView srv = _ctx.Device.CreateShaderResourceView(tex, null);
        ID3D11RenderTargetView rtv = _ctx.Device.CreateRenderTargetView(tex, null);
        return (tex, srv, rtv);
    }

    private void UpdateConstants(in GlassParams p)
    {
        MappedSubresource mapped = _ctx.Context.Map(_cb, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            *(GlassParams*)mapped.DataPointer = p;
        }
        _ctx.Context.Unmap(_cb);
    }

    private void BindPipeline()
    {
        ID3D11DeviceContext c = _ctx.Context;
        c.VSSetShader(_vsQuad);
        c.PSSetShader(_psGlass);
        c.PSSetConstantBuffer(0, _cb);
        c.VSSetConstantBuffer(0, _cb);
        c.PSSetSampler(0, _sampler);
        c.OMSetBlendState(_blend);
        c.OMSetDepthStencilState(null);
        c.RSSetState(_raster);
        c.IASetInputLayout(null);
        c.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
    }

    private void DrawQuad(ID3D11RenderTargetView target, int w, int h, in GlassParams p,
        ID3D11PixelShader ps, ID3D11ShaderResourceView input)
    {
        ID3D11DeviceContext c = _ctx.Context;
        c.PSSetShaderResource(0, null);
        c.PSSetShaderResource(1, null!);
        c.OMSetRenderTargets(target, null);
        c.RSSetViewport(0, 0, w, h, 0, 1);
        c.ClearRenderTargetView(target, new Color4(0, 0, 0, 0));
        c.PSSetShader(ps);
        c.PSSetShaderResource(0, input);
        UpdateConstants(p);
        c.Draw(3, 0);
        c.PSSetShaderResource(0, null);
    }

    /// <summary>一帧完整渲染。</summary>
    public void Render(RenderRequest req)
    {
        ID3D11DeviceContext c = _ctx.Context;
        EnsureTargets(req.CaptureWidth, req.CaptureHeight);
        BindPipeline();

        int w1 = (int)_l1!.Description.Width, h1 = (int)_l1.Description.Height;
        int w2 = (int)_l2!.Description.Width, h2 = (int)_l2.Description.Height;
        // L1/L2/L3 尺寸一致，纹素步长直接用目标尺寸计算

        // ---------- 1. 采集纹理 → L1（半分辨率，同时留作边缘折射的清晰层） ----------
        if (req.CaptureSrv is not null)
        {
            var p = default(GlassParams);
            p.ScreenMap = new Vector4(0, 0, 1f / w1, 1f / h1);
            DrawQuad(_l1Rtv!, w1, h1, p, _psCopy, req.CaptureSrv);

            // ---------- 2/3/4/5. 两轮 H+V 高斯 ----------
            // 一轮 9 抽头的等效 sigma 只有约 9px，窄条 Dock 上星点/文字还会留形；
            // 两轮方差相加，磨砂感才够，而 4 个半分辨率 pass 的开销可以忽略。
            p = default;
            p.ScreenMap = new Vector4(0, 0, 1f / w2, 1f / h2);
            p.Material = new Vector4(req.BlurStep, 0, 0, 0);
            DrawQuad(_l2Rtv!, w2, h2, p, _psBlurH, _l1Srv!);
            DrawQuad(_l3Rtv!, w2, h2, p, _psBlurV, _l2Srv!);
            DrawQuad(_l2Rtv!, w2, h2, p, _psBlurH, _l3Srv!);
            DrawQuad(_l3Rtv!, w2, h2, p, _psBlurV, _l2Srv!);
        }

        var gp = new GlassParams
        {
            PanelRect = new Vector4(req.PanelX, req.PanelY, req.PanelW, req.PanelH),
            ScreenMap = new Vector4(req.CaptureOffsetX, req.CaptureOffsetY,
                1f / Math.Max(1, req.CaptureWidth), 1f / Math.Max(1, req.CaptureHeight)),
            Material = new Vector4(req.BlurStep, req.Tint, req.Exposure, req.Saturation),
            Optics = new Vector4(req.RefractBand, req.RefractAmount, req.Dispersion, req.Bulge),
            Rim = new Vector4(req.RimIntensity, req.RimWidth, req.Gloss, req.Shadow),
            Shape = new Vector4(req.Radius, req.ShadowExtent, req.WindowW, req.WindowH),
            Extra = new Vector4(req.Time, req.DpiScale, req.CaptureSrv is not null ? 1f : 0f, req.Contrast),
            BodyColor = new Vector4(req.BodyR, req.BodyG, req.BodyB, req.BodyAlpha),
            Material2 = new Vector4(req.Scatter, req.Milk, req.Rolloff, req.EdgeSpec),
            Transmission = new Vector4(req.Transmission, req.AlphaPull,
                1f - Math.Clamp(req.BorderCut, 0f, 1f), Math.Clamp(req.EnvLuma, 0f, 1f)),
            Fx = new Vector4(req.EdgeFade, req.WallThickness, req.Smoke, req.Lift),
            Recovery = new Vector4(req.DesktopPassthrough ? 1f : 0f, 0f, 0f, 0f),
        };

        // ---------- 4. 玻璃本体（先把背景铺到目标上，再叠玻璃） ----------
        c.PSSetShaderResource(0, null);
        c.OMSetRenderTargets(req.TargetRtv, null);
        c.RSSetViewport(0, 0, req.WindowW, req.WindowH, 0, 1);
        if (!req.KeepExistingContent)
        {
            c.ClearRenderTargetView(req.TargetRtv, new Color4(0, 0, 0, 0));
        }
        else if (req.CaptureSrv is not null)
        {
            var bp = default(GlassParams);
            bp.ScreenMap = new Vector4(0, 0, 1f / Math.Max(1, req.WindowW), 1f / Math.Max(1, req.WindowH));
            c.PSSetShaderResource(0, req.CaptureSrv);
            c.PSSetShader(_psCopy);
            UpdateConstants(bp);
            c.Draw(3, 0);
        }

        // t0 = 模糊层（本体），t1 = 清晰层（边缘厚壁折射）
        c.PSSetShaderResource(0, req.CaptureSrv is not null ? _l3Srv : null);
        c.PSSetShaderResource(1, _l1Srv);
        c.PSSetShader(_psGlass);
        UpdateConstants(gp);
        c.Draw(3, 0);
        c.PSSetShaderResource(1, null!);

        // ---------- 5. 图标 ----------
        if (req.Icons.Count > 0) DrawIcons(req);
    }

    private void DrawIcons(RenderRequest req)
    {
        _vertices.Clear();
        foreach (IconQuad icon in req.Icons)
        {
            float x0 = icon.X, y0 = icon.Y, x1 = icon.X + icon.W, y1 = icon.Y + icon.H;
            var col = new Vector4(icon.Alpha, icon.Alpha, icon.Alpha, icon.Alpha);
            _vertices.Add(new IconVertex { Pos = new Vector2(x0, y0), Uv = new Vector2(0, 0), Color = col });
            _vertices.Add(new IconVertex { Pos = new Vector2(x1, y0), Uv = new Vector2(1, 0), Color = col });
            _vertices.Add(new IconVertex { Pos = new Vector2(x1, y1), Uv = new Vector2(1, 1), Color = col });
            _vertices.Add(new IconVertex { Pos = new Vector2(x0, y0), Uv = new Vector2(0, 0), Color = col });
            _vertices.Add(new IconVertex { Pos = new Vector2(x1, y1), Uv = new Vector2(1, 1), Color = col });
            _vertices.Add(new IconVertex { Pos = new Vector2(x0, y1), Uv = new Vector2(0, 1), Color = col });
        }

        ID3D11DeviceContext c = _ctx.Context;
        MappedSubresource mapped = c.Map(_vb, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            var span = new Span<IconVertex>((void*)mapped.DataPointer, _vertices.Count);
            CollectionsMarshal.AsSpan(_vertices).CopyTo(span);
        }
        c.Unmap(_vb);

        var p = new GlassParams
        {
            Shape = new Vector4(0, 0, req.WindowW, req.WindowH),
            Extra = new Vector4(req.Time, req.DpiScale, 1, 1),
        };
        UpdateConstants(p);

        c.VSSetShader(_vsIcon);
        c.PSSetShader(_psIcon);
        c.IASetInputLayout(_inputLayout);
        c.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<IconVertex>(), 0);
        c.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);

        int index = 0;
        foreach (IconQuad icon in req.Icons)
        {
            // PS_Icon 声明在 t1（与玻璃的清晰层同槽），必须绑到 1 号槽
            c.PSSetShaderResource(1, icon.Texture);
            c.Draw(6, (uint)(index * 6));
            index++;
        }

        c.PSSetShaderResource(1, null!);
        c.IASetVertexBuffer(0, null!, 0, 0);
    }

    /// <summary>调试：把模糊链的中间结果导出成 BMP，用于定位采样/映射问题。</summary>
    public void DumpMips(string dir)
    {
        Directory.CreateDirectory(dir);
        DumpTexture(_l1, Path.Combine(dir, "mip-L1.bmp"));
        DumpTexture(_l2, Path.Combine(dir, "mip-L2.bmp"));
        DumpTexture(_l3, Path.Combine(dir, "mip-L3.bmp"));
    }

    private void DumpTexture(ID3D11Texture2D? tex, string path)
    {
        if (tex is null) return;
        Texture2DDescription desc = tex.Description;
        int w = (int)desc.Width, h = (int)desc.Height;

        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        };

        using ID3D11Texture2D staging = _ctx.Device.CreateTexture2D(stagingDesc);
        _ctx.Context.CopyResource(staging, tex);
        MappedSubresource mapped = _ctx.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        byte[] pixels = new byte[w * h * 4];
        try
        {
            for (int y = 0; y < h; y++)
            {
                IntPtr row = mapped.DataPointer + y * (int)mapped.RowPitch;
                if (desc.Format == Format.R16G16B16A16_Float)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float r = (float)Marshal.PtrToStructure<Half>(row + (x * 8));
                        float g = (float)Marshal.PtrToStructure<Half>(row + (x * 8) + 2);
                        float b = (float)Marshal.PtrToStructure<Half>(row + (x * 8) + 4);
                        int o = (y * w + x) * 4;
                        pixels[o] = To8(b);
                        pixels[o + 1] = To8(g);
                        pixels[o + 2] = To8(r);
                        pixels[o + 3] = 255;
                    }
                }
            }
        }
        finally
        {
            _ctx.Context.Unmap(staging, 0);
        }

        App.MaterialLab.WriteBmp(path, w, h, pixels);
    }

    private static byte To8(float v) => (byte)Math.Clamp((int)(MathF.Pow(Math.Clamp(v, 0f, 1f), 1f) * 255f), 0, 255);

    public void Dispose()
    {
        foreach (ID3D11ShaderResourceView d in _dots.Values) d.Dispose();
        _dots.Clear();
        _sep?.Dispose();
        _sep = null;
        _start?.Dispose();
        _start = null;
        _launchpad?.Dispose();
        _launchpad = null;
        _statusGlyph?.Dispose();
        _statusGlyph = null;
        _clock?.Dispose();
        _clock = null;

        foreach (ID3D11ShaderResourceView srv in _iconCache.Values) srv.Dispose();
        _iconCache.Clear();

        _l1Srv?.Dispose(); _l1Rtv?.Dispose(); _l1?.Dispose();
        _l2Srv?.Dispose(); _l2Rtv?.Dispose(); _l2?.Dispose();
        _l3Srv?.Dispose(); _l3Rtv?.Dispose(); _l3?.Dispose();

        _inputLayout.Dispose();
        _vb.Dispose();
        _raster.Dispose();
        _blend.Dispose();
        _sampler.Dispose();
        _cb.Dispose();
        _psIcon.Dispose();
        _vsIcon.Dispose();
        _psGlass.Dispose();
        _psBlurV.Dispose();
        _psBlurH.Dispose();
        _psCopy.Dispose();
        _vsQuad.Dispose();
        _lib.Dispose();
    }
}

internal struct IconQuad
{
    public ID3D11ShaderResourceView Texture;
    public float X, Y, W, H, Alpha;
}

internal struct RenderRequest
{
    public ID3D11RenderTargetView TargetRtv;
    public ID3D11ShaderResourceView? CaptureSrv;
    public int CaptureWidth, CaptureHeight;
    public float CaptureOffsetX, CaptureOffsetY;

    public int WindowW, WindowH;
    public float PanelX, PanelY, PanelW, PanelH, Radius;
    public float ShadowExtent, Shadow;

    public float RefractBand, RefractAmount, Dispersion, Bulge;
    public float RimIntensity, RimWidth, Gloss;
    public float Tint, Exposure, Saturation, Contrast;
    public float Scatter, Milk, Rolloff, EdgeSpec;
    public float BlurStep;
    /// <summary>通透度 0..1：0 = 原磨砂，1 = 最透（会让 DWM 把清晰桌面混进来）</summary>
    public float Transmission;
    public bool DesktopPassthrough;

    /// <summary>
    /// 往不透明方向拉多少（0..1，0 = 完全按 Transmission 算）。
    /// 只在大面积面板上用：DWM 会按 (1-alpha) 把它**背后那份锐利内容**混进来，
    /// Dock 背后只有壁纸所以无所谓，启动台背后是开着的窗口和桌面图标 ——
    /// 拉到 0.7 左右，那 21% 的渗漏就收成几个百分点，面板里不再有别人的文字。
    /// 稀释的是渗漏量，不是玻璃本身：底图（壁纸）仍然是清晰的，所以看起来照样"透"。
    /// </summary>
    public float AlphaPull;

    /// <summary>
    /// 把边框去掉多少 0..1（0 = 原样，1 = 完全没有边）。
    /// 去的是这套"框"的四件事：边光亮线、贴壁高光、内侧压暗带、边缘 alpha 加厚。
    /// 面板四周那圈线在大面板上是负分：十几像素的暗带在满屏尺寸下读起来就是一个
    /// 描出来的矩形，四个圆角处边光还会各自叠一次，变成四个亮钩子。
    /// 用"去掉多少"而不是"保留多少"，是因为 struct 字段默认值必须是 0 才安全 ——
    /// 默认 0 就是保持所有现有窗口的观感不变。
    /// </summary>
    public float BorderCut;

    /// <summary>
    /// 边缘羽化宽度（设备像素）：0 = 硬边（SDF 直接切断），大于 0 时最后这段距离内
    /// alpha 平滑降到 0，玻璃像雾一样融进桌面，而不是一块贴上去的板子。
    /// </summary>
    public float EdgeFade;

    /// <summary>
    /// 壁厚权重 0..1：只控制内侧那条压暗带（"这里有一层玻璃"的厚度感），
    /// 和边框亮线是两回事。0 = 跟随 BorderCut（现有窗口行为不变）。
    /// 想要玻璃有厚度又不要一圈描边，就设 0.3~0.5。
    /// </summary>
    public float WallThickness;

    /// <summary>
    /// 亮底"烟熏"强度 0..1。0 = 旧的白玻璃（本体只比背景暗 1%，白字压上去看不见），
    /// 1 = 把背景压成一层有色暗玻璃（白字可读、边光有对比）。
    ///
    /// 只在背景接近纸色（luma &gt; 0.55 且低饱和）时生效；暗底、彩色底一概不受影响，
    /// 因为那两种情况下这套材质本来就已经是暗的，"再加烟"只会变脏。
    /// </summary>
    public float Smoke;

    /// <summary>
    /// 大面板的"背景压平"强度 0..1，0 = 关闭（Dock / 胶囊一律传 0，观感完全不变）。
    ///
    /// 启动台铺满整屏，它透的是用户此刻所在的那个页面：壁纸 + 桌面图标 + 开着的
    /// 窗口。窗口和图标被模糊摊开之后会混成一层中灰，用户看到的就是"面板发灰"——
    /// 但发灰的不是玻璃，是被糊进玻璃里的背景对比度。
    ///
    /// 真磨砂玻璃不会这样：它把背景收成"环境亮度 + 一点隐约轮廓"。所以这里做的
    /// 不是加白（加白会变成白板），而是把背景朝环境白收，只留下一点结构。
    /// </summary>
    public float Lift;

    /// <summary>
    /// 整屏环境亮度 0..1（0 = 未提供）。只给 Lift 做门控用：
    /// 暗色壁纸上不该把面板抬亮，白壁纸上才需要压平背景。
    /// </summary>
    public float EnvLuma;

    public float BodyR, BodyG, BodyB, BodyAlpha;
    public float DpiScale, Time;

    public List<IconQuad> Icons;
    /// <summary>true = 不清屏、不清 SRV 槽，直接把玻璃合成到已有内容上（离屏材质测试用）</summary>
    public bool KeepExistingContent;
}
