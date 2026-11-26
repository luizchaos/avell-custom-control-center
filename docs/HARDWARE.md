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

#### Teste 1 — `safe-trial` com `0x751 = 0xA0` (2026‑11‑26, 10:49–10:53)

| Situação | CPU | fan1 | fan2 |
|---|---|---|---|
| Firmware, ocioso | 53–54 °C | ~2040 | 2168 |
| Firmware, carga (diagnóstico das 10:17) | 82–84 °C | 5085–5097 | 4984 |
| Tabela, "41,5 %" | 54 °C | 2049–2137 | 2148 |
| Tabela, "70 %" | 73 °C | 3850–3993 | 3684 |
| Tabela, "79–94 %" | 76–80 °C | **trava em ~3993** | **trava em 3684** |
| Firmware logo após restaurar | 74 °C | 4244 | 3960 |

- Com bit 3 de `0x7A5` + `0x751 = 0xA0`, as ventoinhas **não passaram de ~4000 RPM**, e a CPU ficou em 80 °C (provável throttling).
  O firmware puro passa de 5000 nessa temperatura.
- No ocioso o RPM foi igual ao do firmware. **Não é conclusivo** se o EC seguiu a tabela, ou se o teto veio do modo `0xA0`.
- Gravar a curva (98 escritas + conferência) levou **~40 s**; restaurar levou ~45 s.
- **fan2** sobe em degraus exatos de 256 (0x864, 0x964, 0xA64…): o byte baixo em `0x46B` quase não muda.
  Suspeita: o byte baixo não está em `0x46B`. Investigar com `fan monitor --raw` (faixa `0x460–0x46F`).

#### Testes 2 e 3 — sondas com 100 % a partir de 40 °C (10:58–11:04)

| Sonda | `0x751` | `0x7A5` | Resultado (ocioso, 54–58 °C) |
|---|---|---|---|
| `probe-max-keep` | `0x00` | `0x08` | 2043–2377 RPM, **igual ao firmware** |
| `probe-max-standard` | `0xA0` | `0x08` | 2041–2366 RPM, **igual ao firmware** |

**Conclusão: este EC ignora a tabela de 16 níveis em `0xF00`.** O caminho `SetFanTable` do vendor não vale para este modelo.
O teto de ~4000 RPM do teste 1 veio do `0x751 = 0xA0` (ver "Office Mode Advanced" abaixo), não da tabela.
`FanTableService.ApplyCurve` e `FanTableEncoder` ficam no código só como referência, e não são usados para controlar as ventoinhas.

#### Estado da curva interna do firmware (`0x460–0x46F`)

```
0x460: 01 3C 43 05 | 08 59 | 00 00     ventoinha 1: nível, limiar °C, ?, ?, RPM hi, RPM lo
0x468: 01 3C 78 78 | 08 13 | 00 A5     ventoinha 2: nível, limiar °C, ?, ?, RPM hi, RPM lo
```

- `0x460`/`0x468` = **nível atual** (01→02→03 com a CPU subindo de 58 para 63 °C); `0x461`/`0x469` = **limiar do nível** (0x3C/0x46/0x50 = 60/70/80 °C).
- **Byte baixo da ventoinha 2 = `0x46D`** (o vendor usa `0x46B`, que neste modelo é fixo: 0x78, ou 0x64 com `0x751 = 0xA0`). Corrigido no código e no diagnóstico.

### Office Mode Advanced ("MyFan2/MyFan3") — o mecanismo de curva deste modelo

Tirado do IL do `MyFanManager_Intel`, e coerente com o registro desta máquina
(`HKLM\SOFTWARE\OEM\GamingCenter2\MyFan3`: `OfficeMode = 2`, `OfficeAdvancedLevel = 4`, presets `OfficeModeAdvL1..L4` com `T1..T5_PWM`).

| Registrador | Uso | Código do vendor |
|---|---|---|
| `0x743–0x747` | **Duty dos 5 níveis T1..T5** (% × 2) | `SetFan_Optimized_PWM`: `EC[0x743+i] = Ti_PWM × 2` |
| `0x786–0x78A` | Defaults MyFan3 dos 5 níveis | `GetFanTablePWMDefault` |
| `0x751 = 0x80 + L` | Office **Basic**, nível fixo L (0..5) | `SetBasicFanLevel` |
| `0x751 = 0xA0` | Office **Advanced** (segue `0x743–0x747`) | `SetFanMode(2)` |
| `0x751 = 0x00` / `0x40` | Gaming (curva interna) / Turbo-FanBoost | `SetFanMode(1)` / `(4)` |
| `0x79E` / `0x79F` / `0x7A0` | Velocidade mín. / temp. mín. / velocidade extra | `SetOfficeModeAdv_*` (`Level2Pwm`: 0,60,80,100,120,140) |
| `0x782` bit 3 / bit 2 | Suporta MyFan3 / tipo de tabela office | `0x782 = 0x5D`: **os dois ligados** |

Presets do vendor no registro (T1..T5, em %):

| Preset | T1 | T2 | T3 | T4 | T5 |
|---|---|---|---|---|---|
| L1 | 30 | 32 | 36 | 45 | 60 |
| L2 | 30 | 35 | 40 | 50 | 60 |
| L3 | 30 | 39 | 44 | 55 | 60 |
| L4 (ativo) | 30 | 35 | 40 | 50 | 60 |

- Isso explica o teste 1: com `0xA0`, o EC seguiu os T1..T5 que já estavam em `0x743–0x747` (T5 ≈ 60 %), e daí o teto de ~4000 RPM.
- As **temperaturas** T1..T5 parecem fixas no firmware (limiares de 10 em 10 °C vistos em `0x461`). A curva customizável deste modelo é **5 velocidades em temperaturas fixas**, mais velocidade mínima, temperatura mínima e velocidade extra.
#### Dump `0x700–0x7FF` (2026‑11‑26, `0x751 = 0x00`)

```
       00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F
0700:  00 00 00 00 00 00 00 00 00 00 D2 00 00 00 00 00
0710:  70 71 71 06 53 D2 70 06 53 D1 70 06 53 81 70 80
0720:  42 00 04 00 00 00 00 00 00 00 00 00 09 00 00 00
0730:  78 78 A5 01 64 64 A5 01 00 30 48 00 01 00 00 00
0740:  0F 00 02 3C 46 50 64 78 81 00 00 00 1F 00 01 B0
0750:  00 00 6C 00 00 00 00 00 40 00 00 42 41 00 10 00
0760:  0B 00 00 00 00 E0 83 00 00 00 00 00 00 00 00 00
0780:  00 00 5D 00 00 00 3C 46 50 64 78 00 04 00 0C 00
0790:  00 00 00 00 00 00 00 00 00 00 79 D3 10 E4 00 00
07A0:  00 00 00 00 80 00 08 00 00 00 00 00 00 00 00 00
07F0:  00 00 00 00 00 00 00 00 00 00 00 13 00 B8 74 4B
```
(As linhas `0x770` e `0x7B0–0x7E0` vieram todas zeradas e foram omitidas.)

- `0x743–0x747 = 3C 46 50 64 78` → **T1..T5 = 30/35/40/50/60 %**, igual ao preset `OfficeModeAdvL4` ativo.
- `0x786–0x78A` = os mesmos valores (defaults MyFan3).
- `0x79E–0x7A0 = 00 00 00`: velocidade mínima, temperatura mínima e velocidade extra desligadas.
- `0x730–0x732 = 120/120/165` e `0x734–0x736 = 100/100/165`: PL1/PL2/PL4 (W) Gaming e Office, iguais ao registro.

**Cruzando com o teste 1 (`0x751 = 0xA0`):** os patamares de RPM batem com T1..T5.

| Nível | Duty | RPM | RPM / % |
|---|---|---|---|
| T1 | 30 % | ~2050 | 68 |
| T2 | 35 % | ~2370 | 68 |
| T3 | 40 % | ~2660 | 66 |
| T4 | 50 % | ~3240 | 65 |
| T5 | 60 % | ~3993 | 67 |

**Relação linear, ~67 RPM por %** (projeção de ~6700 RPM a 100 %; o firmware Gaming chegou a ~5100 ≈ 77 %).

#### Teste 4 — Office Advanced com T1..T5 = 70/75/80/90/100 % (11:15–11:17)

Gravado e conferido (`0x743–0x747 = 8C 96 A0 B4 C8`, `0x751 = 0xA0`), e mesmo assim o nível 1 ficou em **~2043 RPM = 30 %**.
O EC **não lê `0x743–0x747`** nesse estado. Os patamares de 30..60 % do teste 1 batem também com os defaults em `0x786–0x78A`.
Além disso, o `UpdateAdvPwms` do vendor limita T1..T5 entre o primeiro e o último default (30–60 %), e o
"Fan Speed Curve" do app (`UserSet_FanSpeedCurveSetting`) edita a tabela `0xF00`, que este EC ignora.

### Referência: driver Linux da Tuxedo (`tuxedo-drivers`, `uniwill_keyboard.h` / `tuxedo_io.c`)

| Mecanismo | Registradores | Condição |
|---|---|---|
| **Controle universal** (tabelas `0xF00`) | liga com `0x7C5` bit 7 + `0x7C6` bit 2 | `0x78E` bit 6 = 1 |
| **Controle direto (legado)** | `0x751` bit 6 ("full fan mode") + duty em **`0x1804`** (ventoinha 0) / **`0x1809`** (ventoinha 1), 0..200 | EC sem controle universal |
| Curva manual (profile v1) | `0x743–0x747` (copiada de `0x786–0x78A`) + `0x727` bit 6 (custom profile) + `0x741 = 1` (manual) | só alguns modelos |
| Perfis | `0x751`: `0xA0` powersave, `0x00` enthusiast, `0x10` overboost | — |

Regras de segurança da Tuxedo: **nunca gravar duty 0** (0 = 30 % por 3 min e depois para; 1 = para), mínimo de 25 %,
máximo `0xC8`. No modo direto, eles regravam `0x1804/0x1809` várias vezes logo depois de ligar o bit 6, "antes do ramp-up completo",
e repetem a cada 50 min.

#### Testes 5 e 6 — sondas B e A (11:25–11:28)

| Sonda | O que liga | Resultado (ocioso, ~52 °C) |
|---|---|---|
| **B** Office + `0x727` bit 6 | `0x751 = 0xA0`, `0x727 = 0x40`, T1 = 70 % | ~2043 RPM: **ignorado** |
| **A** Controle direto | duty 140 em `0x1804/0x1809`, depois `0x751 = 0x40` | **~4230 / ~4250 RPM, estável por 60 s** |

**Mecanismo confirmado: controle direto.** Gravar o duty (0..200) em `0x1804` (ventoinha 1) e `0x1809` (ventoinha 2) com `0x751` bit 6 ligado.
Ao devolver `0x751 = 0x00`, o firmware retoma na hora (o duty voltou para 64/63 e o RPM começou a cair).

Extras:
- **Com o firmware no controle, `0x1804/0x1809` mostram o duty atual da curva interna** (60 = 30 % no ocioso). É uma telemetria de "% da ventoinha".
- RPM por duty: 30 % ≈ 2050 RPM; 70 % ≈ 4230 RPM. Não é perfeitamente linear (68 → 60 RPM/%). Calibrar com uma varredura depois.
- Com o duty regravado a cada ~4 s, o EC manteve o valor sem desvio por 60 s.

**Este notebook: `0x78E = 0x0C` (bit 6 = 0), então NÃO tem controle universal.** Isso explica por que as sondas `0xF00` falharam.
Caminhos restantes, em ordem: **(B)** Office + `0x727` bit 6; **(A)** controle direto `0x1804/0x1809`.
O caminho (A) significa curva aplicada pelo software (o serviço lê a temperatura e grava o duty), e não pelo firmware.

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

### Sequências do vendor (`LM_ITE_RGB.Set_ITE_Effect_Type_FwMode`, tipos 2/3 = 4 zonas)

| Caso | Pacotes |
|---|---|
| Uma cor (estático ou efeito) | `0x14` slot 1 → `0x08 [2, effect, speed, bright, color=1, dir, save]` |
| Paleta (efeito com várias cores) | `0x14` slots 1..N → `0x08 [..., color=8, ...]` |
| Sem cor (arco-íris do firmware) | `0x08 [..., color=0, ...]` |
| Cor de uma zona ao vivo | só `0x14` no slot `zona` (1..4) |

O AvellCC faz "uma cor por zona" assim: `0x14` slot 1 → `0x08 Static color=1` → `0x14` slots 1..4.

### Confirmado no hardware (2026‑11‑26, sem administrador)

- Abrir `048D:CE00` MI_01 (`0xFF12`) com `GENERIC_READ|WRITE` funciona como usuário comum.
- **Firmware** (`0x80`): `11 03 00 00`, ou seja, `Ver_High = 0x11` e `Ver_Low = 0x03`.
- **Estado** (`0x88`): a resposta **espelha o comando `0x08`** a partir do byte 2: `ctrl effect speed bright color dir save`.
  - Estado de fábrica/vendor encontrado: `02 03 01 24 08 01` = Apply, **Wave**, speed 1 (o mais rápido), brilho 36 (nível 3), paleta cíclica, direita→esquerda.
  - O byte de `save` volta 0 mesmo quando o efeito foi gravado com save.
- `kb zones FF0000 00FF00 0000FF FFFFFF` foi aceito: o `0x88` passou a reportar `Static, brilho 50, color=1`.
- **Confirmado visualmente:** zonas **1→4 = esquerda→direita**, cores corretas. O branco sai branco com a correção
  `(R, 100·G/255, 200·B/255)`, então ela vale para este teclado.
- **Efeito com paleta confirmado visualmente:** `kb effect wave -s 1 -b 4 -d ltr -c FF0000,00FF00,0000FF`
  faz a onda andar para a direita. A cor do slot 1 sai na frente, então lendo da esquerda para a direita
  aparece a paleta invertida e cíclica (…azul, verde, vermelho…). Direção `0` = esquerda→direita, confirmado.

Para voltar ao efeito do vendor (os bytes exatos lidos acima):
```powershell
avellcc kb raw 00 08 02 03 01 24 08 01 01
```
A paleta (slots 1..7) usada pela Wave original não pode ser lida. Os testes sobrescreveram os slots 1..4.
