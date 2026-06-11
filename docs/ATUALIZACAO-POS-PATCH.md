# Guia de atualização (patch do jogo / atualização do Dalamud)

Este plugin foi desenhado pra quebrar o mínimo possível, mas FFXIV + Dalamud é um alvo móvel. Este guia lista **tudo** de que o plugin depende, **onde** cada dependência mora no código e **como** reencontrá-la quando quebrar.

## TL;DR — o que fazer depois de cada patch

```
1. Abra o jogo pelo XIVLauncher e deixe o Dalamud se atualizar.
2. Se o plugin carregar e a saída funcionar  → não faça nada. (caso comum)
3. Se o plugin não compilar mais             → seção "Bump do Dalamud" abaixo.
4. Se compilar mas crashar/ficar preto       → seção "FFXIVClientStructs" abaixo.
5. Se só o modo "sem UI" mostrar coisa errada → seção "Índice da render target" (não precisa nem recompilar).
```

## Mapa de dependências sensíveis a patch

| Dependência | Quem mantém | Onde mora no código | Quebra quando |
|---|---|---|---|
| `Dalamud.NET.Sdk/15.0.0` | goatcorp | `MaskOfKefka/MaskOfKefka.csproj` (linha 1) | Dalamud sobe de versão major (novo API level) |
| API do Dalamud (`UiBuilder`, `Window`, `IPluginLog`, ImGui bindings) | goatcorp | `Plugin.cs`, `Windows/ConfigWindow.cs` | Breaking changes em release major do Dalamud |
| `Device.Instance()`, `D3D11Forwarder`, `D3D11DeviceContext`, `SwapChain` | FFXIVClientStructs (comunidade) | `Capture/GameSources.cs`, `Output/OutputSession.cs` | Raramente; a comunidade atualiza os offsets por nós |
| `SwapChain.BackBuffer`, `.Width`, `.Height` | FFXIVClientStructs | `Capture/GameSources.cs`, `Output/OutputSession.cs` | Idem |
| `Texture.D3D11Texture2D`, `.D3D11ShaderResourceView`, `.ActualWidth/Height` | FFXIVClientStructs | `Capture/GameSources.cs` | Idem |
| Offset `0x20` da tabela de render targets | **nós** | `GameSources.RenderTargetTableOffset` | Se o layout do `RenderTargetManager` mudar |
| Índice da render target "sem UI" | **usuário, em runtime** | Config do plugin (UI), `Configuration.RenderTargetIndex` | Praticamente todo patch gráfico |
| TerraFX.Interop.Windows | goatcorp (distribui junto) | `MaskOfKefka.csproj` (HintPath) | Só se o Dalamud parar de distribuí-la |

Repare no que **não** existe aqui: assinaturas de memória ("sigs"). O plugin não tem nenhuma — esse era o ponto mais frágil do MaskedCarnivale original.

## 1. Bump do Dalamud (novo API level)

**Sintoma**: o Dalamud desabilita o plugin por API level antigo, ou `dotnet build` falha com erros de API.

Desde o Dalamud v9, o API level = versão major (Dalamud 16 → API 16). A cada major:

1. Descubra a versão nova: <https://dalamud.dev/versions/> (ou veja a versão do SDK no [SamplePlugin](https://github.com/goatcorp/SamplePlugin/blob/master/SamplePlugin/SamplePlugin.csproj)).
2. No `MaskOfKefka.csproj`, atualize a primeira linha:
   ```xml
   <Project Sdk="Dalamud.NET.Sdk/16.0.0">
   ```
3. Confira se a versão do .NET mudou (a página "What's New in vXX" do dalamud.dev sempre informa; v14/v15 usam .NET 10). Se mudou: `winget install Microsoft.DotNet.SDK.XX`.
4. `dotnet build -c Release` e corrija os erros guiando-se pelas **breaking changes** listadas em `https://dalamud.dev/versions/vXX/`.

Superfícies do Dalamud que usamos (pouca coisa, de propósito):

- `IDalamudPlugin`, `[PluginService]`, `IDalamudPluginInterface`, `ICommandManager`, `IPluginLog`
- `UiBuilder.Draw`, `OpenConfigUi`, `OpenMainUi` e as flags `Disable*UiHide`
- `Dalamud.Interface.Windowing` (`Window`, `WindowSystem`)
- `Dalamud.Bindings.ImGui` (Checkbox, InputInt, Text*, Button, Separator)

## 2. FFXIVClientStructs (campos do jogo)

**Sintoma**: compila, mas crasha ao ativar a saída, ou a janela fica preta.

A boa notícia: quem encontra os offsets novos a cada patch é a comunidade do FFXIVClientStructs, e o Dalamud já distribui a versão atualizada. Nosso risco é só **renomearem/mudarem** os membros que usamos. Confira cada um aqui:

| Membro usado | Arquivo no repo [aers/FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs) |
|---|---|
| `Device.Instance()`, `Device.SwapChain`, `Device.D3D11Forwarder`, `Device.D3D11DeviceContext` | `FFXIVClientStructs/FFXIV/Client/Graphics/Kernel/Device.cs` |
| `SwapChain.BackBuffer`, `SwapChain.Width`, `SwapChain.Height` | `FFXIVClientStructs/FFXIV/Client/Graphics/Kernel/SwapChain.cs` |
| `Texture.D3D11Texture2D`, `Texture.D3D11ShaderResourceView`, `Texture.ActualWidth`, `Texture.ActualHeight`, `Texture.TextureFormat` | `FFXIVClientStructs/FFXIV/Client/Graphics/Kernel/Texture.cs` |
| `RenderTargetManager.Instance()` + tamanho do struct | `FFXIVClientStructs/FFXIV/Client/Graphics/Render/RenderTargetManager.cs` |

Se um campo sumiu/foi renomeado, o erro de compilação aponta exatamente o lugar; ajuste o nome em `Capture/GameSources.cs` / `Output/OutputSession.cs`.

### O offset `0x20` (tabela de render targets)

`GameSources.RenderTargetTableOffset = 0x20` assume que os ponteiros `Texture*` do `RenderTargetManager` formam uma tabela contígua começando em `+0x20` (hoje: campo `_gBuffers`). **Como validar**: abra o `RenderTargetManager.cs` no repo e veja o offset do primeiro campo `Texture*`/array de texturas. Se mudou, atualize a constante. Esse offset está estável há anos, mas é a única suposição "nossa" sobre layout de memória — por isso está isolada numa constante documentada.

## 3. Índice da render target do modo "sem UI"

**Sintoma**: o modo "sem UI" mostra uma textura errada (sombra, depth, tela preta). O modo com UI continua funcionando — ele não usa índice nenhum.

Não precisa de código. No jogo:

1. `/kefka` → desmarque "Mostrar a UI do jogo na saída".
2. Clique em **"Listar render targets no log"** e abra `/xllog`: cada slot plausível aparece com índice, resolução e formato. Os candidatos têm a **resolução da sua tela** e `srv=sim`.
3. Vá testando os índices candidatos no campo "Índice da render target" olhando a janela de saída — quando aparecer a cena sem UI, achou. A config salva sozinha.

(Pra referência: na era do plugin original era 71; esses índices mudam quando a Square mexe no pipeline gráfico.)

## 4. Mudanças estruturais no Dalamud que merecem atenção

Itens que, se mudarem um dia, pedem revisão de design (não só rename):

- **`UiBuilder.Draw` deixar de rodar na thread de render / dentro do present** — todo o modelo de threading do plugin assume isso (ver `docs/ARQUITETURA.md`). Nunca mudou na história do Dalamud, mas se mudar, procure o novo callback de render na doc.
- **Dalamud parar de distribuir `TerraFX.Interop.Windows.dll`** — verifique `%APPDATA%\XIVLauncher\addon\Hooks\dev\`. Solução: adicionar o pacote NuGet `TerraFX.Interop.Windows` com `Private=true`.
- **Jogo migrar pra DX12** — reescrita do renderer (swapchain/cópia/shader) seria necessária; o conceito (copiar o backbuffer antes do ImGui) continua válido.

## Fontes pra acompanhar

- Versões e breaking changes do Dalamud: <https://dalamud.dev/versions/>
- News de cada release: `https://dalamud.dev/versions/vXX/`
- FFXIVClientStructs: <https://github.com/aers/FFXIVClientStructs>
- Template de referência (sempre atualizado pro SDK novo): <https://github.com/goatcorp/SamplePlugin>
- Discord do goatcorp (canal #plugin-dev), pra quando o patch é grande e tudo está em fluxo.
