# Arquitetura

## Objetivo

Espelhar o frame do jogo **antes** do Dalamud desenhar qualquer coisa, numa janela separada que o OBS captura. O streamer vê todos os plugins na janela principal; a stream só vê o jogo.

## Por que não copiar o MaskedCarnivale original?

O original (ProjectMimer/MaskedCarnivale) funcionava, mas tinha três fontes de fragilidade e atrito:

| Original | Mask of Kefka | Ganho |
|---|---|---|
| Hook por assinatura no `DXGIPresent` (`E8 ?? ?? ?? ?? C6 43 79 00`) | Nenhum hook: renderiza dentro do `UiBuilder.Draw` do Dalamud | Assinatura era o item que mais quebrava por patch; agora o Dalamud mantém esse ponto por nós |
| Processo separado em C++ (`outputwindow.exe`) + textura compartilhada + memória compartilhada | Janela Win32 **in-process** com segunda swapchain no mesmo device D3D11 | Sem IPC, sem exe extra pra distribuir, sem sincronização de handle |
| SharpDX (abandonado desde 2019) | TerraFX.Interop.Windows (distribuída junto com o próprio Dalamud) | Zero dependências externas; sempre compatível com a versão do Dalamud |
| Índices de render target fixos no código (107 com UI / 71 sem UI) | Modo padrão usa o **backbuffer** (sem offset nenhum); modo "sem UI" tem índice configurável em runtime | Patch não exige recompilar; o caso comum nem exige reconfigurar |

## Fluxo por frame

```
Jogo renderiza o frame (cena + UI do jogo) ──▶ backbuffer pronto
        │
        ▼
Jogo chama Present ──▶ Dalamud intercepta e dispara UiBuilder.Draw   ◀── estamos aqui
        │                 │
        │                 ├─ 1. escolhe a fonte:
        │                 │     • com UI:  backbuffer (Device->SwapChain->BackBuffer)
        │                 │     • sem UI:  Texture* do RenderTargetManager (índice da config)
        │                 ├─ 2. CopyResource pra textura intermediária (se a fonte não tem SRV)
        │                 ├─ 3. SwapDeviceContextState (isola TODO o estado do pipeline)
        │                 ├─ 4. desenha triângulo fullscreen na swapchain da janela de saída
        │                 ├─ 5. Present(0, 0) da swapchain de saída
        │                 └─ 6. SwapDeviceContextState de volta (jogo/ImGui intactos)
        ▼
Dalamud desenha o ImGui no backbuffer ──▶ Present real ──▶ janela principal (com overlays)
```

O ponto-chave: no momento do `UiBuilder.Draw`, o backbuffer contém **jogo + UI do jogo**, mas o ImGui ainda **não** foi desenhado. É a janela de tempo exata pra capturar a imagem limpa.

## Threads

| Thread | O que faz | Arquivo |
|---|---|---|
| Render do jogo | Toda manipulação de D3D11: criar/destruir sessão, copiar, desenhar, present. Lifecycle dirigido pela flag `Plugin.OutputRequested` — por isso não há locks | `Plugin.OnDraw`, `OutputSession`, `OutputRenderer` |
| `MaskOfKefka.OutputWindow` | Só o message pump Win32 da janela de saída (mover, redimensionar, fechar). Nunca toca em D3D | `OutputWindowHost` |
| Qualquer uma | Pode setar `OutputRequested` (comando, config) — a mudança real acontece no próximo frame | `Plugin` |

## Isolamento de estado D3D

Desenhar "no meio" do frame do jogo exige não sujar o estado do pipeline (nem do jogo, nem do renderer de ImGui do Dalamud). Em vez de salvar/restaurar dezenas de slots manualmente, usamos `ID3DDeviceContextState` + `SwapDeviceContextState` (D3D11.1): troca **todo** o estado do contexto de uma vez e devolve no final. O original não fazia isso — confiava que o jogo re-setava tudo a cada frame.

## Decisões menores

- **Triângulo fullscreen via `SV_VertexID`**: sem vertex buffer, sem input layout — menos objetos pra criar/vazar.
- **Shaders compilados em runtime** (`D3DCompile`): sem etapa de build de HLSL, o código-fonte do shader fica no próprio `.cs`.
- **Alpha forçado em 1.0 no pixel shader**: render targets do jogo têm alpha arbitrário; sem isso a captura via WGC pode sair translúcida.
- **`DisableUserUiHide`/`DisableCutsceneUiHide`/etc.**: o `UiBuilder.Draw` precisa continuar disparando em cutscene/gpose/UI oculta, senão a stream congela.
- **Validação de ponteiros no scan de render targets**: a tabela do `RenderTargetManager` mistura ponteiros com campos escalares; dereferenciar lixo derruba o jogo. Só seguimos ponteiros canônicos alinhados (ver `GameSources.LooksLikePointer`).
- **Swapchain FLIP_DISCARD com `Present(0, 0)`**: não bloqueia o frame do jogo esperando vsync da janela de saída.
