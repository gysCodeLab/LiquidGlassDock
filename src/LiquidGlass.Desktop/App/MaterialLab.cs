using System.Runtime.InteropServices;
using LiquidGlass.Desktop.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiquidGlass.Desktop.App;

/// <summary>
/// 材质实验室：把玻璃面板离屏渲染到任意背景图上。
/// 用途：
///  1. 不依赖真实桌面（暗色/亮色/高对比/皮肤色调）就能评估材质；
///  2. 参数回归时可以把结果图直接做像素对比；
///  3. 不碰用户桌面，不产生任何窗口。
/// </summary>
internal static class MaterialLab
{
    /// <summary>
    /// LG_LIFT=0..1 覆盖"背景压平"强度，用来离屏对比"发灰 / 压平后"两种观感。
    /// 0 = 原样（Dock 的行为），1 = 启动台的行为。
    /// </summary>
    private static readonly float LabLift =
        float.TryParse(Environment.GetEnvironmentVariable("LG_LIFT"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float lf) && lf >= 0f
            ? MathF.Min(lf, 1f) : 0f;

    public static int Run(string backdropDir, string outDir, string shaderPath, GlassConfig glass,
        int panelW = 820, int panelH = 92)
    {
        string[] files = Directory.GetFiles(backdropDir, "*.bmp");
        if (files.Length == 0)
        {
            Logger.Warn("材质实验室：目录里没有 bmp 背景图");
            return 2;
        }

        Directory.CreateDirectory(outDir);

        // LG_SMOKE=0.6 可以在不重新编译的情况下试白底烟熏的几档强度：
        // 这条曲线（0 = 旧白玻璃，1 = 完整烟熏）只能靠眼睛在真白底上定。
        if (float.TryParse(Environment.GetEnvironmentVariable("LG_SMOKE"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float smoke) && smoke >= 0f)
        {
            glass.Smoke = Math.Clamp(smoke, 0f, 1f);
            Logger.Info($"材质实验室: LG_SMOKE 覆盖 → Smoke={glass.Smoke:F2}");
        }

        D3DContext ctx = D3DContext.Create();
        try
        {
            string source = File.ReadAllText(shaderPath, System.Text.Encoding.ASCII);
            using var renderer = new GlassRenderer(ctx, source);

            foreach (string file in files)
            {
                (int w, int h, byte[] pixels) = ReadBmp(file);
                bool dumpMips = file.Contains("contrast");
                (int ow, int oh, byte[] outPixels) = Composite(ctx, renderer, w, h, pixels, glass,
                    panelW, panelH);

                if (dumpMips) renderer.DumpMips(Path.Combine(outDir, "mips"));
                string name = Path.GetFileNameWithoutExtension(file) + "-glass.png.bmp";
                string target = Path.Combine(outDir, name);
                WriteBmp(target, ow, oh, outPixels);
                Logger.Info($"材质实验室: {Path.GetFileName(file)} → {name}");
            }

            return 0;
        }
        finally
        {
            ctx.Dispose();
        }
    }

    private static (int, int, byte[]) Composite(D3DContext ctx, GlassRenderer renderer, int w, int h, byte[] pixels,
        GlassConfig glass, int wantW, int wantH)    {
        var texDesc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource,
        };

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        ID3D11Texture2D backdrop = ctx.Device.CreateTexture2D(texDesc,
            new SubresourceData(handle.AddrOfPinnedObject(), (uint)(w * 4)));
        handle.Free();

        ID3D11ShaderResourceView backdropSrv = ctx.Device.CreateShaderResourceView(backdrop, null);

        // 环境亮度和真机 LaunchpadWindow._envLuma 一个口径（整屏平均），
        // 否则离屏预览会在暗背景上也把面板抬亮，测出来的东西不代表现场。
        double lumaSum = 0;
        int lumaN = 0;
        for (int i = 0; i + 3 < pixels.Length; i += 64)
        {
            lumaSum += 0.299 * pixels[i + 2] + 0.587 * pixels[i + 1] + 0.114 * pixels[i];
            lumaN++;
        }
        float envLuma = lumaN > 0 ? (float)(lumaSum / lumaN / 255.0) : 0f;

        var rtDesc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };

        ID3D11Texture2D target = ctx.Device.CreateTexture2D(rtDesc);
        ID3D11RenderTargetView rtv = ctx.Device.CreateRenderTargetView(target, null);

        // 面板尺寸可指定（--lab-panel WxH）。默认 820x92 是 Dock 条；
        // 启动台那种大卡片要传实际尺寸，否则边缘光学的失真是测不出来的。
        float panelW = MathF.Min(wantW, w * 0.92f);
        float panelH = MathF.Min(wantH, h * 0.92f);
        float margin = MathF.Min(60f, MathF.Min(w - panelW, h - panelH) * 0.5f);

        // 和 LaunchpadWindow 走同一套尺寸换算，离屏结果才能代表真实观感。
        (float bandV, float amountV) = glass.EdgeFor(panelH);

        var req = new RenderRequest
        {
            TargetRtv = rtv,
            CaptureSrv = backdropSrv,
            CaptureWidth = w,
            CaptureHeight = h,
            CaptureOffsetX = 0,
            CaptureOffsetY = 0,
            WindowW = w,
            WindowH = h,
            PanelX = (w - panelW) * 0.5f,
            PanelY = (h - panelH) * 0.5f,
            PanelW = panelW,
            PanelH = panelH,
            Radius = MathF.Min(panelH * glass.RadiusHint, 56f),
            ShadowExtent = margin,
            Shadow = glass.Shadow,
            RefractBand = glass.EnableRefraction ? bandV : 0f,
            RefractAmount = glass.EnableRefraction ? amountV : 0f,
            Dispersion = glass.EnableRefraction ? glass.Dispersion : 0f,
            Bulge = glass.Bulge,
            RimIntensity = glass.RimIntensity,
            RimWidth = glass.RimWidth,
            Gloss = glass.Gloss,
            Tint = glass.Tint,
            Exposure = glass.Exposure,
            Saturation = glass.Saturation,
            Contrast = glass.Contrast,
            Scatter = glass.Scatter,
            Milk = glass.Milk,
            Rolloff = glass.Rolloff,
            EdgeSpec = glass.EdgeSpec,
            Smoke = glass.Smoke,
            // Dock and launchpad now share the same material defaults.
            BlurStep = glass.BlurStep * (1f - 0.34f * glass.Transmission),
            Transmission = glass.Transmission,
            // 压平时一并按启动台的真实取值走，否则离屏结果的合成 alpha 对不上。
            Lift = LabLift,
            EnvLuma = envLuma,
            AlphaPull = LabLift > 0f ? 0.55f : 0f,
            BodyR = glass.BodyR,
            BodyG = glass.BodyG,
            BodyB = glass.BodyB,
            BodyAlpha = glass.BodyAlpha,
            DpiScale = 1f,
            Time = 0f,
            Icons = new List<IconQuad>(),
            KeepExistingContent = true,
        };

        renderer.Render(req);
        ctx.Context.Flush();

        var staging = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        };

        using ID3D11Texture2D readback = ctx.Device.CreateTexture2D(staging);
        ctx.Context.CopyResource(readback, target);

        byte[] result = new byte[w * h * 4];
        MappedSubresource mapped = ctx.Context.Map(readback, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (int y = 0; y < h; y++)
            {
                IntPtr src = mapped.DataPointer + y * (int)mapped.RowPitch;
                Marshal.Copy(src, result, y * w * 4, w * 4);
            }
        }
        finally
        {
            ctx.Context.Unmap(readback, 0);
        }

        rtv.Dispose();
        target.Dispose();
        backdropSrv.Dispose();
        backdrop.Dispose();

        return (w, h, result);
    }

    /// <summary>读 32 位 BMP（自底向上），返回自上而下的 BGRA。</summary>
    public static (int, int, byte[]) ReadBmp(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        int offset = BitConverter.ToInt32(raw, 10);
        int w = BitConverter.ToInt32(raw, 18);
        int hRaw = BitConverter.ToInt32(raw, 22);
        bool topDown = hRaw < 0;
        int h = Math.Abs(hRaw);
        int bpp = BitConverter.ToInt16(raw, 28);

        if (bpp != 32) throw new NotSupportedException("只支持 32 位 BMP: " + path);

        byte[] pixels = new byte[w * h * 4];
        int rowSize = w * 4;
        for (int y = 0; y < h; y++)
        {
            int srcRow = topDown ? y : h - 1 - y;
            Buffer.BlockCopy(raw, offset + srcRow * rowSize, pixels, y * rowSize, rowSize);
        }

        return (w, h, pixels);
    }

    public static void WriteBmp(string path, int w, int h, byte[] bgra)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        int rowSize = w * 4;
        int imageSize = rowSize * h;

        bw.Write((ushort)0x4D42);
        bw.Write(14 + 40 + imageSize);
        bw.Write(0);
        bw.Write(14 + 40);
        bw.Write(40);
        bw.Write(w);
        bw.Write(h);
        bw.Write((ushort)1);
        bw.Write((ushort)32);
        bw.Write(0);
        bw.Write(imageSize);
        bw.Write(2835);
        bw.Write(2835);
        bw.Write(0);
        bw.Write(0);

        for (int row = h - 1; row >= 0; row--)
            bw.Write(bgra, row * rowSize, rowSize);
    }
}
