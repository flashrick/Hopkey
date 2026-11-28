using System.Diagnostics;
using System.Windows;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Update;
using Hopkey.Core.Util;

namespace Hopkey.App.Bootstrap;

/// <summary>
/// The updater is Hopkey.exe itself, started from the verified staging folder with
/// <c>--apply-update &lt;stage&gt; &lt;target&gt; --wait-pid &lt;pid&gt; [--restart]</c>.
/// It waits for the running Hopkey to exit, copies the new files over the install folder (any name, any place),
/// rolls back on failure and starts the installed Hopkey again.
/// To keep antivirus heuristics quiet it uses no scripts, shells, helper downloads or self-deleting tricks:
/// it is the same signed-or-not binary the user already runs, launched from %LocalAppData% (not %TEMP%).
/// </summary>
public static class UpdaterMode
{
    public sealed record Args(string Stage, string Target, int WaitPid, bool Restart);

    public static Args? Parse(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--apply-update", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= args.Length) return null;
        int pid = 0;
        int w = Array.FindIndex(args, a => a.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase));
        if (w >= 0 && w + 1 < args.Length) int.TryParse(args[w + 1], out pid);
        bool restart = args.Any(a => a.Equals("--restart", StringComparison.OrdinalIgnoreCase));
        return new Args(args[i + 1], args[i + 2], pid, restart);
    }

    public static int Run(Args a)
    {
        var version = UpdateChecker.CurrentVersion.ToString(3);
        Logger.Info($"Updater {version}: stage={a.Stage} target={a.Target} waitPid={a.WaitPid} restart={a.Restart}");
        var targetExe = Path.Combine(a.Target, "Hopkey.exe");
        if (!WaitForExit(a.WaitPid, TimeSpan.FromSeconds(60)))
        {
            Logger.Warn("Updater: the running Hopkey did not exit; update cancelled");
            Fail(LocalizationManager.T("Update.NotExited"));
            return 2;
        }
        bool ok;
        try
        {
            var backup = Path.Combine(UpdateChecker.UpdatesDirectory, "backup");
            UpdateApplier.Apply(a.Stage, a.Target, backup, TimeSpan.FromSeconds(15));
            ok = true;
        }
        catch (Exception ex)
        {
            ok = false;
            Fail(LocalizationManager.F("Update.ApplyFailed", ex.Message));
        }
        if (a.Restart)
        {
            try
            {
                // Same token as this process: an elevated Hopkey comes back elevated.
                var psi = new ProcessStartInfo(targetExe) { UseShellExecute = false, WorkingDirectory = a.Target };
                psi.ArgumentList.Add(ok ? "--updated" : "--update-failed");
                psi.ArgumentList.Add("--wait-pid");
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex) { Logger.Error("Updater: restarting Hopkey failed", ex); }
        }
        Logger.Info($"Updater finished: {(ok ? "success" : "failed, old version restored")}");
        return ok ? 0 : 1;
    }

    private static bool WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit(timeout);
        }
        catch (ArgumentException) { return true; } // already gone
        catch (Exception ex) { Logger.Warn($"Updater: waiting for {pid} failed", ex); return true; }
    }

    private static void Fail(string message)
    {
        try { MessageBox.Show(message, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
    }
}
