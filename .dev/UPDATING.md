# Update guide (game patch / Dalamud update)

Maintainer notes. Users do not need anything in this folder.

This file covers **fixing the plugin** when the game or Dalamud changes. For **shipping a release to users** (version bump, GitHub release, repo.json), see `.dev/RELEASING.md` in the [doomzao/plugins](https://github.com/doomzao/plugins) repository.

This plugin was designed to break as little as possible, but FFXIV plus Dalamud is a moving target. This guide lists **everything** the plugin depends on, **where** each dependency lives in the code, and **how** to find it again when it breaks.

## TL;DR: what to do after each patch

```
1. Launch the game through XIVLauncher and let Dalamud update itself.
2. Plugin loads and the output works          -> do nothing. (common case)
3. Plugin no longer compiles                  -> "Dalamud bump" section below.
4. Compiles but crashes or shows a black window -> "FFXIVClientStructs" section below.
5. Only the no-UI mode shows the wrong thing  -> "Render target index" section (no rebuild needed).
```

## Map of patch-sensitive dependencies

| Dependency | Maintained by | Where it lives | Breaks when |
|---|---|---|---|
| `Dalamud.NET.Sdk/15.0.0` | goatcorp | `MaskOfKefka/MaskOfKefka.csproj` (line 1) | Dalamud bumps its major version (new API level) |
| Dalamud API (`UiBuilder`, `Window`, `IPluginLog`, ImGui bindings) | goatcorp | `Plugin.cs`, `Windows/ConfigWindow.cs` | Breaking changes in a major Dalamud release |
| `Device.Instance()`, `D3D11Forwarder`, `D3D11DeviceContext`, `SwapChain` | FFXIVClientStructs (community) | `Capture/GameSources.cs`, `Output/OutputSession.cs` | Rarely; the community updates the offsets for us |
| `SwapChain.BackBuffer`, `.Width`, `.Height` | FFXIVClientStructs | `Capture/GameSources.cs`, `Output/OutputSession.cs` | Same |
| `Texture.D3D11Texture2D`, `.D3D11ShaderResourceView`, `.ActualWidth/Height` | FFXIVClientStructs | `Capture/GameSources.cs` | Same |
| Offset `0x20` of the render target table | **us** | `GameSources.RenderTargetTableOffset` | If the `RenderTargetManager` layout changes |
| The no-UI render target index | **user, at runtime** | Plugin config (UI), `Configuration.RenderTargetIndex` | Practically every graphics patch |
| TerraFX.Interop.Windows | goatcorp (ships it) | `MaskOfKefka.csproj` (HintPath) | Only if Dalamud stops shipping it |

Note what is **not** on this list: memory signatures ("sigs"). The plugin has none. That was the most fragile part of the original MaskedCarnivale.

## 1. Dalamud bump (new API level)

**Symptom**: Dalamud disables the plugin for an outdated API level, or `dotnet build` fails with API errors.

Since Dalamud v9, API level = major version (Dalamud 16 means API 16). For each major:

1. Find the new version: <https://dalamud.dev/versions/> (or check the SDK version in [SamplePlugin](https://github.com/goatcorp/SamplePlugin/blob/master/SamplePlugin/SamplePlugin.csproj)).
2. In `MaskOfKefka.csproj`, update the first line:
   ```xml
   <Project Sdk="Dalamud.NET.Sdk/16.0.0">
   ```
3. Check whether the .NET version changed (the "What's New in vXX" page on dalamud.dev always says; v14/v15 use .NET 10). If it changed: `winget install Microsoft.DotNet.SDK.XX`.
4. `dotnet build -c Release` and fix errors using the **breaking changes** listed at `https://dalamud.dev/versions/vXX/`.

Dalamud surfaces we use (deliberately few):

- `IDalamudPlugin`, `[PluginService]`, `IDalamudPluginInterface`, `ICommandManager`, `IPluginLog`
- `UiBuilder.Draw`, `OpenConfigUi`, `OpenMainUi` and the `Disable*UiHide` flags
- `Dalamud.Interface.Windowing` (`Window`, `WindowSystem`)
- `Dalamud.Bindings.ImGui` (Checkbox, InputInt, SliderInt, Text*, Button, Separator)

## 2. FFXIVClientStructs (game fields)

**Symptom**: compiles, but crashes when enabling the output, or the window stays black.

Good news: the people who find the new offsets after each patch are the FFXIVClientStructs community, and Dalamud already ships the updated version. Our only risk is members we use being **renamed or changed**. Check each one here:

| Member used | File in the [aers/FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) repo |
|---|---|
| `Device.Instance()`, `Device.SwapChain`, `Device.D3D11Forwarder`, `Device.D3D11DeviceContext` | `FFXIVClientStructs/FFXIV/Client/Graphics/Kernel/Device.cs` |
| `SwapChain.BackBuffer`, `SwapChain.Width`, `SwapChain.Height` | `FFXIVClientStructs/FFXIV/Client/Graphics/Kernel/SwapChain.cs` |
| `Texture.D3D11Texture2D`, `Texture.D3D11ShaderResourceView`, `Texture.ActualWidth`, `Texture.ActualHeight`, `Texture.TextureFormat` | `FFXIVClientStructs/FFXIV/Client/Graphics/Kernel/Texture.cs` |
| `RenderTargetManager.Instance()` plus the struct size | `FFXIVClientStructs/FFXIV/Client/Graphics/Render/RenderTargetManager.cs` |

If a field disappeared or was renamed, the compile error points at the exact spot; fix the name in `Capture/GameSources.cs` / `Output/OutputSession.cs`.

### The `0x20` offset (render target table)

`GameSources.RenderTargetTableOffset = 0x20` assumes the `Texture*` pointers in `RenderTargetManager` form a contiguous table starting at `+0x20` (currently the `_gBuffers` field). **How to validate**: open `RenderTargetManager.cs` in the repo and check the offset of the first `Texture*` field or texture array. If it moved, update the constant. This offset has been stable for years, but it is the only layout assumption that is **ours**, which is why it is isolated in a documented constant.

## 3. The no-UI render target index

**Symptom**: the no-UI mode shows the wrong texture (shadow map, depth, black screen). The UI mode keeps working since it uses no index at all.

No code needed. In game:

1. `/kefka`, then untick "Show the game UI in the output".
2. Click **"List render targets to log"** and open `/xllog`: every plausible slot is listed with index, resolution and format. The candidates have **your screen's resolution** and `srv=yes`.
3. Try the candidate indices in the "Render target index" field while watching the output window. When the scene shows up without UI, that is the one. The config saves itself.

(For reference: in the original plugin's era it was 71; these indices shift whenever Square Enix touches the graphics pipeline.)

## 4. Structural Dalamud changes worth watching

Items that, if they ever change, call for a design review rather than a rename:

- **`UiBuilder.Draw` no longer running on the render thread / inside present**: the plugin's entire threading model assumes it (see `ARCHITECTURE.md`). It has never changed in Dalamud's history, but if it does, look for the new render callback in the docs.
- **Dalamud no longer shipping `TerraFX.Interop.Windows.dll`**: check `%APPDATA%\XIVLauncher\addon\Hooks\dev\`. Fix: add the `TerraFX.Interop.Windows` NuGet package with `Private=true`.
- **The game moving to DX12**: the renderer (swapchain/copy/shader) would need a rewrite; the concept (copy the backbuffer before ImGui) stays valid.

## Sources to follow

- Dalamud versions and breaking changes: <https://dalamud.dev/versions/>
- Per-release news: `https://dalamud.dev/versions/vXX/`
- FFXIVClientStructs: <https://github.com/aers/FFXIVClientStructs>
- Reference template (always updated for the new SDK): <https://github.com/goatcorp/SamplePlugin>
- The goatcorp Discord (#plugin-dev channel), for when a patch is big and everything is in flux.
