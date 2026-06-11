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
    /// Estado desejado da janela de saída. A criação/destruição real acontece no OnDraw
    /// (thread de render), então qualquer thread pode setar isso sem lock.
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
            HelpMessage = "Abre o Mask of Kefka. Subcomandos: on, off, ui.",
        });

        // A saída precisa continuar atualizando mesmo quando o Dalamud esconde a UI
        // (cutscenes, gpose, UI do jogo oculta) — senão a stream congela.
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

        // O Draw já foi desinscrito, então ninguém mais usa a sessão.
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
    /// Roda na thread de render, dentro do present do jogo — o único lugar seguro pra
    /// mexer no device D3D11 sem sincronização extra.
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
                Log.Error(e, "Falha ao abrir a janela de saída.");
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
                Log.Error(e, "Erros consecutivos demais renderizando a saída; desligando.");
                OutputRequested = false;
            }
        }
    }
}
