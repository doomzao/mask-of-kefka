using System;
using Dalamud.Plugin.Services;
using MaskOfKefka.Capture;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace MaskOfKefka.Output;

/// <summary>
/// One output "session": the shared-frame bridge plus the window (with its own device and
/// render loop). On the game's render thread the only work is a texture copy into the
/// shared textures; presentation never touches the game's frame.
/// </summary>
internal sealed unsafe class OutputSession : IDisposable
{
    private readonly SharedFrameBridge bridge = new();
    private readonly OutputWindowHost host;
    private uint frameCounter;
    private bool bridgeInitialized;

    private OutputSession(nint adapterPtr)
    {
        host = new OutputWindowHost(bridge, adapterPtr);
    }

    /// <summary>The user closed the window (or its thread died).</summary>
    public bool WindowClosed => host.Closed;

    public static OutputSession Start(IPluginLog log, Configuration config)
    {
        var dev = GameSources.GameDevice;
        if (dev == null || dev->SwapChain == null || dev->D3D11Forwarder == null)
            throw new InvalidOperationException("Game device unavailable.");

        // The output device must live on the same adapter as the game's for resource sharing.
        var gameDevice = (ID3D11Device*)dev->D3D11Forwarder;
        IDXGIDevice* dxgiDevice = null;
        IDXGIAdapter* adapter = null;
        if (FAILED(gameDevice->QueryInterface(__uuidof<IDXGIDevice>(), (void**)&dxgiDevice)))
            throw new InvalidOperationException("Could not query the game's DXGI device.");
        try
        {
            if (FAILED(dxgiDevice->GetAdapter(&adapter)))
                throw new InvalidOperationException("Could not get the game's DXGI adapter.");
        }
        finally
        {
            dxgiDevice->Release();
        }

        var session = new OutputSession((nint)adapter);
        session.host.Start((int)dev->SwapChain->Width, (int)dev->SwapChain->Height, config.Borderless, config.StartMinimized);
        log.Information("Output window opened ({Width}x{Height}).", dev->SwapChain->Width, dev->SwapChain->Height);
        return session;
    }

    /// <summary>Minimizes the output window (the recommended state while streaming).</summary>
    public void MinimizeWindow() => host.Minimize();

    public void RenderFrame(Configuration config)
    {
        if (host.Closed)
            return;

        host.SetBorderless(config.Borderless); // no-op when unchanged

        // Rate limiter: skips the copy entirely to ease GPU load.
        frameCounter++;
        var interval = Math.Max(1, config.RenderEveryNthFrame);
        if (interval > 1 && frameCounter % (uint)interval != 0)
            return;

        var dev = GameSources.GameDevice;
        if (dev == null || dev->D3D11Forwarder == null || dev->D3D11DeviceContext == null)
            return;

        if (!bridgeInitialized)
        {
            bridge.Initialize((ID3D11Device*)dev->D3D11Forwarder);
            bridgeInitialized = true;
        }

        var context = (ID3D11DeviceContext*)dev->D3D11DeviceContext;

        if (!config.ShowGameUi && GameSources.TryGetRenderTarget(config.RenderTargetIndex, out var sceneTex))
        {
            bridge.Publish(context, sceneTex);
        }
        else if (GameSources.TryGetBackBuffer(out var backBuffer))
        {
            // Default mode (and fallback when the "no UI" index is stale after a patch).
            bridge.Publish(context, backBuffer);
        }
    }

    public void Dispose()
    {
        host.Dispose();
        bridge.Dispose();
    }
}
