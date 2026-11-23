using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace YuLiangToolkit.Tools.Live2D.Rendering.Gpu;

/// <summary>
/// Live2D GPU 渲染的内置 HLSL 着色器（运行时用 D3DCompiler 编译，不往仓库塞二进制）。
/// <para>语义严格对照官方 Cubism <c>CubismEffect.fx</c> / <c>CubismRenderer_D3D11</c>：
/// 纹理直采样（非预乘）→ 乘 MultiplyColor → ScreenColor 融合 → 预乘 alpha 输出；
/// 蒙版在独立 R8 渲染目标里累积（初始 1，逐蒙版乘 (1 - 蒙版alpha)），主 pass 按屏幕位置采样。</para>
/// <para><b>坑</b>：HLSL 源码里的注释必须全是 ASCII——d3dcompiler 按 ANSI（本机 GBK）读无 BOM
/// 源串，UTF-8 中文注释会把后面的内容截断（实测 X3000 unexpected end of file）。
/// 中文说明写在下面的 C# 注释里，别写进 raw string。</para>
/// </summary>
internal static class Live2DGpuShaders
{
    /// <summary>顶点：屏幕像素坐标 + 图集 UV；像素坐标 → NDC（D3D 像素中心 = 整数 + 0.5，
    /// 与 CPU 光栅器同一语义）；uv.y 按官方 shader 翻转（纹理按图片行方向存）。</summary>
    private const string VertexShader = """
        struct VSIn
        {
            float2 pos : POSITION;
            float2 uv  : TEXCOORD0;
        };

        struct VSOut
        {
            float4 pos : SV_POSITION;
            float2 uv  : TEXCOORD0;
        };

        cbuffer PerFrame : register(b0)
        {
            float4 OutputSize; // x = width, y = height (pixels)
        }

        VSOut main(VSIn i)
        {
            VSOut o;
            o.pos = float4(i.pos.x / OutputSize.x * 2.0 - 1.0,
                           1.0 - i.pos.y / OutputSize.y * 2.0,
                           0.0, 1.0);
            o.uv = float2(i.uv.x, 1.0 - i.uv.y);
            return o;
        }
        """;

    /// <summary>主 pass 像素着色器 = 官方 CubismEffect.fx 的 PixelNormal 公式 + 蒙版覆盖度：
    /// tex.rgb *= MultiplyColor；screen 融合 ScreenColor；alpha = tex.a × Opacity × 蒙版；
    /// 蒙版 R 通道 = 覆盖度 Π(1 - 蒙版alpha)（整帧 1:1 屏幕映射，按 SV_POSITION 采样）；
    /// 输出预乘 alpha（rgb *= alpha），混合状态用 (ONE, INV_SRC_ALPHA) 系（见 GpuRenderDevice）。</summary>
    private const string PixelShaderMain = """
        struct VSOut
        {
            float4 pos : SV_POSITION;
            float2 uv  : TEXCOORD0;
        };

        cbuffer PerFrame : register(b0)
        {
            float4 OutputSize;
        }

        cbuffer PerDraw : register(b1)
        {
            float4 MulColor;   // rgb = MultiplyColor (0-1)
            float4 ScrColor;   // rgb = ScreenColor (0-1)
            float4 Params;     // x = Opacity, y = MaskEnabled (0/1), z = InvertedMask (0/1), w = unused
        }

        Texture2D Tex : register(t0);
        Texture2D MaskTex : register(t1);

        SamplerState TexSampler : register(s0);
        SamplerState MaskSampler : register(s1);

        float4 main(VSOut i) : SV_TARGET
        {
            float4 t = Tex.Sample(TexSampler, i.uv);

            // Texture uploaded with RGB premultiplied by alpha, so bilinear filtering across
            // UV-island edges cannot tint edge pixels from transparent neighbours.
            // Un-premultiply back to straight color before the official Cubism color math.
            float3 straight = t.a > 0.003 ? t.rgb / t.a : float3(0.0, 0.0, 0.0);

            float3 rgb = straight * MulColor.rgb;
            rgb = rgb + ScrColor.rgb - rgb * ScrColor.rgb;

            float alpha = t.a * Params.x;

            if (Params.y > 0.5)
            {
                float2 maskUv = i.pos.xy / OutputSize.xy;
                float coverage = MaskTex.Sample(MaskSampler, maskUv).r;
                alpha *= Params.z > 0.5 ? coverage : 1.0 - coverage;
            }

            return float4(rgb * alpha, alpha);
        }
        """;

    /// <summary>蒙版 pass 像素着色器：只取纹理 alpha（不乘蒙版网格自身不透明度，官方 Blend_Mask 语义）。
    /// 输出 alpha = 纹理 alpha；混合状态 (ZERO, INV_SRC_ALPHA) 实现 out = dst × (1 - alpha)，
    /// R 通道从 1 开始逐蒙版累积出 Π(1 - 蒙版alpha)，与 CPU 蒙版覆盖缓冲完全一致。</summary>
    private const string PixelShaderMask = """
        struct VSOut
        {
            float4 pos : SV_POSITION;
            float2 uv  : TEXCOORD0;
        };

        Texture2D Tex : register(t0);
        SamplerState TexSampler : register(s0);

        float4 main(VSOut i) : SV_TARGET
        {
            float a = Tex.Sample(TexSampler, i.uv).a;
            return float4(0.0, 0.0, 0.0, a);
        }
        """;

    /// <summary>顶点布局：POSITION(R32G32_Float, 0 字节) + TEXCOORD(R32G32_Float, 8 字节)。</summary>
    internal static readonly InputElementDescription[] InputElements =
    {
        new("POSITION", 0, Format.R32G32_Float, 0, 0),
        new("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
    };

    /// <summary>编译 VS / 主 PS / 蒙版 PS（失败抛异常，由创建方回退 CPU 渲染器）。
    /// <para><c>Compiler.Compile</c> 参数顺序：（源码, 入口名, 源文件名, 目标模型, …）——别把 target 传进 sourceName。</para></summary>
    internal static (byte[] Vertex, byte[] Main, byte[] Mask) Compile()
    {
        var vertex = Compiler.Compile(VertexShader, "main", "Live2DVS", "vs_4_0", ShaderFlags.None, EffectFlags.None).ToArray();
        var main = Compiler.Compile(PixelShaderMain, "main", "Live2DPSMain", "ps_4_0", ShaderFlags.None, EffectFlags.None).ToArray();
        var mask = Compiler.Compile(PixelShaderMask, "main", "Live2DPSMask", "ps_4_0", ShaderFlags.None, EffectFlags.None).ToArray();
        return (vertex, main, mask);
    }
}
