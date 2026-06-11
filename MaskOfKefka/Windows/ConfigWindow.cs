using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MaskOfKefka.Capture;

namespace MaskOfKefka.Windows;

public class ConfigWindow : Window
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin) : base("Mask of Kefka###MaskOfKefkaConfig")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 260),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        var config = plugin.Config;

        var active = plugin.OutputRequested;
        if (ImGui.Checkbox("Output window active", ref active))
            plugin.OutputRequested = active;
        ImGui.TextDisabled("In OBS, use 'Window Capture' (Windows 10 method) on the \"Mask of Kefka\" window.");

        ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f), "Performance: keep the output window MINIMIZED while streaming.");
        ImGui.TextWrapped(
            "A visible output window can cost a lot of game fps (Windows composes it every frame). " +
            "Set up your capture, then minimize the window: the cost drops to zero and, on current " +
            "Windows 11, OBS/Discord keep capturing it normally. If your capture freezes while " +
            "minimized (older Windows), keep the window small instead.");
        if (plugin.OutputActive && ImGui.Button("Minimize output window"))
            plugin.MinimizeOutput();

        ImGui.Separator();

        var showUi = config.ShowGameUi;
        if (ImGui.Checkbox("Show the game UI in the output", ref showUi))
        {
            config.ShowGameUi = showUi;
            config.Save();
        }

        if (!config.ShowGameUi)
        {
            ImGui.TextWrapped(
                "No-UI mode: pick the render target index that holds the scene. " +
                "This index changes with every game patch. Adjust it until the output window shows the scene without UI. " +
                "While the index is invalid, the output falls back to the UI mode.");

            var index = config.RenderTargetIndex;
            if (ImGui.InputInt("Render target index", ref index))
            {
                config.RenderTargetIndex = Math.Clamp(index, -1, GameSources.RenderTargetSlotCount - 1);
                config.Save();
            }

            ImGui.TextDisabled($"Range: 0 to {GameSources.RenderTargetSlotCount - 1}. Use -1 to disable.");

            if (ImGui.Button("List render targets to log"))
                GameSources.DumpRenderTargets(Plugin.Log);
            ImGui.SameLine();
            ImGui.TextDisabled("(open with /xllog)");
        }

        ImGui.Separator();

        var borderless = config.Borderless;
        if (ImGui.Checkbox("Borderless window (hides the title bar)", ref borderless))
        {
            config.Borderless = borderless;
            config.Save();
        }
        if (config.Borderless)
            ImGui.TextDisabled("Drag from anywhere inside the window; resize from the edges (8px).");

        var everyNth = config.RenderEveryNthFrame;
        if (ImGui.SliderInt("Render 1 frame every", ref everyNth, 1, 4))
        {
            config.RenderEveryNthFrame = Math.Clamp(everyNth, 1, 4);
            config.Save();
        }
        ImGui.TextDisabled(config.RenderEveryNthFrame == 1
            ? "Output at the same rate as the game (max cost, max smoothness)."
            : $"Game at 60 fps -> output at {60 / config.RenderEveryNthFrame} fps. Output GPU cost drops proportionally.");

        ImGui.Separator();

        var startMinimized = config.StartMinimized;
        if (ImGui.Checkbox("Open the output window minimized (recommended)", ref startMinimized))
        {
            config.StartMinimized = startMinimized;
            config.Save();
        }

        var autoStart = config.AutoStart;
        if (ImGui.Checkbox("Open the output window when the plugin loads", ref autoStart))
        {
            config.AutoStart = autoStart;
            config.Save();
        }
    }
}
