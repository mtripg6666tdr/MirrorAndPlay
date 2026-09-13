using System;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MirrorAndPlay
{
    public class GpuScaler : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;

        public int OutputWidth { get; }
        public int OutputHeight { get; }

        private ID3D11Texture2D _renderTargetTex = null!;
        private ID3D11RenderTargetView _renderTargetView = null!;
        private ID3D11Texture2D _stagingTex = null!;
        private ID3D11VertexShader _vertexShader = null!;
        private ID3D11PixelShader _pixelShader = null!;
        private ID3D11SamplerState _samplerState = null!;
        private ID3D11Buffer _constantBuffer = null!;

        [StructLayout(LayoutKind.Sequential)]
        private struct CropParams
        {
            public Vector2 CropOffset; // クロップ開始位置 (正規化 0.0 ~ 1.0)
            public Vector2 CropScale;  // クロップ範囲サイズ (正規化 0.0 ~ 1.0)
        }

        public GpuScaler(ID3D11Device device, int outputWidth = 960, int outputHeight = 540)
        {
            this._device = device;
            this._context = device.ImmediateContext;
            this.OutputWidth = outputWidth;
            this.OutputHeight = outputHeight;

            this.InitializeResources();
            this.InitializeShaders();
        }

        private void InitializeResources()
        {
            // 1. 描画先テクスチャ (960x540 / RenderTarget)
            var rtDesc = new Texture2DDescription
            {
                Width = (uint)this.OutputWidth,
                Height = (uint)this.OutputHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None
            };
            this._renderTargetTex = this._device.CreateTexture2D(rtDesc);
            this._renderTargetView = this._device.CreateRenderTargetView(this._renderTargetTex);

            // 2. CPU読み出し用 Staging テクスチャ (960x540)
            var stagingDesc = rtDesc;
            stagingDesc.BindFlags = BindFlags.None;
            stagingDesc.Usage = ResourceUsage.Staging;
            stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
            this._stagingTex = this._device.CreateTexture2D(stagingDesc);

            // 3. バイリニアサンプラー
            var sampDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never
            };
            this._samplerState = this._device.CreateSamplerState(sampDesc);

            // 4. 定数バッファ (Cropパラメータ転送用)
            this._constantBuffer = this._device.CreateBuffer(new BufferDescription
            {
                ByteWidth = (sizeof(float) * 4 + 15) & ~15, // 16バイトアライメント
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });
        }

        private void InitializeShaders()
        {
            // 頂点バッファ不要で画面全体を覆うトライアングルを生成し、UVをクロップ領域にマッピングするHLSL
            const string hlsl = @"
cbuffer CropBuffer : register(b0) {
    float2 CropOffset;
    float2 CropScale;
};

struct VSOutput {
    float4 Pos : SV_Position;
    float2 UV  : TEXCOORD0;
};

VSOutput VSMain(uint id : SV_VertexID) {
    VSOutput output;
    float2 uv = float2((id << 1) & 2, id & 2);
    output.Pos = float4(uv * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
    output.UV = CropOffset + uv * CropScale;
    return output;
}

Texture2D srcTex : register(t0);
SamplerState srcSampler : register(s0);

float4 PSMain(VSOutput input) : SV_Target {
    return srcTex.Sample(srcSampler, input.UV);
}";

            // 頂点シェーダーのコンパイル
            var vsResult = Compiler.Compile(hlsl, "VSMain", "shader.hlsl", "vs_5_0", out var vsBlob, out var vsError);
            if (vsResult.Failure || vsBlob == null)
            {
                throw new InvalidOperationException($"VS Compile Failed: {vsError}");
            }
            using (vsBlob)
            {
                this._vertexShader = this._device.CreateVertexShader(vsBlob.AsSpan());
            }

            // ピクセルシェーダーのコンパイル
            var psResult = Compiler.Compile(hlsl, "PSMain", "shader.hlsl", "ps_5_0", out var psBlob, out var psError);
            if (psResult.Failure || psBlob == null)
            {
                throw new InvalidOperationException($"PS Compile Failed: {psError}");
            }
            using (psBlob)
            {
                this._pixelShader = this._device.CreatePixelShader(psBlob.AsSpan());
            }
        }

        /// <summary>
        /// キャプチャしたテクスチャをGPU上でクロップ＆縮小し、CPU側のストリーム（FFmpegのstdin）に書き出す
        /// </summary>
        public unsafe void ProcessFrame(ID3D11Texture2D sourceTexture, Rectangle cropRect, Stream destinationStream)
        {
            var srcDesc = sourceTexture.Description;

            // 1. クロップ範囲を UV座標 (0.0 ~ 1.0) に正規化
            var cropParams = new CropParams
            {
                CropOffset = new Vector2((float)cropRect.X / srcDesc.Width, (float)cropRect.Y / srcDesc.Height),
                CropScale = new Vector2((float)cropRect.Width / srcDesc.Width, (float)cropRect.Height / srcDesc.Height)
            };

            // 定数バッファの更新 (ポインタ直接代入で高速転送)
            var mappedBox = this._context.Map(this._constantBuffer, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            *(CropParams*)mappedBox.DataPointer = cropParams;
            this._context.Unmap(this._constantBuffer, 0);

            // 2. パイプラインステートをセットアップ
            using var srv = this._device.CreateShaderResourceView(sourceTexture);
            this._context.RSSetViewport(0, 0, this.OutputWidth, this.OutputHeight);
            this._context.OMSetRenderTargets(this._renderTargetView);

            this._context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            this._context.VSSetShader(this._vertexShader);
            this._context.VSSetConstantBuffer(0, this._constantBuffer);
            this._context.PSSetShader(this._pixelShader);
            this._context.PSSetShaderResource(0, srv);
            this._context.PSSetSampler(0, this._samplerState);

            // 3. GPU描画（3頂点で画面全体をフィルしながらバイリニア縮小）
            this._context.Draw(3, 0);

            // 4. ステート解除
            this._context.PSSetShaderResource(0, null!);
            this._context.OMSetRenderTargets((ID3D11RenderTargetView)null!);

            // 5. 960x540 の縮小済みテクスチャだけを Staging にコピー
            this._context.CopyResource(this._stagingTex, this._renderTargetTex);

            // 6. CPUへ読み出してFFmpegの標準入力に流し込む
            var mappedStaging = this._context.Map(this._stagingTex, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var srcPtr = (byte*)mappedStaging.DataPointer;
                var rowPitch = (int)mappedStaging.RowPitch;
                var packedRowSize = this.OutputWidth * 4; // 960px * 4byte = 3840 bytes

                if (rowPitch == packedRowSize)
                {
                    // パディングがない場合は一括転送 (約2MB)
                    var span = new ReadOnlySpan<byte>(srcPtr, this.OutputWidth * this.OutputHeight * 4);
                    destinationStream.Write(span);
                }
                else
                {
                    // 行パディングがある場合は1行ずつ転送
                    for (var y = 0; y < this.OutputHeight; y++)
                    {
                        var rowSpan = new ReadOnlySpan<byte>(srcPtr + (y * rowPitch), packedRowSize);
                        destinationStream.Write(rowSpan);
                    }
                }
            }
            finally
            {
                this._context.Unmap(this._stagingTex, 0);
            }
        }

        public void Dispose()
        {
            this._constantBuffer.Dispose();
            this._samplerState.Dispose();
            this._pixelShader.Dispose();
            this._vertexShader.Dispose();
            this._stagingTex.Dispose();
            this._renderTargetView.Dispose();
            this._renderTargetTex.Dispose();
        }
    }
}