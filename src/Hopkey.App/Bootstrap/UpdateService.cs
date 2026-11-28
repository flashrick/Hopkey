using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Hopkey.App.Infrastructure;
using Hopkey.App.Tray;
using Hopkey.Core.Config;
using Hopkey.Core.Native;
using Hopkey.Core.Update;
using Hopkey.Core.Util;

namespace Hopkey.App.Bootstrap;

/// <summary>
/// Checks GitHub for a newer release (shortly after startup, then daily, if enabled) and runs the update the user
/// asks for: download → SHA-256 check → unpack to %LocalAppData%\Hopkey\updates → start the updater (the new
/// Hopkey.exe in --apply-update mode) → exit. Nothing is installed without the user clicking "update".
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly Dispatcher _ui;
    private readonly SettingsStore _settings;
    private readonly TrayIconService _tray;
    private readonly DispatcherTimer _timer;
    private string? _notifiedTag;
    private bool _busy;

    public ReleaseInfo? Available { get; private set; }
    /// <summary>Short status for the settings page.</summary>
    public string Status { get; private set; } = "";
    public event Action? StatusChanged;

    public UpdateService(Dispatcher ui, SettingsStore settings, TrayIconService tray)
    {
        _ui = ui;
        _settings = settings;
        _tray = tray;
        _tray.UpdateRequested += () => _ = UpdateNowAsync();
        _timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = FirstCheckDelay };
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = CheckInterval;
            if (_settings.Current.General.CheckForUpdates) _ = CheckAsync(manual: false);
        };
    }

    public void Start(UpdateOutcome outcome)
    {
        if (outcome == UpdateOutcome.Updated)
            _tray.ShowBalloon(LocalizationManager.F("Update.Done", UpdateChecker.CurrentVersion.ToString(3)));
        // Old staging folders and the backup: the updater has exited by the time we're running again.
        Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(20)); UpdateChecker.CleanUp(); });
        _timer.Start();
    }

    /// <summary>Check now. Manual checks report the result in a dialog and offer to update.</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        SetStatus(LocalizationManager.T("Update.Checking"));
        try
        {
            var latest = await Task.Run(() => UpdateChecker.GetLatestAsync(CancellationToken.None));
            var current = UpdateChecker.CurrentVersion;
            Available = latest != null && UpdateChecker.IsNewer(latest, current) ? latest : null;
            _tray.SetUpdate(Available?.Tag);
            if (Available == null)
            {
                SetStatus(LocalizationManager.F("Update.UpToDate", "v" + current.ToString(3)));
                Logger.Info($"Update check: up to date ({current.ToString(3)}; latest {latest?.Tag ?? "none"})");
                if (manual) MessageBox.Show(Status, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SetStatus(LocalizationManager.F("Update.AvailableShort", Available.Tag));
            Logger.Info($"Update check: {Available.Tag} available (have {current.ToString(3)})");
            if (manual)
            {
                _busy = false;
                await UpdateNowAsync();
            }
            else if (_notifiedTag != Available.Tag)
            {
                _notifiedTag = Available.Tag;
                _tray.ShowBalloon(LocalizationManager.F("Update.Available", Available.Tag), onClick: () => _ = UpdateNowAsync());
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Update check failed", ex);
            SetStatus(LocalizationManager.F("Update.CheckFailed", ex.Message));
            if (manual) MessageBox.Show(Status, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; }
    }

    /// <summary>Ask, download, verify, then hand over to the updater and exit.</summary>
    public async Task UpdateNowAsync()
    {
        var r = Available;
        if (r == null || _busy) return;
        var answer = MessageBox.Show(
            LocalizationManager.F("Update.Confirm", r.Tag, "v" + UpdateChecker.CurrentVersion.ToString(3)),
            "Hopkey", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return;
        _busy = true;
        try
        {
            SetStatus(LocalizationManager.F("Update.Downloading", r.Tag));
            _tray.ShowBalloon(Status);
            var stage = await Task.Run(() => UpdateChecker.DownloadAndStageAsync(r, CancellationToken.None));
            if (!StartUpdater(stage)) return;
            Logger.Info($"Updater started for {r.Tag}; exiting");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.Warn("Update download failed", ex);
            SetStatus(LocalizationManager.F("Update.DownloadFailed", ex.Message));
            MessageBox.Show(Status, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; }
    }

    private bool StartUpdater(string stage)
    {
        var target = PathUtil.TrimEndSeparator(AppContext.BaseDirectory);
        var updater = Path.Combine(stage, "Hopkey.exe");
        var args = new[] { "--apply-update", stage, target, "--wait-pid", Environment.ProcessId.ToString(), "--restart" };
        // Elevation only when the install folder really needs it (e.g. Program Files while not running elevated).
        bool needsAdmin = !ElevationHelper.IsSelfElevated && !UpdateApplier.CanWrite(target);
        if (needsAdmin)
        {
            var line = string.Join(' ', args.Select(Quote));
            if (Shell32.ShellExecute(updater, line, "runas", stage)) return true;
            SetStatus(LocalizationManager.T("Update.ElevationDeclined"));
            MessageBox.Show(Status, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var psi = new ProcessStartInfo(updater) { UseShellExecute = false, WorkingDirectory = stage };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
        return true;
    }

    private static string Quote(string s) => s.Contains(' ') || s.Contains('"') ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;

    private void SetStatus(string s)
    {
        Status = s;
        StatusChanged?.Invoke();
    }

    public void Dispose() => _timer.Stop();
}
