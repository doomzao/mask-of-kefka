using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MaskOfKefka.Output;
using MaskOfKefka.Windows;

namespace MaskOfKefka;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/kefka";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Config { get; }

    /// <summary>
    /// Desired state of the output window. The actual creation/destruction happens in OnDraw
    /// (render thread), so any thread can set this without a lock.
    /// </summary>
    public bool OutputRequested { get; set; }

    public bool OutputActive => session is { WindowClosed: false };

    private readonly WindowSystem windowSystem = new("MaskOfKefka");
    private readonly ConfigWindow configWindow;

    private OutputSession? session;
    private int consecutiveRenderErrors;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens Mask of Kefka. Subcommands: on, off, ui.",
        });

        // The output must keep updating even while Dalamud hides its UI
        // (cutscenes, gpose, hidden game UI), otherwise the stream freezes.
        PluginInterface.UiBuilder.DisableAutomaticUiHide = true;
        PluginInterface.UiBuilder.DisableUserUiHide = true;
        PluginInterface.UiBuilder.DisableCutsceneUiHide = true;
        PluginInterface.UiBuilder.DisableGposeUiHide = true;

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;

        if (Config.AutoStart)
            OutputRequested = true;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        CommandManager.RemoveHandler(CommandName);
        windowSystem.RemoveAllWindows();

        // Draw is already unsubscribed, so nothing else is using the session.
        session?.Dispose();
        session = null;
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "on" or "enable":
                OutputRequested = true;
                break;
            case "off" or "disable":
                OutputRequested = false;
                break;
            case "ui":
                Config.ShowGameUi = !Config.ShowGameUi;
                Config.Save();
                break;
            default:
                configWindow.Toggle();
                break;
        }
    }

    /// <summary>
    /// Runs on the render thread, inside the game's present: the only safe place to touch
    /// the D3D11 device without extra synchronization.
    /// </summary>
    private void OnDraw()
    {
        windowSystem.Draw();

        if (session is { WindowClosed: true })
            OutputRequested = false;

        if (OutputRequested && session == null)
        {
            try
            {
                session = OutputSession.Start(Log, Config);
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to open the output window.");
                OutputRequested = false;
                return;
            }
        }
        else if (!OutputRequested && session != null)
        {
            session.Dispose();
            session = null;
            return;
        }

        if (session == null)
            return;

        try
        {
            session.RenderFrame(Config);
            consecutiveRenderErrors = 0;
        }
        catch (Exception e)
        {
            if (++consecutiveRenderErrors >= 10)
            {
                Log.Error(e, "Too many consecutive errors rendering the output; shutting it down.");
                OutputRequested = false;
            }
        }
    }
}
