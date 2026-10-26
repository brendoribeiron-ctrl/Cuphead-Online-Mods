# NetTests — testar o online numa máquina só

Nada aqui entra na `Assembly-CSharp.dll` (o csproj principal ignora `NetTests\**`).

## NetLab (`NetLab/`)

Compilar: `Tools\dotnet\dotnet.exe build NetTests\NetLab\NetLab.csproj -c Release`
Executável: `NetTests\NetLab\bin\Release\net8.0-windows\NetLab.exe`

| Comando | Para quê |
|---|---|
| `selftest [--quick]` | Todos os testes automáticos, sem abrir o jogo. Código de saída 0 = tudo passou. |
| `proxy --listen 127.0.0.1:7778 --target 127.0.0.1:7777 --delay 60 --jitter 15 --loss 1` | Fica entre duas instâncias simulando internet ruim. Também: `--kbps`, `--dup`, `--cut-after s`, `--freeze-at s --freeze-for s`, `--udp`. |
| `client --connect ip:porta --script walk` | Amigo simulado do lobby antigo (tela transmitida). Mede fps/banda e salva quadros com `--save-frames`. |
| `host --port 7777` | Host simulado do lobby antigo: manda quadros de teste e mostra os botões do amigo. |
| `peer --listen 7777` / `peer --connect ip:porta` | Par simulado do netcode lockstep (tipos 0x20..0x3F): Hello, Config, Input com reenvio, InputAck, Checksum, Ping/Pong, Bye. |

No TCP a perda não some com bytes: vira atraso de retransmissão (`--rto`, padrão 250 ms), que é o que o jogo sente de verdade.

## Duas instâncias do jogo (`scripts/`)

- `make-testgame.ps1 -Dll <dll>` cria `Tools\TestGame`, uma cópia leve do jogo (assets por junction/hardlink) para não mexer na instalação de verdade.
- `run-local.ps1 -Repo <checkout> -Delay 60 -Loss 1` compila o checkout, instala na cópia, liga o proxy e abre host e amigo em janelas 800x450. Logs em `Tools\TestGame\host.log`, `join.log`, `proxy.log`.

- `run-netcode.ps1 -Dll <dll> [-Lobby] [-Mode sync|stream] [-Level Veggies] [-Delay 40 -Loss 1] [-CutAfter 40]` abre host e amigo com bots nos controles (`-netcode-botinput`), cada um com uma cópia dos saves (`-savedir`, o save de verdade não é tocado), e no fim compara os hashes dos snapshots e mede a velocidade (tem que dar ~60 frames/s). Código de saída 0 = sincronizado.
- `shot-windows.ps1` tira print das janelas do jogo de teste, mesmo atrás de outras janelas.

## Resultados com o jogo de verdade (integração)

| Teste | Resultado |
|---|---|
| Mapa, transporte de teste, 40 ms ±10 | 6600 frames, 22 snapshots iguais, 0 dessincronias, 60,3 frames/s |
| Fase Veggies, 30 ms ±5 | 4800 frames iguais (com mortes e retry), 60,2 frames/s |
| Fase Veggies, 50 ms ±15, perda 1% | hashes iguais; 1 correção no frame 150; velocidade cai para ~45 frames/s |
| Lobby (-host/-join, modo sync) | sala, entrada, início e lockstep automáticos; 6600 frames iguais |
| Lobby com queda a cada 40 s | amigo volta em ~1 s e o lockstep recomeça; o host corrige a posição no frame 30 |
| Lobby modo stream | amigo vê a tela do host a ~19 fps, ~400 KB/s |

## O que os testes do lockstep mostraram (bots, 60 ticks/s)

- Rede local e 20 ms de ida: ~100% da velocidade com input delay 3.
- 60 ms de ida: delay 3 trava (~70%); delay 8 roda a ~98%. O input delay precisa acompanhar o ping (delay ≥ ping/16,7 ms + folga).
- Perda de 1% no TCP derruba para ~60%: cada pacote perdido congela o lockstep pela retransmissão. Vale reenviar inputs redundantes e, se possível, usar UDP para os inputs.
