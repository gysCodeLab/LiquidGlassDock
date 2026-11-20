using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;

namespace LiquidGlass.Desktop.Graphics;

/// <summary>
/// 着色器编译与缓存。运行时从 Shaders/Glass.hlsl 编译，
/// d3dcompiler_47.dll 是 Windows 自带组件，不需要额外分发。
/// </summary>
internal sealed class ShaderLibrary : IDisposable
{
    private readonly Dictionary<string, byte[]> _bytecode = new(StringComparer.Ordinal);
    private readonly D3DContext _ctx;

    public ShaderLibrary(D3DContext ctx)
    {
        _ctx = ctx;
    }

    public byte[] Compile(string source, string entryPoint, string profile)
    {
        string key = entryPoint + "|" + profile;
        if (_bytecode.TryGetValue(key, out byte[]? cached)) return cached;

        // 着色器源码保持纯 ASCII：fxc 按 ANSI 接收源码，非 ASCII 注释在 CJK 代码页下会被截断
        ReadOnlyMemory<byte> code = Compiler.Compile(source, entryPoint, "Glass.hlsl", profile,
            ShaderFlags.OptimizationLevel3 | ShaderFlags.PackMatrixRowMajor, EffectFlags.None);
        byte[] bytes = code.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException($"着色器 {entryPoint} 编译结果为空");

        _bytecode[key] = bytes;
        Debug.WriteLine($"[shader] {entryPoint} ({profile}) {bytes.Length} bytes");
        return bytes;
    }

    public ID3D11VertexShader Vertex(string source, string entry)
        => _ctx.Device.CreateVertexShader(Compile(source, entry, "vs_5_0"));

    public ID3D11PixelShader Pixel(string source, string entry)
        => _ctx.Device.CreatePixelShader(Compile(source, entry, "ps_5_0"));

    public void Dispose() => _bytecode.Clear();
}
