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
        if (ImGui.Checkbox("Janela de saída ativa", ref active))
            plugin.OutputRequested = active;
        ImGui.TextDisabled("No OBS, use 'Captura de Janela' (método Windows 10) na janela \"Mask of Kefka\".");

        ImGui.Separator();

        var showUi = config.ShowGameUi;
        if (ImGui.Checkbox("Mostrar a UI do jogo na saída", ref showUi))
        {
            config.ShowGameUi = showUi;
            config.Save();
        }

        if (!config.ShowGameUi)
        {
            ImGui.TextWrapped(
                "Modo sem UI: escolha o índice da render target que contém a cena. " +
                "Esse índice muda a cada patch do jogo — ajuste até a janela de saída mostrar a cena sem UI. " +
                "Enquanto o índice for inválido, a saída cai no modo com UI.");

            var index = config.RenderTargetIndex;
            if (ImGui.InputInt("Índice da render target", ref index))
            {
                config.RenderTargetIndex = Math.Clamp(index, -1, GameSources.RenderTargetSlotCount - 1);
                config.Save();
            }

            ImGui.TextDisabled($"Intervalo: 0 a {GameSources.RenderTargetSlotCount - 1}. Use -1 para desativar.");

            if (ImGui.Button("Listar render targets no log"))
                GameSources.DumpRenderTargets(Plugin.Log);
            ImGui.SameLine();
            ImGui.TextDisabled("(abra com /xllog)");
        }

        ImGui.Separator();

        var borderless = config.Borderless;
        if (ImGui.Checkbox("Janela sem borda (esconde a barra de título)", ref borderless))
        {
            config.Borderless = borderless;
            config.Save();
        }
        if (config.Borderless)
            ImGui.TextDisabled("Arraste segurando qualquer ponto da janela; redimensione pelas bordas (8px).");

        var everyNth = config.RenderEveryNthFrame;
        if (ImGui.SliderInt("Renderizar 1 frame a cada", ref everyNth, 1, 4))
        {
            config.RenderEveryNthFrame = Math.Clamp(everyNth, 1, 4);
            config.Save();
        }
        ImGui.TextDisabled(config.RenderEveryNthFrame == 1
            ? "Saída na mesma taxa do jogo (custo máximo, fluidez máxima)."
            : $"Jogo a 60 fps -> saída a {60 / config.RenderEveryNthFrame} fps. Custo de GPU da saída cai na mesma proporção.");

        ImGui.Separator();

        var autoStart = config.AutoStart;
        if (ImGui.Checkbox("Abrir a janela de saída ao carregar o plugin", ref autoStart))
        {
            config.AutoStart = autoStart;
            config.Save();
        }
    }
}
