# AvellCC — Avell Custom Control Center

Controle de RGB do teclado e curvas de ventoinha para o **Avell A65/C65 LIV** (plataforma **Uniwill**).

> ⚠️ **Só um controlador por vez.** Pare o *Avell Custom Control* antes de usar o AvellCC para alterar qualquer coisa.
> Os dois escrevem nos mesmos registradores do EC e nos mesmos dispositivos do teclado.
> Ver [docs/ARCHITECTURE.md — Regra 1](docs/ARCHITECTURE.md#regra-1--só-um-controlador-roda-por-vez).

> 🛟 **Backup antes de tudo.** A primeira tabela de ventoinha lida do EC é salva em
> `%ProgramData%\AvellCC\backup\fan-table.original.json` e **nunca é sobrescrita**.
> Nenhuma escrita na tabela acontece sem esse arquivo.

## Documentação

- [docs/HARDWARE.md](docs/HARDWARE.md): protocolo do EC (WMI), mapa de registradores, tabela de ventoinha, protocolo ITE 8291.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): arquitetura, regras de segurança, fases.

## Estrutura

```
src/AvellCC.Core     protocolos e regras (EC via WMI, tabela de fan, backup, pacotes ITE 8291)
src/AvellCC.Cli      avellcc.exe (status, fan show/backup/restore-original)
tests/               testes do Core (rodam sem hardware, com FakeEc)
tools/diag           diagnóstico em PowerShell, SOMENTE LEITURA, não precisa compilar
```

## Passo 1 — diagnóstico (somente leitura)

Em um PowerShell **"Executar como administrador"**, na pasta do projeto:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\diag\Invoke-AvellDiag.ps1 -Samples 10
```

O script só usa o formato de **leitura** do EC e não tem nenhuma função de escrita.
Ele cria o backup original (se ainda não existir) e gera o relatório em `diag-output\`.

## Build

Requer o **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`).

```powershell
dotnet build AvellCC.sln -c Release
dotnet test AvellCC.sln
```

## Referências

Os projetos [Clevo-Monster-RGB-Controller](https://github.com/gutsv-project/Clevo-Monster-RGB-Controller),
[custom-clevo-laptop-controller-windows-app](https://github.com/farukborann/custom-clevo-laptop-controller-windows-app) e
[BtoFanControl](https://github.com/Marqis/BtoFanControl) são de laptops **Clevo** (`InsydeDCHU.dll` / `ClevoEcInfo.dll`).
Eles serviram de referência de arquitetura (tray, startup, travas, regras da curva), mas **não** de protocolo, que aqui é Uniwill.
