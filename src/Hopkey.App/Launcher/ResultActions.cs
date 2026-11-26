using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using Hopkey.App.Infrastructure;
using Hopkey.Core.Config;
using Hopkey.Core.Exclusion;
using Hopkey.Core.History;
using Hopkey.Core.Native;
using Hopkey.Core.Search;
using Hopkey.Core.Util;

namespace Hopkey.App.Launcher;

public enum ResultActionKind
{
    Open, OpenFolder, RunAsAdmin, CopyPath, CopyFile, OpenInTerminal, Properties, ExcludeFolder, ToggleFavorite,
    OpenFileLocation, SearchWeb, CopyResult, RemoveHistory,
}

public static class ResultActions
{
    private static readonly HashSet<string> s_adminExt = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".lnk", ".bat", ".cmd", ".ps1", ".msi", ".com" };

    public static List<ActionItemViewModel> For(SearchResult r, bool isFavorite, bool inHistory)
    {
        var list = new List<ActionItemViewModel>();
        void A(ResultActionKind k, string key, string glyph, string? shortcut = null) =>
            list.Add(new ActionItemViewModel { Kind = k, Name = LocalizationManager.T(key), Glyph = glyph, Shortcut = shortcut });

        switch (r.Kind)
        {
            case ResultKind.File:
                A(ResultActionKind.Open, "Action.Open", "", "Enter");
                A(ResultActionKind.OpenFolder, "Action.OpenFolder", "", "Ctrl+Enter");
                if (r.Path != null && s_adminExt.Contains(Path.GetExtension(r.Path))) A(ResultActionKind.RunAsAdmin, "Action.RunAsAdmin", "", "Shift+Enter");
                A(ResultActionKind.CopyPath, "Action.CopyPath", "", "Ctrl+C");
                A(ResultActionKind.CopyFile, "Action.CopyFile", "");
                A(ResultActionKind.OpenInTerminal, "Action.OpenInTerminal", "");
                A(ResultActionKind.Properties, "Action.Properties", "");
                A(ResultActionKind.ToggleFavorite, isFavorite ? "Action.RemoveFavorite" : "Action.AddFavorite", "");
                A(ResultActionKind.ExcludeFolder, "Action.ExcludeFolder", "");
                break;
            case ResultKind.Folder:
                A(ResultActionKind.Open, "Action.Open", "", "Enter");
                A(ResultActionKind.OpenFolder, "Action.OpenFolder", "", "Ctrl+Enter");
                A(ResultActionKind.CopyPath, "Action.CopyPath", "", "Ctrl+C");
                A(ResultActionKind.CopyFile, "Action.CopyFolder", "");
                A(ResultActionKind.OpenInTerminal, "Action.OpenInTerminal", "");
                A(ResultActionKind.Properties, "Action.Properties", "");
                A(ResultActionKind.ToggleFavorite, isFavorite ? "Action.RemoveFavorite" : "Action.AddFavorite", "");
                A(ResultActionKind.ExcludeFolder, "Action.ExcludeThisFolder", "");
                break;
            case ResultKind.App:
                A(ResultActionKind.Open, "Action.Open", "", "Enter");
                if (!r.IsUwp) A(ResultActionKind.RunAsAdmin, "Action.RunAsAdmin", "", "Shift+Enter");
                if (!r.IsUwp) A(ResultActionKind.OpenFileLocation, "Action.OpenFileLocation", "", "Ctrl+Enter");
                if (!r.IsUwp) A(ResultActionKind.CopyPath, "Action.CopyPath", "");
                break;
            case ResultKind.Web:
                A(ResultActionKind.SearchWeb, "Action.SearchWeb", "", "Enter");
                A(ResultActionKind.CopyPath, "Action.CopyPath", "");
                break;
            case ResultKind.Calculator:
                A(ResultActionKind.CopyResult, "Action.CopyResult", "", "Enter");
                break;
        }
        if (inHistory) A(ResultActionKind.RemoveHistory, "Action.RemoveHistory", "");
        return list;
    }
}

/// <summary>Executes launcher actions. Returns true if the launcher should hide afterwards.</summary>
public sealed class ActionExecutor
{
    private readonly HistoryStore _history;
    private readonly SettingsStore _settings;

    public ActionExecutor(HistoryStore history, SettingsStore settings)
    {
        _history = history;
        _settings = settings;
    }

    public static UsageKind ToUsage(ResultKind k) => k switch
    {
        ResultKind.Folder => UsageKind.Folder,
        ResultKind.App => UsageKind.App,
        ResultKind.Web => UsageKind.Web,
        ResultKind.Calculator => UsageKind.Calculator,
        ResultKind.Command => UsageKind.Command,
        _ => UsageKind.File,
    };

    public void RecordLaunch(SearchResult r, string? query)
    {
        if (r.Path == null || r.Kind is ResultKind.Web or ResultKind.Calculator) return;
        _history.RecordLaunch(r.Path, ToUsage(r.Kind), query);
        if (r.Kind == ResultKind.Folder) _history.RecordFolderVisit(r.Path, FolderVisitSource.Launcher);
        else if (r.Kind == ResultKind.File && PathUtil.GetParent(r.Path) is { } parent) _history.RecordFolderVisit(parent, FolderVisitSource.Launcher);
    }

    /// <summary>Execute; throws on failure (caller shows the message).</summary>
    public bool Execute(SearchResult r, ResultActionKind action, string? query, Window? owner)
    {
        var path = r.Path;
        switch (action)
        {
            case ResultActionKind.Open:
                if (r.Kind == ResultKind.Calculator) return Execute(r, ResultActionKind.CopyResult, query, owner);
                if (r.Kind == ResultKind.Web) return Execute(r, ResultActionKind.SearchWeb, query, owner);
                if (path == null) return false;
                if (r.IsUwp || path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    Shell32.ShellExecute("explorer.exe", "\"" + path + "\"");
                else
                    Shell32.ShellExecute(path, null, null, r.Kind == ResultKind.Folder ? null : PathUtil.GetParent(path));
                RecordLaunch(r, query);
                return true;
            case ResultActionKind.OpenFolder:
            case ResultActionKind.OpenFileLocation:
                if (path == null) return false;
                if (!Shell32.OpenFolderAndSelect(path))
                    Shell32.ShellExecute("explorer.exe", $"/select,\"{path}\"");
                RecordLaunch(r, query);
                return true;
            case ResultActionKind.RunAsAdmin:
                if (path == null) return false;
                if (Shell32.ShellExecute(path, null, "runas", PathUtil.GetParent(path))) RecordLaunch(r, query);
                return true;
            case ResultActionKind.CopyPath:
                if (path == null) return false;
                Clipboard.SetText(path);
                return true;
            case ResultActionKind.CopyFile:
                if (path == null) return false;
                var sc = new StringCollection { path };
                Clipboard.SetFileDropList(sc);
                return true;
            case ResultActionKind.OpenInTerminal:
            {
                if (path == null) return false;
                var dir = r.Kind == ResultKind.Folder ? path : PathUtil.GetParent(path) ?? path;
                OpenTerminal(dir);
                return true;
            }
            case ResultActionKind.Properties:
                if (path == null) return false;
                Shell32.ShellExecute(path, null, "properties", null, IntPtr.Zero, invokeIdList: true);
                return true;
            case ResultActionKind.ExcludeFolder:
            {
                if (path == null) return false;
                var dir = r.Kind == ResultKind.Folder ? path : PathUtil.GetParent(path);
                if (dir == null) return false;
                var answer = MessageBox.Show(owner!, LocalizationManager.F("Launcher.ConfirmExclude", dir), LocalizationManager.T("AppName"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return false;
                _settings.Update(s =>
                {
                    if (!s.Exclusion.Rules.Any(x => x.Kind == ExclusionKind.Path && PathUtil.Equal(PathUtil.Normalize(x.Pattern), dir)))
                        s.Exclusion.Rules.Add(new ExclusionRule(ExclusionKind.Path, dir, ExclusionTarget.DirectoriesOnly));
                });
                return false;
            }
            case ResultActionKind.ToggleFavorite:
            {
                if (path == null) return false;
                var target = r.Kind == ResultKind.File ? PathUtil.GetParent(path) ?? path : path;
                if (r.Kind == ResultKind.File && r.IsFavorite) target = path;
                _settings.Update(s =>
                {
                    int idx = s.Favorites.FindIndex(f => PathUtil.Equal(f.Path, target) || PathUtil.Equal(f.Path, path));
                    if (idx >= 0) s.Favorites.RemoveAt(idx);
                    else s.Favorites.Add(new FavoriteItem(target));
                });
                return false;
            }
            case ResultActionKind.SearchWeb:
                if (path == null) return false;
                Shell32.ShellExecute(path);
                return true;
            case ResultActionKind.CopyResult:
                Clipboard.SetText(r.CopyText ?? r.Title);
                return true;
            case ResultActionKind.RemoveHistory:
                if (path != null) _history.Remove(path);
                return false;
        }
        return false;
    }

    public static void OpenTerminal(string dir)
    {
        try
        {
            Shell32.ShellExecute("wt.exe", $"-d \"{dir}\"");
        }
        catch (Win32Exception)
        {
            Shell32.ShellExecute("powershell.exe", $"-NoExit -Command Set-Location -LiteralPath '{dir.Replace("'", "''")}'", null, dir);
        }
    }
}
