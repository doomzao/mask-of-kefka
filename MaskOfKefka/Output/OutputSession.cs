using System;
using Dalamud.Plugin.Services;
using MaskOfKefka.Capture;
using TerraFX.Interop.DirectX;

namespace MaskOfKefka.Output;

/// <summary>
/// Uma "sessão" de saída: a janela + o renderer. Criada e destruída exclusivamente na
/// thread de render (dentro do UiBuilder.Draw), o que dispensa locks no caminho quente.
/// </summary>
internal sealed unsafe class OutputSession : IDisposable
{
    private readonly OutputWindowHost host = new();
    private readonly OutputRenderer renderer = new();
    private uint frameCounter;

    private OutputSession()
    {
    }

    /// <summary>Usuário fechou a janela no X (ou a thread dela morreu).</summary>
    public bool WindowClosed => host.Closed;

    public static OutputSession Start(IPluginLog log, Configuration config)
    {
        var dev = GameSources.GameDevice;
        if (dev == null || dev->SwapChain == null)
            throw new InvalidOperationException("Device do jogo indisponível.");

        var session = new OutputSession();
        session.host.Start((int)dev->SwapChain->Width, (int)dev->SwapChain->Height, config.Borderless);
        log.Information("Janela de saída aberta ({Width}x{Height}).", dev->SwapChain->Width, dev->SwapChain->Height);
        return session;
    }

    public void RenderFrame(Configuration config)
    {
        if (!host.Ready || host.Closed)
            return;

        host.SetBorderless(config.Borderless); // no-op se não mudou

        // Limitador de taxa: pula frames inteiros (cópia, draw e present) pra aliviar a GPU.
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
            // Modo padrão (e fallback quando o índice "sem UI" não vale mais após um patch).
            renderer.RenderFrame(backBuffer, null);
        }
    }

    public void Dispose()
    {
        renderer.Dispose();
        host.Dispose();
    }
}
