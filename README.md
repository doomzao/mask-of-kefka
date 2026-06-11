# Mask of Kefka

A [Dalamud](https://dalamud.dev) plugin for FFXIV that creates a **second window with a clean image of the game**: no ImGui, no plugin overlays, no Dalamud at all. Capture that window in OBS and stream with all your quality-of-life plugins without showing them.

Inspired by [MaskedCarnivale (ProjectMimer)](https://github.com/ProjectMimer/MaskedCarnivale), rewritten from scratch for current Dalamud.

## Features

- Clean mirror of the game (scene + game UI) in a separate window, free of any Dalamud overlay.
- Optional no-UI mode: shows only the scene, for immersive recordings.
- Borderless mode: hides the title bar for a clean capture. Drag the window from anywhere inside it and resize from the edges.
- Output rate limiter: render 1 frame every N to cut the GPU cost of the output.
- Plugins that modify the game content itself (Penumbra and similar) remain visible, since they change the scene rather than draw overlays.

## Commands

| Command | Effect |
|---|---|
| `/kefka` | Opens the configuration window |
| `/kefka on` / `/kefka off` | Starts/stops the output window |
| `/kefka ui` | Toggles the game UI in the output |

## Capturing in OBS

1. Enable the output window (`/kefka on`).
2. In OBS, add a **Window Capture** source and select the `Mask of Kefka` window.
3. Use the **Windows 10 (1903 and up)** capture method (Windows Graphics Capture).

Do not use Game Capture: it hooks the game process and captures the main window, overlays included.

The same applies to Discord screen sharing and similar tools: share the `Mask of Kefka` window, not the game. Keep the window unminimized (it can sit behind other windows).

Tips:

- To show your cursor on stream, enable the software cursor in the game settings. The hardware cursor does not appear in the capture.
- Enable the borderless option in `/kefka` so the title bar does not show up in the capture.

## Installing

Requirements: XIVLauncher with Dalamud, and the .NET 10 SDK to build.

```powershell
dotnet build -c Release
```

Then, in game:

1. `/xlsettings`, then the **Experimental** tab, then **Dev Plugin Locations**.
2. Add the path `...\mask-of-kefka\MaskOfKefka\bin\Release\MaskOfKefka.dll` and save.
3. `/xlplugins`, then **Dev Tools**, then **Installed Dev Plugins**, and enable Mask of Kefka.

## Known limitations

- **HDR**: the output window is SDR. With the game in HDR the colors may look washed out; prefer SDR for streaming.
- The Dalamud button that the game itself draws in the system menu shows up in the output, because it is part of the game UI. Disable it in the Dalamud settings if it bothers you.
- The no-UI mode depends on a render target index that changes between game patches. It is reconfigurable in the plugin settings, no rebuild needed.

## License

[AGPL-3.0-or-later](LICENSE)
