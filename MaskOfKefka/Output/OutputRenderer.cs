using System;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace MaskOfKefka.Output;

/// <summary>
/// Draws a game texture into the output window. Always runs on the game's render thread
/// (inside UiBuilder.Draw), using the game's own D3D11 device, which is why there are no
/// shared textures and no separate process.
///
/// All pipeline state is isolated in a dedicated ID3DDeviceContextState: the game's state
/// and ImGui's state are left untouched.
/// </summary>
internal sealed unsafe class OutputRenderer : IDisposable
{
    // Fullscreen triangle generated from SV_VertexID (no vertex buffer, no input layout).
    // Alpha is forced to 1 so the OBS capture does not come out translucent.
    private const string ShaderSource = """
        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut vsmain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
            o.uv = uv;
            return o;
        }

        Texture2D tex0 : register(t0);
        SamplerState samp0 : register(s0);

        float4 psmain(VSOut i) : SV_Target
        {
            return float4(tex0.Sample(samp0, i.uv).rgb, 1.0);
        }
        """;

    private ID3D11Device* device;
    private ID3D11DeviceContext1* context;
    private ID3DDeviceContextState* contextState;
    private IDXGISwapChain1* swapChain;
    private ID3D11RenderTargetView* rtv;
    private ID3D11VertexShader* vertexShader;
    private ID3D11PixelShader* pixelShader;
    private ID3D11SamplerState* sampler;

    // Intermediate copy for the backbuffer case (it has no shader resource bind flag).
    private ID3D11Texture2D* copyTex;
    private ID3D11ShaderResourceView* copySrv;
    private D3D11_TEXTURE2D_DESC copyDesc;

    private HWND hwnd;
    private uint swapWidth;
    private uint swapHeight;

    public bool Initialized { get; private set; }

    public void Initialize(ID3D11Device* gameDevice, ID3D11DeviceContext* gameContext, HWND windowHandle)
    {
        if (gameDevice == null || gameContext == null)
            throw new InvalidOperationException("Game D3D11 device/context unavailable.");

        device = gameDevice;
        hwnd = windowHandle;

        fixed (ID3D11DeviceContext1** ctx1 = &context)
            ThrowIfFailed(gameContext->QueryInterface(__uuidof<ID3D11DeviceContext1>(), (void**)ctx1), "QueryInterface ID3D11DeviceContext1");

        CreateContextState();
        CompileShaders();
        CreateSampler();
        CreateSwapChain();

        Initialized = true;
    }

    /// <summary>
    /// Copies/draws the texture into the window. When <paramref name="sourceSrv"/> is null,
    /// the texture is copied into an intermediate with a shader resource bind flag
    /// (the backbuffer path).
    /// </summary>
    public void RenderFrame(ID3D11Texture2D* sourceTexture, ID3D11ShaderResourceView* sourceSrv)
    {
        if (!Initialized || sourceTexture == null)
            return;

        RECT rc;
        if (!GetClientRect(hwnd, &rc))
            return;
        var width = (uint)(rc.right - rc.left);
        var height = (uint)(rc.bottom - rc.top);
        if (width == 0 || height == 0)
            return; // minimized

        if (rtv == null || width != swapWidth || height != swapHeight)
            ResizeSwapChain(width, height);

        var srv = sourceSrv;
        if (srv == null)
        {
            D3D11_TEXTURE2D_DESC srcDesc;
            sourceTexture->GetDesc(&srcDesc);
            if (copyTex == null || srcDesc.Width != copyDesc.Width || srcDesc.Height != copyDesc.Height || srcDesc.Format != copyDesc.Format)
                RecreateCopyTexture(&srcDesc);

            context->CopyResource((ID3D11Resource*)copyTex, (ID3D11Resource*)sourceTexture);
            srv = copySrv;
        }

        ID3DDeviceContextState* previousState = null;
        context->SwapDeviceContextState(contextState, &previousState);
        try
        {
            var viewport = new D3D11_VIEWPORT { Width = width, Height = height, MaxDepth = 1.0f };
            context->RSSetViewports(1, &viewport);

            var renderTarget = rtv;
            context->OMSetRenderTargets(1, &renderTarget, null);

            context->IASetInputLayout(null);
            context->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY.D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(vertexShader, null, 0);
            context->PSSetShader(pixelShader, null, 0);

            var samplerLocal = sampler;
            context->PSSetSamplers(0, 1, &samplerLocal);
            context->PSSetShaderResources(0, 1, &srv);

            context->Draw(3, 0);

            // Unbind the game's texture from the pipeline before handing the state back.
            ID3D11ShaderResourceView* nullSrv = null;
            context->PSSetShaderResources(0, 1, &nullSrv);
            ID3D11RenderTargetView* nullRtv = null;
            context->OMSetRenderTargets(1, &nullRtv, null);

            ThrowIfFailed(swapChain->Present(0, 0), "Present");
        }
        finally
        {
            context->SwapDeviceContextState(previousState, null);
            previousState->Release();
        }
    }

    public void Dispose()
    {
        Initialized = false;
        ReleaseCopyTexture();
        Release(ref rtv);
        Release(ref swapChain);
        Release(ref sampler);
        Release(ref pixelShader);
        Release(ref vertexShader);
        Release(ref contextState);
        Release(ref context);
        device = null; // owned by the game; we never AddRef'd it
    }

    private void CreateContextState()
    {
        ID3D11Device1* device1 = null;
        ThrowIfFailed(device->QueryInterface(__uuidof<ID3D11Device1>(), (void**)&device1), "QueryInterface ID3D11Device1");
        try
        {
            var featureLevel = device->GetFeatureLevel();
            fixed (ID3DDeviceContextState** state = &contextState)
            {
                ThrowIfFailed(
                    device1->CreateDeviceContextState(0, &featureLevel, 1, D3D11.D3D11_SDK_VERSION, __uuidof<ID3D11Device>(), null, state),
                    "CreateDeviceContextState");
            }
        }
        finally
        {
            device1->Release();
        }
    }

    private void CompileShaders()
    {
        var vsBlob = CompileShader("vsmain", "vs_5_0");
        try
        {
            fixed (ID3D11VertexShader** vs = &vertexShader)
                ThrowIfFailed(device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), null, vs), "CreateVertexShader");
        }
        finally
        {
            vsBlob->Release();
        }

        var psBlob = CompileShader("psmain", "ps_5_0");
        try
        {
            fixed (ID3D11PixelShader** ps = &pixelShader)
                ThrowIfFailed(device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), null, ps), "CreatePixelShader");
        }
        finally
        {
            psBlob->Release();
        }
    }

    private static ID3DBlob* CompileShader(string entryPoint, string target)
    {
        var source = Encoding.ASCII.GetBytes(ShaderSource);
        var entryBytes = Encoding.ASCII.GetBytes(entryPoint + "\0");
        var targetBytes = Encoding.ASCII.GetBytes(target + "\0");

        ID3DBlob* code = null;
        ID3DBlob* errors = null;
        fixed (byte* pSource = source)
        fixed (byte* pEntry = entryBytes)
        fixed (byte* pTarget = targetBytes)
        {
            var hr = D3DCompile(pSource, (nuint)source.Length, null, null, null, (sbyte*)pEntry, (sbyte*)pTarget, 0, 0, &code, &errors);
            if (FAILED(hr))
            {
                var message = errors != null ? Marshal.PtrToStringAnsi((nint)errors->GetBufferPointer()) : "no details";
                if (errors != null)
                    errors->Release();
                throw new InvalidOperationException($"D3DCompile({entryPoint}) failed: 0x{(int)hr:X8}, {message}");
            }

            if (errors != null)
                errors->Release();
            return code;
        }
    }

    private void CreateSampler()
    {
        var desc = new D3D11_SAMPLER_DESC
        {
            Filter = D3D11_FILTER.D3D11_FILTER_MIN_MAG_MIP_LINEAR,
            AddressU = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressV = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            AddressW = D3D11_TEXTURE_ADDRESS_MODE.D3D11_TEXTURE_ADDRESS_CLAMP,
            ComparisonFunc = D3D11_COMPARISON_FUNC.D3D11_COMPARISON_ALWAYS,
            MaxLOD = float.MaxValue,
        };
        fixed (ID3D11SamplerState** s = &sampler)
            ThrowIfFailed(device->CreateSamplerState(&desc, s), "CreateSamplerState");
    }

    private void CreateSwapChain()
    {
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        IDXGIFactory2* factory = null;
        try
        {
            ThrowIfFailed(device->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgiDevice), "QueryInterface IDXGIDevice");
            ThrowIfFailed(dxgiDevice->GetAdapter(&adapter), "GetAdapter");
            ThrowIfFailed(adapter->GetParent(__uuidof<IDXGIFactory2>(), (void**)&factory), "GetParent IDXGIFactory2");

            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = 0, // use the current client area size
                Height = 0,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                BufferUsage = DXGI.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
            };

            fixed (IDXGISwapChain1** sc = &swapChain)
                ThrowIfFailed(factory->CreateSwapChainForHwnd((IUnknown*)device, hwnd, &desc, null, null, sc), "CreateSwapChainForHwnd");

            // Alt+Enter on the output window must not attempt exclusive fullscreen.
            _ = factory->MakeWindowAssociation(hwnd, DXGI.DXGI_MWA_NO_ALT_ENTER);
        }
        finally
        {
            if (factory != null) factory->Release();
            if (adapter != null) adapter->Release();
            if (dxgiDevice != null) dxgiDevice->Release();
        }
    }

    private void ResizeSwapChain(uint width, uint height)
    {
        Release(ref rtv);
        ThrowIfFailed(swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 0), "ResizeBuffers");

        ID3D11Texture2D* backBuffer = null;
        ThrowIfFailed(swapChain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&backBuffer), "GetBuffer");
        try
        {
            fixed (ID3D11RenderTargetView** view = &rtv)
                ThrowIfFailed(device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, view), "CreateRenderTargetView");
        }
        finally
        {
            backBuffer->Release();
        }

        swapWidth = width;
        swapHeight = height;
    }

    private void RecreateCopyTexture(D3D11_TEXTURE2D_DESC* sourceDesc)
    {
        ReleaseCopyTexture();

        var desc = *sourceDesc;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        desc.BindFlags = (uint)D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags = 0;

        fixed (ID3D11Texture2D** tex = &copyTex)
            ThrowIfFailed(device->CreateTexture2D(&desc, null, tex), "CreateTexture2D (copy)");
        fixed (ID3D11ShaderResourceView** view = &copySrv)
            ThrowIfFailed(device->CreateShaderResourceView((ID3D11Resource*)copyTex, null, view), "CreateShaderResourceView (copy)");

        copyDesc = desc;
    }

    private void ReleaseCopyTexture()
    {
        Release(ref copySrv);
        Release(ref copyTex);
        copyDesc = default;
    }

    private static void Release<T>(ref T* comObject) where T : unmanaged
    {
        if (comObject != null)
        {
            ((IUnknown*)comObject)->Release();
            comObject = null;
        }
    }

    private static void ThrowIfFailed(HRESULT hr, string what)
    {
        if (FAILED(hr))
            throw new InvalidOperationException($"{what} failed: 0x{(int)hr:X8}");
    }
}
