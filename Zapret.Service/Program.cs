using Microsoft.Extensions.DependencyInjection.Extensions;
using Zapret.Core;
using Zapret.Core.Engine;
using Zapret.Core.Flowseal;
using Zapret.Core.GitHub;
using Zapret.Core.SystemIntegration;
using Zapret.Service;

// Uninstall support. The privileged binary undoes the privileged changes, so the uninstaller never has
// to know how any of them were made (SPEC.md §10.1).
if (args.Contains("--cleanup", StringComparer.OrdinalIgnoreCase))
{
    return await Cleanup.RunAsync(
        removeEngine: args.Contains("--remove-engine", StringComparer.OrdinalIgnoreCase),
        keepSettings: args.Contains("--keep-settings", StringComparer.OrdinalIgnoreCase));
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = AppPaths.ManagerServiceName);

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider("service"));

// Every dependency is registered with an explicit factory: the constructors carry optional parameters
// for testability, and the container should not have to guess which overload was meant.
builder.Services.AddSingleton<ISettingsStore>(sp =>
    new SettingsStore(AppPaths.SettingsFile, sp.GetRequiredService<ILogger<SettingsStore>>()));

builder.Services.AddSingleton<IEngineStateStore>(sp =>
    new EngineStateStore(AppPaths.CurrentStateFile, sp.GetRequiredService<ILogger<EngineStateStore>>()));

builder.Services.AddSingleton<IFlowsealAdapter>(sp =>
    new FlowsealAdapter(sp.GetRequiredService<ILogger<FlowsealAdapter>>()));

builder.Services.AddSingleton(sp =>
    new WinwsProcessController(sp.GetRequiredService<ILogger<WinwsProcessController>>()));

builder.Services.AddSingleton(sp =>
    new UpstreamServiceEngineController(sp.GetRequiredService<ILogger<UpstreamServiceEngineController>>()));

builder.Services.AddSingleton(sp =>
    new ArchiveExtractor(sp.GetRequiredService<ILogger<ArchiveExtractor>>()));

builder.Services.AddSingleton(sp =>
    new HostsManager(AppPaths.SystemHostsFile, AppPaths.HostsBackups, sp.GetRequiredService<ILogger<HostsManager>>()));

builder.Services.AddSingleton(sp =>
    new TcpTimestamps(sp.GetRequiredService<ILogger<TcpTimestamps>>()));

// One shared HttpClient for release metadata, downloads and reachability probes.
builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(5) });

builder.Services.AddSingleton<IGitHubReleaseClient>(sp =>
    new GitHubReleaseClient(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ILogger<GitHubReleaseClient>>()));

builder.Services.AddSingleton(sp =>
    new HttpTargetProbe(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ILogger<HttpTargetProbe>>()));
builder.Services.AddSingleton<ITargetProbe>(sp => sp.GetRequiredService<HttpTargetProbe>());

// A bounded, in-memory history of what actually happened, surfaced on the dashboard.
builder.Services.AddSingleton(_ => new ManagerEventLog());

builder.Services.TryAddSingleton<EngineHost>();

builder.Services.AddHostedService<ZapretPipeServer>();

var host = builder.Build();
await host.RunAsync();
return 0;
