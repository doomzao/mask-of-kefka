# Mask of Kefka

Plugin de [Dalamud](https://dalamud.dev) para FFXIV que cria uma **segunda janela com a imagem limpa do jogo** — sem ImGui, sem overlays de plugins, sem o próprio Dalamud — para você capturar no OBS e streamar com qualidade de vida sem expor seus mods.

Reimplementação moderna do conceito do [MaskedCarnivale (ProjectMimer)](https://github.com/ProjectMimer/MaskedCarnivale), reescrita do zero para o Dalamud v15 / API 15 / .NET 10, com uma arquitetura bem mais simples (veja [docs/ARQUITETURA.md](docs/ARQUITETURA.md)).

## Como funciona (resumo)

A cada frame, ainda na thread de render do jogo, o plugin copia o **backbuffer antes do Dalamud desenhar o ImGui** e o apresenta numa segunda janela nativa (`Mask of Kefka`) criada dentro do próprio processo do jogo. O OBS captura essa janela.

- **Modo padrão (com UI do jogo)**: cena + UI nativa do jogo, sem nenhum overlay do Dalamud. Robusto a patches — não depende de offsets.
- **Modo sem UI** (opcional): mostra só a cena, lendo uma render target interna do jogo. O índice dessa render target muda a cada patch e é configurável em runtime (sem recompilar).

> Assim como o original: plugins que modificam o **conteúdo** do jogo (Penumbra, etc.) continuam visíveis — eles alteram a cena em si, não são overlays.

## Comandos

| Comando | Efeito |
|---|---|
| `/kefka` | Abre a janela de configuração |
| `/kefka on` / `/kefka off` | Liga/desliga a janela de saída |
| `/kefka ui` | Alterna a UI do jogo na saída |

## Capturando no OBS

1. Ative a janela de saída (`/kefka on`).
2. No OBS, adicione uma fonte **Captura de Janela** e selecione a janela `Mask of Kefka`.
3. Use o método de captura **Windows 10 (1903 e mais recentes)** (Windows Graphics Capture).

Não use "Captura de Jogo" — ela hooka o processo do jogo e captura a janela principal (com os overlays).

Dica: para mostrar o cursor na stream, ative o cursor por software nas configurações do jogo (o cursor de hardware não aparece na cópia do backbuffer).

Opções úteis na config (`/kefka`):

- **Janela sem borda** — esconde a barra de título pra captura ficar limpa (Discord compartilha a janela inteira, incluindo a barra). Sem borda, arraste a janela segurando qualquer ponto dela e redimensione pelas bordas.
- **Renderizar 1 frame a cada N** — reduz o custo de GPU da saída na mesma proporção (jogo a 60 fps com N=2 → stream a 30 fps).

## Build

Requisitos:

- .NET 10 SDK
- XIVLauncher com Dalamud instalado (o SDK acha as DLLs em `%APPDATA%\XIVLauncher\addon\Hooks\dev\`)

```powershell
dotnet build -c Release
```

Saída: `MaskOfKefka\bin\Release\MaskOfKefka.dll` (e `MaskOfKefka\latest.zip` empacotado pelo DalamudPackager).

## Instalando como dev plugin

1. No jogo: `/xlsettings` → aba **Experimental** → **Dev Plugin Locations**.
2. Adicione o caminho `...\mask-of-kefka\MaskOfKefka\bin\Release\MaskOfKefka.dll` e salve.
3. `/xlplugins` → aba **Dev Tools** → **Installed Dev Plugins** → habilite o Mask of Kefka.

## Quebrou depois de um patch?

Veja **[docs/ATUALIZACAO-POS-PATCH.md](docs/ATUALIZACAO-POS-PATCH.md)** — o guia cobre cada dependência sensível a patch, onde ela mora no código e como reencontrá-la.

## Limitações conhecidas

- **HDR**: a janela de saída é SDR (`B8G8R8A8`). Com o jogo em HDR as cores podem sair lavadas; prefira SDR pra stream.
- O botão/menu do Dalamud que o **jogo** desenha (no menu de sistema) aparece na saída, porque faz parte da UI do jogo — desative-o nas configurações do Dalamud se incomodar.
- O modo "sem UI" depende de um índice de render target que muda por patch (reconfigurável na UI, sem recompilar).

## Licença

[AGPL-3.0-or-later](LICENSE) — mesma licença padrão do ecossistema de plugins do Dalamud.
