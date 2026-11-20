using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace LiquidGlass.Desktop.Graphics;

/// <summary>
/// 把一个 HWND 变成"逐像素透明"的 GPU 画布：
/// WS_EX_NOREDIRECTIONBITMAP 的窗口本身不画任何东西，
/// 内容全部来自 composition swapchain（预乘 alpha），由 DWM 直接合成。
/// </summary>
internal sealed class CompositionHost : IDisposable
{
    private readonly IDXGISwapChain1 _swapChain;
    private readonly IDXGISwapChain2? _swapChain2;
    private readonly SwapChainFlags _flags;
    private readonly IDCompositionDevice _dcomp;
    private readonly IDCompositionTarget _target;
    private readonly IDCompositionVisual _visual;

    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D? _backBuffer;
    public ID3D11RenderTargetView Rtv => _rtv ?? throw new InvalidOperationException("RTV 未创建");

    public int Width { get; private set; }
    public int Height { get; private set; }

    public CompositionHost(D3DContext ctx, IntPtr hwnd, int width, int height)
    {
        Width = width;
        Height = height;
        PixelSize = 0;

        var desc = new SwapChainDescription1
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        };

        // 先要 DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT。
        //
        // 这个标志会给我们一个句柄，由 DWM 的合成节奏驱动：
        // 句柄置位 == "现在可以再画一帧，画完立刻 Present 一定赶得上下一轮合成"。
        // 有了它，帧节拍从"我们自己掐表猜显示器"变成"DWM 直接告诉我们什么时候画"，
        // 帧间隔天然等于刷新周期，不会出现某一帧被连显两次、下一帧被吞掉的judder。
        //
        // 老的驱动 / 某些虚拟显示环境不接受这个标志，那就退回不带标志创建 ——
        // 功能不受影响，只是帧节拍要靠上层的时间轴自己撑。
        _swapChain = CreateWithFallback(ctx, desc, out _flags);

        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(ctx.DxgiDevice);
        _dcomp.CreateTargetForHwnd(hwnd, true, out _target).CheckError();
        _visual = _dcomp.CreateVisual();
        _visual.SetContent(_swapChain).CheckError();

        using (var dev1 = ctx.Device.QueryInterfaceOrNull<IDXGIDevice1>())
        {
            if (dev1 is not null) dev1.MaximumFrameLatency = 1;
        }

        _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (_swapChain2 is not null && _flags == SwapChainFlags.FrameLatencyWaitableObject)
        {
            // 1 帧延迟：我们只比 DWM 领先一帧，指针位置到屏幕的延时最短，
            // 也不会因为排队太深而在快速划动时"追不上"。
            try { _swapChain2.MaximumFrameLatency = 1; }
            catch { /* 不支持的实现，忽略：等待对象仍在，只是延迟默认值 */ }
        }

        RecreateTarget(ctx);
        _target.SetRoot(_visual).CheckError();
        _dcomp.Commit().CheckError();
    }

    public long PixelSize { get; private set; }

    /// <summary>
    /// 帧延迟等待对象。非零时表示"由 DWM 的合成节奏驱动画帧"这条路可用；
    /// 为零则上层退回自己掐时间轴。
    /// </summary>
    public IntPtr FrameLatencyHandle
    {
        get
        {
            if (_flags != SwapChainFlags.FrameLatencyWaitableObject || _swapChain2 is null)
                return IntPtr.Zero;
            try { return _swapChain2.FrameLatencyWaitableObject; }
            catch { return IntPtr.Zero; }
        }
    }

    private static IDXGISwapChain1 CreateWithFallback(D3DContext ctx, SwapChainDescription1 desc,
        out SwapChainFlags used)
    {
        desc.Flags = SwapChainFlags.FrameLatencyWaitableObject;
        try
        {
            used = SwapChainFlags.FrameLatencyWaitableObject;
            return ctx.Factory.CreateSwapChainForComposition(ctx.Device, desc, null);
        }
        catch (Exception ex)
        {
            App.Logger.Warn("交换链不接受帧延迟等待标志，退回普通创建（帧节拍改由时间轴控制）: " + ex.Message);
        }

        desc.Flags = SwapChainFlags.None;
        used = SwapChainFlags.None;
        return ctx.Factory.CreateSwapChainForComposition(ctx.Device, desc, null);
    }

    /// <summary>诊断用：上一次 Present 落在第几次刷新上。judder 分析靠它。</summary>
    public bool TryGetRefreshStats(out uint presentRefreshCount, out uint syncRefreshCount)
    {
        presentRefreshCount = 0;
        syncRefreshCount = 0;
        try
        {
            if (!_swapChain.GetFrameStatistics(out Vortice.DXGI.FrameStatistics stats).Success) return false;
            presentRefreshCount = stats.PresentRefreshCount;
            syncRefreshCount = stats.SyncRefreshCount;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RecreateTarget(D3DContext ctx)
    {
        _rtv?.Dispose();
        _backBuffer?.Dispose();
        _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = ctx.Device.CreateRenderTargetView(_backBuffer, null);
    }

    /// <summary>
    /// 诊断用：把当前后台缓冲直接导出成 BMP。
    /// 拿它和屏幕截图对比，就能分清"渲染本身错了"还是"合成/交换链层面错了"。
    /// </summary>
    public void DumpFrame(D3DContext ctx, string path)
    {
        if (_backBuffer is null) return;
        Texture2DDescription desc = _backBuffer.Description;
        int w = (int)desc.Width, h = (int)desc.Height;

        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        };

        using ID3D11Texture2D staging = ctx.Device.CreateTexture2D(stagingDesc);
        ctx.Context.CopyResource(staging, _backBuffer);
        MappedSubresource mapped = ctx.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        byte[] pixels = new byte[w * h * 4];
        try
        {
            for (int y = 0; y < h; y++)
            {
                IntPtr row = mapped.DataPointer + y * (int)mapped.RowPitch;
                for (int x = 0; x < w * 4; x++)
                    pixels[y * w * 4 + x] = System.Runtime.InteropServices.Marshal.ReadByte(row, x);
            }
        }
        finally
        {
            ctx.Context.Unmap(staging, 0);
        }

        // 导出时把预乘 alpha 平铺到棋盘上，透明区域才不会看成黑色
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            int px = (i / 4) % w, py = (i / 4) / w;
            int bg = ((px / 12 + py / 12) % 2 == 0) ? 40 : 90;
            pixels[i] = (byte)Math.Clamp(pixels[i] + bg * (255 - a) / 255, 0, 255);
            pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] + bg * (255 - a) / 255, 0, 255);
            pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] + bg * (255 - a) / 255, 0, 255);
            pixels[i + 3] = 255;
        }

        App.MaterialLab.WriteBmp(path, w, h, pixels);
    }

    public void Resize(D3DContext ctx, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (width == Width && height == Height) return;

        Width = width;
        Height = height;

        // ResizeBuffers 的硬性要求：后台缓冲**一个未释放的引用都不能有**。
        // 这里有三处引用必须都断掉，漏一处就是 DXGI_ERROR_INVALID_CALL：
        //   1. 设备上下文里绑着的渲染目标（上一帧留下）
        //   2. 我们自己的 RTV
        //   3. 我们自己的后台缓冲纹理（原代码漏了这条）
        ctx.Context.ClearState();
        _rtv?.Dispose();
        _rtv = null;
        _backBuffer?.Dispose();
        _backBuffer = null;

        // 标志必须和创建时一致，改成 None 会让"帧延迟等待对象"失效。
        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, _flags).CheckError();
        RecreateTarget(ctx);
        _dcomp.Commit();
    }

    /// <param name="syncInterval">
    /// 1 = 与垂直同步对齐。做动画时必须用它：Present(0) 只是把帧丢给 DWM 就返回，
    /// 帧落在刷新周期的哪一段完全随机，肉眼看到的就是"一卡一卡"。空闲时用 0，
    /// 不为了没变化的画面白等一个刷新周期。
    /// </param>
    public void Present(uint syncInterval)
    {
        _swapChain.Present(syncInterval);
    }

    public void Dispose()
    {
        _backBuffer?.Dispose();
        _rtv?.Dispose();
        _visual.Dispose();
        _target.Dispose();
        _dcomp.Dispose();
        _swapChain2?.Dispose();
        _swapChain.Dispose();
    }
}
