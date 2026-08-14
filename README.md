# Запрет by Grubeer

A native Windows 11 manager for the [Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube)
DPI bypass engine: it installs, validates, updates and rolls back the engine, discovers whatever
strategies the installed build ships, and controls the engine's lifecycle from a Fluent desktop UI.

**Запрет by Grubeer** is the manager. **Flowseal Zapret** is the upstream engine — it is redistributed
unmodified, never renamed, and this project is not affiliated with or endorsed by Flowseal.

## Documents

| Document | What it is |
| --- | --- |
| [SPEC.md](SPEC.md) | The product specification, authoritative |
| [docs/flowseal-compatibility.md](docs/flowseal-compatibility.md) | The upstream contract: discovery, capability detection, validation, update transaction, rollback |
| [docs/adr-0001-ui-stack.md](docs/adr-0001-ui-stack.md) | Why WPF + WPF UI rather than WinUI 3 |
| [docs/adr-0002-privilege-model.md](docs/adr-0002-privilege-model.md) | Why a privileged service plus an unelevated UI |

## Layout

```text
Zapret.Core/       engine adapter, strategy parser, update transaction, GitHub client, IPC contracts
Zapret.Service/    Windows service: privileged lifecycle owner and named-pipe endpoint
Zapret.App/        WPF UI and tray, runs unelevated
Zapret.Tests/      unit tests over real upstream fixtures
installer/         Inno Setup script (planned)
```

## Build

Requires the .NET 8 SDK. Visual Studio is not needed.

```bash
dotnet build ZapretByGrubeer.sln
```

```bash
dotnet test Zapret.Tests/Zapret.Tests.csproj
```

The tests run entirely offline against the real upstream `.bat` files of engine 1.10.1, checked in under
`Zapret.Tests/Fixtures/upstream/1.10.1/`. No engine binary, driver, or administrator rights are needed.

## Current state

| Area | State |
| --- | --- |
| Specification and compatibility contract | done |
| Strategy discovery, capability detection, `.bat` argument parser | done, covered by tests |
| Engine version detection, compatibility validation | done, covered by tests |
| Transactional engine install/update with rollback and retention | done, covered by tests |
| GitHub release client (ETag, dismissal, preview filtering) | done, covered by tests |
| Managed hosts section, TCP timestamp handling, target probe | done, hosts covered by tests |
| Engine controllers (managed process and upstream service mode) | done, needs on-machine verification |
| Privileged service and named-pipe IPC with per-operation authorization | done, needs on-machine verification |
| UI (Fluent shell, pages, tray, notifications) | not started |
| Manager self-update | not started |
| Installer and uninstaller | not started |

## Attribution

The upstream engine is redistributed under its own licence, shipped verbatim inside every
`runtime\versions\<version>` directory and shown in the About page.
