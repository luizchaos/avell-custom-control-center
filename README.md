# AvellCC — Avell Custom Control Center

Controle de **RGB do teclado** e **curvas de ventoinha** para o **Avell A65/C65 LIV** (plataforma **Uniwill**).

> ⚠️ **Só um controlador por vez.** O instalador para e desativa o *Avell Custom Control* (`GCUBridge`).
> Os dois escrevem nos mesmos registradores do EC. Ver [docs/ARCHITECTURE.md — Regra 1](docs/ARCHITECTURE.md#regra-1--só-um-controlador-roda-por-vez).

> 🛟 **Backups imutáveis.** Antes da primeira escrita em cada grupo de registradores, o estado original é salvo em
> `C:\ProgramData\AvellCC\backup\*.original.json` e nunca é sobrescrito.

## Instalar

PowerShell **"Executar como administrador"**, na pasta do repositório (requer o .NET 8 SDK):

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\install.ps1
```

O script roda os testes, publica em `C:\Program Files\AvellCC`, desativa o `GCUBridge` e cria o serviço **AvellCC**
(LocalSystem, início automático, reinício automático em falha). Também cria o atalho no Menu Iniciar e abre o app.
As ventoinhas começam no modo **firmware**; escolha uma curva na aba **Ventoinhas**.

Para remover (mantém os backups; `-RestoreVendor` reativa o app da Avell):

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\uninstall.ps1 -RestoreVendor
```

## Como funciona

```
AvellCC.exe (WPF + bandeja, sem admin) ──named pipe──► serviço AvellCC (LocalSystem)
                                                         ├─ ventoinhas: curva por software → duty direto no EC (0x1804/0x1809)
                                                         ├─ teclado: ITE 8291 (HID), 4 zonas
                                                         └─ C:\ProgramData\AvellCC (settings.json, curves\, backup\)
```

- **Ventoinhas:** este EC não aceita curva gravada no firmware (testado: tabela `0xF00` e Office Advanced são ignorados).
  O serviço lê a temperatura a cada 2 s e aplica a curva com histerese e rampa. Mínimo de 25 %, emergência a 100 %
  (CPU ≥ 92 °C / GPU ≥ 87 °C), e devolve ao firmware ao parar, suspender, dar erro ou detectar o Avell Custom Control.
- **Teclado:** cor única, uma cor por zona, ou efeitos do firmware com paleta. A configuração é gravada na memória do teclado.

## Documentação

- [docs/HARDWARE.md](docs/HARDWARE.md): protocolo do EC (WMI), registradores, todos os testes de ventoinha, protocolo ITE 8291.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): arquitetura, regras de segurança, decisão da curva por software, fases.

## Estrutura

```
src/AvellCC.Core     EC (WMI), controle direto, controlador de curva, loop, teclado, backups, settings
src/AvellCC.Ipc      protocolo, servidor e cliente do named pipe
src/AvellCC.Service  serviço Windows (eventos de energia, reinício automático)
src/AvellCC.App      UI WPF + bandeja
src/AvellCC.Cli      avellcc.exe: diagnóstico e testes de hardware (recusa gravar com o serviço rodando)
tests/               110 testes (FakeEc, transporte HID falso, pipe real)
tools/               diag (PowerShell, só leitura), install.ps1, uninstall.ps1
curves/              curvas de exemplo e sondas usadas nos testes de hardware
```

## Desenvolver sem hardware

```powershell
dotnet build AvellCC.sln -c Release
dotnet test AvellCC.sln
.\src\AvellCC.Service\bin\x64\Release\net8.0-windows\AvellCC.Service.exe --simulate
.\src\AvellCC.App\bin\x64\Release\net8.0-windows\AvellCC.exe --simulate
```

O `--simulate` usa um EC simulado (carga em onda, modelo térmico) e um pipe separado (`AvellCC.Control.sim`).
Não exige administrador e não toca no hardware.

## Referências

- [tuxedo-drivers](https://github.com/tuxedocomputers/tuxedo-drivers) (Linux, GPL): de onde veio o mecanismo de controle direto para EC Uniwill legado.
- [Clevo-Monster-RGB-Controller](https://github.com/gutsv-project/Clevo-Monster-RGB-Controller), [custom-clevo-laptop-controller-windows-app](https://github.com/farukborann/custom-clevo-laptop-controller-windows-app), [BtoFanControl](https://github.com/Marqis/BtoFanControl):
  laptops **Clevo**. Serviram de referência de arquitetura (tray, startup, regras de curva, histerese), não de protocolo.
