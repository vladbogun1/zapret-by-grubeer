namespace Zapret.App;

/// <summary>
/// Application entry point. WinForms is referenced only for the tray icon (ADR-0001), so
/// <c>Application</c> is qualified everywhere to keep WPF and WinForms types apart.
/// </summary>
public partial class App : System.Windows.Application
{
}
