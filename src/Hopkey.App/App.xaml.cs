using System.Windows;
using Hopkey.App.Bootstrap;

namespace Hopkey.App;

public partial class App : Application
{
    private readonly StartupOptions _options;
    private AppHost? _host;

    public App(StartupOptions options)
    {
        _options = options;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashHandler.Install(this);
        _host = new AppHost(this, _options);
        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Shutdown();
        base.OnExit(e);
    }
}
