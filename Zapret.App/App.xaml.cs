using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Zapret.Core;
using Zapret.Core.Engine;
using WinForms = System.Windows.Forms;

namespace Zapret.App;

/// <summary>
/// Application entry point. WinForms is referenced only for the tray icon (ADR-0001), so
/// <c>Application</c> is qualified everywhere to keep WPF and WinForms types apart.
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>Passed by the installer's autostart entry so the app comes up in the tray.</summary>
    public const string TrayArgument = "--tray";

    private const string InstanceMutexName = @"Local\ZapretByGrubeer.SingleInstance";
    private const string ActivationEventName = @"Local\ZapretByGrubeer.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private WinForms.NotifyIcon? _trayIcon;
    private MainWindow? _window;

    public static ManagerClient Client { get; } = new();

    /// <summary>
    /// Shell navigation hook, set by the main window. Pages raise navigation intents by key rather than
    /// reaching into the window, so a page never depends on the shell's implementation.
    /// </summary>
    public static Action<string>? Navigate { get; set; }

    /// <summary>
    /// UI-scoped settings. The service owns the machine-wide file under %ProgramData%, which a standard
    /// user cannot write, so the UI keeps its own preferences and release-feed memory per user
    /// (ADR-0002). Anything machine-wide is changed through the service instead.
    /// </summary>
    public static ISettingsStore Settings { get; } =
        new SettingsStore(Path.Combine(AppPaths.LocalAppData, "manager.json"));

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static Zapret.Core.GitHub.GitHubReleaseClient Releases { get; } = new(Http);

    public static Zapret.Core.Update.ManagerUpdateService ManagerUpdates { get; } = new(Settings, Releases);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance: a second launch activates the existing window instead of starting a second
        // controller (SPEC.md §9).
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        AppPaths.EnsureUserDirectories();

        // A UI crash must leave evidence a user can send, not just a Windows error dialog.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("dispatcher", args.Exception);
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash("domain", args.ExceptionObject as Exception);

        base.OnStartup(e);

        // Language before any window exists, so nothing renders in the wrong language even for a frame.
        Localization.Loc.Instance.Apply(Settings.Read().Language);

        ApplyTheme();
        CreateTrayIcon();
        WatchForActivationRequests();

        try
        {
            _window = new MainWindow(Client);
        }
        catch (Exception ex)
        {
            LogCrash("startup", ex);
            throw;
        }

        var startHidden = e.Args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase);
        if (!startHidden) ShowMainWindow();

        // The window is up before anything slow happens: status, strategies and update checks are all
        // asynchronous (SPEC.md §9).
        _ = Client.RefreshAsync();
    }

    private void ApplyTheme()
    {
        var settings = Settings.Read();

        var theme = settings.ThemeOverride?.ToLowerInvariant() switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light,
        };

        ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccent: true);
    }

    public static void ApplyTheme(string? themeOverride)
    {
        var theme = themeOverride?.ToLowerInvariant() switch
        {
            "light" => ApplicationTheme.Light,
            "dark" => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Dark
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light,
        };

        ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.Mica, updateAccent: true);
    }

    private void CreateTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open " + AppPaths.DisplayName, null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var start = new WinForms.ToolStripMenuItem("Start engine", null, async (_, _) => await Client.StartAsync());
        var stop = new WinForms.ToolStripMenuItem("Stop engine", null, async (_, _) => await Client.StopAsync());
        menu.Items.Add(start);
        menu.Items.Add(stop);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Shutdown));

        menu.Opening += (_, _) =>
        {
            var running = Client.Status?.EngineStatus == EngineStatus.Running;
            start.Enabled = !running && Client.ServiceAvailable;
            stop.Enabled = running && Client.ServiceAvailable;
        };

        _trayIcon = new WinForms.NotifyIcon
        {
            // Pulled from the executable itself, so the tray, taskbar and Apps list can never disagree.
            Icon = LoadProductIcon(),
            Text = AppPaths.DisplayName,
            Visible = true,
            ContextMenuStrip = menu,
        };

        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);

        Client.Changed += UpdateTrayTooltip;
    }

    private static void LogCrash(string stage, Exception? exception)
    {
        if (exception is null) return;

        try
        {
            Directory.CreateDirectory(AppPaths.LocalAppData);
            File.AppendAllText(
                Path.Combine(AppPaths.LocalAppData, "ui-crash.log"),
                $"{DateTimeOffset.UtcNow:u} [{stage}] {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Reporting a crash must never cause one.
        }
    }

    private static Icon LoadProductIcon()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (executable is not null)
            {
                var icon = Icon.ExtractAssociatedIcon(executable);
                if (icon is not null) return icon;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Fall through to the generic icon rather than starting without a tray presence.
        }

        return SystemIcons.Application;
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null) return;

        var status = Client.Status;
        var text = !Client.ServiceAvailable
            ? $"{AppPaths.DisplayName} — service not running"
            : status?.EngineStatus switch
            {
                EngineStatus.Running => $"{AppPaths.DisplayName} — running ({status.StrategyDisplayName})",
                EngineStatus.Faulted => $"{AppPaths.DisplayName} — stopped unexpectedly",
                _ => $"{AppPaths.DisplayName} — stopped",
            };

        // The tray tooltip is limited to 63 characters by the shell.
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>Shows a native toast, falling back to a tray balloon when no AUMID is registered yet.</summary>
    public void Notify(string title, string message)
    {
        if (!Settings.Read().NotificationsEnabled) return;

        try
        {
            ToastNotifications.Show(title, message);
        }
        catch (Exception)
        {
            _trayIcon?.ShowBalloonTip(8000, title, message, WinForms.ToolTipIcon.Info);
        }
    }

    private void ShowMainWindow()
    {
        _window ??= new MainWindow(Client);

        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle))
            {
                using (handle) handle.Set();
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is shutting down; nothing to activate.
        }
    }

    private void WatchForActivationRequests()
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);

        var thread = new Thread(() =>
        {
            while (_activationEvent is not null && _activationEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, ShowMainWindow);
            }
        })
        {
            IsBackground = true,
            Name = "activation-listener",
        };

        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _activationEvent?.Dispose();

        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}

/// <summary>
/// Native Windows toasts. An unpackaged application needs a Start Menu shortcut carrying the AUMID for
/// these to appear, which the installer creates; until then the caller falls back to a tray balloon.
/// </summary>
internal static class ToastNotifications
{
    private const string ApplicationUserModelId = "Grubeer.ZapretByGrubeer";

    public static void Show(string title, string message)
    {
        var xml = new Windows.Data.Xml.Dom.XmlDocument();
        xml.LoadXml($"""
            <toast>
              <visual>
                <binding template="ToastGeneric">
                  <text>{Escape(title)}</text>
                  <text>{Escape(message)}</text>
                </binding>
              </visual>
            </toast>
            """);

        Windows.UI.Notifications.ToastNotificationManager
            .CreateToastNotifier(ApplicationUserModelId)
            .Show(new Windows.UI.Notifications.ToastNotification(xml));
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
