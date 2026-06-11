using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.DirectX.DirectX;
using static TerraFX.Interop.Windows.Windows;

namespace MaskOfKefka.Output;

/// <summary>
/// The output window plus everything that presents to it: its own D3D11 device, swapchain
/// and render loop, all on a dedicated thread. Frames arrive from the game through the
/// SharedFrameBridge. Nothing here ever runs inside the game's frame, so the window can be
/// visible (and composed by DWM, and vsynced) at zero cost to the game's fps.
/// </summary>
internal sealed unsafe class OutputWindowHost : IDisposable
{
    private const string WindowClassName = "MaskOfKefkaOutput";
    private const string WindowTitle = "Mask of Kefka";

    /// <summary>Custom message: reapplies the window style (bordered/borderless) on the owning thread.</summary>
    private const uint WmApplyStyle = WM.WM_APP + 1;

    private const uint BorderedStyle = WS.WS_OVERLAPPEDWINDOW;
    private const uint BorderlessStyle = WS.WS_POPUP | WS.WS_THICKFRAME | WS.WS_MINIMIZEBOX | WS.WS_MAXIMIZEBOX;

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

    // WndProc is static (native callback); since there is only one window per plugin,
    // the state it needs lives in statics.
    private static volatile bool borderless;

    private readonly SharedFrameBridge bridge;
    private readonly nint adapterPtr; // IDXGIAdapter*, ref owned by us, released by the thread

    private Thread? thread;
    private nint hwnd;
    private volatile bool ready;
    private volatile bool closed;
    private bool startMinimized;

    // Everything below is owned exclusively by the window thread.
    private ID3D11Device* device;
    private ID3D11DeviceContext* context;
    private IDXGISwapChain1* swapChain;
    private ID3D11RenderTargetView* rtv;
    private ID3D11VertexShader* vertexShader;
    private ID3D11PixelShader* pixelShader;
    private ID3D11SamplerState* sampler;
    private ID3D11Texture2D* frameTexA;
    private ID3D11Texture2D* frameTexB;
    private ID3D11ShaderResourceView* frameSrvA;
    private ID3D11ShaderResourceView* frameSrvB;
    private uint swapWidth;
    private uint swapHeight;
    private int seenGeneration;

    public OutputWindowHost(SharedFrameBridge bridge, nint adapterPtr)
    {
        this.bridge = bridge;
        this.adapterPtr = adapterPtr;
    }

    public HWND Hwnd => (HWND)(void*)hwnd;

    /// <summary>Window created and presenting.</summary>
    public bool Ready => ready;

    /// <summary>Window was closed (by the user or an error) and its thread ended.</summary>
    public bool Closed => closed;

    public void Start(int clientWidth, int clientHeight, bool startBorderless, bool startMinimized)
    {
        if (thread != null)
            throw new InvalidOperationException("Output window already started.");

        borderless = startBorderless;
        this.startMinimized = startMinimized;
        thread = new Thread(() => ThreadMain(clientWidth, clientHeight))
        {
            IsBackground = true,
            Name = "MaskOfKefka.OutputWindow",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Dispose()
    {
        if (ready && hwnd != 0)
            PostMessageW((HWND)(void*)hwnd, WM.WM_CLOSE, 0u, 0);
        thread?.Join(3000);
        closed = true;
    }

    /// <summary>Minimizes the output window. Safe to call from any thread.</summary>
    public void Minimize()
    {
        if (ready && hwnd != 0)
            PostMessageW((HWND)(void*)hwnd, WM.WM_SYSCOMMAND, SC.SC_MINIMIZE, 0);
    }

    /// <summary>Toggles bordered/borderless at runtime. Safe to call from any thread.</summary>
    public void SetBorderless(bool value)
    {
        if (borderless == value)
            return;
        borderless = value;
        if (ready && hwnd != 0)
            PostMessageW((HWND)(void*)hwnd, WmApplyStyle, 0u, 0);
    }

    private void ThreadMain(int clientWidth, int clientHeight)
    {
        try
        {
            RunWindow(clientWidth, clientHeight);
        }
        finally
        {
            ready = false;
            closed = true;
            hwnd = 0;
        }
    }

    private void RunWindow(int clientWidth, int clientHeight)
    {
        fixed (char* clsName = WindowClassName)
        fixed (char* title = WindowTitle)
        {
            var hInstance = (HINSTANCE)(void*)GetModuleHandleW(null);

            // A registration may have survived a previous plugin reload; removal failing is fine.
            _ = UnregisterClassW(clsName, hInstance);

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS.CS_HREDRAW | CS.CS_VREDRAW,
                lpfnWndProc = &WndProc,
                hInstance = hInstance,
                hCursor = LoadCursorW(HINSTANCE.NULL, IDC.IDC_ARROW),
                lpszClassName = clsName,
            };
            if (RegisterClassExW(&wc) == 0)
                return;

            try
            {
                var style = borderless ? BorderlessStyle : BorderedStyle;
                var rect = new RECT { right = clientWidth, bottom = clientHeight };
                _ = AdjustWindowRectEx(&rect, style, BOOL.FALSE, 0);

                var window = CreateWindowExW(
                    0,
                    clsName,
                    title,
                    style,
                    CW_USEDEFAULT,
                    CW_USEDEFAULT,
                    rect.right - rect.left,
                    rect.bottom - rect.top,
                    HWND.NULL,
                    HMENU.NULL,
                    hInstance,
                    null);
                if (window == HWND.NULL)
                    return;

                hwnd = (nint)window.Value;

                // Without stealing focus from the game. Minimized is the recommended way
                // to stream: zero composition cost and capture keeps working on Win11.
                _ = ShowWindow(window, startMinimized ? SW.SW_SHOWMINNOACTIVE : SW.SW_SHOWNOACTIVATE);

                try
                {
                    InitD3D(window);
                    Plugin.Log.Information("[output] window device and swapchain ready");
                    ready = true;
                    MainLoop(window);
                }
                catch (Exception e)
                {
                    Plugin.Log.Error(e, "[output] window thread failed");
                }
                finally
                {
                    ReleaseD3D();
                }
            }
            finally
            {
                _ = UnregisterClassW(clsName, hInstance);
            }
        }
    }

    private void MainLoop(HWND window)
    {
        MSG msg;
        while (true)
        {
            while (PeekMessageW(&msg, HWND.NULL, 0, 0, PM.PM_REMOVE))
            {
                if (msg.message == WM.WM_QUIT)
                    return;
                _ = TranslateMessage(&msg);
                _ = DispatchMessageW(&msg);
            }

            RenderOnce(window);
        }
    }

    private void RenderOnce(HWND window)
    {
        RECT rc;
        if (!GetClientRect(window, &rc))
        {
            Thread.Sleep(50);
            return;
        }
        var width = (uint)(rc.right - rc.left);
        var height = (uint)(rc.bottom - rc.top);
        if (width == 0 || height == 0)
        {
            Thread.Sleep(50);
            return;
        }

        if (rtv == null || width != swapWidth || height != swapHeight)
            ResizeSwapChain(width, height);

        var generation = bridge.Snapshot(out var h0, out var h1);
        if (generation != seenGeneration)
        {
            ReleaseFrameTextures();
            if (h0 != 0 && h1 != 0)
            {
                OpenFrameTexture(h0, ref frameTexA, ref frameSrvA);
                OpenFrameTexture(h1, ref frameTexB, ref frameSrvB);
            }
            seenGeneration = generation;
        }

        // Only ever read the published texture; the producer is writing the other one.
        var published = bridge.PublishedIndex;
        var srvLocal = published switch
        {
            0 => frameSrvA,
            1 => frameSrvB,
            _ => null,
        };
        if (srvLocal == null)
        {
            Thread.Sleep(10);
            return;
        }

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
        context->PSSetShaderResources(0, 1, &srvLocal);
        context->Draw(3, 0);

        ID3D11ShaderResourceView* nullSrv = null;
        context->PSSetShaderResources(0, 1, &nullSrv);

        // Vsync here paces this loop at the refresh rate. Blocking is harmless on this thread.
        _ = swapChain->Present(1, 0);
    }

    private void InitD3D(HWND window)
    {
        var adapter = (IDXGIAdapter*)adapterPtr;

        var levels = stackalloc D3D_FEATURE_LEVEL[2] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0 };
        fixed (ID3D11Device** dev = &device)
        fixed (ID3D11DeviceContext** ctx = &context)
        {
            ThrowIfFailed(
                D3D11CreateDevice(adapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, HMODULE.NULL, 0, levels, 2, D3D11.D3D11_SDK_VERSION, dev, null, ctx),
                "D3D11CreateDevice (output)");
        }

        CompileShaders();
        CreateSampler();

        IDXGIFactory2* factory = null;
        ThrowIfFailed(adapter->GetParent(__uuidof<IDXGIFactory2>(), (void**)&factory), "GetParent IDXGIFactory2");
        try
        {
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = 0,
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
                ThrowIfFailed(factory->CreateSwapChainForHwnd((IUnknown*)device, window, &desc, null, null, sc), "CreateSwapChainForHwnd (output)");

            // Alt+Enter on the output window must not attempt exclusive fullscreen.
            _ = factory->MakeWindowAssociation(window, DXGI.DXGI_MWA_NO_ALT_ENTER);
        }
        finally
        {
            factory->Release();
        }
    }

    private void ReleaseD3D()
    {
        ReleaseFrameTextures();
        Release(ref rtv);
        Release(ref swapChain);
        Release(ref sampler);
        Release(ref pixelShader);
        Release(ref vertexShader);
        Release(ref context);
        Release(ref device);
        if (adapterPtr != 0)
            ((IDXGIAdapter*)adapterPtr)->Release();
    }

    private void OpenFrameTexture(nint handle, ref ID3D11Texture2D* texture, ref ID3D11ShaderResourceView* srv)
    {
        fixed (ID3D11Texture2D** tex = &texture)
        {
            var hr = device->OpenSharedResource((HANDLE)(void*)handle, __uuidof<ID3D11Texture2D>(), (void**)tex);
            if (FAILED(hr))
            {
                Plugin.Log.Error($"[output] OpenSharedResource failed: 0x{(int)hr:X8} (handle=0x{handle:X})");
                texture = null;
                return;
            }
        }
        fixed (ID3D11ShaderResourceView** view = &srv)
        {
            var hr = device->CreateShaderResourceView((ID3D11Resource*)texture, null, view);
            if (FAILED(hr))
            {
                Plugin.Log.Error($"[output] CreateShaderResourceView failed: 0x{(int)hr:X8}");
                Release(ref texture);
                return;
            }
        }
        Plugin.Log.Information($"[output] shared texture opened (handle=0x{handle:X})");
    }

    private void ReleaseFrameTextures()
    {
        Release(ref frameSrvA);
        Release(ref frameSrvB);
        Release(ref frameTexA);
        Release(ref frameTexB);
    }

    private void ResizeSwapChain(uint width, uint height)
    {
        Release(ref rtv);
        ThrowIfFailed(swapChain->ResizeBuffers(0, width, height, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 0), "ResizeBuffers (output)");

        ID3D11Texture2D* backBuffer = null;
        ThrowIfFailed(swapChain->GetBuffer(0, __uuidof<ID3D11Texture2D>(), (void**)&backBuffer), "GetBuffer (output)");
        try
        {
            fixed (ID3D11RenderTargetView** view = &rtv)
                ThrowIfFailed(device->CreateRenderTargetView((ID3D11Resource*)backBuffer, null, view), "CreateRenderTargetView (output)");
        }
        finally
        {
            backBuffer->Release();
        }

        swapWidth = width;
        swapHeight = height;
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

    [UnmanagedCallersOnly]
    private static LRESULT WndProc(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case WM.WM_DESTROY:
                PostQuitMessage(0);
                return 0;

            // No background: the swapchain covers the whole client area; avoids resize flicker.
            case WM.WM_ERASEBKGND:
                return 1;

            case WmApplyStyle:
            {
                var style = (borderless ? BorderlessStyle : BorderedStyle) | WS.WS_VISIBLE;
                _ = SetWindowLongPtrW(window, GWL.GWL_STYLE, (nint)style);
                // SWP_FRAMECHANGED forces Windows to recompute the frame with the new style.
                _ = SetWindowPos(window, HWND.NULL, 0, 0, 0, 0,
                    SWP.SWP_NOMOVE | SWP.SWP_NOSIZE | SWP.SWP_NOZORDER | SWP.SWP_NOACTIVATE | SWP.SWP_FRAMECHANGED);
                return 0;
            }

            // Borderless has no title bar: the window body becomes the drag handle and an
            // 8px strip along the edges becomes the resize handle.
            case WM.WM_NCHITTEST when borderless:
            {
                var x = (int)(short)((nuint)lParam.Value & 0xFFFF);
                var y = (int)(short)(((nuint)lParam.Value >> 16) & 0xFFFF);
                RECT rect;
                if (!GetWindowRect(window, &rect))
                    break;

                const int grip = 8;
                var left = x < rect.left + grip;
                var right = x >= rect.right - grip;
                var top = y < rect.top + grip;
                var bottom = y >= rect.bottom - grip;

                if (top && left) return HTTOPLEFT;
                if (top && right) return HTTOPRIGHT;
                if (bottom && left) return HTBOTTOMLEFT;
                if (bottom && right) return HTBOTTOMRIGHT;
                if (left) return HTLEFT;
                if (right) return HTRIGHT;
                if (top) return HTTOP;
                if (bottom) return HTBOTTOM;
                return HTCAPTION;
            }
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }
}
