using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Hopkey.App.Infrastructure;

namespace Hopkey.App.Onboarding;

public partial class OnboardingWindow : Window
{
    private static readonly IEasingFunction s_ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private int _lastPage;

    public OnboardingViewModel ViewModel { get; }

    public OnboardingWindow(OnboardingViewModel vm)
    {
        ViewModel = vm;
        DataContext = vm;
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        vm.PropertyChanged += OnVmPropertyChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => { UpdatePageChrome(); NextButton.Focus(); };
        Closed += (_, _) => { vm.PropertyChanged -= OnVmPropertyChanged; DataContext = null; vm.Dispose(); };
    }

    private static IEasingFunction Freeze(CubicEase e) { e.Freeze(); return e; }

    public void ActivateWindow()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ForegroundHelper.Activate(new WindowInteropHelper(this).Handle);
        Activate();
    }

    public void NotifyLauncherHotkey() => ViewModel.MarkLauncherTried();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OnboardingViewModel.Page)) { UpdatePageChrome(); PlayPageAnimation(); }
    }

    private void UpdatePageChrome()
    {
        int page = ViewModel.Page;
        var pages = new FrameworkElement[] { Page0, Page1, Page2 };
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < Dots.Children.Count; i++)
        {
            var dot = (Border)Dots.Children[i];
            dot.Width = i == page ? 18 : 6;
            if (i == page) dot.SetResourceReference(Border.BackgroundProperty, "Brush.Hop");
            else dot.SetResourceReference(Border.BackgroundProperty, "Brush.Divider");
        }
        BackButton.Visibility = ViewModel.IsFirst ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = ViewModel.IsLast ? Visibility.Collapsed : Visibility.Visible;
        if (pages[page] is Panel p && p.Children.Count > 0 && p.Children[0] is TextBlock title)
            AutomationProperties.SetName(Pages, title.Text);
        NextButton.Focus();
    }

    private void PlayPageAnimation()
    {
        int page = ViewModel.Page;
        double from = page >= _lastPage ? 12 : -12;
        _lastPage = page;
        if (!SystemParameters.ClientAreaAnimation) return;
        Pages.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)) { EasingFunction = s_ease });
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(120)) { EasingFunction = s_ease });
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsLast) Close();
        else ViewModel.Page++;
    }

    private void OnBack(object sender, RoutedEventArgs e) => ViewModel.Page--;
    private void OnSkip(object sender, RoutedEventArgs e) => Close();
    private void OnOpenTaskbar(object sender, RoutedEventArgs e) => ViewModel.RequestOpenTaskbarSettings();

    private void OnDot(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && int.TryParse(t, out int page)) ViewModel.Page = page;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { Close(); e.Handled = true; }
        else if ((key == Key.Tab && mods == ModifierKeys.Control) || (key == Key.Right && mods == ModifierKeys.Alt)) { ViewModel.Page++; e.Handled = true; }
        else if ((key == Key.Tab && mods == (ModifierKeys.Control | ModifierKeys.Shift)) || (key == Key.Left && mods == ModifierKeys.Alt)) { ViewModel.Page--; e.Handled = true; }
    }
}
