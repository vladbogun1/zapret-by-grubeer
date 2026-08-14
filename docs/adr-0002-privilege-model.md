# ADR-0002 — Privileged service plus unelevated UI

Date: 2026-08-14
Status: accepted

## Context

The engine needs administrative rights: `winws.exe` loads the WinDivert kernel driver,
service installation calls `sc create`, the hosts file and `netsh interface tcp` are
machine-wide. Upstream solves this by relaunching `service.bat` through
`Start-Process -Verb RunAs`, so every action costs a UAC prompt and a console window.

The manager additionally has to autostart quietly, sit in the tray, show toast
notifications, supervise the engine, and perform transactional engine updates — none of
which fit "prompt the user for elevation each time".

Three options were considered.

1. **Elevated UI** (`requireAdministrator` on the app): one prompt per launch, no tray
   autostart without scheduled-task tricks, drag-and-drop from Explorer broken, toast
   notifications and accessibility degraded in an elevated process, and the whole UI running
   as administrator for the sake of a few operations.
2. **On-demand elevation** (spawn an elevated helper per action): a UAC prompt for every
   start/stop, impossible to supervise a long-running engine, no autostart of the engine.
3. **Privileged service plus unelevated UI**: the model used by Windows itself, PowerToys'
   elevated components, and comparable network tools.

## Decision

Option 3.

* `ZapretByGrubeer.Service` — Windows service, `LocalSystem`, `start=auto`, installed and
  started by the installer. It owns: engine start/stop and supervision, service creation and
  removal, WinDivert cleanup, the `netsh` timestamp fix, hosts-file managed section, all
  writes under `%ProgramData%\ZapretByGrubeer`, and engine update transactions including
  rollback.
* `ZapretByGrubeer.exe` — the UI, `asInvoker`, launched normally or at logon into the tray.
  No UAC prompt, so autostart is silent; notifications, accessibility and DPI behave
  normally.
* Transport — named pipe `\\.\pipe\ZapretByGrubeer`, newline-delimited JSON, one request per
  response, no dynamic type resolution in the serializer.

## Authorization

* The pipe ACL grants `Authenticated Users` read/write so any signed-in user can *query*
  state.
* Mutating operations require the caller's token to be a member of the local Administrators
  group, checked by impersonating the client on the pipe and inspecting the token. A
  non-administrator receives a structured `Unauthorized`, and the UI reflects this as
  read-only mode with an explanation rather than a silent failure.
* Every mutating request is logged with the caller SID, the operation, and its outcome.
* The service never accepts a path, executable, or URL chosen by the client. Clients name
  intents (`ApplyStrategy { id }`, `UpdateEngine { tag }`); the service resolves them against
  its own state and against the active runtime directory. This is what keeps a
  standard-user-reachable pipe from becoming a local privilege-escalation primitive.

## Consequences

* Two processes to install, version and update; the installer and the self-updater must keep
  them in lockstep, and the IPC payloads carry a protocol version so a mismatched pair
  reports "restart required" instead of misbehaving.
* The UI must be fully functional in read-only mode: the service may be stopped, or the user
  may not be an administrator. Every privileged action in the UI therefore has a defined
  disabled state with a reason.
* A single elevated "repair service" action remains in the UI as the recovery path when the
  service is missing or stopped.
* Uninstall is simpler and safer: one owner of every privileged change, so the uninstaller
  knows exactly what to undo and what to leave alone.
