# Cuphead Online Mods

Modo online do Cuphead em duas versões:

- `desktop/` – mod para a versão PC (Windows), aplicado sobre o Assembly-CSharp do jogo.
- `web/` – mod para a build WebGL, feita a partir do projeto Unity 2017.4.9f1 exportado.
- `shared/` – código de rede comum aos dois (protocolo, lockstep, NetInput, NetRandom).
- `tools/` – scripts de build, patch e testes.

## Sem arquivos do jogo

Este repositório **não** contém arquivos do Cuphead: nem assets, nem o projeto Unity exportado, nem builds do jogo.
Cada pessoa usa a própria cópia do jogo, e as ferramentas daqui aplicam o mod nela.

## Requisitos

- Cuphead (PC) com a DLC, Unity 2017.4.9f1 (Mono)
- Unity 2017.4.9f1 + WebGL Build Support (para a versão web)
