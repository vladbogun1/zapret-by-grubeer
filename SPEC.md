# Запрет by Grubeer — Product Specification

Status: authoritative, consolidated
Last updated: 2026-08-14

This document is the single source of truth for the product. It merges the baseline
product requirements with the authoritative naming/installation/UI/update requirements
supplied on 2026-08-13, which override any conflicting earlier assumption.

Companion documents:

* [`docs/flowseal-compatibility.md`](docs/flowseal-compatibility.md) — upstream contract, capability detection, validation, rollback.
* [`docs/adr-0001-ui-stack.md`](docs/adr-0001-ui-stack.md) — why WPF + WPF UI instead of WinUI 3.
* [`docs/adr-0002-privilege-model.md`](docs/adr-0002-privilege-model.md) — service + non-elevated UI split.

---

## 1. Identity

| Field | Value |
| --- | --- |
| Product name (display, everywhere) | **Запрет by Grubeer** |
| Executable | `ZapretByGrubeer.exe` |
| Company | Grubeer |
| Manager version | independent SemVer, starts at `1.0.0` |
| Upstream engine | `Flowseal/zapret-discord-youtube` ("Flowseal Zapret") |

The display name `Запрет by Grubeer` is used in: installer, Windows *Installed apps*,
Start Menu shortcut, window title, About screen, update dialogs, executable metadata
(`ProductName`, `FileDescription`), uninstaller, and log headers. ASCII-safe internal
names (`ZapretByGrubeer.exe`, `ZapretByGrubeer` service, `%ProgramData%\ZapretByGrubeer`)
are used for anything a filesystem, service control manager, or upstream script touches.

**Запрет by Grubeer** is the Windows manager: GUI, lifecycle controller, updater.
**Flowseal/zapret-discord-youtube** is the upstream network engine and is never renamed,
never modified in place, and never presented as being authored by this project. The
application must not present itself as an official Flowseal application. Every screen that
shows an engine version labels it as *Flowseal Zapret*.

The Flowseal engine number (e.g. `1.10.1`) is never used as the manager version.

---

## 2. Scope

### 2.1 What the manager does

1. Installs, updates, validates, and rolls back the upstream Zapret engine.
2. Discovers the strategies a given engine build ships, with no hardcoded list.
3. Starts/stops a selected strategy, either as a supervised process or a Windows service.
4. Monitors engine health and reports unexpected stops.
5. Exposes upstream settings that exist in the installed build: game filter, IPSet filter,
   user domain/IP lists, active fake replacement, hosts file updates, IPSet list updates.
6. Runs upstream diagnostics and strategy tests when the installed build provides them.
7. Notifies about new manager releases and new engine releases, separately.
8. Uninstalls cleanly, without damaging network configuration it does not own.

### 2.2 Non-goals

* No modification of upstream engine sources or behaviour.
* No portable/no-install distribution as a primary target (see §12).
* No custom always-on popup framework, no gaming-style skin, no browser shell.
* No bundled VPN, no proxy, no traffic logging of user content.

---

## 3. Platform and stack

* Windows 11 primary target; Windows 10 22H2 x64 supported as a floor.
* x64 only (upstream ships `WinDivert64.sys` and an x64 `winws.exe`).
* **UI: WPF on .NET 8 (LTS) with WPF UI 4.3.x** for Fluent/Windows 11 styling.
  Rationale and the WinUI 3 evaluation are in `docs/adr-0001-ui-stack.md`.
* Privileged work runs in a manager-owned Windows service; the UI runs unelevated.
  Rationale in `docs/adr-0002-privilege-model.md`.

The result must look at home next to Windows 11 Settings, PowerToys, and Dev Home:
native typography (Segoe UI Variable), standard Windows spacing, native controls,
`NavigationView`-style navigation, real focus visuals, full keyboard operation,
screen-reader names on every control, per-monitor DPI v2, multi-monitor correctness,
light/dark/system themes, Windows accent colour where appropriate, and native toast
notifications. Custom drawing is limited to the status indicator and icons.

---

## 4. Solution layout

```text
zapret-grubeer/
    Zapret.Core/        engine adapter, discovery, GitHub client, models, config
    Zapret.Service/     Windows service: privileged lifecycle + IPC endpoint
    Zapret.App/         WPF UI + tray (unelevated)
    Zapret.Tests/       unit tests, fixtures with real upstream .bat files
    installer/          Inno Setup script and assets
    docs/               specification satellites
```

---

## 5. Filesystem layout

Application (user-selectable at install time, default shown):

```text
C:\Program Files\Zapret by Grubeer\
    ZapretByGrubeer.exe          UI + tray
    ZapretByGrubeer.Service.exe  privileged service
    (dependencies)
```

Machine-wide mutable state, always ASCII, never inside Program Files:

```text
%ProgramData%\ZapretByGrubeer\
    runtime\
        versions\1.10.1\        an extracted upstream build, verbatim
        versions\1.10.2\
        staging\                download + inspection area for a candidate
        current.json            which version is active, and why
    data\
        settings.json           manager settings
        engine.json             selected strategy, service mode, upstream toggles
        lists\                  user-owned domain/IP lists, survive engine updates
        backups\hosts\          timestamped hosts backups
    logs\
        manager-YYYYMMDD.log
        service-YYYYMMDD.log
        engine-YYYYMMDD.log
```

Per-user state:

```text
%LocalAppData%\ZapretByGrubeer\
    ui.json                     window placement, last page, theme override
    cache\github\               ETag cache for release metadata
```

Rules:

* The install directory is treated as read-only at runtime.
* Nothing mutable is written next to the executable.
* The install directory may be on any drive (`D:\Programs\Zapret by Grubeer` must work).
  Paths are always resolved from the running assembly location or from
  `Environment.GetFolderPath`; drive `C:` is never assumed.
* The engine runtime path must stay ASCII, free of Cyrillic and of characters that break
  the upstream cygwin-based `winws.exe` and its `.bat` scripts. The display name stays
  `Запрет by Grubeer` regardless. If a user picks a Cyrillic install path for the UI, the
  engine is still placed under `%ProgramData%\ZapretByGrubeer\runtime`, so upstream is
  unaffected.

---

## 6. Privilege model

* `ZapretByGrubeer.Service` — Windows service, `LocalSystem`, `start=auto`. Owns every
  privileged operation: creating/removing the engine service, starting/stopping
  `winws.exe`, WinDivert driver cleanup, `netsh` TCP timestamp fix, hosts file edits,
  writing `%ProgramData%` state, performing engine update transactions.
* `ZapretByGrubeer.exe` — the UI, runs as the signed-in user with `asInvoker`. No UAC
  prompt on launch, so autostart-to-tray is silent, notifications and accessibility work
  normally.
* IPC: named pipe `\\.\pipe\ZapretByGrubeer`, JSON request/response, pipe ACL granting
  read/write to `Authenticated Users` for query operations and requiring proof of an
  Administrators-group caller token for mutating operations. Every mutating request is
  logged with the caller SID.
* If the service is missing or stopped, the UI stays usable in read-only mode and offers
  a single elevated repair action.

---

## 7. Engine lifecycle

Two run modes, user-selectable:

1. **Managed process** — the service launches `bin\winws.exe` with parsed arguments as a
   child process, supervises it, and restarts it on unexpected exit (with backoff and a
   notification after repeated failures). Chosen by default: it gives accurate status,
   clean stop, and log capture.
2. **Windows service (upstream-compatible)** — replicates upstream `service.bat`
   behaviour: service name `zapret`, display name `zapret`, `start=auto`,
   `binPath = "<runtime>\bin\winws.exe" <args>`, plus the upstream registry marker
   `HKLM\SYSTEM\CurrentControlSet\Services\zapret /v zapret-discord-youtube = <strategy>`.
   This keeps a manager-installed engine recognisable to upstream tooling and vice versa.

Both modes derive arguments from the selected strategy `.bat` through the manager's own
parser (`docs/flowseal-compatibility.md` §4), not by executing the batch file.

The manager detects an engine installed by upstream tooling (existing `zapret` service or
registry marker) and adopts it instead of fighting it, showing "engine installed outside
Запрет by Grubeer" and offering to take over.

---

## 8. Updates

### 8.1 Manager updates

* Source: this project's own GitHub repository; the URL is configuration, set during
  development (`ManagerUpdateOptions.RepositoryUrl`), not scattered constants.
* Startup check is asynchronous and never blocks the window appearing.
* Notification is native and non-intrusive:

  > **New Запрет by Grubeer version available**
  > Version 1.3.0 is available.
  > `[View changes]` `[Update]` `[Later]`

* Never a silent replacement by default. Never forced, unless a release is explicitly
  flagged critical for compatibility.
* Settings: *Automatically check for updates*, *Notify me when a new version is available*.
  *Automatically download updates* is reserved for later and off.

### 8.2 Engine updates

* Source: `Flowseal/zapret-discord-youtube` GitHub Releases.
* Notification carries context, not just "update available":

  > **Flowseal engine 1.10.2 available**
  > Your current engine is 1.10.1.
  > The update contains new or modified bypass strategies.
  > Your custom services and settings will be preserved.
  > `[Update]` `[View changes]` `[Later]`

* Manager updates and engine updates are visually and textually distinct; a user must
  never be unsure which component an update refers to.
* The transaction, validation, and rollback rules are normative and specified in
  `docs/flowseal-compatibility.md` §6–§8.

### 8.3 Release polling

* Checked at startup, then at most once every 6 hours while running, plus manual check.
* Persisted per feed: `lastCheckUtc`, `lastSeenTag`, `etag`, `dismissedTag`.
* A dismissed release is never re-announced.
* `If-None-Match` on every request; unauthenticated GitHub API only, no token required.
* Stable releases only. Drafts and prereleases are ignored unless
  *Settings → Updates → Allow preview engine releases* is enabled; off by default.

### 8.4 GitHub unavailable

Update checking is optional functionality. When GitHub is unreachable the manager does not
disable Zapret, does not block startup, and does not raise alarming errors. The Updates
page shows "Could not check for updates." and the installed engine keeps running.

### 8.5 Updates page

```text
Запрет by Grubeer      Installed 1.2.0   Latest 1.2.0   ✓ Up to date   [Check for updates]
Flowseal engine        Installed 1.10.1  Latest 1.10.1  ✓ Up to date   [Check for updates]

☑ Check for updates automatically
☑ Notify about new Запрет by Grubeer versions
☑ Notify about new Flowseal engine versions
☐ Allow preview releases
```

Release notes are shown before installing. Markdown is rendered as native text; HTML and
scripts from release notes are never executed or interpreted.

### 8.6 Post-update report

```text
Zapret engine updated
1.10.1 → 1.10.2
17 strategies discovered.
Selected strategy: ALT11
Status: ✓ Running     Discord: ✓     YouTube: ✓
[Done]
```

If the previously selected strategy no longer exists in the new build, the manager says so
explicitly and proposes the closest compatible strategy; it never silently switches to a
different strategy.

---

## 9. UI structure

| Page | Contents |
| --- | --- |
| Home | engine state, selected strategy, start/stop, uptime, last error, quick reachability check for Discord/YouTube |
| Strategies | dynamically discovered catalog, description, apply, run upstream tests when available |
| Services | run mode (managed process / Windows service), autostart, install/remove engine service |
| Lists | user domain and IP lists, edited safely, preserved across engine updates |
| Diagnostics | upstream diagnostics when available, manager self-check, log access |
| Updates | §8.5 |
| Settings | theme, notifications, update behaviour, run mode, engine location, advanced |
| About | product name, manager version, engine version, upstream credit and license |

Interaction rules:

* Single instance: launching again activates the existing window rather than starting a
  second controller. Privileged helper processes are exempt.
* The main window renders before any slow work. GitHub queries, strategy benchmarks,
  diagnostics, and network enumeration are asynchronous, never on the startup path.
* Notifications are native Windows toasts, optional, and used for: engine update
  available, current strategy stopped unexpectedly, strategy testing completed.

---

## 10. Installer

Inno Setup, per-machine, x64, elevated.

* Pages: install location (default `C:\Program Files\Zapret by Grubeer`, `Browse…`, any
  drive), Start Menu shortcut, desktop shortcut (unchecked by default), launch after
  install.
* No Flowseal-technical questions during installation. The engine is downloaded and
  configured on first run.
* Registers and starts `ZapretByGrubeer` service.
* Adds one Start Menu entry: *Запрет by Grubeer*. No extra shortcuts; utility actions live
  inside the application.
* `Installed apps` metadata: DisplayName `Запрет by Grubeer`, publisher `Grubeer`, version
  = manager version, icon at all required resolutions.

### 10.1 Uninstall

Windows Settings → Installed apps → *Запрет by Grubeer* → Uninstall must work, and asks:

* Remove only Запрет by Grubeer, **or** remove Запрет by Grubeer and the installed Zapret engine.
* Optional checkbox: preserve my settings and custom service lists.

The uninstaller stops manager-owned processes, removes manager-owned autostart, scheduled
tasks and services, removes the managed hosts section, removes engine runtime if selected,
removes application files, and leaves every unrelated network setting alone. Anything the
manager did not create, it does not delete.

---

## 11. Forward compatibility

This is a first-class requirement, detailed in `docs/flowseal-compatibility.md`. Summary of
the invariants every feature is checked against:

* No fixed snapshot of Flowseal. No hardcoded strategy list, count, or filenames.
* Capabilities are detected from the installed build, never assumed.
* A new upstream strategy appears in the UI with no manager release.
* An upstream release newer than anything tested is validated, not rejected.
* Unknown or removed upstream features disable the affected control with an explanation
  and a log entry; they never crash the application.
* A working engine is never destroyed because an unfamiliar release exists.
* Version-specific adapters are added only when a real upstream break requires one.

The question asked of every feature: *will this break merely because Flowseal added another
strategy or changed a release number?* If yes, it is redesigned.

---

## 12. Portable mode

Not a priority. The primary distribution is a properly installed native Windows
application. Portable support may be revisited later only if it does not complicate the
architecture.

---

## 13. Logging and privacy

* Logs live under `%ProgramData%\ZapretByGrubeer\logs`, rolling, with the display product
  name in the header.
* Logged: manager and engine versions, discovery and capability results, strategy applied,
  service operations, update transactions and their outcomes, compatibility issues.
* Not logged: browsed hostnames beyond the user's own list edits, packet contents, or
  anything resembling traffic capture.
* No telemetry is sent anywhere. The only outbound requests are GitHub release metadata,
  release asset downloads, and the upstream-provided hosts/IPSet list URLs when the user
  triggers those actions.

---

## 14. Licensing and attribution

Upstream is redistributed under its own license (`LICENSE.txt` in each engine build), which
is shipped verbatim inside every `runtime\versions\<v>` directory and shown in About. The
About page credits `Flowseal/zapret-discord-youtube` with a link, and states that Запрет by
Grubeer is an independent manager, not affiliated with or endorsed by Flowseal.
