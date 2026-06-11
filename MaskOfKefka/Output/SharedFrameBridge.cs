using System;
using System.Threading;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using static TerraFX.Interop.Windows.Windows;

namespace MaskOfKefka.Output;

/// <summary>
/// Hands frames from the game's D3D11 device to the output window's own device through
/// shared textures. The producer (game render thread) only pays a CopyResource plus a
/// Flush; presentation happens entirely on the window's device and thread, outside the
/// game's frame. This is what keeps the game's fps intact.
///
/// The game's device rejects keyed-mutex sharing (E_INVALIDARG), so synchronization is
/// done by double buffering instead: two legacy shared textures, the producer alternates
/// between them and publishes the index of the last fully written one. The consumer only
/// ever reads the published texture, never the one being written. The Flush after the
/// copy makes sure the GPU work is submitted before the index is published.
/// </summary>
internal sealed unsafe class SharedFrameBridge : IDisposable
{
    private ID3D11Device* device; // game device, not owned
    private ID3D11Texture2D* texA;
    private ID3D11Texture2D* texB;
    private D3D11_TEXTURE2D_DESC desc;

    private nint handleA;
    private nint handleB;
    private int generation;
    private int publishedIndex = -1;
    private int writeIndex;
    private int recreateCooldown;

    public void Initialize(ID3D11Device* gameDevice) => device = gameDevice;

    /// <summary>Index of the texture the consumer may read right now (-1 = none yet).</summary>
    public int PublishedIndex => Volatile.Read(ref publishedIndex);

    /// <summary>Consumer-side snapshot. Generation changes whenever the handles change.</summary>
    public int Snapshot(out nint h0, out nint h1)
    {
        var gen = Volatile.Read(ref generation);
        h0 = Volatile.Read(ref handleA);
        h1 = Volatile.Read(ref handleB);
        return gen;
    }

    /// <summary>Copies the source into the back texture and publishes it. Never blocks.</summary>
    public bool Publish(ID3D11DeviceContext* context, ID3D11Texture2D* source)
    {
        if (device == null || source == null)
            return false;

        D3D11_TEXTURE2D_DESC srcDesc;
        source->GetDesc(&srcDesc);
        if (srcDesc.SampleDesc.Count != 1)
            return false; // shared textures cannot be MSAA

        if (texA == null || texB == null || srcDesc.Width != desc.Width || srcDesc.Height != desc.Height || srcDesc.Format != desc.Format)
        {
            if (recreateCooldown > 0)
            {
                recreateCooldown--;
                return false;
            }
            Recreate(&srcDesc);
            if (texA == null || texB == null)
            {
                recreateCooldown = 300; // creation failed; do not retry (and spam the log) every frame
                return false;
            }
        }

        var target = writeIndex == 0 ? texA : texB;
        context->CopyResource((ID3D11Resource*)target, (ID3D11Resource*)source);
        // Submit the copy before announcing it; the consumer reads from another device.
        context->Flush();

        Volatile.Write(ref publishedIndex, writeIndex);
        writeIndex = 1 - writeIndex;
        return true;
    }

    public void Dispose()
    {
        ReleaseTextures();
        device = null;
    }

    private void Recreate(D3D11_TEXTURE2D_DESC* sourceDesc)
    {
        ReleaseTextures();

        var d = *sourceDesc;
        d.MipLevels = 1;
        d.ArraySize = 1;
        d.Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT;
        d.BindFlags = (uint)(D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET);
        d.CPUAccessFlags = 0;
        d.MiscFlags = (uint)D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED;

        if (!TryCreate(&d, ref texA, out var hA) || !TryCreate(&d, ref texB, out var hB))
        {
            ReleaseTextures();
            Plugin.Log.Error("[bridge] shared texture pair creation failed; output stays blank");
            return;
        }

        desc = d;
        Volatile.Write(ref handleA, hA);
        Volatile.Write(ref handleB, hB);
        Volatile.Write(ref publishedIndex, -1);
        writeIndex = 0;
        Volatile.Write(ref generation, generation + 1);
        Plugin.Log.Information($"[bridge] shared texture pair ready: {d.Width}x{d.Height} fmt={d.Format}");
    }

    private bool TryCreate(D3D11_TEXTURE2D_DESC* d, ref ID3D11Texture2D* texture, out nint handle)
    {
        handle = 0;

        fixed (ID3D11Texture2D** tex = &texture)
        {
            var hr = device->CreateTexture2D(d, null, tex);
            if (FAILED(hr))
            {
                Plugin.Log.Error($"[bridge] CreateTexture2D(shared) failed: 0x{(int)hr:X8}");
                texture = null;
                return false;
            }
        }

        IDXGIResource* resource = null;
        if (FAILED(texture->QueryInterface(__uuidof<IDXGIResource>(), (void**)&resource)))
        {
            Plugin.Log.Error("[bridge] QueryInterface IDXGIResource failed");
            return false;
        }
        HANDLE h = default;
        var hrHandle = resource->GetSharedHandle(&h);
        resource->Release();
        if (FAILED(hrHandle))
        {
            Plugin.Log.Error($"[bridge] GetSharedHandle failed: 0x{(int)hrHandle:X8}");
            return false;
        }

        handle = (nint)h.Value;
        return true;
    }

    private void ReleaseTextures()
    {
        if (texA != null)
        {
            texA->Release();
            texA = null;
        }
        if (texB != null)
        {
            texB->Release();
            texB = null;
        }
        Volatile.Write(ref handleA, 0);
        Volatile.Write(ref handleB, 0);
        Volatile.Write(ref publishedIndex, -1);
        Volatile.Write(ref generation, generation + 1);
        desc = default;
    }
}
