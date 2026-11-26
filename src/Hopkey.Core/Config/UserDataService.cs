using System.Text.Json;
using Hopkey.Core.History;
using Hopkey.Core.Util;

namespace Hopkey.Core.Config;

/// <summary>
/// One-file backup of everything the user has configured or taught the app:
/// settings.json (all options, exclusion rules, favorites, web search engines) and
/// history.json (launch counts, query associations, recent folders, filter-chip usage).
/// The file index and caches are not included; they are rebuilt.
/// </summary>
public sealed class UserDataPackage
{
    public string Format { get; set; } = UserDataService.FormatId;
    public int Version { get; set; } = UserDataService.FormatVersion;
    public DateTime ExportedUtc { get; set; }
    public string? AppVersion { get; set; }
    public AppSettings? Settings { get; set; }
    public HistoryData? History { get; set; }
}

public sealed class UserDataService
{
    public const string FormatId = "hopkey-userdata";
    public const int FormatVersion = 1;
    public const string FileExtension = ".hopkey.json";

    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;

    public UserDataService(SettingsStore settings, HistoryStore history)
    {
        _settings = settings;
        _history = history;
    }

    public static string BackupDirectory => Path.Combine(AppPaths.RoamingDirectory, "backups");

    public static string DefaultFileName(DateTime now) => $"hopkey-userdata-{now:yyyyMMdd-HHmm}{FileExtension}";

    public UserDataPackage CreatePackage(string? appVersion) => new()
    {
        ExportedUtc = DateTime.UtcNow,
        AppVersion = appVersion,
        Settings = _settings.Current.Clone(),
        History = _history.ExportData(),
    };

    public void Export(string file, string? appVersion) => Write(file, CreatePackage(appVersion));

    public static void Write(string file, UserDataPackage package)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        AppPaths.AtomicWrite(file, JsonSerializer.SerializeToUtf8Bytes(package, JsonContext.Default.UserDataPackage));
    }

    /// <summary>Reads and validates an export file. Throws <see cref="InvalidDataException"/> for files that aren't Hopkey user data.</summary>
    public static UserDataPackage Read(string file)
    {
        ReadOnlySpan<byte> bytes = File.ReadAllBytes(file);
        if (bytes.StartsWith("﻿"u8)) bytes = bytes[3..];
        UserDataPackage? pkg;
        try { pkg = JsonSerializer.Deserialize(bytes, JsonContext.Default.UserDataPackage); }
        catch (JsonException ex) { throw new InvalidDataException("Not a valid Hopkey user data file: " + ex.Message, ex); }
        if (pkg == null || pkg.Format != FormatId)
            throw new InvalidDataException("Not a Hopkey user data file.");
        if (pkg.Version > FormatVersion)
            throw new InvalidDataException($"This file was made by a newer Hopkey (format {pkg.Version}); please update Hopkey first.");
        if (pkg.Settings == null && pkg.History == null)
            throw new InvalidDataException("The file contains no settings or history.");
        if (pkg.Settings != null) SettingsStore.Normalize(pkg.Settings);
        if (pkg.History != null) HistoryStore.Normalize(pkg.History);
        return pkg;
    }

    /// <summary>
    /// Replaces current settings and/or history with the package's (each only if present).
    /// The current data is first saved to <paramref name="backupDirectory"/>; returns that backup file (null if not written).
    /// </summary>
    public string? Import(UserDataPackage package, string? appVersion, string? backupDirectory = null)
    {
        string? backup = null;
        backupDirectory ??= BackupDirectory;
        try
        {
            backup = Path.Combine(backupDirectory, $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}{FileExtension}");
            Write(backup, CreatePackage(appVersion));
        }
        catch (Exception ex)
        {
            Logger.Warn("Backup before import failed", ex);
            backup = null;
        }
        if (package.History != null) _history.ReplaceAll(package.History);
        if (package.Settings != null) _settings.ReplaceAll(package.Settings);
        Logger.Info($"Imported user data (settings: {package.Settings != null}, history: {package.History != null}); backup: {backup ?? "none"}");
        return backup;
    }
}
