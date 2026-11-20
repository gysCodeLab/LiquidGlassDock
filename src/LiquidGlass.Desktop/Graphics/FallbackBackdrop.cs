using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using LiquidGlass.Desktop.Platform;

namespace LiquidGlass.Desktop.Graphics;

/// <summary>
/// 采集会话失效时的「兜底背景」：用 GDI 抓一次真实桌面，直接当玻璃的折射源。
///
/// 为什么必须有它：
/// DXGI Desktop Duplication 会在锁屏、唤醒、驱动重置、explorer 重建这些时刻失效，
/// 而锁屏期间**一定**返回 E_ACCESSDENIED（桌面在安全桌面上），没有任何办法绕。
/// 采集没了以后，着色器只能走降级分支画一块中性灰 —— 叠上玻璃本身的冷色偏移，
/// 屏幕上就是「Dock 背景变成一块静止的蓝灰色板子」。更糟的是刚唤醒那几秒
/// DWM 还没把桌面合成出来，采到的帧整块是黑的，于是又变成「静止的黑板」。
///
/// 用户看到的不是"降级"，是"坏了"。所以这里用最原始也最可靠的 GDI BitBlt
/// 抓一小块屏幕顶上去：Dock 自身带 WDA_EXCLUDEFROMCAPTURE，抓到的正好是
/// 它**背后**的内容（实测确认），不会形成自反馈的无限套娃。
///
/// 代价：一次 BitBlt（本机 1005x171 约 0.6ms）+ 一次纹理上传，2Hz 刷新，
/// 而且只在采集不可用时才跑 —— 正常状态下这条路径一次都不会触发。
/// </summary>
internal sealed class FallbackBackdrop : IDisposable
{
    private readonly D3DContext _ctx;

    private ID3D11Texture2D? _tex;
    private ID3D11ShaderResourceView? _srv;

    private int _w;
    private int _h;

    /// <summary>上次真的去抓屏的时刻（TickCount64）。</summary>
    private long _lastGrabAt = long.MinValue / 2;

    /// <summary>上次抓成功的时刻。太久没成功说明这块也不可用，调用方据此决定要不要给占位。</summary>
    public long LastOkAt { get; private set; }

    public FallbackBackdrop(D3DContext ctx) => _ctx = ctx;

    public ID3D11ShaderResourceView? Srv => _srv;
    public int X { get; private set; }
    public int Y { get; private set; }
    public int W => _w;
    public int H => _h;
    public bool Ready => _srv is not null;

    /// <summary>
    /// 刷新兜底图。返回 true = 这一次真的抓到了新像素（调用方据此置脏重绘）。
    /// 没到刷新间隔、或者已经有一张同位置同尺寸的图，就直接返回 false，
    /// 不会把 BitBlt 挂到每一帧上。
    /// </summary>
    public bool Refresh(int x, int y, int w, int h, int minIntervalMs)
    {
        if (w < 8 || h < 8) return false;

        long now = Environment.TickCount64;
        bool sameBox = x == X && y == Y && w == _w && h == _h;
        if (Ready && sameBox && now - _lastGrabAt < minIntervalMs) return false;

        _lastGrabAt = now;

        using Bitmap? shot = ScreenCapture.Grab(x, y, w, h);
        if (shot is null) return false;

        return Upload(shot, x, y);
    }

    /// <summary>
    /// 把一张**现成的**位图当背景上传，x/y 是它在屏幕上的位置（坐标换算用）。
    /// 启动台走这条：它的背景是渲染好的壁纸，不是屏幕抓取。
    /// </summary>
    public bool Upload(Bitmap shot, int x, int y)
    {
        int w = shot.Width;
        int h = shot.Height;
        if (w < 8 || h < 8) return false;

        if (!EnsureTexture(w, h)) return false;

        BitmapData data = shot.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            // 上一轮渲染可能还把这个 SRV 绑在 PS 上，先解绑再 Map。
            // 不解的话运行时会把 Map 变成一次隐式同步拷贝，偶发几毫秒的卡顿。
            _ctx.Context.PSSetShaderResource(0, null);

            MappedSubresource mapped = _ctx.Context.Map(_tex!, 0, MapMode.WriteDiscard,
                Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = w * 4;
                var row = new byte[rowBytes];
                for (int i = 0; i < h; i++)
                {
                    Marshal.Copy(data.Scan0 + i * data.Stride, row, 0, rowBytes);
                    Marshal.Copy(row, 0, mapped.DataPointer + i * (int)mapped.RowPitch, rowBytes);
                }
            }
            finally
            {
                _ctx.Context.Unmap(_tex!, 0);
            }
        }
        catch (Exception ex)
        {
            App.Logger.Warn("[glass] 兜底背景上传失败: " + ex.Message);
            return false;
        }
        finally
        {
            shot.UnlockBits(data);
        }

        X = x;
        Y = y;
        _w = w;
        _h = h;
        LastOkAt = Environment.TickCount64;
        return true;
    }

    private bool EnsureTexture(int w, int h)
    {
        if (_tex is not null && _w == w && _h == h && _srv is not null) return true;

        _srv?.Dispose();
        _srv = null;
        _tex?.Dispose();
        _tex = null;

        try
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None,
            };

            _tex = _ctx.Device.CreateTexture2D(desc);
            _srv = _ctx.Device.CreateShaderResourceView(_tex, null);
            return _srv is not null;
        }
        catch (Exception ex)
        {
            App.Logger.Warn("[glass] 兜底背景建纹理失败: " + ex.Message);
            _tex?.Dispose();
            _tex = null;
            return false;
        }
    }

    public void Dispose()
    {
        _srv?.Dispose();
        _srv = null;
        _tex?.Dispose();
        _tex = null;
    }
}
