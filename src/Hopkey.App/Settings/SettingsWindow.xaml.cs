using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Config;
using Hopkey.Core.Exclusion;
using Hopkey.Core.Util;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace Hopkey.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    /// <summary>Settings-only control styles (toggle, buttons, inputs, DataGrid). Kept out of App resources so the launcher doesn't load them.</summary>
    public static readonly Uri ControlsUri = new("pack://application:,,,/Hopkey;component/Themes/SettingsControls.xaml");

    public SettingsWindow(SettingsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        ApplyChoices();
        ElevateButton.Visibility = vm.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        // Detach bindings so a closed window that is not yet collected can't keep writing into its view model.
        Closed += (_, _) => { DataContext = null; _vm.Dispose(); };
        LocalizationManager.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => LocalizationManager.LanguageChanged -= OnLanguageChanged;
        Activated += (_, _) => _vm.RefreshPerf();
    }

    private void OnLanguageChanged()
    {
        _vm.RefreshLocalization();
        ApplyChoices();
    }

    private void ApplyChoices()
    {
        KindColumn.ItemsSource = _vm.Kinds;
        TargetColumn.ItemsSource = _vm.Targets;
        // DataGrid columns are not in the visual tree, so DynamicResource headers don't refresh: set them here.
        static void H(System.Windows.Controls.DataGrid g, params string[] keys)
        {
            for (int i = 0; i < keys.Length && i < g.Columns.Count; i++) g.Columns[i].Header = LocalizationManager.T(keys[i]);
        }
        H(RulesGrid, "Settings.Index.Col.Enabled", "Settings.Index.Col.Kind", "Settings.Index.Col.Pattern", "Settings.Index.Col.Target", "Settings.Index.Col.BuiltIn");
        H(FavoritesGrid, "Settings.Fav.Alias", "Settings.Fav.Path");
        H(WebGrid, "Settings.Index.Col.Enabled", "Settings.Web.Keyword", "Settings.Web.Name", "Settings.Web.Url");
    }

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
    }

    // ---------------- General
    private void OnRestartElevated(object sender, RoutedEventArgs e) => _vm.RequestRestartElevated();

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, LocalizationManager.T("Settings.Search.ClearHistory") + "?", "Hopkey", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _vm.ClearHistory();
    }

    // ---------------- Index
    private static string? PickFolder(Window owner)
    {
        using var dlg = new WinForms.FolderBrowserDialog { UseDescriptionForTitle = true, ShowNewFolderButton = false };
        var helper = new System.Windows.Interop.WindowInteropHelper(owner);
        var result = dlg.ShowDialog(new Win32Owner(helper.Handle));
        return result == WinForms.DialogResult.OK ? dlg.SelectedPath : null;
    }

    private sealed class Win32Owner : WinForms.IWin32Window
    {
        public Win32Owner(IntPtr h) => Handle = h;
        public IntPtr Handle { get; }
    }

    private void OnAddRoot(object sender, RoutedEventArgs e)
    {
        var p = PickFolder(this);
        if (p != null) _vm.AddRoot(p);
    }

    private void OnRemoveRoot(object sender, RoutedEventArgs e)
    {
        if (RootsList.SelectedItem is string s) _vm.RemoveRoot(s);
    }

    private void OnRebuild(object sender, RoutedEventArgs e) => _vm.Rebuild();

    private void OnAddExcludeFolder(object sender, RoutedEventArgs e)
    {
        var p = PickFolder(this);
        if (p != null) _vm.AddRule(ExclusionKind.Path, p);
    }

    private void OnAddExcludeName(object sender, RoutedEventArgs e) => AddPatternRule(ExclusionKind.Name, "node_modules");
    private void OnAddExcludeGlob(object sender, RoutedEventArgs e) => AddPatternRule(ExclusionKind.Glob, @"**\build\obj");
    private void OnAddExcludeRegex(object sender, RoutedEventArgs e) => AddPatternRule(ExclusionKind.Regex, @"\\bin\\(Debug|Release)$");

    private void AddPatternRule(ExclusionKind kind, string example)
    {
        var text = InputDialog.Show(this, LocalizationManager.T("Settings.Index.NewPattern") + $"  ({example})", "");
        if (string.IsNullOrWhiteSpace(text)) return;
        var rule = new ExclusionRule(kind, text.Trim());
        var err = rule.Validate();
        if (err != null)
        {
            MessageBox.Show(this, err, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _vm.AddRule(kind, text.Trim());
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not ExclusionRuleViewModel r) return;
        if (!_vm.RemoveRule(r))
            MessageBox.Show(this, LocalizationManager.T("Settings.Index.BuiltInCannotDelete"), "Hopkey", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnImportRules(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Rules (*.json;*.txt)|*.json;*.txt|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.ImportRules(ExclusionRuleIO.Import(dlg.FileName)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnExportRules(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "hopkey-exclusions.json" };
        if (dlg.ShowDialog(this) != true) return;
        try { ExclusionRuleIO.Export(dlg.FileName, _vm.Rules.Select(r => r.Rule)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnRestoreRules(object sender, RoutedEventArgs e) => _vm.RestoreDefaultRules();

    // ---------------- Favorites
    private void OnAddFavFolder(object sender, RoutedEventArgs e)
    {
        var p = PickFolder(this);
        if (p != null) _vm.AddFavorite(p);
    }

    private void OnAddFavFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog();
        if (dlg.ShowDialog(this) == true) _vm.AddFavorite(dlg.FileName);
    }

    private void OnRemoveFav(object sender, RoutedEventArgs e)
    {
        if (FavoritesGrid.SelectedItem is FavoriteViewModel f) _vm.RemoveFavorite(f);
    }

    private void OnFavUp(object sender, RoutedEventArgs e)
    {
        if (FavoritesGrid.SelectedItem is FavoriteViewModel f) { _vm.MoveFavorite(f, -1); FavoritesGrid.SelectedItem = f; }
    }

    private void OnFavDown(object sender, RoutedEventArgs e)
    {
        if (FavoritesGrid.SelectedItem is FavoriteViewModel f) { _vm.MoveFavorite(f, 1); FavoritesGrid.SelectedItem = f; }
    }

    // ---------------- Web
    private void OnAddWeb(object sender, RoutedEventArgs e) => _vm.AddWebEngine();

    private void OnRemoveWeb(object sender, RoutedEventArgs e)
    {
        if (WebGrid.SelectedItem is WebEngineViewModel w) _vm.RemoveWebEngine(w);
    }

    private void OnRestoreWeb(object sender, RoutedEventArgs e) => _vm.RestoreDefaultWeb();

    // ---------------- About
    private void OnOpenDataDir(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.RoamingDirectory);
    private void OnOpenLogDir(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.LogDirectory);
    private void OnOpenGuide(object sender, RoutedEventArgs e) => _vm.RequestGuide();
    private void OnCheckUpdates(object sender, RoutedEventArgs e) => _vm.RequestUpdateCheck();

    private static string UserDataFilter =>
        $"{LocalizationManager.T("Settings.UserData.FileFilter")} (*{UserDataService.FileExtension})|*{UserDataService.FileExtension}|JSON (*.json)|*.json|All files|*.*";

    private void OnExportUserData(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = UserDataFilter,
            FileName = UserDataService.DefaultFileName(DateTime.Now),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _vm.ExportUserData(dlg.FileName);
            MessageBox.Show(this, LocalizationManager.F("Settings.UserData.Exported", dlg.FileName), "Hopkey", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("Export user data failed", ex);
            MessageBox.Show(this, ex.Message, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnImportUserData(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = UserDataFilter };
        if (dlg.ShowDialog(this) != true) return;
        UserDataPackage pkg;
        try { pkg = UserDataService.Read(dlg.FileName); }
        catch (Exception ex)
        {
            MessageBox.Show(this, LocalizationManager.F("Settings.UserData.Invalid", ex.Message), "Hopkey", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var contents = "";
        if (pkg.Settings != null)
            contents += LocalizationManager.F("Settings.UserData.HasSettings", pkg.Settings.Favorites.Count, pkg.Settings.Exclusion.Rules.Count, pkg.Settings.WebSearch.Count) + "\n";
        if (pkg.History != null)
            contents += LocalizationManager.F("Settings.UserData.HasHistory", pkg.History.Usage.Count, pkg.History.RecentFolders.Count) + "\n";
        var exported = pkg.ExportedUtc == default ? "?" : pkg.ExportedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var confirm = LocalizationManager.F("Settings.UserData.Confirm", contents, exported, pkg.AppVersion ?? "?");
        if (MessageBox.Show(this, confirm, "Hopkey", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        try
        {
            var backup = _vm.ImportUserData(pkg);
            var msg = LocalizationManager.T("Settings.UserData.Imported");
            if (backup != null) msg += "\n\n" + LocalizationManager.F("Settings.UserData.Backup", backup);
            MessageBox.Show(this, msg, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.Error("Import user data failed", ex);
            MessageBox.Show(this, ex.Message, "Hopkey", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Warn("Open folder failed", ex); }
    }
}

/// <summary>Minimal input box built in code.</summary>
public static class InputDialog
{
    public static string? Show(Window owner, string prompt, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 12), MinWidth = 380 };
        var ok = new Button { Content = LocalizationManager.T("OK"), IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = LocalizationManager.T("Cancel"), IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = "Hopkey", Content = panel, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        w.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = SettingsWindow.ControlsUri });
        ok.SetResourceReference(FrameworkElement.StyleProperty, "Hk.Button.Primary");
        w.SetResourceReference(Window.BackgroundProperty, "Brush.Background");
        w.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
        w.SetResourceReference(Window.FontFamilyProperty, "Font.Family");
        w.SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(w);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
