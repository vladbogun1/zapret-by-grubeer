# ADR-0001 — UI stack: WPF + WPF UI, not WinUI 3

Date: 2026-08-14
Status: accepted

## Context

The specification requires a first-class Windows 11 desktop application — native
typography, native controls, Windows 11 navigation, focus states, keyboard access,
accessibility, DPI and multi-monitor correctness, light/dark/system themes, accent colour,
native notifications — and explicitly rules out Electron, browser shells, WebView-based UI,
console wrappers, dated WinForms utilities and gaming-style skins.

It also states the preference order: **WinUI 3 first, if it can satisfy all requirements
without making installation, elevation, services, tray support, or updates fragile;
otherwise modern WPF.** That conditional is the whole decision.

The application's hard functional needs are: run privileged operations (create/remove a
Windows service, load a kernel driver via WinDivert, edit the hosts file, `netsh`), live in
the tray, autostart quietly, install to a user-chosen directory on any drive, and update
itself.

## Evaluation

**WinUI 3 / Windows App SDK**

* Elevation remains the blocker. WinRT activation in an elevated process is still not
  supported for WinUI 3; an unpackaged app manifested `requireAdministrator` is reported to
  launch without a UAC prompt and without actually being elevated. Any design that needs an
  elevated UI window is fragile by construction.
* Tray support is not part of the framework; it requires Win32 interop or a third-party
  wrapper.
* Deployment adds a runtime dependency: framework-dependent unpackaged apps need the Windows
  App SDK runtime present, or a self-contained publish that inflates the installer.
  MSIX packaging, the well-supported path, conflicts with per-machine services, arbitrary
  install directories and self-updating from GitHub Releases.
* Tooling: no Visual Studio is installed on the development machine; WinUI 3 project and
  packaging support is materially worse outside VS than WPF's.

**WPF on .NET 8 with WPF UI 4.3.x**

* Fluent Windows 11 design system, `NavigationView`, Mica/backdrop support, theme switching
  including system-follow, accent colour and Fluent System Icons — the visual target is
  reachable with standard controls and no custom drawing.
* Tray support is *not* part of WPF UI 4.x (the `NotifyIcon` control that existed in 3.x was
  removed, verified against the shipped assembly), and the obvious third-party replacement
  ships only `net10.0-windows` and `net462` assets, so on .NET 8 NuGet falls back to the
  .NET Framework build. The tray icon therefore uses `System.Windows.Forms.NotifyIcon` with
  `UseWindowsForms`, which is in-box, has no supply-chain surface, and is what most WPF tray
  applications do. No WinForms window is ever shown.
* Elevation, service installation, driver loading and `netsh` are ordinary, well-trodden
  scenarios.
* Single self-contained or framework-dependent `ZapretByGrubeer.exe`, installable anywhere,
  full Win32 version metadata, works with Inno Setup and with self-update.
* Accessibility (UI Automation), per-monitor DPI v2, and multi-monitor behaviour are mature.
* Builds with the .NET SDK alone; no Visual Studio required.

## Decision

WPF targeting `net8.0-windows` (LTS), styled with WPF UI 4.3.x. Windows 11 is the design
target; Windows 10 22H2 degrades gracefully (no Mica, otherwise identical).

WinUI 3 is rejected under the specification's own condition: it makes elevation, services,
tray support and updates fragile, which is exactly what the condition forbids.

## Consequences

* The Fluent look is a library dependency, so WPF UI is pinned and updated deliberately.
* Some Windows 11 surfaces (Mica, rounded corners) are applied through interop helpers and
  must be verified on Windows 10, where they are simply absent.
* Native toast notifications go through the Windows notification APIs with an explicit AUMID
  registered by the installer, since the app is not MSIX-packaged.
* If WinUI 3 ever supports elevated activation properly, revisiting this is a UI-layer
  rewrite only — `Zapret.Core` and `Zapret.Service` carry no UI framework dependency, which
  is a design constraint enforced by project references.
