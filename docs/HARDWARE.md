# Hardware — Avell A65 LIV (plataforma Uniwill)

> Fonte: análise estática (somente leitura) do `GCUService.exe` do *Avell Custom Control* 1.1.0.45
> instalado nesta máquina, em 2026‑11‑26. Nada foi executado nem escrito no hardware para obter estes dados.
> Itens marcados **[não verificado]** precisam ser confirmados com `tools/diag/Invoke-AvellDiag.ps1`.

## Identificação

| Item | Valor |
|---|---|
| SMBIOS Manufacturer / Model | `Avell High Performance` / `A65 LIV` |
| BIOS | AMI `N.1.01` |
| Plataforma | **Uniwill** (não Clevo — `InsydeDCHU.dll` / `ClevoEcInfo.dll` não se aplicam) |
| EC Project ID | EC `0x740` = `0x0F` (`EC_PROJECT_CML_Gaming` na lista do vendor). Lido em 2026‑11‑26 |

## 1. Acesso ao EC (Embedded Controller)

Feito pelo método WMI/ACPI do próprio BIOS. Não precisa de driver de terceiros. **Exige administrador.**

```
Namespace : root\WMI
Objeto    : AcpiTest_MULong.InstanceName='ACPI\PNP0C14\1_1'
Método    : GetSetULong(Data: UInt64) -> Return: UInt32

Leitura   : Data = 0x10000000000 + addr          ; valor = Return & 0xFF
Escrita   : Data = ((UInt64)value << 16) + addr  ; Return ignorado
```

`addr` é de 16 bits (`0x0000–0xFFFF`). Confirmado no IL de `MyControlCenter.WMIEC::WMIReadECRAM/WMIWriteECRAM`
e testado na máquina em 2026‑11‑26: leituras consistentes, tabela estável entre duas leituras.

**Latência:** cada chamada WMI leva da ordem de **100–150 ms**. Isso é estimado, porque as amostras do diagnóstico
(cerca de 50 leituras cada) saíram com uns 8 s de intervalo. Consequências:
- telemetria: ler só o necessário (2 temperaturas + 4 bytes de RPM ≈ 1 s por ciclo);
- gravar a tabela inteira (96 bytes + verificação) leva **dezenas de segundos**. A UI precisa mostrar progresso.

> ⚠️ Se o bit de leitura (`1 << 40`) faltar, uma "leitura" vira **escrita de 0** no endereço.
> Toda leitura tem que passar por uma única função testada (`WmiEc.Read`), e nunca deve ser montada à mão.

O vendor também tem um caminho alternativo, `ECIO.dll` (`OemGetECRAM`/`OemSetECRAM`), sobre o driver `SparkIO.sys` (I/O direto nas portas do EC).
**Não usamos esse caminho**: ele compete com o firmware pelo EC e depende de um driver não assinado pela Microsoft.

## 2. Mapa de registradores (EC RAM)

### Ventoinhas — telemetria

| Endereço | Uso | Observação |
|---|---|---|
| `0x464` (hi) / `0x465` (lo) | Ventoinha 1 (CPU) | o vendor chama de `GetEcCpuFanDuty`, mas o `ECSpec` diz `MAIN_FAN_RPM`. Valor = `(hi << 8) | lo` |
| `0x46C` (hi) / `0x46B` (lo) | Ventoinha 2 (GPU) | idem, `SECOND_FAN_RPM` |
| `0x741` | `FAN_ALERT` / `AP_OEM_BYTE` | compartilhado |

Sob carga (CPU de 72 a 84 °C), os valores lidos subiram de 3317 para 5097 (ventoinha 1) e de 3192 para 4984 (ventoinha 2).
É a faixa típica de RPM de notebook, então **provavelmente é RPM direto**. Ainda falta comparar com o número que o Control Center mostra.

### Ventoinhas — modo

| Endereço | Valor escrito pelo vendor | Significado |
|---|---|---|
| `0x751` | `0x40` | Performance / Turbo / Benchmark (bit 6 = FanBoost) |
| `0x751` | `0xA0` | Standard / Quiet |
| `0x751` | `0x00` | (outro caminho do `SetFanMode`) |
| `0x7A5` bit 3 (`0x08`) | — | "UserHiModeToFantable": o EC passa a usar a tabela do usuário |
| `0x7A5` | — | também controla Quiet mode (`SetFanQuietModeEnable`) e LED de power |
| `0x78C` | — | nível do modo Turbo |
| `0x7AB` | — | índice do perfil de modo (MyFanCCI) |

### Ventoinhas — tabela de curva (firmware)

Duas tabelas de **16 níveis**, uma para a CPU e outra para a GPU:

| Bloco | CPU | GPU |
|---|---|---|
| Temperatura de **subida** (°C) | `0xF00–0xF0F` | `0xF30–0xF3F` |
| Temperatura de **descida** (°C) | `0xF10–0xF1F` | `0xF40–0xF4F` |
| **Duty** (0–200 = 0–100 %, `% × 2`) | `0xF20–0xF2F` | `0xF50–0xF5F` |

Semântica da escrita, tirada do `FanTable_Manager::SetCpuFanTable`, para `i = 0..15`:

```
EC[0xF00 + i] = (i < 15) ? Table[i+1].UpT   : 0xFF   // subir do nível i para o i+1 quando T >= UpT
EC[0xF11 + i] = Table[i].DownT          (i < 15)     // descer do nível i+1 para o i quando T <= DownT
EC[0xF20 + i] = Table[i].Duty * 2                    // duty do nível i
```

A **histerese é feita pelo próprio EC** (limiares de subida e descida separados).
Se o nosso app travar, o EC continua aplicando a última tabela gravada.

A leitura (`CreateFanTableFileFromEC`) lê os 3 blocos crus e divide o duty por 2.
O **backup guarda os bytes crus** (`0xF00–0xF5F`, 96 bytes) e os registradores de modo, para restaurar exatamente o que estava lá.

#### Estado de fábrica deste notebook (lido em 2026‑11‑26)

```
0xF00–0xF5F = 00 × 96     (tabela do usuário VAZIA)
0x751 = 0x00   0x7A5 = 0x00 (bit 3 desligado)   0x78C = 0x04   0x7AB = 0x00
```

Ou seja, **o EC está usando a curva interna do firmware**, e a área `0xF00` não está em uso.
A curva de fábrica propriamente dita fica dentro do firmware do EC e **não é legível** por esses endereços.
Consequências:
- "Restaurar original" = **desligar o modo tabela do usuário** (`0x7A5` e `0x751` aos valores salvos) **e** zerar a tabela.
  O código grava os registradores de modo **antes** da tabela. Assim o EC nunca roda, nem por um instante, com uma tabela de duty 0 ativa.
- Para aplicar uma curva nossa (M3), a ordem é a inversa: gravar e conferir a tabela, e **só depois** ligar o bit 3 de `0x7A5`.
- Não dá para "partir da curva de fábrica" lendo o EC. O editor vai começar de um preset nosso, com valores conservadores.

### Outros

| Endereço | Uso |
|---|---|
| `0x730–0x736`, `0x783–0x785`, `0x7A7–0x7A9` | Power limits PL1/PL2/PL4 (gaming / office / battery saver) |
| `0x738` | CPU Tau |
| `0x765`, `0x766` | Support bytes (bit 2 de `0x766` = suporte a RGB keyboard) |
| `0x767` | Trigger byte (bit 5 = RGB keyboard, bit 6 = logo, bit 7 = welcome, bit 2 = FanBoost) |
| `0x768` | Status byte |
| `0x769–0x76B` | Nível RGB R/G/B (teclado *single‑zone*, controlado pelo EC) |
| `0x7B9` | Limite de carga da bateria |
| `0x748–0x74B` | Lightbar via EC (controle, R, G, B) |

### Temperaturas

O serviço da Avell **não lê temperatura do EC**:
- **CPU:** contador de desempenho do Windows `Thermal Zone Information\Temperature` (Kelvin → `−273.2`).
- **GPU:** NVAPI (`NVControlSetting.dll` / `GPUInfoDLL.dll`).

O EC tem a própria leitura de temperatura (é ela que move a curva). **Confirmado em 2026‑11‑26** (10 amostras sob carga):

| Endereço | Uso | Evidência |
|---|---|---|
| `0x43E` | **CPU (°C)** | 69 → 84; o Thermal Zone `\_SB.ECTZ` marcou 72 → 84 (o próprio ECTZ é alimentado pelo EC) |
| `0x44F` | **GPU (°C)** | 52 → 75; a `nvidia-smi` marcou 60 → 76 (o EC atualiza com alguns segundos de atraso) |

Decisão para o app:
1. **Curva:** mostrar a temperatura **do EC** no editor, porque é a que o firmware usa para decidir o nível.
2. **Dashboard:** usar o [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) para a temperatura de pacote da CPU e da GPU NVIDIA. Se não houver, usar o Thermal Zone do Windows.

## 3. Teclado RGB — ITE 8291 (HID)

| Dispositivo | Interface | Usage Page | Papel (nomes do vendor) |
|---|---|---|---|
| `048D:CE00` rev `0x0002` | `MI_01` | `0xFF12` | `USAGE_PAGE_4Zone` → **teclado 4 zonas** |
| `048D:6005` rev `0x0003` | `MI_01` | `0xFF03` | `USAGE_PAGE_Ligbar` / `ME_2ND` → **lightbar** (a confirmar) |

Os dois expõem um *feature report* de **9 bytes**: `byte[0] = 0x00` (report ID), seguido de 8 bytes de comando.
O envio é feito com `HidD_SetFeature`, e a leitura com `HidD_GetFeature` depois de um comando de consulta.
O vendor espera 1 ms entre comandos e serializa tudo com um lock.

| Cmd | Bytes (`[0]` = report ID) | Função |
|---|---|---|
| `0x08` | `00 08 ctrl effect speed bright color dir save` | Define efeito / liga / desliga |
| `0x09` | `00 09 02 level 00 00 00 00 00` | Brilho |
| `0x12` | `00 12 00 00 08 00 00 00 00` | Prepara "picture" (per‑key, seguido de output reports de 65 bytes) |
| `0x14` | `00 14 00 idx R G B 00 00` | Cor do slot `idx` (zona `1..4` no 4‑zone) |
| `0x16` | `00 16 00 row 00 00 00 00 00` | Índice de linha (per‑key) |
| `0x1A` | `00 1A 00 a b 00 00 00 c` | Timeout / sleep |
| `0x80` | `00 80 …` → GetFeature: `fw = [2..5]` | Versão do firmware (consulta) |
| `0x88` | `00 88 …` → GetFeature: `[2..7]` | Efeito atual (consulta) |

`ctrl` (byte 2 do `0x08`): `1` = LED off, `2` = default/aplicar, `3` = welcome, `4` = night, `5`/`6` = usados no power‑off.

**Brilho** (UI 0..4 → firmware): `0, 8, 22, 36, 50` (máximo `0x32`).
**Velocidade** (UI 0..4 → firmware): `10, 7, 5, 3, 1` (lento → rápido).
**Direção:** `0` = esquerda→direita, `1` = direita→esquerda, `2` = baixo→cima, `3` = cima→baixo, `4` = ao pressionar tecla.

**Efeitos** (`ITE_SPEC`):

| Efeito | Código | Efeito | Código |
|---|---|---|---|
| Static | `0x01` | Breathing | `0x02` |
| Wave | `0x03` | Reactive | `0x04` |
| Rainbow | `0x05` | Ripple | `0x06` |
| Marquee | `0x09` | Raindrop | `0x0A` |
| Stack | `0x0C` | Impact | `0x0D` |
| Aurora | `0x0E` | Neon | `0x0F` |
| Spark | `0x11` | Flash | `0x12` |
| Mix | `0x13` | Gaming | `0x15` |
| RippleO | `0x16` | Alphabet | `0x17` |
| StarSpark | `0x18` | StarHitting | `0x19` |
| Thinking | `0x21` | Music | `0x22` |
| UserMode (per‑key) | `0x33` | | |

**Correção de cor 4‑zone** (`WKDColor.cheatRGB_4Zone(projectId, r, g, b)`). O LED tem verde e azul mais fortes, e o vendor compensa:

```
se projectId ∈ {6, 7}:
    (255,165,0) -> (255, 60, 0)
    (255,255,0) -> (255,120, 0)
    senão       -> (R, 120*G/255, 220*B/255)
senão:
    (255,100,0) -> (255, 40, 0)
    (255,169,0) -> (255, 70, 0)
    senão       -> (R, 100*G/255, 200*B/255)
```

Este notebook tem Project ID `0x0F`, então vale o ramo **"senão"**: `(R, 100·G/255, 200·B/255)`.

Sequência típica para cor fixa em 4 zonas (protocolo ITE 8291 padrão): enviar `0x14` para os slots `1..4` com as cores já corrigidas, e depois `0x08` com `ctrl=2`, `effect=0x01`, `bright=…`, `save=…`.
**[não verificado nesta máquina]** — será testado na etapa de RGB.
