using System.Text.Json;
using Hopkey.Core.Util;

namespace Hopkey.Core.Config;

/// <summary>Loads/saves settings.json atomically. Immutable snapshot semantics: Current is replaced on Update.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private volatile AppSettings _current;

    public event Action<AppSettings, AppSettings>? Changed; // (old, new)

    public AppSettings Current => _current;

    private SettingsStore(string path, AppSettings settings)
    {
        _path = path;
        _current = settings;
    }

    public static SettingsStore Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        AppSettings settings;
        try
        {
            if (File.Exists(path))
            {
                ReadOnlySpan<byte> bytes = File.ReadAllBytes(path);
                if (bytes.StartsWith("﻿"u8)) bytes = bytes[3..]; // UTF-8 BOM (e.g. edited in Notepad / PowerShell)
                settings = JsonSerializer.Deserialize(bytes, JsonContext.Default.AppSettings) ?? new AppSettings();
                Migrate(settings);
            }
            else
            {
                settings = new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("settings.json is corrupt; backing up and using defaults", ex);
            try { File.Copy(path, path + ".bad", true); } catch { }
            settings = new AppSettings();
        }
        var store = new SettingsStore(path, settings);
        store.Save();
        return store;
    }

    private static void Migrate(AppSettings s)
    {
        s.General ??= new();
        s.Hotkeys ??= new();
        s.QuickSwitch ??= new();
        s.Index ??= new();
        s.Exclusion ??= new();
        s.Search ??= new();
        s.Favorites ??= new();
        s.WebSearch ??= WebSearchEngine.Defaults();
        s.QuickSwitch.DisabledProcesses ??= new();
        s.QuickSwitch.AutoSwitchExcludedProcesses ??= new();
        s.Index.Roots ??= new();
        s.Exclusion.Rules ??= Exclusion.DefaultExclusions.Create();
        Exclusion.DefaultExclusions.MergeMissingBuiltIns(s.Exclusion.Rules);
        if (s.Search.MaxResults is < 5 or > 500) s.Search.MaxResults = 50;
        s.SchemaVersion = 1;
    }

    public void Update(Action<AppSettings> mutate)
    {
        AppSettings old, next;
        lock (_gate)
        {
            old = _current;
            next = CloneSettings(old);
            mutate(next);
            _current = next;
            Save();
        }
        try { Changed?.Invoke(old, next); }
        catch (Exception ex) { Logger.Error("Settings change handler failed", ex); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_current, JsonContext.Default.AppSettings);
            AppPaths.AtomicWrite(_path, bytes);
        }
        catch (Exception ex) { Logger.Error("Failed to save settings", ex); }
    }

    public static AppSettings CloneSettings(AppSettings s)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(s, JsonContext.Default.AppSettings);
        return JsonSerializer.Deserialize(bytes, JsonContext.Default.AppSettings)!;
    }
}
