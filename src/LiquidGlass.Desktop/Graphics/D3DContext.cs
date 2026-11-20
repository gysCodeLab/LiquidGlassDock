using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LiquidGlass.Desktop.Graphics;

/// <summary>
/// D3D11 设备与 DXGI 工厂的唯一持有者。
/// BGRA 支持是必须的：DComp 合成 swapchain 只吃 B8G8R8A8。
/// </summary>
internal sealed class D3DContext : IDisposable
{
    private static readonly FeatureLevel[] Levels =
    {
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    };

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIFactory2 Factory { get; }
    public IDXGIAdapter1 Adapter { get; }
    public IDXGIDevice DxgiDevice { get; }
    public FeatureLevel FeatureLevel { get; }
    public string AdapterName { get; }

    private D3DContext(ID3D11Device device, ID3D11DeviceContext context, IDXGIFactory2 factory,
        IDXGIAdapter1 adapter, IDXGIDevice dxgiDevice, FeatureLevel level, string adapterName)
    {
        Device = device;
        Context = context;
        Factory = factory;
        Adapter = adapter;
        DxgiDevice = dxgiDevice;
        FeatureLevel = level;
        AdapterName = adapterName;
    }

    public static D3DContext Create()
    {
        IDXGIFactory2 factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

        App.Logger.Info("[d3d] 适配器枚举:");
        IDXGIAdapter1? best = null;
        string bestName = "unknown";
        for (uint i = 0; ; i++)
        {
            Result r = factory.EnumAdapters1(i, out IDXGIAdapter1 adapter);
            if (r.Failure) break;

            AdapterDescription1 desc = adapter.Description1;
            App.Logger.Info($"  [{i}] {desc.Description.TrimEnd('\0')} flags={desc.Flags} vendor=0x{desc.VendorId:X4}");

            bool isSoftware = desc.Flags.HasFlag(AdapterFlags.Software);
            if (!isSoftware && best is null)
            {
                best = adapter;
                bestName = desc.Description.TrimEnd('\0');
            }
            else
            {
                adapter.Dispose();
            }
        }

        // 逐级降级：默认适配器+BGRA → 显式适配器 → WARP。
        //
        // default+bgra 排第一不是图省事：这台机器（Intel Arc）上显式传适配器
        // 加 BGRA 会返回 E_INVALIDARG，白白先烧掉两次失败的设备创建，
        // 启动时间多出约 1.8 秒 —— 期间窗口还黑着。让 D3D 自己挑默认适配器
        // 一次成功，通常也就是主显卡。真要指定适配器，放后面当退路。
        var attempts = new (string Name, IDXGIAdapter1? Adapter, DriverType Type, DeviceCreationFlags Flags)[]
        {
            ("default+bgra", null, DriverType.Hardware, DeviceCreationFlags.BgraSupport),
            ("hardware+bgra", best, DriverType.Hardware, DeviceCreationFlags.BgraSupport),
            ("hardware", best, DriverType.Hardware, DeviceCreationFlags.None),
            ("warp+bgra", null, DriverType.Warp, DeviceCreationFlags.BgraSupport),
        };

        foreach (var (name, adapter, type, flags) in attempts)
        {
            Result dev = D3D11.D3D11CreateDevice(adapter, type, flags, Levels,
                out ID3D11Device device, out FeatureLevel level, out ID3D11DeviceContext ctx);
            App.Logger.Info($"[d3d] {name} -> 0x{dev.Code:X8} level={level}");
            if (dev.Success)
            {
                IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
                // 记录设备真正落在哪块适配器上 —— default 路径下 best 可能是 null，
                // 也可能与实际使用的适配器不是同一块，凭枚举猜会埋坑。
                IDXGIAdapter1 used = dxgiDevice.GetAdapter().QueryInterface<IDXGIAdapter1>();
                string usedName = used.Description1.Description.TrimEnd('\0');
                // 枚举时留下的 best 若没被设备用上，这里补一次释放，别留悬空 COM 引用
                if (best is not null && best.NativePointer != used.NativePointer)
                    best.Dispose();
                return new D3DContext(device, ctx, factory, used, dxgiDevice, level, usedName);
            }
        }

        throw new InvalidOperationException("D3D11CreateDevice 全部降级路径均失败");
    }

    public void Dispose()
    {
        DxgiDevice.Dispose();
        Adapter.Dispose();
        Factory.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
