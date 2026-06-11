using System;
using Dalamud.Plugin.Services;
using MaskOfKefka.Capture;
using TerraFX.Interop.DirectX;

namespace MaskOfKefka.Output;

/// <summary>
/// One output "session": the window plus the renderer. Created and destroyed exclusively on
/// the render thread (inside UiBuilder.Draw), which removes the need for locks on the hot path.
/// </summary>
internal sealed unsafe class OutputSession : IDisposable
{
    private readonly OutputWindowHost host = new();
    private readonly OutputRenderer renderer = new();
    private uint frameCounter;

    private OutputSession()
    {
    }

    /// <summary>The user closed the window (or its thread died).</summary>
    public bool WindowClosed => host.Closed;

    public static OutputSession Start(IPluginLog log, Configuration config)
    {
        var dev = GameSources.GameDevice;
        if (dev == null || dev->SwapChain == null)
            throw new InvalidOperationException("Game device unavailable.");

        var session = new OutputSession();
        session.host.Start((int)dev->SwapChain->Width, (int)dev->SwapChain->Height, config.Borderless);
        log.Information("Output window opened ({Width}x{Height}).", dev->SwapChain->Width, dev->SwapChain->Height);
        return session;
    }

    public void RenderFrame(Configuration config)
    {
        if (!host.Ready || host.Closed)
            return;

        host.SetBorderless(config.Borderless); // no-op when unchanged

        // Rate limiter: skips whole frames (copy, draw and present) to ease GPU load.
        frameCounter++;
        var interval = Math.Max(1, config.RenderEveryNthFrame);
        if (interval > 1 && frameCounter % (uint)interval != 0)
            return;

        if (!renderer.Initialized)
        {
            var dev = GameSources.GameDevice;
            if (dev == null || dev->D3D11Forwarder == null || dev->D3D11DeviceContext == null)
                return;
            renderer.Initialize((ID3D11Device*)dev->D3D11Forwarder, (ID3D11DeviceContext*)dev->D3D11DeviceContext, host.Hwnd);
        }

        if (!config.ShowGameUi && GameSources.TryGetRenderTarget(config.RenderTargetIndex, out var sceneTex, out var sceneSrv))
        {
            renderer.RenderFrame(sceneTex, sceneSrv);
        }
        else if (GameSources.TryGetBackBuffer(out var backBuffer))
        {
            // Default mode (and fallback when the "no UI" index is stale after a patch).
            renderer.RenderFrame(backBuffer, null);
        }
    }

    public void Dispose()
    {
        renderer.Dispose();
        host.Dispose();
    }
}
