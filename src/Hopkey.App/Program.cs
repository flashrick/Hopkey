using System.Runtime;
using Hopkey.App.Bootstrap;
using Hopkey.Core.Config;
using Hopkey.Core.Util;

namespace Hopkey.App;

public enum UpdateOutcome { None, Updated, Failed }

public sealed record StartupOptions(bool Autostart, int WaitPid, bool ElevatedRestart, bool ForceOnboarding = false,
    UpdateOutcome Update = UpdateOutcome.None);

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Updater mode (started from the staged new version): replace the install folder's files, restart, exit.
        if (UpdaterMode.Parse(args) is { } update)
        {
            try { AppPaths.EnsureDirectories(); } catch { }
            Logger.Initialize();
            try { Infrastructure.LocalizationManager.Apply(SettingsStore.Load().Current.General.Language); } catch { }
            int rc = UpdaterMode.Run(update);
            Logger.Flush();
            return rc;
        }

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
        instance.Listen(() => app.Dispatcher.BeginInvoke(() => AppHost.Instance?.OnSecondInstance()));
        int code = app.Run();
        Logger.Info("Hopkey exited");
        Logger.Flush();
        return code;
    }

    private static StartupOptions Parse(string[] args)
    {
        bool autostart = false, elevated = false, onboarding = false;
        var update = UpdateOutcome.None;
        int waitPid = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--autostart": autostart = true; break;
                case "--elevated-restart": elevated = true; break;
                case "--onboarding": onboarding = true; break;
                case "--updated": update = UpdateOutcome.Updated; break;
                case "--update-failed": update = UpdateOutcome.Failed; break;
                case "--wait-pid" when i + 1 < args.Length:
                    int.TryParse(args[++i], out waitPid);
                    break;
            }
        }
        return new StartupOptions(autostart, waitPid, elevated, onboarding, update);
    }
}
