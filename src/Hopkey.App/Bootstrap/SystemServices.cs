using System.Diagnostics;
using System.Windows;
using Hopkey.Core.Config;
using Hopkey.Core.Native;
using Hopkey.Core.Util;
using Microsoft.Win32;

namespace Hopkey.App.Bootstrap;

/// <summary>HKCU Run autostart and the elevated scheduled-task autostart.</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Hopkey";
    private const string TaskName = "Hopkey";

    public static bool IsRunEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    /// <param name="userChange">The user just changed the setting: only then may removing the elevated task ask for UAC.</param>
    public static void Apply(GeneralSettings g, bool userChange)
    {
        try
        {
            bool elevated = ElevationHelper.IsSelfElevated;
            RemoveLegacyEntries(elevated);
            bool wantTask = g.Autostart && g.AutostartElevated;
            if (wantTask)
            {
                if (elevated) CreateElevatedTask();
                // Only one autostart entry: with both, the non-elevated Run entry can win the single-instance race.
                // Not elevated, the task can't be (re)created: keep the existing one, else fall back to the Run key.
                SetRun(!elevated && !ElevatedTaskExists());
                return;
            }
            // Autostart off, or not elevated: the elevated task must go even if "elevated" is still ticked —
            // otherwise unticking "start with Windows" leaves the app starting elevated at every logon.
            if (ElevatedTaskExists())
            {
                if (elevated) RunSchtasks($"/Delete /TN {TaskName} /F");
                else if (userChange) DeleteElevatedTaskWithPrompt(); // a non-elevated process gets "access denied" for this task
                else Logger.Info("Elevated autostart task present but disabled in settings; it is removed on the next elevated start");
            }
            SetRun(g.Autostart);
        }
        catch (Exception ex) { Logger.Warn("Autostart apply failed", ex); }
    }

    /// <summary>Autostart entries left by the app's old name (CopyListary); the current ones are re-created by Apply.</summary>
    private static void RemoveLegacyEntries(bool elevated)
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            if (k?.GetValue(AppPaths.LegacyName) != null) k.DeleteValue(AppPaths.LegacyName, false);
        if (RunSchtasks($"/Query /TN {AppPaths.LegacyName}", quiet: true) != 0) return;
        if (elevated) RunSchtasks($"/Delete /TN {AppPaths.LegacyName} /F");
        else Logger.Info($"Legacy {AppPaths.LegacyName} logon task present; it is removed on the next elevated start");
    }

    private static void SetRun(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, $"\"{AppPaths.ExecutablePath}\" --autostart");
        else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
    }

    public static bool ElevatedTaskExists() => RunSchtasks($"/Query /TN {TaskName}", quiet: true) == 0;

    /// <summary>
    /// Registers the logon task from XML: schtasks /SC ONLOGON leaves the defaults "start only on AC power",
    /// "stop when going on battery" (Task Scheduler kills the app when a laptop is unplugged), a 72-hour time limit
    /// and below-normal priority 7 — all wrong for an always-running tray app.
    /// </summary>
    private static void CreateElevatedTask()
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string exe = System.Security.SecurityElement.Escape(AppPaths.ExecutablePath);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Hopkey (run as administrator at logon)</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{sid}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--autostart</Arguments></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), $"Hopkey-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            RunSchtasks($"/Create /TN {TaskName} /XML \"{file}\" /F");
        }
        finally { try { File.Delete(file); } catch { } }
    }

    private static void DeleteElevatedTaskWithPrompt()
    {
        Logger.Info("Removing the elevated autostart task (needs administrator rights)");
        if (!Shell32.ShellExecute("schtasks.exe", $"/Delete /TN {TaskName} /F", "runas", hidden: true))
            Logger.Warn("Elevated autostart task not removed (UAC declined)");
    }

    private static int RunSchtasks(string args, bool quiet = false)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        using var p = Process.Start(psi);
        if (p == null) return -1;
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(10_000)) return -1;
        if (p.ExitCode != 0 && !quiet) Logger.Warn($"schtasks {args} failed: {err.Result.Trim()}");
        return p.ExitCode;
    }
}

/// <summary>Restart as administrator (runas); the new instance waits for this one to exit.</summary>
public static class ElevationService
{
    public static bool RestartElevated(Action beforeExit)
    {
        try
        {
            int pid = Environment.ProcessId;
            if (!Shell32.ShellExecute(AppPaths.ExecutablePath, $"--wait-pid {pid} --elevated-restart", "runas"))
                return false; // UAC cancelled
            beforeExit();
            Application.Current.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Restart as administrator failed", ex);
            return false;
        }
    }
}
