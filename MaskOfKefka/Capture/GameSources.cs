using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using TerraFX.Interop.DirectX;

namespace MaskOfKefka.Capture;

/// <summary>
/// Access to the game's textures via FFXIVClientStructs. Everything here is patch-sensitive:
/// see .dev/UPDATING.md before touching it.
/// </summary>
internal static unsafe class GameSources
{
    /// <summary>
    /// Offset where the Texture* pointer table starts inside RenderTargetManager
    /// (right after the vtable/header, at the _gBuffers field). Verify it in
    /// FFXIVClientStructs/FFXIV/Client/Graphics/Render/RenderTargetManager.cs after each patch.
    /// </summary>
    internal const int RenderTargetTableOffset = 0x20;

    /// <summary>
    /// Not every slot in the table is a texture pointer; the tail of the struct holds
    /// resolution fields and flags. The scan validates each pointer before dereferencing,
    /// but the range stays within the struct size.
    /// </summary>
    internal static int RenderTargetSlotCount => (sizeof(RenderTargetManager) - RenderTargetTableOffset) / 8;

    internal static Device* GameDevice => Device.Instance();

    /// <summary>Game backbuffer: scene + game UI, still without ImGui/Dalamud.</summary>
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

    /// <summary>Arbitrary game render target ("no UI" mode). Only usable when it has an SRV.</summary>
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

    /// <summary>Maintenance tool: lists the plausible slots to the log to find the scene index.</summary>
    internal static void DumpRenderTargets(IPluginLog log)
    {
        var rtm = RenderTargetManager.Instance();
        if (rtm == null)
        {
            log.Warning("RenderTargetManager unavailable.");
            return;
        }

        log.Information($"RenderTargetManager @ {(nuint)rtm:X}, {RenderTargetSlotCount} slots starting at +0x{RenderTargetTableOffset:X}:");
        var slots = (Texture**)((byte*)rtm + RenderTargetTableOffset);
        for (var i = 0; i < RenderTargetSlotCount; i++)
        {
            var t = slots[i];
            if (!LooksLikePointer(t) || !LooksLikePointer(t->D3D11Texture2D))
                continue;
            if (t->ActualWidth == 0 || t->ActualWidth > 16384 || t->ActualHeight == 0 || t->ActualHeight > 16384)
                continue;

            log.Information($"  [{i,3}] {t->ActualWidth}x{t->ActualHeight} fmt={t->TextureFormat} srv={(t->D3D11ShaderResourceView != null ? "yes" : "no")}");
        }
    }

    /// <summary>
    /// The table mixes pointers with scalar fields (ints, flags). Before dereferencing,
    /// require a canonical, 8-aligned user-space address to avoid access violations.
    /// </summary>
    private static bool LooksLikePointer(void* p)
        => p != null && ((nuint)p & 0x7) == 0 && (nuint)p > 0x10000 && (nuint)p < 0x7FFF_FFFF_0000;
}
