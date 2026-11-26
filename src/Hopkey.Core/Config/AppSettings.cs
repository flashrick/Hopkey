using Hopkey.Core.Exclusion;

namespace Hopkey.Core.Config;

public enum ThemeMode { System, Light, Dark }

/// <summary>Secondary order of file/folder results within the same keyword-match tier.</summary>
public enum ResultSortMode { Relevance, ModifiedNewest, CreatedNewest, SizeLargest }

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public QuickSwitchSettings QuickSwitch { get; set; } = new();
    public IndexSettings Index { get; set; } = new();
    public ExclusionSettings Exclusion { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public List<FavoriteItem> Favorites { get; set; } = new();
    public List<WebSearchEngine> WebSearch { get; set; } = WebSearchEngine.Defaults();

    public AppSettings Clone() => SettingsStore.CloneSettings(this);
}

public sealed class GeneralSettings
{
    public bool Autostart { get; set; }
    public bool AutostartElevated { get; set; }
    public string Language { get; set; } = "zh-CN";
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool StartElevated { get; set; }
}

public sealed class HotkeySettings
{
    /// <summary>"DoubleCtrl" or a combination like "Alt+Space".</summary>
    public string Launcher { get; set; } = HotkeySpec.DoubleCtrl;
    /// <summary>Secondary RegisterHotKey combination; empty = none.</summary>
    public string LauncherAlt { get; set; } = "Ctrl+Alt+Space";
    public string DialogJump { get; set; } = "Ctrl+G";
    public bool TypeToSearch { get; set; } = true;
}

public sealed class QuickSwitchSettings
{
    public bool Enabled { get; set; } = true;
    public bool ShowToolbar { get; set; } = true;
    public bool AutoSwitchOnOpen { get; set; }
    public int AutoSwitchMaxAgeMinutes { get; set; } = 10;
    public bool FollowExplorer { get; set; } = true;
    public bool DialogJumpHotkey { get; set; } = true;
    public List<string> DisabledProcesses { get; set; } = new();
    public List<string> AutoSwitchExcludedProcesses { get; set; } = new();
}

public sealed class IndexSettings
{
    /// <summary>Empty = all fixed drives.</summary>
    public List<string> Roots { get; set; } = new();
    public bool UseUsnWhenElevated { get; set; } = true;
    public bool IndexApps { get; set; } = true;
    public bool IndexUwpApps { get; set; }
    public int SaveIntervalMinutes { get; set; } = 10;
}

public sealed class ExclusionSettings
{
    public List<ExclusionRule> Rules { get; set; } = DefaultExclusions.Create();
    public bool ExcludeHidden { get; set; }
    public bool ExcludeSystem { get; set; } = true;
}

public sealed class SearchSettings
{
    public int MaxResults { get; set; } = 50;
    public bool Pinyin { get; set; } = true;
    public bool FullPinyin { get; set; }
    public bool UseHistoryBoost { get; set; } = true;
    /// <summary>Show file/folder details on launcher results (read lazily from disk; not stored in the index).</summary>
    public bool ShowCreated { get; set; }
    public bool ShowModified { get; set; }
    public bool ShowSize { get; set; }
    public ResultSortMode SortBy { get; set; } = ResultSortMode.Relevance;
}

public sealed class FavoriteItem
{
    public string Path { get; set; } = "";
    public string? Alias { get; set; }
    public FavoriteItem() { }
    public FavoriteItem(string path, string? alias = null) { Path = path; Alias = alias; }
}

public sealed class WebSearchEngine
{
    public string Keyword { get; set; } = "";
    public string Name { get; set; } = "";
    public string UrlTemplate { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public WebSearchEngine() { }
    public WebSearchEngine(string keyword, string name, string url, bool enabled = true)
    { Keyword = keyword; Name = name; UrlTemplate = url; Enabled = enabled; }

    public static List<WebSearchEngine> Defaults() => new()
    {
        new("g", "Google", "https://www.google.com/search?q={q}"),
        new("bd", "百度", "https://www.baidu.com/s?wd={q}"),
        new("bing", "Bing", "https://www.bing.com/search?q={q}"),
        new("gh", "GitHub", "https://github.com/search?q={q}"),
    };

    public string BuildUrl(string query) => UrlTemplate.Replace("{q}", Uri.EscapeDataString(query));
}
