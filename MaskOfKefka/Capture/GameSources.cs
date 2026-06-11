using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;

namespace MaskOfKefka.Capture;

/// <summary>
/// Acesso às texturas do jogo via FFXIVClientStructs. Tudo aqui é dependente de patch:
/// veja docs/ATUALIZACAO-POS-PATCH.md antes de mexer.
/// </summary>
internal static unsafe class GameSources
{
    /// <summary>
    /// Offset onde começa a tabela de ponteiros Texture* dentro do RenderTargetManager
    /// (logo após o vtable/header, no campo _gBuffers). Confira em
    /// FFXIVClientStructs/FFXIV/Client/Graphics/Render/RenderTargetManager.cs após cada patch.
    /// </summary>
    internal const int RenderTargetTableOffset = 0x20;

    /// <summary>
    /// Nem todo slot da tabela é um ponteiro de textura — o fim do struct tem campos de
    /// resolução e flags. O scan valida cada ponteiro antes de dereferenciar, mas mantemos
    /// o intervalo dentro do tamanho do struct.
    /// </summary>
    internal static int RenderTargetSlotCount => (sizeof(RenderTargetManager) - RenderTargetTableOffset) / 8;

    internal static Device* GameDevice => Device.Instance();

    /// <summary>Backbuffer do jogo: cena + UI do jogo, ainda sem ImGui/Dalamud.</summary>
    internal static bool TryGetBackBuffer(out ID3D11Texture2D* texture)
    {
        texture = null;
        var dev = Device.Instance();
        if (dev == null || dev->SwapChain == null)
            return false;

        var bb = dev->SwapChain->BackBuffer;
        if (bb == null || !LooksLikePointer(bb->D3D11Texture2D))
            return false;

        texture = (ID3D11Texture2D*)bb->D3D11Texture2D;
        return true;
    }

    /// <summary>Render target arbitrária do jogo (modo "sem UI"). Só serve se tiver SRV.</summary>
    internal static bool TryGetRenderTarget(int index, out ID3D11Texture2D* texture, out ID3D11ShaderResourceView* srv)
    {
        texture = null;
        srv = null;

        if (index < 0 || index >= RenderTargetSlotCount)
            return false;

        var rtm = RenderTargetManager.Instance();
        if (rtm == null)
            return false;

        var slots = (Texture**)((byte*)rtm + RenderTargetTableOffset);
        var t = slots[index];
        if (!LooksLikePointer(t) || !LooksLikePointer(t->D3D11Texture2D) || t->ActualWidth == 0 || t->ActualWidth > 16384)
            return false;

        texture = (ID3D11Texture2D*)t->D3D11Texture2D;
        srv = (ID3D11ShaderResourceView*)t->D3D11ShaderResourceView;
        return srv != null;
    }

    /// <summary>Ferramenta de manutenção: lista os slots plausíveis no log pra achar o índice da cena.</summary>
    internal static void DumpRenderTargets(IPluginLog log)
    {
        var rtm = RenderTargetManager.Instance();
        if (rtm == null)
        {
            log.Warning("RenderTargetManager indisponível.");
            return;
        }

        log.Information($"RenderTargetManager @ {(nuint)rtm:X}, {RenderTargetSlotCount} slots a partir de +0x{RenderTargetTableOffset:X}:");
        var slots = (Texture**)((byte*)rtm + RenderTargetTableOffset);
        for (var i = 0; i < RenderTargetSlotCount; i++)
        {
            var t = slots[i];
            if (!LooksLikePointer(t) || !LooksLikePointer(t->D3D11Texture2D))
                continue;
            if (t->ActualWidth == 0 || t->ActualWidth > 16384 || t->ActualHeight == 0 || t->ActualHeight > 16384)
                continue;

            log.Information($"  [{i,3}] {t->ActualWidth}x{t->ActualHeight} fmt={t->TextureFormat} srv={(t->D3D11ShaderResourceView != null ? "sim" : "nao")}");
        }
    }

    /// <summary>
    /// A tabela contém slots que não são ponteiros (ints, flags). Antes de dereferenciar,
    /// exige endereço canônico de user-space alinhado a 8 — evita access violation.
    /// </summary>
    private static bool LooksLikePointer(void* p)
        => p != null && ((nuint)p & 0x7) == 0 && (nuint)p > 0x10000 && (nuint)p < 0x7FFF_FFFF_0000;
}
