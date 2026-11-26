using System.Runtime;
using Hopkey.App.Bootstrap;
using Hopkey.Core.Config;
using Hopkey.Core.Util;

namespace Hopkey.App;

public sealed record StartupOptions(bool Autostart, int WaitPid, bool ElevatedRestart);

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Multi-core JIT profile: speeds up subsequent cold starts.
        try
        {
            AppPaths.MigrateFromLegacy();
            AppPaths.EnsureDirectories();
            ProfileOptimization.SetProfileRoot(AppPaths.LocalDirectory);
            ProfileOptimization.StartProfile("startup.jitprofile");
        }
        catch { }

        var options = Parse(args);
        Logger.Initialize();
        if (Environment.GetEnvironmentVariable("HOPKEY_DEBUG") == "1") Logger.MinLevel = LogLevel.Debug;
        Logger.Info($"Hopkey starting (args: {string.Join(' ', args)}), elevated={Core.Native.ElevationHelper.IsSelfElevated}");

        using var instance = new SingleInstance();
        if (!instance.TryAcquire(options.WaitPid))
        {
            Logger.Info("Another instance is running; signalling it and exiting");
            SingleInstance.SignalExisting();
            Logger.Flush();
            return 0;
        }

        var app = new App(options);
        app.InitializeComponent();
        AppHost.Instance = null;
        instance.Listen(() => app.Dispatcher.BeginInvoke(() => AppHost.Instance?.ShowLauncher()));
        int code = app.Run();
        Logger.Info("Hopkey exited");
        Logger.Flush();
        return code;
    }

    private static StartupOptions Parse(string[] args)
    {
        bool autostart = false, elevated = false;
        int waitPid = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--autostart": autostart = true; break;
                case "--elevated-restart": elevated = true; break;
                case "--wait-pid" when i + 1 < args.Length:
                    int.TryParse(args[++i], out waitPid);
                    break;
            }
        }
        return new StartupOptions(autostart, waitPid, elevated);
    }
}
