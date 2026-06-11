using System;
using Dalamud.Configuration;

namespace MaskOfKefka;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Show the game UI in the output window (default mode, robust across patches).</summary>
    public bool ShowGameUi { get; set; } = true;

    /// <summary>
    /// Render target index used by the "no UI" mode. Patch-dependent: rediscover it through
    /// the config window after each game patch. -1 = none (falls back to the UI mode).
    /// </summary>
    public int RenderTargetIndex { get; set; } = -1;

    /// <summary>Open the output window automatically when the plugin loads.</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>
    /// Render the output once every N game frames (1 = every frame). Cuts the GPU cost
    /// proportionally; the stream runs at game_fps / N.
    /// </summary>
    public int RenderEveryNthFrame { get; set; } = 1;

    /// <summary>Borderless output window, no title bar (for a clean stream capture).</summary>
    public bool Borderless { get; set; } = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
