using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace LiquidGlass.Desktop.Graphics;

/// <summary>
/// 把 Dock 那套液态玻璃**离屏烘成一张位图**。
///
/// 为什么这么做：预览卡片是个几百像素的临时窗口，为它单独开一条 composition
/// 交换链、再让 DWM 缩略图和它抢层级，代价和风险都不成比例。而玻璃的观感
/// （模糊、奶白、折射、边光、外阴影）全部在 <see cref="GlassRenderer"/> 里，
/// 它的 Render 本来就只认"渲染目标 + 窗口尺寸 + 面板矩形"，与交换链无关。
/// 所以直接渲到一张离屏纹理上、读回内存，卡片拿它当背景画 ——
/// 像素级和 Dock 玻璃同一套着色器，不是"尽力模仿"。
///
/// 频率也合适：卡片每次浮出只烘一次（约 300x250，一次 GPU 渲染 + 一次回读）。
/// </summary>
internal static class CardGlassBaker
{
    /// <summary>渲染时不画任何图标 —— Render 里有 `Icons.Count`，不能给 null。</summary>
    public static readonly List<IconQuad> NoIcons = new();

    public static Bitmap? Bake(D3DContext ctx, GlassRenderer renderer, RenderRequest req)
    {
        int w = req.WindowW, h = req.WindowH;
        if (w < 16 || h < 16) return null;

        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
        };

        ID3D11Texture2D? target = null;
        ID3D11RenderTargetView? rtv = null;
        ID3D11Texture2D? staging = null;
        try
        {
            target = ctx.Device.CreateTexture2D(desc);
            rtv = ctx.Device.CreateRenderTargetView(target, null);

            req.TargetRtv = rtv;
            renderer.Render(req);

            // 回读前必须解绑：绑着 RenderTarget 的目标会被 CopyResource 拒绝
            ctx.Context.ClearState();

            var stagingDesc = desc;
            stagingDesc.Usage = ResourceUsage.Staging;
            stagingDesc.BindFlags = BindFlags.None;
            stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
            staging = ctx.Device.CreateTexture2D(stagingDesc);

            ctx.Context.CopyResource(staging, target);
            MappedSubresource mapped = ctx.Context.Map(staging, 0, MapMode.Read,
                Vortice.Direct3D11.MapFlags.None);

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                // 着色器输出是预乘 alpha（float4(c * alpha, alpha)），
                // 正好对上 Format32bppPArgb，不用再算一遍乘法。
                int rowBytes = w * 4;
                var row = new byte[rowBytes];
                for (int y = 0; y < h; y++)
                {
                    IntPtr src = mapped.DataPointer + y * (int)mapped.RowPitch;
                    Marshal.Copy(src, row, 0, rowBytes);
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, rowBytes);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
                ctx.Context.Unmap(staging, 0);
            }

            return bmp;
        }
        catch (Exception ex)
        {
            App.Logger.Warn("[preview] 玻璃烘图失败: " + ex.Message);
            return null;
        }
        finally
        {
            staging?.Dispose();
            rtv?.Dispose();
            target?.Dispose();
        }
    }
}
