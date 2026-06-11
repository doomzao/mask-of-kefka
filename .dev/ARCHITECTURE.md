# Architecture

Maintainer notes. Users do not need anything in this folder.

## Goal

Mirror the game's frame **before** Dalamud draws anything, into a separate window that OBS captures. The streamer sees every plugin on the main window; the stream only sees the game.

## The core idea

At `UiBuilder.Draw` time the backbuffer contains **game + game UI**, but ImGui has **not** been drawn yet. That is the exact time window to grab the clean image. No hooks, no signatures: Dalamud maintains that callback for us.

## The two-device design (and why it exists)

The first implementation drew and presented the mirror window directly inside `UiBuilder.Draw`, on the game's device. It worked, but presenting a visible window from inside the game's frame blocked for ~4.3 ms per frame in DWM composition, cutting the game's fps hard (measured: `Present` 4.3 ms, everything else 0.01 ms). No present flag fixes that; the wait is the compositor's.

So presentation was moved off the game's path entirely:

```
Game render thread (inside UiBuilder.Draw):
    pick source texture
      * with UI: backbuffer (Device->SwapChain->BackBuffer)
      * no UI:   Texture* from RenderTargetManager (configured index)
    CopyResource into shared texture A or B (alternating) + Flush
    publish the index of the texture just written          <- total cost ~0.0 ms

Output window thread (own message pump, own D3D11 device on the same adapter):
    open shared textures A and B (legacy DXGI shared handles)
    loop: pump messages, draw the published texture to its swapchain, Present(1, 0)
```

The game thread never waits on anything; the window thread can block on vsync freely.

## Synchronization: double buffer, no keyed mutex

The game's device rejects `D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX` (and the NT-handle variant) with E_INVALIDARG, so keyed mutexes are not an option here. Instead:

- Two legacy shared textures (`D3D11_RESOURCE_MISC_SHARED`), producer alternates between them.
- The producer copies, calls `Flush()` (so the GPU work is submitted), then publishes the index with a volatile write.
- The consumer only ever samples the published texture, never the one being written.

Sharing the same texture unsynchronized crashed the game (device removed); the double buffer is what makes the unsynchronized mode safe.

## The visible-window cost (known issue, by design guidance)

Even with presentation fully decoupled, a *visible* output window still costs game fps on some setups (measured on an RTX 4080: 60 -> 40 fps, independent of window size or monitor). The cost is systemic: DWM composes the extra window every frame and the game window can lose its optimized fullscreen presentation path. Minimized, the cost drops to ~zero, and on current Windows 11 builds WGC capture keeps updating minimized windows.

Decision: instead of fighting the compositor, the product guidance is to **minimize the window while streaming**. The UI warns about it, offers a "Minimize output window" button, and a "start minimized" option. The README documents the behavior, including the fallback for older Windows builds where minimized capture freezes (keep the window small).

## Threads

| Thread | What it does | File |
|---|---|---|
| Game render | Source pick + CopyResource + Flush + publish index. Session lifecycle, driven by `Plugin.OutputRequested` | `Plugin.OnDraw`, `OutputSession`, `SharedFrameBridge` |
| `MaskOfKefka.OutputWindow` | Win32 message pump plus the entire presentation: own device, swapchain, draw loop, vsync present | `OutputWindowHost` |
| Any | May set `OutputRequested` (command, config); the actual change happens on the next frame | `Plugin` |

## Smaller decisions

- **Fullscreen triangle via `SV_VertexID`**: no vertex buffer, no input layout, fewer objects to create or leak.
- **Shaders compiled at runtime** (`D3DCompile`): no HLSL build step; the shader source lives in the `.cs` file.
- **Alpha forced to 1.0 in the pixel shader**: game render targets carry arbitrary alpha; without this, WGC capture can come out translucent.
- **`DisableUserUiHide`/`DisableCutsceneUiHide`/etc.**: `UiBuilder.Draw` must keep firing during cutscenes/gpose/hidden UI, otherwise the stream freezes.
- **Pointer validation in the render target scan**: the `RenderTargetManager` table mixes pointers with scalar fields; dereferencing garbage crashes the game. We only follow canonical aligned pointers (see `GameSources.LooksLikePointer`).
- **Creation failure cooldown in the bridge**: shared texture creation failures retry every ~300 frames instead of every frame, so a hostile desc cannot spam the log or the driver.
- **The output device is created on the game's adapter** (`D3D_DRIVER_TYPE_UNKNOWN` + explicit `IDXGIAdapter`): legacy shared handles only work across devices on the same adapter.
