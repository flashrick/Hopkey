using System.Windows;

namespace Hopkey.App.Infrastructure;

/// <summary>
/// zh-CN / en strings. XAML uses {DynamicResource S.Key}; code uses LocalizationManager.T("Key").
/// Dictionaries are built in code so every UI thread (main, toolbar) can create its own copy.
/// </summary>
public static class LocalizationManager
{
    private const string Marker = "CL.Strings";
    public static string Language { get; private set; } = "zh-CN";
    public static event Action? LanguageChanged;

    private static readonly Dictionary<string, (string Zh, string En)> s_strings = new()
    {
        // General
        ["AppName"] = ("Hopkey", "Hopkey"),
        ["Search"] = ("搜索", "Search"),
        ["Settings"] = ("设置", "Settings"),
        ["RebuildIndex"] = ("重建索引", "Rebuild index"),
        ["Pause"] = ("暂停", "Pause"),
        ["Resume"] = ("恢复", "Resume"),
        ["Exit"] = ("退出", "Exit"),
        ["Paused"] = ("已暂停", "Paused"),
        ["Add"] = ("添加", "Add"),
        ["Remove"] = ("删除", "Remove"),
        ["Edit"] = ("编辑", "Edit"),
        ["MoveUp"] = ("上移", "Move up"),
        ["MoveDown"] = ("下移", "Move down"),
        ["Import"] = ("导入…", "Import…"),
        ["Export"] = ("导出…", "Export…"),
        ["RestoreDefaults"] = ("恢复默认", "Restore defaults"),
        ["Saved"] = ("已保存", "Saved"),
        ["Open"] = ("打开", "Open"),
        ["Browse"] = ("浏览…", "Browse…"),
        ["Yes"] = ("是", "Yes"),
        ["No"] = ("否", "No"),
        ["Cancel"] = ("取消", "Cancel"),
        ["OK"] = ("确定", "OK"),

        // Launcher
        ["Launcher.Placeholder"] = ("输入关键词开始搜索", "Type to search"),
        ["Launcher.EmptyHint"] = ("输入关键词开始搜索；双击 Ctrl 随时唤起", "Type to search. Double-tap Ctrl any time."),
        ["Launcher.NoResults"] = ("无匹配结果", "No results"),
        ["Launcher.Indexing"] = ("正在建立索引… {0:N0} 项", "Indexing… {0:N0} items"),
        ["Launcher.Indexed"] = ("已索引 {0:N0} 项", "{0:N0} items indexed"),
        ["Launcher.IndexLoading"] = ("正在加载索引…", "Loading index…"),
        ["Launcher.IndexError"] = ("索引出错，点击查看日志", "Index error — click to view log"),
        ["Launcher.IndexPaused"] = ("索引已暂停", "Indexing paused"),
        ["Launcher.KeyHints"] = ("Enter 打开 · Ctrl+Enter 打开位置 · Tab 更多操作", "Enter open · Ctrl+Enter location · Tab actions"),
        ["Launcher.ContextTag"] = ("在此窗口中打开：{0}", "Open in this window: {0}"),
        ["Launcher.SearchWeb"] = ("在 {0} 中搜索“{1}”", "Search {0} for \"{1}\""),
        ["Launcher.CopyResult"] = ("按 Enter 复制结果", "Press Enter to copy"),
        ["Launcher.Group.favorites"] = ("收藏", "Favorites"),
        ["Launcher.Group.recent"] = ("最近使用", "Recently used"),
        ["Launcher.Group.recentFolders"] = ("最近文件夹", "Recent folders"),
        ["Launcher.OpenFailed"] = ("打开失败：{0}", "Failed to open: {0}"),
        ["Launcher.Copied"] = ("已复制", "Copied"),
        ["Launcher.Excluded"] = ("已排除：{0}", "Excluded: {0}"),
        ["Launcher.ConfirmExclude"] = ("将“{0}”加入排除列表？\n该文件夹会立即从索引中移除。", "Exclude \"{0}\"?\nIt will be removed from the index immediately."),
        ["Launcher.ConfirmDelete"] = ("删除“{0}”？\n文件会移到回收站，并从索引中移除。", "Delete \"{0}\"?\nIt will be moved to the Recycle Bin and removed from the index."),
        ["Launcher.ConfirmDeleteFolder"] = ("删除文件夹“{0}”及其全部内容？\n会移到回收站，并从索引中移除。", "Delete the folder \"{0}\" and everything in it?\nIt will be moved to the Recycle Bin and removed from the index."),
        ["Launcher.Back"] = ("← 返回", "← Back"),

        // Actions
        ["Action.Open"] = ("打开", "Open"),
        ["Action.OpenFolder"] = ("打开所在文件夹", "Open containing folder"),
        ["Action.RunAsAdmin"] = ("以管理员身份运行", "Run as administrator"),
        ["Action.CopyPath"] = ("复制路径", "Copy path"),
        ["Action.CopyFile"] = ("复制文件", "Copy file"),
        ["Action.CopyFolder"] = ("复制文件夹", "Copy folder"),
        ["Action.OpenInTerminal"] = ("在终端中打开", "Open in terminal"),
        ["Action.Properties"] = ("属性", "Properties"),
        ["Action.ExcludeFolder"] = ("排除所在文件夹", "Exclude containing folder"),
        ["Action.ExcludeThisFolder"] = ("排除此文件夹", "Exclude this folder"),
        ["Action.AddFavorite"] = ("添加到收藏", "Add to favorites"),
        ["Action.RemoveFavorite"] = ("从收藏中移除", "Remove from favorites"),
        ["Action.OpenFileLocation"] = ("打开文件位置", "Open file location"),
        ["Action.SearchWeb"] = ("搜索", "Search"),
        ["Action.CopyResult"] = ("复制结果", "Copy result"),
        ["Action.RemoveHistory"] = ("从历史记录中移除", "Remove from history"),
        ["Action.Delete"] = ("删除", "Delete"),

        // QuickSwitch toolbar
        ["QS.Search"] = ("搜索并跳转", "Search & jump"),
        ["QS.Recommended"] = ("跳转到推荐文件夹 (Ctrl+G)", "Jump to recommended folder (Ctrl+G)"),
        ["QS.OpenFolders"] = ("已打开的文件夹", "Open folders"),
        ["QS.Favorites"] = ("收藏", "Favorites"),
        ["QS.Recent"] = ("最近", "Recent"),
        ["QS.More"] = ("更多", "More"),
        ["QS.NoExplorer"] = ("没有打开的资源管理器窗口", "No open Explorer windows"),
        ["QS.NoFavorites"] = ("暂无收藏", "No favorites"),
        ["QS.NoRecent"] = ("暂无最近文件夹", "No recent folders"),
        ["QS.Loading"] = ("正在获取…", "Loading…"),
        ["QS.Settings"] = ("QuickSwitch 设置…", "QuickSwitch settings…"),
        ["QS.DisableAutoForApp"] = ("对此程序禁用自动跳转", "Disable auto switch for this app"),
        ["QS.DisableForApp"] = ("对此程序禁用 QuickSwitch", "Disable QuickSwitch for this app"),
        ["QS.AddCurrentToFavorites"] = ("将当前文件夹加入收藏", "Add current folder to favorites"),
        ["QS.ElevatedHint"] = ("目标程序以管理员身份运行，点击以管理员身份重启 Hopkey", "The target app runs as administrator. Click to restart Hopkey as administrator"),
        ["QS.NavigateFailed"] = ("跳转失败：{0}", "Navigation failed: {0}"),
        ["QS.NotFound"] = ("文件夹不存在", "Folder not found"),

        ["Launcher.Tip.Filter"] = ("提示：搜索后点击结果上方的类型标签，只看文件夹、应用或某种文件（Ctrl+Tab 也能切换）", "Tip: after searching, click a type chip above the results to see only folders, apps or one file type (Ctrl+Tab cycles them)"),
        ["Launcher.Tip.Calc"] = ("提示：输入 =12*3 直接计算", "Tip: type =12*3 to calculate"),
        ["Launcher.Tip.Web"] = ("提示：g 关键词 用 Google 搜索（还有 bd、bing、gh）", "Tip: g <words> searches Google (also bd, bing, gh)"),
        ["Launcher.Tip.Actions"] = ("提示：按 Tab 查看更多操作，Ctrl+Enter 打开所在位置", "Tip: Tab for more actions, Ctrl+Enter opens the location"),
        ["Launcher.Tip.Pinyin"] = ("提示：支持拼音，如 wd 或 wendang 都能找到“文档”", "Tip: pinyin works too, e.g. wd or wendang finds 文档"),
        // Tray
        ["Tray.Guide"] = ("新手指南", "Getting started"),
        ["Tray.Tooltip"] = ("Hopkey — 双击 Ctrl 搜索", "Hopkey — double-tap Ctrl to search"),
        ["Tray.HotkeyConflict"] = ("热键 {0} 已被其他程序占用", "Hotkey {0} is already in use"),
        ["Tray.RunAsAdmin"] = ("以管理员身份重启", "Restart as administrator"),
        ["Tray.About"] = ("关于", "About"),
        ["Tray.Update"] = ("更新到 {0}", "Update to {0}"),

        // Updates
        ["Update.Available"] = ("Hopkey {0} 已发布，点此更新。", "Hopkey {0} is available. Click here to update."),
        ["Update.AvailableShort"] = ("发现新版本 {0}", "{0} is available"),
        ["Update.Confirm"] = ("发现新版本 Hopkey {0}（当前 {1}）。\n\nHopkey 会先退出，由更新器替换程序文件，然后自动重新启动。设置和索引都会保留。\n\n现在更新吗？",
            "Hopkey {0} is available (you have {1}).\n\nHopkey will close, the updater will replace its files, and then Hopkey restarts. Your settings and index are kept.\n\nUpdate now?"),
        ["Update.Checking"] = ("正在检查更新…", "Checking for updates…"),
        ["Update.Downloading"] = ("正在下载 Hopkey {0}…", "Downloading Hopkey {0}…"),
        ["Update.Done"] = ("已更新到 Hopkey v{0}", "Updated to Hopkey v{0}"),
        ["Update.UpToDate"] = ("已是最新版本（{0}）", "You're up to date ({0})"),
        ["Update.CheckFailed"] = ("检查更新失败：{0}", "Couldn't check for updates: {0}"),
        ["Update.DownloadFailed"] = ("下载更新失败：{0}", "Couldn't download the update: {0}"),
        ["Update.ApplyFailed"] = ("更新失败，已恢复为原来的版本。\n{0}", "The update failed; the previous version was restored.\n{0}"),
        ["Update.NotExited"] = ("Hopkey 没有退出，更新已取消。", "Hopkey didn't exit, so the update was cancelled."),
        ["Update.ElevationDeclined"] = ("安装目录需要管理员权限，更新已取消。", "The install folder needs administrator rights, so the update was cancelled."),

        // Onboarding
        ["Onboarding.Title"] = ("欢迎使用 Hopkey", "Welcome to Hopkey"),
        ["Onboarding.Next"] = ("下一步", "Next"),
        ["Onboarding.Back"] = ("上一步", "Back"),
        ["Onboarding.Skip"] = ("跳过", "Skip"),
        ["Onboarding.Finish"] = ("开始使用", "Get started"),
        ["Onboarding.Open"] = ("新手指南", "Getting started"),
        ["Onboarding.Balloon"] = ("Hopkey 仍在后台运行。双击 Ctrl 即可搜索；图标在任务栏右下角的 ^ 里。", "Hopkey is still running. Double-tap Ctrl to search; the icon is under ^ in the taskbar corner."),
        ["Onboarding.BalloonCombo"] = ("Hopkey 仍在后台运行。按 {0} 即可搜索；图标在任务栏右下角的 ^ 里。", "Hopkey is still running. Press {0} to search; the icon is under ^ in the taskbar corner."),
        ["Onboarding.P1.Title"] = ("Hopkey 已经在后台运行了", "Hopkey is running"),
        ["Onboarding.P1.Body"] = ("它没有主窗口。在任何地方按下快捷键，就能搜索文件、文件夹和应用，并在当前窗口里直接打开。", "It has no main window. Press a shortcut anywhere to search files, folders and apps, and open them right where you are."),
        ["Onboarding.P1.DoubleTap"] = ("快速按两次", "Tap twice quickly"),
        ["Onboarding.P1.Press"] = ("按下", "Press"),
        ["Onboarding.P1.Or"] = ("或者", "or"),
        ["Onboarding.P1.Try"] = ("现在就试试：唤出搜索框，输入几个字母，按 Esc 关闭。", "Try it now: open the search box, type a few letters, press Esc to close."),
        ["Onboarding.P1.Done"] = ("成功！以后在任何地方都可以这样唤出 Hopkey。", "Nice! That works from anywhere."),
        ["Onboarding.P1.Paused"] = ("Hopkey 当前已暂停，请先在托盘菜单中选择“恢复”。", "Hopkey is paused. Choose \"Resume\" in the tray menu first."),
        ["Onboarding.P1.Indexing"] = ("首次运行会在后台建立文件索引，通常不到一分钟，期间搜索结果可能不完整。", "The first file index builds in the background (usually under a minute); results may be incomplete until then."),
        ["Onboarding.P2.Title"] = ("在任务栏右下角找到 Hopkey", "Find Hopkey in the taskbar corner"),
        ["Onboarding.P2.Body"] = ("Hopkey 只在通知区域（托盘）显示一个图标：单击打开搜索，右键打开菜单（设置、暂停、退出）。关闭这个窗口后它会继续运行。", "Hopkey lives in the notification area (system tray): click the icon to search, right-click for the menu (settings, pause, exit). It keeps running after you close this window."),
        ["Onboarding.P2.Promoted"] = ("图标已显示在任务栏上。", "The icon is pinned to the taskbar."),
        ["Onboarding.P2.Hidden"] = ("图标目前收在 ^ 隐藏区里", "The icon is hidden behind ^"),
        ["Onboarding.P2.HiddenHelp"] = ("Windows 11 默认会收起新图标。点击下方按钮，在“其他系统托盘图标”里打开 Hopkey；也可以直接把图标从 ^ 里拖到任务栏上。", "Windows 11 hides new icons by default. Click below and turn on Hopkey under \"Other system tray icons\", or drag the icon out of ^ onto the taskbar."),
        ["Onboarding.P2.Unknown"] = ("看不到图标时，点击任务栏右下角的 ^ 查看隐藏的图标，可以把它拖到任务栏上固定。", "Can't see the icon? Click ^ in the taskbar corner to show hidden icons, then drag it onto the taskbar."),
        ["Onboarding.P2.OpenTaskbar"] = ("打开任务栏设置", "Open taskbar settings"),
        ["Onboarding.P2.AutostartHint"] = ("开机后自动在后台运行，无需每次手动打开。", "Runs in the background after you sign in, so it's always ready."),
        ["Onboarding.P3.Title"] = ("几个顺手的用法", "A few handy tricks"),
        ["Onboarding.P3.Dialogs"] = ("在“打开 / 保存”对话框和资源管理器里", "In Open/Save dialogs and Explorer"),
        ["Onboarding.P3.Follow"] = ("在“打开 / 保存”对话框里，切到资源管理器打开某个文件夹，再切回对话框——对话框会自动跳到那个文件夹。", "In an Open/Save dialog, switch to Explorer and open a folder, then switch back: the dialog jumps to that folder."),
        ["Onboarding.P3.ToolbarLabel"] = ("工具条", "Toolbar"),
        ["Onboarding.P3.Toolbar"] = ("对话框下方的 Hopkey 工具条：一键跳到资源管理器里打开着的文件夹、收藏或最近的文件夹。", "The Hopkey toolbar under file dialogs: jump to a folder open in Explorer, a favorite or a recent folder."),
        ["Onboarding.P3.DialogJump"] = ("在对话框里直接跳到推荐的文件夹。", "Jump the dialog straight to the suggested folder."),
        ["Onboarding.P3.TypeToSearch"] = ("在资源管理器或对话框里直接打字，就会开始搜索，结果在当前窗口打开。", "Just start typing in Explorer or a dialog to search; results open in that window."),
        ["Onboarding.P3.Launcher"] = ("在搜索框里", "In the search box"),
        ["Onboarding.P3.Filter"] = ("搜索后点击结果上方的类型标签，只看这一类结果；也可以按 Ctrl+Tab 切换。", "After searching, click a type chip above the results to show only that kind; Ctrl+Tab cycles them."),
        ["Onboarding.P3.Calc"] = ("直接计算", "Calculate"),
        ["Onboarding.P3.Web"] = ("用 Google 搜索（bd 百度、bing、gh GitHub）", "Search Google (also bd, bing, gh)"),
        ["Onboarding.P3.Actions"] = ("更多操作；Ctrl+Enter 打开所在位置", "More actions; Ctrl+Enter opens the location"),
        ["Onboarding.P3.Direct"] = ("直接打开第 1–9 个结果", "Open result 1–9 directly"),
        ["Onboarding.P3.Footer"] = ("随时可以从托盘菜单 →“新手指南”再次打开本页。", "Reopen this guide any time from the tray menu → \"Getting started\"."),
        // Settings
        ["Settings.Title"] = ("Hopkey 设置", "Hopkey Settings"),
        ["Settings.General"] = ("常规", "General"),
        ["Settings.QuickSwitch"] = ("QuickSwitch", "QuickSwitch"),
        ["Settings.Index"] = ("索引与排除", "Index & Exclusions"),
        ["Settings.Favorites"] = ("收藏", "Favorites"),
        ["Settings.WebSearch"] = ("网页搜索", "Web Search"),
        ["Settings.About"] = ("关于", "About"),
        ["Settings.Autostart"] = ("开机自动启动", "Start with Windows"),
        ["Settings.AutostartElevated"] = ("以管理员身份开机自启动（计划任务）", "Start elevated with Windows (scheduled task)"),
        ["Settings.Language"] = ("语言", "Language"),
        ["Settings.Theme"] = ("主题", "Theme"),
        ["Settings.Theme.System"] = ("跟随系统", "System"),
        ["Settings.Theme.Light"] = ("浅色", "Light"),
        ["Settings.Theme.Dark"] = ("深色", "Dark"),
        ["Settings.LauncherHotkey"] = ("启动器热键", "Launcher hotkey"),
        ["Settings.DoubleCtrl"] = ("双击 Ctrl", "Double-tap Ctrl"),
        ["Settings.Combination"] = ("组合键", "Key combination"),
        ["Settings.AltHotkey"] = ("备用组合键", "Secondary hotkey"),
        ["Settings.TypeToSearch"] = ("在资源管理器与文件对话框中直接打字搜索", "Type to search in Explorer and file dialogs"),
        ["Settings.Privilege"] = ("权限", "Privileges"),
        ["Settings.Elevated"] = ("已以管理员身份运行", "Running as administrator"),
        ["Settings.NotElevated"] = ("普通权限运行（以管理员运行可启用 MFT 快速索引，并可操作管理员程序的对话框）", "Running without elevation (run as admin for fast MFT indexing and elevated dialogs)"),
        ["Settings.HotkeyHint"] = ("点击后按下组合键；Backspace 清除", "Click, then press keys; Backspace clears"),
        ["Settings.HotkeyInvalid"] = ("热键无效或被占用", "Invalid or unavailable hotkey"),
        ["Settings.QS.Enabled"] = ("启用 QuickSwitch", "Enable QuickSwitch"),
        ["Settings.QS.ShowToolbar"] = ("在文件对话框下方显示工具栏", "Show toolbar under file dialogs"),
        ["Settings.QS.AutoOnOpen"] = ("打开对话框时自动跳转到最近的资源管理器文件夹", "Auto jump to the last Explorer folder when a dialog opens"),
        ["Settings.QS.MaxAge"] = ("仅当资源管理器文件夹在以下分钟内使用过", "Only if the Explorer folder was used within (minutes)"),
        ["Settings.QS.Follow"] = ("切换到资源管理器再切回对话框时，对话框跟随", "Follow Explorer when switching back to the dialog"),
        ["Settings.QS.DialogJump"] = ("对话框内热键跳转到推荐文件夹", "Dialog hotkey jumps to recommended folder"),
        ["Settings.QS.DisabledApps"] = ("禁用 QuickSwitch 的程序（进程名）", "Apps with QuickSwitch disabled (process name)"),
        ["Settings.QS.AutoExcludedApps"] = ("禁用自动跳转/跟随的程序（进程名）", "Apps with auto switch disabled (process name)"),
        ["Settings.Index.Roots"] = ("索引位置", "Indexed locations"),
        ["Settings.Index.AllDrives"] = ("所有固定磁盘", "All fixed drives"),
        ["Settings.Index.UseUsn"] = ("使用 NTFS MFT/USN 快速索引（需管理员）", "Use NTFS MFT/USN fast indexing (requires admin)"),
        ["Settings.Index.Apps"] = ("索引开始菜单应用", "Index Start menu apps"),
        ["Settings.Index.Uwp"] = ("索引应用商店（UWP）应用", "Index Store (UWP) apps"),
        ["Settings.Index.Status"] = ("索引状态", "Index status"),
        ["Settings.Index.Exclusions"] = ("排除规则", "Exclusion rules"),
        ["Settings.Index.AddFolder"] = ("添加文件夹…", "Add folder…"),
        ["Settings.Index.AddName"] = ("添加名称模式", "Add name pattern"),
        ["Settings.Index.AddGlob"] = ("添加路径通配", "Add path wildcard"),
        ["Settings.Index.AddRegex"] = ("添加正则", "Add regex"),
        ["Settings.Index.ExcludeHidden"] = ("排除隐藏文件和文件夹", "Exclude hidden files and folders"),
        ["Settings.Index.ExcludeSystem"] = ("排除受保护的系统文件", "Exclude protected system files"),
        ["Settings.Index.Col.Enabled"] = ("启用", "On"),
        ["Settings.Index.Col.Kind"] = ("类型", "Type"),
        ["Settings.Index.Col.Pattern"] = ("模式", "Pattern"),
        ["Settings.Index.Col.Target"] = ("作用对象", "Applies to"),
        ["Settings.Index.Col.BuiltIn"] = ("内置", "Built-in"),
        ["Settings.Index.BuiltInCannotDelete"] = ("内置规则不能删除，只能停用", "Built-in rules can only be disabled"),
        ["Settings.Index.NewPattern"] = ("请输入模式：", "Enter pattern:"),
        ["Settings.Search.Pinyin"] = ("拼音匹配（首字母和全拼）", "Pinyin matching (initials and full pinyin)"),
        ["Settings.Search.HistoryBoost"] = ("根据使用历史调整排序", "Rank by usage history"),
        ["Settings.Search.MaxResults"] = ("最多结果数", "Max results"),
        ["Settings.Search.ClearHistory"] = ("清除使用历史", "Clear history"),
        ["Settings.Search.ShowDetails"] = ("在搜索结果中显示：", "Show in results:"),
        ["Settings.Search.ShowCreated"] = ("创建日期", "Date created"),
        ["Settings.Search.ShowModified"] = ("修改日期", "Date modified"),
        ["Settings.Search.ShowSize"] = ("文件大小", "File size"),
        ["Settings.Search.SortBy"] = ("结果排序", "Sort results by"),
        ["Settings.Search.SortHint"] = ("关键词匹配程度始终优先（完全匹配 > 开头匹配 > 词首匹配 > 其他），同一匹配程度内再按所选项排序。",
            "Keyword match always comes first (exact > prefix > word start > other); the chosen order applies within each match level."),
        ["Sort.Relevance"] = ("相关度", "Relevance"),
        ["Sort.ModifiedNewest"] = ("修改日期（新→旧）", "Date modified (newest first)"),
        ["Sort.CreatedNewest"] = ("创建日期（新→旧）", "Date created (newest first)"),
        ["Sort.SizeLargest"] = ("文件大小（大→小）", "Size (largest first)"),
        ["Launcher.Facet.All"] = ("全部", "All"),
        ["Launcher.Facet.Folder"] = ("文件夹", "Folders"),
        ["Launcher.Facet.App"] = ("应用", "Apps"),
        ["Launcher.Detail.Created"] = ("创建 {0}", "Created {0}"),
        ["Launcher.Detail.Modified"] = ("修改 {0}", "Modified {0}"),
        ["Settings.Fav.Path"] = ("路径", "Path"),
        ["Settings.Fav.Alias"] = ("别名", "Alias"),
        ["Settings.Fav.AddFolder"] = ("添加文件夹…", "Add folder…"),
        ["Settings.Fav.AddFile"] = ("添加文件…", "Add file…"),
        ["Settings.Web.Keyword"] = ("关键字", "Keyword"),
        ["Settings.Web.Name"] = ("名称", "Name"),
        ["Settings.Web.Url"] = ("URL 模板（{q} 为查询）", "URL template ({q} = query)"),
        ["Settings.Web.Invalid"] = ("关键字需唯一且不含空格，URL 需包含 {q}", "Keywords must be unique without spaces; URL must contain {q}"),
        ["Settings.About.Version"] = ("版本", "Version"),
        ["Settings.Update"] = ("更新", "Updates"),
        ["Settings.Update.Auto"] = ("自动检查更新（启动后及每天一次，从 GitHub 检查；更新前会先询问）", "Check for updates automatically (after startup and daily, from GitHub; asks before updating)"),
        ["Settings.Update.Check"] = ("检查更新", "Check for updates"),
        ["Settings.About.DataDir"] = ("打开数据目录", "Open data folder"),
        ["Settings.About.LogDir"] = ("打开日志目录", "Open log folder"),
        ["Settings.UserData"] = ("用户数据", "User data"),
        ["Settings.UserData.Hint"] = ("导出或导入全部配置（选项、热键、排除规则、收藏、网页搜索）和使用偏好（启动记录、搜索词关联、最近文件夹、筛选习惯），用于备份或迁移到另一台电脑。文件索引不包含在内，会自动重建。",
            "Export or import all configuration (options, hotkeys, exclusion rules, favorites, web search) and usage preferences (launch history, query associations, recent folders, filter habits) to back up or move to another PC. The file index is not included; it is rebuilt."),
        ["Settings.UserData.Export"] = ("导出用户数据…", "Export user data…"),
        ["Settings.UserData.Import"] = ("导入用户数据…", "Import user data…"),
        ["Settings.UserData.FileFilter"] = ("Hopkey 用户数据", "Hopkey user data"),
        ["Settings.UserData.Exported"] = ("已导出到：\n{0}", "Exported to:\n{0}"),
        ["Settings.UserData.Confirm"] = ("导入将替换当前数据：\n\n{0}\n导出时间：{1}\nHopkey 版本：{2}\n\n当前数据会先自动备份。继续？",
            "Importing will replace your current data:\n\n{0}\nExported: {1}\nHopkey version: {2}\n\nYour current data is backed up first. Continue?"),
        ["Settings.UserData.HasSettings"] = ("• 配置（{0} 条收藏，{1} 条排除规则，{2} 个网页搜索）", "• Configuration ({0} favorites, {1} exclusion rules, {2} web searches)"),
        ["Settings.UserData.HasHistory"] = ("• 使用偏好（{0} 条使用记录，{1} 个最近文件夹）", "• Usage preferences ({0} usage records, {1} recent folders)"),
        ["Settings.UserData.Imported"] = ("导入完成。", "Import complete."),
        ["Settings.UserData.Backup"] = ("原数据已备份到：\n{0}", "Previous data backed up to:\n{0}"),
        ["Settings.UserData.Invalid"] = ("无法读取该文件：{0}", "Can't read this file: {0}"),
        ["Settings.About.Perf"] =("性能指标（最近一次）", "Performance (latest)"),
        ["Settings.About.License"] = ("本软件为开源学习项目，按“原样”提供。", "Provided as-is for learning purposes."),
        ["Kind.Path"] = ("路径", "Path"),
        ["Kind.Name"] = ("名称", "Name"),
        ["Kind.Glob"] = ("路径通配", "Wildcard"),
        ["Kind.Regex"] = ("正则", "Regex"),
        ["Target.Both"] = ("文件和文件夹", "Files & folders"),
        ["Target.DirectoriesOnly"] = ("仅文件夹", "Folders only"),
        ["Target.FilesOnly"] = ("仅文件", "Files only"),
    };

    public static string T(string key)
    {
        if (!s_strings.TryGetValue(key, out var v)) return key;
        return Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? v.Zh : v.En;
    }

    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    public static ResourceDictionary CreateDictionary()
    {
        var d = new ResourceDictionary { [Marker] = true };
        foreach (var k in s_strings.Keys) d["S." + k] = T(k);
        return d;
    }

    public static void Apply(string language)
    {
        Language = string.IsNullOrEmpty(language) ? "zh-CN" : language;
        var app = Application.Current;
        if (app != null)
        {
            var md = app.Resources.MergedDictionaries;
            for (int i = md.Count - 1; i >= 0; i--)
                if (md[i].Contains(Marker)) md.RemoveAt(i);
            md.Add(CreateDictionary());
        }
        LanguageChanged?.Invoke();
    }

    public static void ApplyTo(ResourceDictionary target)
    {
        var md = target.MergedDictionaries;
        for (int i = md.Count - 1; i >= 0; i--)
            if (md[i].Contains(Marker)) md.RemoveAt(i);
        md.Add(CreateDictionary());
    }
}
