# Hopkey

A Listary-style file search launcher and file dialog enhancer for Windows 10/11 (x64). C# / .NET 10 / WPF, zero third-party dependencies.

The full design is in [docs/PLAN.md](docs/PLAN.md).

## Features

- **QuickSwitch (Open/Save dialog enhancement)**: a floating toolbar sits under the dialog with the open Explorer folders, favorites, recent folders, and a recommended folder. Also supports jumping with Ctrl+G, following Explorer when you switch back to the dialog, an optional auto-jump when the dialog opens, and per-app opt-outs.
- **Folder exclusion**: rules by path, name wildcard, path wildcard (`**\obj`), or regex, plus switches for hidden and system files. Exclusions apply at index time and as a search-time filter. Adding a rule prunes the index immediately; removing one restores entries incrementally. Rules can be imported and exported.
- **Indexing**: when run as administrator, it reads the NTFS MFT (`FSCTL_ENUM_USN_DATA`) and follows the USN journal for real-time updates. Otherwise it uses a parallel crawler plus `FileSystemWatcher`. The index uses a struct-of-arrays layout with char arenas and is cached as raw binary.
- **Launcher**: opened by double-tapping Ctrl (or a key combination). Search runs a vectorized parallel scan, then precise scoring. Supports pinyin initials, multiple keywords, path keywords, `folder:`/`file:`/`app:`/`ext:`, history-based ranking, an action menu (Tab or right-click; includes Delete — Shift+Del — which sends the item to the Recycle Bin and drops it from the index), web search (`g`/`bd`/`bing`/`gh`) and a calculator. When results span several types, filter chips (All / Folders / Apps / PDF …) switch the search to one type; the chips you click most move to the front (Ctrl+Tab cycles them). Results can optionally show date created/modified and size, and can be sorted by those within each keyword-match level.
- **Type-to-search**: start typing in an Explorer window or a file dialog and the results navigate that same window.
- **Getting started guide**: on the first launch a short three-page guide lets you try the hotkey, shows where the tray icon is (on Windows 11 it detects whether the icon is hidden behind `^` and links to taskbar settings), and lists the main tricks. Reopen it from the tray menu or Settings → About.
- **User data export/import**: Settings → About saves all configuration (options, hotkeys, exclusion rules, favorites, web search) and usage preferences (launch history, query associations, recent folders, filter habits) to one file, for backup or moving to another PC. Importing backs up the current data first.
- Settings window with live apply, light/dark themes, Chinese/English UI, tray icon, autostart, and restart as administrator.

## Build

```powershell
dotnet build Hopkey.sln -c Release
```

## Run

```powershell
dotnet run --project src/Hopkey.App -c Release
```

## Publish (ReadyToRun)

```powershell
dotnet publish src/Hopkey.App -c Release -o publish
```

## Data locations

- Settings and history: `%AppData%\Hopkey\`
- Index cache, pinyin cache and logs: `%LocalAppData%\Hopkey\`

## License

[MIT](LICENSE)
