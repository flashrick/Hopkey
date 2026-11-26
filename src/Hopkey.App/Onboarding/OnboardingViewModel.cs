using System.Windows.Threading;
using Hopkey.App.Infrastructure;
using Hopkey.App.Mvvm;
using Hopkey.App.Tray;
using Hopkey.Core.Config;

namespace Hopkey.App.Onboarding;

/// <summary>Welcome guide: 3 pages (try the hotkey → find the tray icon → more tricks). See docs/ONBOARDING.md.</summary>
public sealed class OnboardingViewModel : ObservableObject, IDisposable
{
    public const int PageCount = 3;

    private readonly SettingsStore _settings;
    private readonly Func<bool> _isPaused;
    private readonly DispatcherTimer _trayTimer;
    private int _page;
    private bool _triedLauncher;
    private TrayPromotion _tray = TrayPromotion.Unknown;

    public event Action? OpenTaskbarSettingsRequested;

    public OnboardingViewModel(SettingsStore settings, bool isFirstRun, Func<bool> isPaused)
    {
        _settings = settings;
        _isPaused = isPaused;
        IsFirstRun = isFirstRun;
        _trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _trayTimer.Tick += (_, _) => Tray = TrayPromotionProbe.Read();
        LocalizationManager.LanguageChanged += OnLanguageChanged;
        _settings.Changed += OnSettingsChanged;
        RefreshHotkeys();
    }

    // ---------------- Paging
    public int Page
    {
        get => _page;
        set
        {
            value = Math.Clamp(value, 0, PageCount - 1);
            if (!Set(ref _page, value)) return;
            OnPropertyChanged(nameof(IsFirst));
            OnPropertyChanged(nameof(IsLast));
            OnPropertyChanged(nameof(NextText));
            if (value == 1) { Tray = TrayPromotionProbe.Read(); _trayTimer.Start(); }
            else _trayTimer.Stop();
        }
    }

    public bool IsFirst => _page == 0;
    public bool IsLast => _page == PageCount - 1;
    public string NextText => LocalizationManager.T(IsLast ? "Onboarding.Finish" : "Onboarding.Next");

    // ---------------- Page 1
    public bool IsFirstRun { get; }
    public bool IsPaused => _isPaused();
    public bool ShowIndexingHint => IsFirstRun && !IsPaused;
    public IReadOnlyList<string> LauncherKeys { get; private set; } = Array.Empty<string>();
    public bool LauncherIsDoubleTap { get; private set; }
    public string LauncherVerb => LocalizationManager.T(LauncherIsDoubleTap ? "Onboarding.P1.DoubleTap" : "Onboarding.P1.Press");
    public IReadOnlyList<string> AltKeys { get; private set; } = Array.Empty<string>();
    public bool HasAltKeys => AltKeys.Count > 0;

    public bool TriedLauncher
    {
        get => _triedLauncher;
        private set => Set(ref _triedLauncher, value);
    }

    public void MarkLauncherTried()
    {
        if (!IsPaused) TriedLauncher = true;
    }

    // ---------------- Page 2
    public TrayPromotion Tray
    {
        get => _tray;
        private set
        {
            if (!Set(ref _tray, value)) return;
            OnPropertyChanged(nameof(TrayPromoted));
            OnPropertyChanged(nameof(TrayHidden));
            OnPropertyChanged(nameof(TrayUnknown));
        }
    }
    public bool TrayPromoted => _tray == TrayPromotion.Promoted;
    public bool TrayHidden => _tray == TrayPromotion.Hidden;
    public bool TrayUnknown => _tray == TrayPromotion.Unknown;

    public bool Autostart
    {
        get => _settings.Current.General.Autostart;
        set { if (value != Autostart) _settings.Update(s => s.General.Autostart = value); OnPropertyChanged(); }
    }

    public void RequestOpenTaskbarSettings() => OpenTaskbarSettingsRequested?.Invoke();

    // ---------------- Page 3
    public IReadOnlyList<string> DialogJumpKeys { get; private set; } = Array.Empty<string>();
    public bool ShowDialogJumpTip => DialogJumpKeys.Count > 0;
    public bool ShowToolbarTip { get; private set; }
    public bool ShowTypeToSearchTip { get; private set; }
    public bool ShowFollowTip { get; private set; }
    public bool ShowDialogsCard => ShowFollowTip || ShowToolbarTip || ShowDialogJumpTip || ShowTypeToSearchTip;    public IReadOnlyList<string> TabKeys { get; } = new[] { "Tab" };
    public IReadOnlyList<string> OpenLocationKeys { get; } = new[] { "Ctrl", "Enter" };
    public IReadOnlyList<string> DirectKeys { get; } = new[] { "Alt", "1…9" };
    public IReadOnlyList<string> TypeKeys { get; } = new[] { "A", "B", "C" };

    // ---------------- Language
    public bool IsZh
    {
        get => _settings.Current.General.Language != "en";
        set { if (value) SetLanguage("zh-CN"); }
    }
    public bool IsEn
    {
        get => _settings.Current.General.Language == "en";
        set { if (value) SetLanguage("en"); }
    }

    private void SetLanguage(string lang)
    {
        if (_settings.Current.General.Language != lang) _settings.Update(s => s.General.Language = lang);
    }

    public void RefreshHotkeys()
    {
        var s = _settings.Current;
        LauncherKeys = OnboardingPolicy.Keycaps(s.Hotkeys.Launcher);
        LauncherIsDoubleTap = HotkeySpec.Parse(s.Hotkeys.Launcher).IsDoubleCtrl;
        AltKeys = OnboardingPolicy.Keycaps(s.Hotkeys.LauncherAlt);
        DialogJumpKeys = s.QuickSwitch.Enabled && s.QuickSwitch.DialogJumpHotkey
            ? OnboardingPolicy.Keycaps(s.Hotkeys.DialogJump) : Array.Empty<string>();
        ShowFollowTip = s.QuickSwitch.Enabled && s.QuickSwitch.FollowExplorer;
        ShowToolbarTip = s.QuickSwitch.Enabled && s.QuickSwitch.ShowToolbar;
        ShowTypeToSearchTip = s.Hotkeys.TypeToSearch;
        OnPropertyChanged(null);
    }

    /// <summary>Re-reads the paused state (tray Pause/Resume while the guide is open).</summary>
    public void RefreshPaused()
    {
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(ShowIndexingHint));
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(LauncherVerb));
        OnPropertyChanged(nameof(IsZh));
        OnPropertyChanged(nameof(IsEn));
    }

    // Settings may change from the settings window (hotkeys, QuickSwitch, autostart) or from this guide (language).
    private void OnSettingsChanged(AppSettings o, AppSettings n) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(RefreshHotkeys);

    public void Dispose()
    {
        _trayTimer.Stop();
        LocalizationManager.LanguageChanged -= OnLanguageChanged;
        _settings.Changed -= OnSettingsChanged;
    }
}
