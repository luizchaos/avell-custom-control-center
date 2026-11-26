# Arquitetura

## Visão geral

```
┌──────────────────────────┐   named pipe (JSON)   ┌────────────────────────────────────┐
│ AvellCC.App (UI + tray)  │ ────────────────────▶ │ AvellCC.Service (Windows Service)  │
│ usuário comum, sem admin │ ◀──────────────────── │ LocalSystem, único dono do hardware│
└──────────────────────────┘                       │  ├─ Ec        (WMI GetSetULong)    │
                                                   │  ├─ Fan       (tabela no EC)       │
                                                   │  ├─ Keyboard  (ITE 8291 HID)       │
                                                   │  ├─ Backup    (%ProgramData%)      │
                                                   │  └─ Profiles  (JSON)               │
                                                   └────────────────────────────────────┘
AvellCC.Core  — biblioteca sem dependência de UI (protocolos, modelos, regras de segurança)
AvellCC.Cli   — ferramenta de linha de comando sobre o Core (diagnóstico, backup, restore)
```

- Só o **serviço** acessa o hardware. A UI nunca roda como administrador.
- O Core é testável sem hardware: `IEcAccess` tem uma implementação real (`WmiEc`) e uma simulada (`FakeEc`).
- Todo acesso ao EC passa por **um único lock** no processo e por **um mutex global** (`Global\AvellCC.Ec`), para que a CLI e o serviço nunca falem com o EC ao mesmo tempo.

## Regra 1 — Só um controlador roda por vez

> **O Avell Custom Control e o AvellCC não podem rodar ao mesmo tempo.**

O serviço da Avell (`GCUBridge` + `GCUService.exe` + `ControlCenterU.exe`) escreve nos **mesmos endereços do EC**
(modo da ventoinha `0x751`, tabela `0xF00–0xF5F`, `0x7A5`) e nos **mesmos dispositivos HID** do teclado.
Ele também reaplica as próprias configurações em eventos como troca de AC/bateria, retorno do sleep e teclas Fn.
Com os dois ativos, um desfaz o que o outro gravou, e o resultado é imprevisível.

### Como parar o Avell Custom Control

Em um PowerShell **como administrador**:

```powershell
# 1. Fechar a UI e o tray
Get-Process ControlCenterU, OSDTpDetect, EnableTray -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. Parar o serviço e impedir que ele volte no boot
Stop-Service GCUBridge -Force
Set-Service GCUBridge -StartupType Disabled

# 3. Conferir (não deve listar nada rodando)
Get-Process GCUService, GCUBridge, ControlCenterU -ErrorAction SilentlyContinue
```

### Como voltar para o Avell Custom Control

```powershell
Set-Service GCUBridge -StartupType Automatic
Start-Service GCUBridge
```

### Como o AvellCC aplica a regra

- Na inicialização, o serviço e a CLI procuram os processos `GCUService`, `GCUBridge` e `ControlCenterU`.
  Se algum estiver rodando, **toda operação de escrita é recusada**, com uma mensagem explicando como parar o vendor.
  Leituras continuam permitidas, para diagnóstico.
- Uma flag `--allow-vendor-running` existe só para leitura. Não existe override para escrita.

## Regra 2 — Backup da curva original, antes de qualquer alteração

> **A primeira tabela de ventoinha lida do EC é gravada como `original` e nunca é sobrescrita.**

- Local: `%ProgramData%\AvellCC\backup\`
  - `fan-table.original.json`: **imutável**. É criado uma única vez, na primeira leitura bem-sucedida, e depois marcado como somente leitura.
  - `fan-table.<timestamp>.json`: um snapshot **antes de cada** gravação.
- Conteúdo: bytes crus de `0xF00–0xF5F` (96 bytes), registradores de modo (`0x751`, `0x7A5`, `0x78C`, `0x7AB`),
  Project ID (`0x740`), data, versão do BIOS e um SHA‑256 dos bytes.
- **Toda escrita na tabela exige que `fan-table.original.json` exista e seja válido.**
  Se não existir, o app faz o backup primeiro. Se o backup falhar, a escrita é abortada.
- Na restauração, o app grava os bytes crus do original e relê o EC para confirmar que bateu byte a byte.
- Comando: `avellcc fan restore-original`, e também um botão "Restaurar curva de fábrica" na UI.

**Registradores do Office Mode Advanced**, o mecanismo de curva que este EC realmente obedece:
- `ec-registers.original.json`: **imutável**, criado na primeira leitura estável, antes da primeira escrita.
  Contém `0x751`, `0x7A5`, `0x743–0x747` (T1..T5), `0x79E–0x7A0`, com SHA‑256.
- `ec-registers.<timestamp>.before-<motivo>.json`: snapshot antes de cada escrita.
- Aplicar: velocidades primeiro, `0x751 = 0xA0` por último. Restaurar: `0x751` primeiro, depois o resto.
- Comandos: `avellcc fan office backup | trial <curva> | restore-original --yes`.

> Observação: "fábrica" aqui significa **o que estava no EC na primeira vez que o AvellCC leu**.
> Neste notebook, o original (criado em 2026‑11‑26 10:16) tem a **tabela zerada e o bit 3 de `0x7A5` desligado**,
> ou seja, o EC estava usando a curva interna do firmware. Restaurar o original devolve o controle a essa curva interna
> (ver docs/HARDWARE.md, "Estado de fábrica deste notebook").
> O código do vendor tem caminhos para `FanTable\Default` e `FanTable\User`, mas nesta máquina (2026‑11‑26) não existe
> nenhuma cópia, nem no registro (`HKLM\SOFTWARE\OEM\GamingCenter2\MyFan3`) nem em disco.
> **O nosso backup do EC é a única cópia da curva original**: guarde também uma cópia fora do notebook.

## Decisão revista: a curva é aplicada pelo software (2026‑11‑26)

A ideia original era gravar a curva no firmware do EC, para que ela continuasse valendo mesmo com o app travado.
Os testes mostraram que **este EC não aceita curva customizada no firmware**:
- tabela `0xF00`: é ignorada, porque o EC é "legado" (`0x78E` bit 6 = 0);
- Office Advanced `0x743`: também é ignorado, com ou sem `0x727` bit 6.

O único mecanismo que funciona é o **controle direto** (`0x1804/0x1809` + `0x751` bit 6), o mesmo que a Tuxedo usa no Linux
para essa geração de EC. Então a curva passa a ser aplicada por um loop no serviço: ler temperatura → calcular duty → gravar.

Riscos e mitigações:

| Risco | Mitigação |
|---|---|
| Serviço morre e o duty fica parado num valor baixo | Recovery do Windows Service (reinicia em segundos e reassume); duty mínimo de 25 % |
| Temperatura dispara | Emergência no próprio loop: CPU ≥ 92 °C ou GPU ≥ 87 °C → 100 % até cair 10 °C |
| Sensor com leitura inválida (0/255) | 100 % até a leitura voltar |
| Sleep / hibernação | Desliga o controle direto ao suspender e religa ao retomar (igual à Tuxedo) |
| Algo desliga o `0x751` bit 6 (tecla Fn, BIOS) | O loop confere o `0x751` a cada ciclo e reassume |
| Parada normal / Ctrl+C / exceção | `finally` devolve `0x751` ao original: o firmware retoma |
| Reboot | O BIOS zera o `0x751`: o firmware retoma até o serviço subir |

Proteção final do hardware: a CPU reduz o próprio desempenho por temperatura (Tjmax), e o EC tem desligamento térmico próprio.

## Regra 3 — Segurança na escrita da curva

Toda curva passa pelo validador do Core antes de chegar ao EC:
- 16 níveis; `UpT` e `DownT` estritamente crescentes; `DownT[i] < UpT[i]` (histerese mínima de 3 °C);
- duty não-decrescente; duty do último nível ≥ 80 %; nenhuma temperatura acima de 100 °C;
- verificação *read-back* depois de gravar. Se divergir, o app restaura o snapshot anterior.

## Fases

| Fase | Entrega | Escreve no hardware? |
|---|---|---|
| M0 | Diagnóstico + backup original (`tools/diag`, `avellcc diag`) | **Não** |
| M1 | Teclado RGB: cor por zona, brilho, efeitos de firmware | HID |
| M2 | Telemetria: RPM, temperatura do EC, dashboard | Não |
| M3 | Curva customizada no EC, com backup, validação e restore | EC |
| M4 | Serviço Windows + UI WPF + tray + início automático | — |
| M5 | Perfis (AC/bateria, por app), atalhos | — |
