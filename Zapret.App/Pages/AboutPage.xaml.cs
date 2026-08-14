using System.Reflection;
using System.Windows.Controls;
using Zapret.Core;

namespace Zapret.App.Pages;

public partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "1.0.0";

        // Strip the build metadata the SDK appends (+<commit hash>).
        var plus = version.IndexOf('+');
        ManagerVersionText.Text = "Version " + (plus > 0 ? version[..plus] : version);

        ManagerRepositoryLink.NavigateUri = "https://github.com/" + App.Settings.Read().ManagerRepository;

        // The engine version is whatever is installed right now; the two are independent by design.
        var engine = App.Client.Status?.EngineVersion;
        EngineVersionText.Text = engine is null
            ? "Flowseal Zapret — not installed"
            : $"Flowseal Zapret {engine}";
    }
}
