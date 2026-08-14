using System.Drawing;
using System.IO;
using System.Windows;
using Zapret.Core;
using Zapret.Core.AutoSelect;
using WinForms = System.Windows.Forms;

namespace Zapret.Shell;

/// <summary>
/// Entry point of the 2.0 interface. Single instance, tray presence, and a window that is one screen — there is
/// no navigation to set up, because there is nowhere to navigate to.
/// </summary>
public partial class App : System.Windows.Application
{
    public const string TrayArgument = "--tray";

    private const string InstanceMutex = @"Local\ZapretByGrubeer.Shell.Instance";
    private const string ActivateEvent = @"Local\ZapretByGrubeer.Shell.Activate";

    private Mutex? _instance;
    private EventWaitHandle? _activate;
    private WinForms.NotifyIcon? _tray;
    private MainWindow? _window;

    public static ProductClient Client { get; } = new();

    public static ISettingsStore Settings { get; } =
        new SettingsStore(Path.Combine(AppPaths.LocalAppData, "shell.json"));

    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, InstanceMutex, out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEvent, out var handle))
            {
                using (handle) handle.Set();
            }

            Shutdown();
            return;
        }

        AppPaths.EnsureUserDirectories();

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = false;
        };

        base.OnStartup(e);

        Text.Current.Apply(Settings.Read().Language);

        CreateTray();
        WatchForActivation();

        _window = new MainWindow();
        if (!e.Args.Contains(TrayArgument, StringComparer.OrdinalIgnoreCase)) Show();

        // The state arrives by subscription; nothing here waits on the network.
        Client.Start();
    }

    private void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Text.Current["tray.open"], null, (_, _) => Dispatcher.Invoke(Show));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Text.Current["tray.exit"], null, (_, _) => Dispatcher.Invoke(Shutdown));

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Запрет by Grubeer",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(Show);

        // The tray tooltip is the one place a stage name is worth repeating.
        Client.StateChanged += state =>
        {
            if (_tray is null) return;

            var stage = state.Stage switch
            {
                ProductStage.Working => Text.Current["stage.working"],
                ProductStage.Repairing or ProductStage.Degraded => Text.Current["stage.repairing"],
                ProductStage.Preparing => Text.Current["stage.preparing"],
                ProductStage.Stuck => Text.Current["stage.stuck"],
                ProductStage.Off => Text.Current["stage.off"],
                _ => Text.Current["stage.unavailable"],
            };

            var text = $"Запрет by Grubeer — {stage}";
            _tray.Text = text.Length > 63 ? text[..63] : text;
        };
    }

    private static Icon LoadIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is not null)
            {
                var icon = Icon.ExtractAssociatedIcon(path);
                if (icon is not null) return icon;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            // Fall through rather than start without a tray presence.
        }

        return SystemIcons.Application;
    }

    private void Show()
    {
        _window ??= new MainWindow();

        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;

        _window.Activate();
    }

    private void WatchForActivation()
    {
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEvent);

        new Thread(() =>
        {
            while (_activate is not null && _activate.WaitOne())
            {
                Dispatcher.BeginInvoke(Show);
            }
        })
        {
            IsBackground = true,
            Name = "activate-listener",
        }.Start();
    }

    private static void Log(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalAppData);
            File.AppendAllText(
                Path.Combine(AppPaths.LocalAppData, "shell-crash.log"),
                $"{DateTimeOffset.UtcNow:u} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Reporting a crash must never cause one.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _activate?.Dispose();
        Client.Dispose();

        if (_instance is not null)
        {
            try { _instance.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
            _instance.Dispose();
        }

        base.OnExit(e);
    }
}
