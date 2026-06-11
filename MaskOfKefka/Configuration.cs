using System;
using Dalamud.Configuration;

namespace MaskOfKefka;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Mostra a UI do jogo na janela de saída (modo padrão, robusto a patches).</summary>
    public bool ShowGameUi { get; set; } = true;

    /// <summary>
    /// Índice da render target usada no modo "sem UI". Depende do patch do jogo:
    /// redescubra pela janela de config após cada patch. -1 = nenhum (cai no modo com UI).
    /// </summary>
    public int RenderTargetIndex { get; set; } = -1;

    /// <summary>Abre a janela de saída automaticamente quando o plugin carrega.</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>
    /// Renderiza a saída 1 a cada N frames do jogo (1 = todo frame). Reduz o custo de GPU
    /// na mesma proporção; a stream fica com fps_do_jogo / N.
    /// </summary>
    public int RenderEveryNthFrame { get; set; } = 1;

    /// <summary>Janela de saída sem borda/barra de título (pra captura limpa na stream).</summary>
    public bool Borderless { get; set; } = false;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
