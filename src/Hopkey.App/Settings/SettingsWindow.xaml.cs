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

    public SettingsWindow(SettingsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        ApplyChoices();
        ElevateButton.Visibility = vm.IsElevated ? Visibility.Collapsed : Visibility.Visible;
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        Closed += (_, _) => _vm.Dispose();
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
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var w = new Window
        {
            Title = "Hopkey", Content = panel, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        w.SetResourceReference(Window.BackgroundProperty, "Brush.Background");
        w.SetResourceReference(Window.ForegroundProperty, "Brush.Text");
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
