# Architecture

Maintainer notes. Users do not need anything in this folder.

## Goal

Mirror the game's frame **before** Dalamud draws anything, into a separate window that OBS captures. The streamer sees every plugin on the main window; the stream only sees the game.

## Why not copy the original MaskedCarnivale?

The original (ProjectMimer/MaskedCarnivale) worked, but had three sources of fragility and friction:

| Original | Mask of Kefka | Gain |
|---|---|---|
| Signature hook on `DXGIPresent` (`E8 ?? ?? ?? ?? C6 43 79 00`) | No hooks: renders inside Dalamud's `UiBuilder.Draw` | The signature was the item that broke most often per patch; now Dalamud maintains that point for us |
| Separate C++ process (`outputwindow.exe`) plus shared texture plus shared memory | **In-process** Win32 window with a second swapchain on the same D3D11 device | No IPC, no extra exe to distribute, no handle synchronization |
| SharpDX (abandoned since 2019) | TerraFX.Interop.Windows (shipped with Dalamud itself) | Zero external dependencies; always compatible with the running Dalamud |
| Render target indices hardcoded in source (107 with UI / 71 without) | Default mode uses the **backbuffer** (no offsets at all); the no-UI mode index is configurable at runtime | A patch does not require a rebuild; the common case does not even require reconfiguring |

## Per-frame flow

```
Game renders the frame (scene + game UI) --> backbuffer ready
        |
        v
Game calls Present --> Dalamud intercepts and fires UiBuilder.Draw   <-- we are here
        |                 |
        |                 +- 1. pick the source:
        |                 |      * with UI: backbuffer (Device->SwapChain->BackBuffer)
        |                 |      * no UI:   Texture* from RenderTargetManager (configured index)
        |                 +- 2. CopyResource into an intermediate texture (when the source has no SRV)
        |                 +- 3. SwapDeviceContextState (isolates ALL pipeline state)
        |                 +- 4. draw a fullscreen triangle into the output window's swapchain
        |                 +- 5. Present(0, 0) on the output swapchain
        |                 +- 6. SwapDeviceContextState back (game/ImGui untouched)
        v
Dalamud draws ImGui on the backbuffer --> real Present --> main window (with overlays)
```

The key point: at `UiBuilder.Draw` time the backbuffer contains **game + game UI**, but ImGui has **not** been drawn yet. That is the exact time window to capture the clean image.

## Threads

| Thread | What it does | File |
|---|---|---|
| Game render | All D3D11 work: create/destroy the session, copy, draw, present. The lifecycle is driven by the `Plugin.OutputRequested` flag, which is why there are no locks | `Plugin.OnDraw`, `OutputSession`, `OutputRenderer` |
| `MaskOfKefka.OutputWindow` | Only the Win32 message pump of the output window (move, resize, close). Never touches D3D | `OutputWindowHost` |
| Any | May set `OutputRequested` (command, config); the actual change happens on the next frame | `Plugin` |

## D3D state isolation

Drawing "in the middle" of the game's frame requires not dirtying pipeline state (neither the game's nor Dalamud's ImGui renderer's). Instead of saving/restoring dozens of slots manually, we use `ID3DDeviceContextState` plus `SwapDeviceContextState` (D3D11.1): it swaps **all** context state at once and hands it back at the end. The original did not do this; it trusted the game to reset everything each frame.

## Smaller decisions

- **Fullscreen triangle via `SV_VertexID`**: no vertex buffer, no input layout, fewer objects to create or leak.
- **Shaders compiled at runtime** (`D3DCompile`): no HLSL build step; the shader source lives in the `.cs` file.
- **Alpha forced to 1.0 in the pixel shader**: game render targets carry arbitrary alpha; without this, WGC capture can come out translucent.
- **`DisableUserUiHide`/`DisableCutsceneUiHide`/etc.**: `UiBuilder.Draw` must keep firing during cutscenes/gpose/hidden UI, otherwise the stream freezes.
- **Pointer validation in the render target scan**: the `RenderTargetManager` table mixes pointers with scalar fields; dereferencing garbage crashes the game. We only follow canonical aligned pointers (see `GameSources.LooksLikePointer`).
- **FLIP_DISCARD swapchain with `Present(0, 0)`**: never blocks the game's frame waiting on the output window's vsync.
