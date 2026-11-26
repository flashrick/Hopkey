# Hopkey 项目方案与技术设计（v1）

> 目标读者：主开发。按本文件直接实施，不需要再做架构决策。文中“必须”为硬约束，“推荐”为默认实现，可在不改变接口的前提下微调。
> 平台：Windows 10/11 x64；C# / .NET 10（`net10.0-windows`）；WPF + WinForms（仅 `NotifyIcon`）；零第三方 NuGet。

---

## 1. 目标与非目标

### 1.1 目标
| 优先级 | 功能 | 说明 |
|---|---|---|
| P1 | QuickSwitch | 标准打开/保存对话框增强：悬浮工具栏、自动跳转、跟随资源管理器、Ctrl+G |
| P2 | 文件夹排除 | 统一 `ExclusionMatcher`，索引与搜索共用；规则变更增量生效 |
| P3 | 索引 | NTFS USN/MFT（管理员）+ 目录爬取 + `FileSystemWatcher`（普通权限）；二进制缓存 |
| P3 | 搜索启动器 | 双击 Ctrl / 组合键唤起；多关键词、拼音、历史加权、动作菜单、网页搜索、计算器 |
| P4 | 资源管理器集成 | Explorer/对话框中直接打字唤起“上下文模式”，选中文件夹即在原窗口导航 |
| P4 | 收藏与最近文件夹 | 启动器与 QuickSwitch 共用 |
| P5 | 设置窗口、基础设施 | 托盘、单实例、日志、崩溃处理、主题、中英文、自启动、以管理员身份重启 |

### 1.2 非目标（v1 不做）
- 文件内容全文搜索；网络盘/可移动盘的默认索引（可手动添加为根目录，走爬取）。
- 非标准文件对话框（Qt/GTK/Java Swing 自绘、Office Backstage、`SHBrowseForFolder` 树形旧对话框）的导航；仅标准 `#32770` 通用对话框。
- 桌面（`Progman`/`WorkerW`）上的打字即搜。
- ReFS 的 USN（ReFS 卷一律走爬取）；插件系统；云同步；自动更新。

---

## 2. 架构

### 2.1 总体结构
```
+------------------------------- Hopkey.App (WPF exe) ---------------------------------+
|  Program.Main -> SingleInstance -> App -> AppHost (组合根, 生命周期)                        |
|                                                                                            |
|  [UI 线程 / WPF Dispatcher]          [Hook 线程 "CL-Hooks"]        [Shell 线程 "CL-Shell"]  |
|   TrayIconService                     KeyboardHook (WH_KEYBOARD_LL)  ExplorerService (COM)  |
|   LauncherWindow + ViewModel          WinEventHub (SetWinEventHook)  FileDialogNavigator    |
|   QuickSwitchService(状态机)          HotkeyService(双击Ctrl判定)     (SendMessageTimeout)   |
|   SettingsWindow                      仅过滤+转发 -> BeginInvoke                             |
|   HotkeyService(RegisterHotKey)                                                            |
|   TypeToSearchService  <------------- 事件 -------------+     [Toolbar 线程 "CL-Toolbar"]    |
|                                                               QuickSwitchToolbar(被对话框拥有)|
|   IconCache --> [Icon 线程 "CL-Icons" STA]                                                  |
+-----------------------------------------|-------------------------------------------------+
                                          v
+------------------------------- Hopkey.Core (class library) -------------------------+
|  Config(SettingsStore/AppSettings)   Exclusion(ExclusionMatcher)   History(HistoryStore)   |
|  Indexing: IndexManager -> IIndexSource{UsnIndexSource, CrawlerIndexSource} -> FileIndex   |
|            (索引写线程 "CL-IndexWriter"、每卷 USN 监视线程、FSW 线程池回调)                    |
|  Search: SearchEngine -> ISearchProvider{Index, App, History, Web, Calculator} (线程池)      |
|  Pinyin   Native(P/Invoke + ComImport)   Util(Logger, PathUtil)                             |
+--------------------------------------------------------------------------------------------+
```

### 2.2 线程模型（必须遵守）
| 线程 | 类型 | 职责 | 约束 |
|---|---|---|---|
| UI | STA, WPF Dispatcher | 所有窗口、托盘、`RegisterHotKey`（通过隐藏 `HwndSource` 收 `WM_HOTKEY`）、服务状态机 | 不做任何跨进程阻塞调用 |
| CL-Hooks | 后台 STA，`Dispatcher.Run()` 提供消息泵 | 安装 `WH_KEYBOARD_LL` 与所有 `SetWinEventHook(WINEVENT_OUTOFCONTEXT)` | 回调必须在 1ms 级返回：只读窗口类名（带缓存）、判定、`Dispatcher.BeginInvoke` 到 UI；**不可**调用 COM、不可 `SendMessage` 到其他进程。LL 钩子超时（`LowLevelHooksTimeout`）多次会被系统静默卸载 |
| CL-Shell | 后台 STA，`Dispatcher.Run()` | `ShellWindows` COM（缓存，Explorer 前台事件时增量刷新，绝不在热路径上枚举）、`IShellBrowser`、对话框导航 | 对外暴露 `Task` API；单次调用超时 5s，超时则丢弃并重建该线程（Explorer 卡死时自愈） |
| CL-Toolbar | 后台 STA，独立 WPF Dispatcher | 仅承载 QuickSwitch 工具栏窗口及其菜单 | 工具栏被对话框跨进程“拥有”，系统会隐式附加输入队列；隔离到独立线程，目标进程卡死时只影响工具栏，不影响启动器/托盘。主题资源在本线程各自加载（Application 资源跨线程不可用） |
| CL-Icons | 后台 STA，队列 | `IShellItemImageFactory`/`SHGetFileInfo` 取图标 | 低优先级，结果 `Freeze()` 后交给 UI |
| CL-IndexWriter | 后台线程 | 唯一写索引线程：应用变更批次（300ms 合并）、剪枝、持久化 | 写时持 `VolumeIndex` 写锁 |
| USN 监视 | 每卷一个后台线程 | 阻塞 `FSCTL_READ_USN_JOURNAL`，产出变更到 Channel | 退出用 `CancelIoEx` |
| 搜索 | 线程池 `Task.Run` + `CancellationToken` | 并行扫描卷、TopN 合并 | 持读锁；每次新输入取消上一次 |

工具类 `DispatcherThread`（App/Infrastructure）统一创建后台 Dispatcher 线程：`new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext()); ready.Set(); Dispatcher.Run(); }) { IsBackground = true }` + `SetApartmentState(STA)`；提供 `InvokeAsync<T>(Func<T>, TimeSpan timeout)`。

所有 native 回调委托（`WinEventProc`、`LowLevelKeyboardProc`、`WndProc`）必须保存在字段中防 GC；回调体整体 `try/catch` 记录日志（回调中抛出异常会使进程崩溃）。

---

## 3. 解决方案与文件清单（开发者按此创建）

### 3.1 工程设置
- `Hopkey.sln`
- `src/Hopkey.Core/Hopkey.Core.csproj`：`net10.0-windows`，`Nullable=enable`，`ImplicitUsings=enable`，`AllowUnsafeBlocks=true`，`NoWarn` 加 `SYSLIB1054`（统一用 `[DllImport(CharSet=Unicode, SetLastError=true)]`，不用 LibraryImport，减少结构体封送坑）。`<UseWPF>false`。
- `src/Hopkey.App/Hopkey.App.csproj`：`WinExe`，`UseWPF=true`，`UseWindowsForms=true`，`ImplicitUsings=disable`，`Nullable=enable`，`ApplicationManifest=app.manifest`，`ApplicationIcon=Assets\app.ico`；`<ApplicationDefinition Remove="App.xaml"/><Page Include="App.xaml"/>`（自定义 `Program.Main`）。引用 Core。
- `app.manifest`：`requestedExecutionLevel level="asInvoker"`；`<dpiAwareness>PerMonitorV2</dpiAwareness>` + `<dpiAware>true/pm</dpiAware>`；`<longPathAware>true</longPathAware>`；Win10 `supportedOS`；Common Controls v6 依赖。
- 运行时 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`（拼音首字母要 GB2312，框架内置，无需 NuGet）。
- 性能相关工程属性（两工程，Release）：`PublishReadyToRun=true`、`TieredPGO=true`（.NET 10 默认开，显式写出）、`TieredCompilation=true`、`InvariantGlobalization=false`、`ServerGarbageCollector=false`、`ConcurrentGarbageCollection=true`、`SatelliteResourceLanguages=en`、`DebugType=portable`；发布为 `win-x64` framework-dependent 单目录（WPF 不支持 NativeAOT/裁剪）。详见 §9。

### 3.2 Hopkey.Core
| 路径 | 职责 |
|---|---|
| `Config/AppPaths.cs` | `%AppData%\Hopkey`、`%LocalAppData%\Hopkey`、logs、index.bin、history.json 路径；确保目录存在 |
| `Config/AppSettings.cs` | 全部设置模型（见 §4.1），含默认值 |
| `Config/HotkeySpec.cs` | 热键描述解析/格式化：`"DoubleCtrl"`、`"Alt+Space"`、`"Ctrl+G"` |
| `Config/SettingsStore.cs` | 读写 settings.json（原子写：写 .tmp 再 `File.Replace`），`SchemaVersion` 迁移，损坏时备份为 `.bad` 并用默认；`Changed(old,new)` 事件 |
| `Config/JsonContext.cs` | `JsonSerializerContext` 源生成（AppSettings、HistoryData），`WriteIndented`、枚举字符串化 |
| `Exclusion/ExclusionRule.cs` | 规则模型与枚举 |
| `Exclusion/WildcardPattern.cs` | 通配符 → 正则源码转换（`*`、`?`、`**\`）；名称通配合并为一个编译正则 |
| `Exclusion/ExclusionMatcher.cs` | 编译后的不可变匹配器；索引与搜索共用；`Fingerprint`、`Diff` |
| `Exclusion/DefaultExclusions.cs` | 内置默认规则集 |
| `Exclusion/ExclusionRuleIO.cs` | 规则导入/导出（JSON 数组；也接受每行一条的纯文本） |
| `Indexing/IndexEntryFlags.cs` | 条目标志位 |
| `Indexing/NameArena.cs` | 可增长 `char[]` 竞技场（`\0` 分隔），原名与预小写各一份；追加、压实 |
| `Indexing/VolumeIndex.cs` | 单根的 SoA 紧凑索引、路径重建、读写锁、`ApplyBatch`、`Prune` |
| `Indexing/KeyMap.cs` | FRN → 下标：有序 `ulong[]` 二分 + 追加条目溢出字典 |
| `Indexing/FileIndex.cs` | 所有 `VolumeIndex` 的集合（写时复制数组）+ 统计 |
| `Indexing/IndexChange.cs` | 变更记录结构 |
| `Indexing/IIndexSource.cs` | 索引源抽象 |
| `Indexing/IndexBuilder.cs` | 原始条目 → 父链解析 → 排除判定（记忆化）→ 压实为 `VolumeIndex` |
| `Indexing/UsnIndexSource.cs` | MFT 枚举 + USN 日志追赶与实时监视（需管理员 + NTFS） |
| `Indexing/DirectoryCrawler.cs` | 并行目录爬取（`FileSystemEnumerable`，跳过重解析点递归，遵守排除） |
| `Indexing/CrawlerIndexSource.cs` | 爬取构建 + `FileSystemWatcher` 实时更新 |
| `Indexing/IndexPersistence.cs` | index.bin 二进制读写 |
| `Indexing/IndexManager.cs` | 编排：选择源、加载缓存、追赶、重建、排除变更增量处理、定时保存、状态事件 |
| `Indexing/AppCatalog.cs` | 开始菜单/桌面 .lnk/.exe/.appref-ms/.url 与（可选）UWP 应用列表 |
| `Search/SearchQuery.cs` | 解析后的查询（关键词、过滤器、web/calc 意图） |
| `Search/QueryParser.cs` | 文本 → `SearchQuery` |
| `Search/ArenaScanner.cs` | 第一阶段：分区向量化 `IndexOf` 扫描竞技场、偏移→条目二分、预评分、每 worker TopK（零分配） |
| `Search/NameMatcher.cs` | 第二阶段：单关键词对名称精确打分（含缩写/模糊）+ 高亮区间 |
| `Search/SearchResult.cs` | 结果模型 |
| `Search/ISearchProvider.cs` | 提供者接口 |
| `Search/TopN.cs` | 有界最小堆 |
| `Search/SearchEngine.cs` | 并发调用提供者、合并、历史加权、排除后过滤 |
| `Search/Providers/IndexSearchProvider.cs` | 扫描 `FileIndex` |
| `Search/Providers/AppSearchProvider.cs` | 扫描 `AppCatalog` |
| `Search/Providers/HistorySearchProvider.cs` | 空查询：最近/收藏；非空：历史项匹配 |
| `Search/Providers/WebSearchProvider.cs` | 关键字前缀网页搜索 |
| `Search/Providers/CalculatorProvider.cs` | 表达式求值结果 |
| `Search/Calculator.cs` | 递归下降求值（`+ - * / % ^ ()`、一元负号、小数） |
| `History/HistoryStore.cs` | 使用记录、最近文件夹、查询关联；history.json 防抖保存 |
| `History/HistoryModels.cs` | `UsageRecord`、`RecentFolder`、`HistoryData` |
| `History/FavoritesService.cs` | 收藏增删改排序（数据存 settings.json） |
| `Pinyin/PinyinHelper.cs` | 汉字判断、首字母串、全拼（可选，懒加载） |
| `Pinyin/PinyinInitialsTable.cs` | `U+4E00–U+9FFF` → 首字母 `byte[20992]` 表：优先读嵌入资源 `pinyin-initials.bin`；缺失时启动后由 GB2312 一级字分界法生成（二级字记 0=未知）并缓存到 `%LocalAppData%` |
| `Pinyin/pinyin-initials.bin`、`Pinyin/pinyin-full.txt`（EmbeddedResource，可选） | 完整首字母表 / 全拼数据，见 §13 待定 |
| `Native/User32.cs`、`Kernel32.cs`、`Shell32.cs`、`Dwmapi.cs`、`Advapi32.cs`、`Gdi32.cs`、`Ole32.cs` | P/Invoke 声明 |
| `Native/NativeStructs.cs` | RECT/POINT/KBDLLHOOKSTRUCT/INPUT/GUITHREADINFO/MONITORINFO/SHFILEINFO 等 |
| `Native/UsnNative.cs` | USN/MFT 结构体、FSCTL 常量 |
| `Native/ComInterfaces.cs` | `IServiceProvider`、`IShellBrowser`（按 vtable 顺序声明至 `BrowseObject`：`GetWindow, ContextSensitiveHelp, InsertMenusSB, SetMenuSB, RemoveMenusSB, SetStatusTextSB, EnableModelessSB, TranslateAcceleratorSB, BrowseObject`）、`IShellItem`、`IShellItemImageFactory` 的 `[ComImport]` |
| `Native/ElevationHelper.cs` | 自身是否管理员；目标进程完整性级别/是否提升 |
| `Util/Logger.cs` | 异步文件日志（按天滚动，保留 7 天） |
| `Util/PathUtil.cs` | 规范化、比较（OrdinalIgnoreCase）、`IsUnder`、env 展开、带超时的 `DirectoryExists` |

### 3.3 Hopkey.App
| 路径 | 职责 |
|---|---|
| `Program.cs` | `[STAThread] Main`：解析参数（`--autostart`、`--wait-pid N`、`--elevated-restart`）、单实例、创建 App |
| `App.xaml/.cs` | 资源合并（主题、字符串）；`OnStartup` 创建 `AppHost` |
| `Bootstrap/AppHost.cs` | 组合根：创建并持有全部服务，按顺序启动/停止 |
| `Bootstrap/SingleInstance.cs` | 命名 Mutex + 命名 EventWaitHandle 唤醒 |
| `Bootstrap/CrashHandler.cs` | 全局异常处理、崩溃日志、提示 |
| `Bootstrap/ElevationService.cs` | 以管理员身份重启（`runas`）；管理员自启动计划任务（`schtasks.exe`） |
| `Bootstrap/AutostartService.cs` | HKCU Run 键读写 |
| `Infrastructure/DispatcherThread.cs` | 后台 Dispatcher 线程封装 |
| `Infrastructure/ForegroundHelper.cs` | 可靠前台激活（AttachThreadInput 技巧） |
| `Infrastructure/WindowInfoCache.cs` | hwnd→类名/进程/是否提升 缓存（`EVENT_OBJECT_DESTROY` 失效） |
| `Infrastructure/ScreenUtil.cs` | 显示器工作区、DWM 扩展边框、物理像素定位 |
| `Infrastructure/ThemeManager.cs` | Light/Dark/跟随系统，切换 ResourceDictionary，DWM 暗色标题栏 |
| `Infrastructure/LocalizationManager.cs` | zh-CN/en 字符串字典切换 |
| `Infrastructure/Warmup.cs` | 启动空闲期预热：预创建启动器窗口、跑一次假搜索、触发结果模板布局、`RuntimeHelpers.PrepareMethod` 热路径 |
| `Infrastructure/PerfLog.cs` | `Stopwatch` 打点（热键→可见、按键→结果、对话框→工具栏、索引构建/加载），写日志并在“关于”页显示最近值 |
| `Mvvm/ObservableObject.cs`、`Mvvm/RelayCommand.cs` | 轻量 MVVM |
| `Tray/TrayIconService.cs` | NotifyIcon + 菜单 |
| `Hooks/WinEventHub.cs` | 管理 WinEvent 钩子，向 UI 发布 `Foreground/LocationChanged/Destroyed/NameChanged/Hidden` |
| `Hooks/KeyboardHook.cs` | LL 键盘钩子，按顺序调用已注册过滤器（可吞键） |
| `Hooks/HotkeyService.cs` | 双击 Ctrl 判定、`RegisterHotKey`、对话框内 Ctrl+G |
| `Explorer/ExplorerService.cs` | ShellWindows 枚举/z 序/活动标签/导航/选中；最近活动 Explorer 跟踪 |
| `Explorer/ExplorerModels.cs` | `ExplorerWindowInfo`、`ExplorerActivation` |
| `Explorer/TypeToSearchService.cs` | 打字即搜触发与上下文导航分发 |
| `QuickSwitch/FileDialogInfo.cs` | 对话框句柄快照（各控件 hwnd、类型、是否提升） |
| `QuickSwitch/FileDialogDetector.cs` | 判定并解析对话框结构 |
| `QuickSwitch/FileDialogNavigator.cs` | 导航（文件名框主路径 + 地址栏兜底）、读当前目录、设置文件名 |
| `QuickSwitch/QuickSwitchService.cs` | 对话框状态机：自动跳转、跟随、热键、工具栏显示 |
| `QuickSwitch/ToolbarHost.cs` | 在 CL-Toolbar 线程上创建/持有工具栏，提供跨线程 `AttachTo/Reposition/Hide` |
| `QuickSwitch/QuickSwitchToolbar.xaml/.cs` | 悬浮工具栏窗口（横向条，贴在对话框下沿） |
| `QuickSwitch/ToolbarPlacement.cs` | 依附位置计算（物理像素） |
| `QuickSwitch/FolderMenuBuilder.cs` | 生成“已打开文件夹/收藏/最近”菜单 |
| `Launcher/LauncherWindow.xaml/.cs` | 启动器窗口 |
| `Launcher/LauncherViewModel.cs` | 查询、结果、选择、动作菜单状态 |
| `Launcher/ResultItemViewModel.cs` | 单条结果显示数据（异步图标） |
| `Launcher/LauncherContext.cs` | 普通/上下文模式（目标 Explorer 或对话框 hwnd） |
| `Launcher/ResultActions.cs` | 动作定义与按结果类型的动作列表 |
| `Launcher/ActionExecutor.cs` | 执行打开/打开位置/管理员运行/复制/终端/属性/排除/收藏 |
| `Launcher/IconCache.cs` | 图标：按扩展名缓存（`SHGFI_USEFILEATTRIBUTES`，不碰磁盘）；仅 .exe/.lnk/.ico/.url/.appref-ms 与特殊文件夹按路径后台加载；先占位后替换；LRU |
| `Launcher/HighlightText.cs` | 附加属性：按高亮区间生成 `Run` |
| `Settings/SettingsWindow.xaml/.cs`、`Settings/SettingsViewModel.cs` | 设置窗口与 VM |
| `Settings/Pages/GeneralPage.xaml/.cs`、`QuickSwitchPage`、`IndexPage`、`FavoritesPage`、`WebSearchPage`、`AboutPage` | 各标签页（UserControl） |
| `Settings/HotkeyBox.cs` | 捕获组合键的 TextBox 控件 |
| `Themes/Light.xaml`、`Themes/Dark.xaml`、`Themes/Controls.xaml` | 颜色画刷与控件样式 |
| `Localization/Strings.zh-CN.xaml`、`Localization/Strings.en.xaml` | 字符串资源（`DynamicResource`） |
| `Assets/app.ico`、`Assets/tray.ico`、`Assets/tray-paused.ico` | 图标 |

---

## 4. 关键数据结构与接口

### 4.1 设置
```csharp
public sealed class AppSettings {
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public QuickSwitchSettings QuickSwitch { get; set; } = new();
    public IndexSettings Index { get; set; } = new();
    public ExclusionSettings Exclusion { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public List<FavoriteItem> Favorites { get; set; } = new();
    public List<WebSearchEngine> WebSearch { get; set; } = WebSearchEngine.Defaults();
    public AppSettings Clone();                        // JSON 往返深拷贝
}
public sealed class GeneralSettings { bool Autostart; bool AutostartElevated; string Language = "zh-CN"; ThemeMode Theme = ThemeMode.System; bool StartElevated; }
public sealed class HotkeySettings { string Launcher = "DoubleCtrl"; string LauncherAlt = "Alt+Space"; string DialogJump = "Ctrl+G"; bool TypeToSearch = true; }
public sealed class QuickSwitchSettings {
    bool Enabled = true; bool ShowToolbar = true; bool AutoSwitchOnOpen = false;
    int AutoSwitchMaxAgeMinutes = 10; bool FollowExplorer = true; bool DialogJumpHotkey = true;
    List<string> DisabledProcesses = new();            // 完全禁用 QuickSwitch，例如 "photoshop.exe"
    List<string> AutoSwitchExcludedProcesses = new();  // 仅禁用自动跳转/跟随，保留工具栏与 Ctrl+G
}
public sealed class IndexSettings { List<string> Roots = new(); /* 空=全部固定盘 */ bool UseUsnWhenElevated = true; bool IndexApps = true; bool IndexUwpApps = false; int SaveIntervalMinutes = 10; }
public sealed class ExclusionSettings { List<ExclusionRule> Rules = DefaultExclusions.Create(); bool ExcludeHidden = false; bool ExcludeSystem = true; }
public sealed class SearchSettings { int MaxResults = 50; bool Pinyin = true; bool FullPinyin = false; bool UseHistoryBoost = true; }
public sealed record FavoriteItem(string Path, string? Alias);
public sealed record WebSearchEngine(string Keyword, string Name, string UrlTemplate /* {q} */, bool Enabled);
public enum ThemeMode { System, Light, Dark }
```
（上方字段以 `{ get; set; }` 属性实现，此处为简写。）`SettingsStore.Update(Action<AppSettings>)`：克隆→修改→保存→触发 `Changed(old, new)`；各服务在 UI 线程订阅并按差异重新应用（热键重注册、钩子开关、排除重算等）。

### 4.2 排除
```csharp
public enum ExclusionKind { Path, Name, Glob, Regex }
public enum ExclusionTarget { Both, DirectoriesOnly, FilesOnly }
public sealed class ExclusionRule {
    public Guid Id { get; set; } = Guid.NewGuid();
    public ExclusionKind Kind { get; set; }
    public string Pattern { get; set; } = "";   // Path: 绝对路径(可含 %ENV%)；Name: 名称或通配名(node_modules、*.tmp)；
                                                // Glob: 含 \ 的路径通配(**\build\obj、C:\Users\*\AppData\Local\Temp)；Regex: .NET 正则，匹配完整路径
    public ExclusionTarget Target { get; set; } = ExclusionTarget.Both;
    public bool Enabled { get; set; } = true;
    public bool BuiltIn { get; set; }
}
public sealed class ExclusionMatcher {
    public static ExclusionMatcher Create(ExclusionSettings s);
    public static ExclusionMatcher Empty { get; }
    public string Fingerprint { get; }            // 启用规则+开关的 SHA256 前 16 字节 hex
    public bool HasFullPathRules { get; }          // 存在 Glob/Regex（需要拼完整路径）
    // 索引用：只判定“自身这一层”（祖先已由调用方保证未被排除）
    public bool IsExcludedEntry(ReadOnlySpan<char> name, string? parentFullPath, bool isDirectory, FileAttributes attrs);
    // 通用：判定任意完整路径（逐段检查名称规则 + 路径前缀 + 正则；属性只检查自身，attrs 可传 null 表示未知）
    public bool IsExcludedPath(string fullPath, bool isDirectory, FileAttributes? attrs = null);
    public ExclusionDiff Diff(ExclusionMatcher previous);
}
public sealed record ExclusionDiff(bool HasAdditions, bool HasRemovals, IReadOnlyList<string> RemovedPathRules, bool RemovedNonPathRules);
```
内部编译（`Create` 时一次完成，之后只读、线程安全）：
- Path：展开环境变量、`Path.GetFullPath`、去尾部 `\`，存入 `HashSet<string>(OrdinalIgnoreCase)`（构建期只需判“目录完整路径是否恰好等于”）+ 按长度排序数组（`IsExcludedPath` 前缀判断）。
- Name：无通配符的放入 `HashSet<string>`，按 Target 分目录/文件两组，查找用 `GetAlternateLookup<ReadOnlySpan<char>>()` 零分配；含通配符的全部合并成**一个**编译正则 `^(?:p1|p2|…)$`（`IgnoreCase|CultureInvariant|Compiled`），同样按 Target 分组。
- Glob/Regex：合并为一个正则，对完整路径匹配（`**` → `.*`，`*` → `[^\\]*`，`?` → `[^\\]`）；默认 Target=DirectoriesOnly，这样代价是 O(目录数) 而非 O(文件数)；用户显式选“文件”时才对文件拼完整路径。非法正则在 UI 校验时拒绝、加载时跳过并记日志。
- **驱动器根目录永不被属性规则排除**（根目录带 Hidden|System）。

默认规则（`BuiltIn=true`，可禁用不可删除）：Name：`$RECYCLE.BIN`、`System Volume Information`、`$WinREAgent`、`node_modules`、`.git`、`.svn`、`.hg`、`__pycache__`、`.vs`、`*.tmp`（FilesOnly）、`~$*`（FilesOnly）；Path：`%SystemRoot%\WinSxS`、`%SystemRoot%\Installer`、`%SystemRoot%\servicing`、`%SystemRoot%\SoftwareDistribution`、`%TEMP%`、`%ProgramData%\Package Cache`。

### 4.3 索引
```csharp
[Flags] public enum IndexEntryFlags : byte { None=0, Directory=1, Hidden=2, System=4, ReparsePoint=8, Deleted=16, HasCjk=32 }
public enum IndexSourceKind { Usn, Crawler }

public sealed class VolumeIndex {
    public string RootPath { get; }                // "C:\" 或 "D:\Data"
    public IndexSourceKind Kind { get; }
    public ulong JournalId { get; set; }  public long NextUsn { get; set; }   // 仅 USN
    // SoA，容量倍增（1.5x），下标即条目 id；0 号为根。不变式：条目只追加，NameOffset 随下标严格递增
    internal int[] Parent;          // 父条目下标，根为 -1                                  4B
    internal int[] NameOffset;      // 名称在两个竞技场中的起始偏移（两者布局完全相同）    4B
    internal byte[] NameLength;     // NTFS 名称 ≤255 个 UTF-16 单元                        1B
    internal IndexEntryFlags[] Flags;                                                     // 1B
    internal NameArena Names;       // 原始大小写，"\0name\0name\0…"                       ~2(L+1)B
    internal NameArena Lower;       // 预小写（char.ToLowerInvariant 逐字符），布局同上    ~2(L+1)B
    internal KeyMap? Keys;          // 仅 USN：FRN → 下标（构建后按 FRN 有序的 ulong[] + 溢出字典） 8B
    internal Dictionary<string,int>? DirPaths;   // 仅爬取源：目录完整路径 → 下标（FSW 事件定位用）
    internal PinyinArena? Pinyin;   // 仅 HasCjk 条目：小写首字母串竞技场 + 有序 int[] entryIds
    public int Count { get; }  public int LiveCount { get; }  public int GarbageChars { get; }
    public ReaderWriterLockSlim Lock { get; }
    public ReadOnlySpan<char> GetName(int i);
    public string GetFullPath(int i);              // 向上回溯拼接；目录路径 LRU 缓存 4096
    public int EntryAtOffset(int arenaOffset);     // 对 NameOffset 二分（递增不变式保证正确）
    public bool TryFindByKey(ulong key, out int index);
    public bool TryFindDirectory(string fullPath, out int index); // 爬取源：目录路径→下标 Dictionary（仅目录）
    public void ApplyBatch(IReadOnlyList<IndexChange> changes, ExclusionMatcher m); // 写锁内
    public int Prune(ExclusionMatcher m);           // 写锁内，返回删除数（§6.2）
}
public sealed class FileIndex {
    public IReadOnlyList<VolumeIndex> Volumes { get; }   // volatile 数组，写时复制替换
    public long TotalLiveCount { get; }
    public void SetVolume(VolumeIndex v);  public void RemoveVolume(string root);
    public event Action? Changed;                         // 节流 1s
}
public enum ChangeKind { Created, Deleted, Renamed, AttributesChanged }
public readonly record struct IndexChange(ChangeKind Kind, ulong Key, ulong ParentKey, string? Name, FileAttributes Attributes,
                                          string? FullPath, string? OldFullPath);  // USN 用 Key/ParentKey，爬取用 FullPath/OldFullPath
public interface IIndexSource : IDisposable {
    string RootPath { get; }
    IndexSourceKind Kind { get; }
    Task<VolumeIndex> BuildAsync(ExclusionMatcher m, IProgress<long>? progress, CancellationToken ct);
    // 用缓存继续：USN 回放日志成功返回 true；爬取源返回 false（调用方保留缓存可搜，并后台重建替换）
    Task<bool> CatchUpAsync(VolumeIndex cached, ExclusionMatcher m, CancellationToken ct);
    void StartWatching(ChannelWriter<(string Root, IndexChange Change)> sink);
    void StopWatching();
    event Action<string>? RebuildRequired;   // 日志被覆盖/ID 变化/FSW 缓冲溢出
}
public sealed class IndexManager : IDisposable {
    public FileIndex Index { get; }
    public IndexStatus Status { get; }  public event Action<IndexStatus>? StatusChanged; // Loading/Building(n)/Ready/Paused/Error
    public Task StartAsync(CancellationToken ct);       // 加载缓存→追赶→监视；无缓存则构建
    public Task RebuildAsync(string? root = null);
    public void OnExclusionChanged(ExclusionMatcher oldM, ExclusionMatcher newM);  // §6.2
    public void OnRootsChanged(IReadOnlyList<string> roots);
    public Task SaveAsync();
    public void Pause(bool paused);
}
```
**改名规则（保持 NameOffset 递增）**：新名长度 ≤ 旧名 → 原地覆写两个竞技场，多余位置填 `\0`；更长 → 旧条目置 `Deleted`，追加新条目（同 Key），若为目录则一次 O(n) 扫描把 `Parent==old` 改为 `new`（目录改长名很少见，200 万条约 2ms）。删除只置位，名称留作垃圾，`EntryAtOffset` 命中已删除条目即跳过。

内存预估（平均名长 L≈18）：`4+4+1+1 = 10B` + 两竞技场 `4(L+1) ≈ 76B` + USN `KeyMap 8B` ≈ **94 B/条**；200 万条 ≈ 190MB，100 万条 ≈ 95MB（与 Everything 同量级）。不存大小/时间戳。

**index.bin 格式**：`"CLIX"` + `int version(1)` + `string exclusionFingerprint` + `int volumeCount`；每卷：`root, kind, volumeSerial, journalId, nextUsn, count, arenaLength`，随后 Parent/NameOffset/NameLength/Flags/Names/KeyMap 以 `MemoryMarshal.AsBytes` 原样写出（**不存 Lower 竞技场与拼音**，加载时并行重算，比读盘更快且文件小一半）。保存前若 `GarbageChars > 20%` 或删除条目 >10% 先压实（压实时按 FRN 重排，使 KeyMap 重新有序）。读入：`RandomAccess.Read` 直接读进预分配数组（1MB 块）；版本/卷序列号不符则丢弃该卷。写入走 `.tmp` + `File.Replace`。保存时机：构建完成、每 `SaveIntervalMinutes`（有变更时）、退出。

### 4.4 搜索
```csharp
public enum ResultKind { File, Folder, App, Web, Calculator, Command }
public enum TypeFilter { Any, Folder, File, App }
public sealed record SearchQuery(string Raw, IReadOnlyList<string> Keywords, TypeFilter Type, string? Extension,
                                 WebSearchEngine? Web, string? WebText, string? Expression, bool IsEmpty);
public sealed class SearchResult {
    public ResultKind Kind { get; init; }
    public string Title { get; init; } = "";        public string? Subtitle { get; init; }   // 显示名 / 完整路径
    public string? Path { get; init; }              public string? Arguments { get; init; } // App: lnk 路径或 shell:AppsFolder\AUMID
    public IReadOnlyList<(int Start, int Length)> Highlights { get; init; } = [];
    public double Score { get; set; }
    public string? CopyText { get; init; }          // 计算器结果
}
public interface ISearchProvider { string Name { get; } bool CanHandle(SearchQuery q);
    void Search(SearchQuery q, ResultSink sink, CancellationToken ct); }   // ResultSink 线程安全 TopN
public sealed class SearchEngine {
    public SearchEngine(IEnumerable<ISearchProvider> providers, HistoryStore history, Func<ExclusionMatcher> matcher, Func<SearchSettings> settings);
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string text, CancellationToken ct);
}
```

### 4.5 历史 / Explorer / 热键 / QuickSwitch
```csharp
public sealed class HistoryStore {
    public static HistoryStore Load(string path);
    public void RecordLaunch(string pathOrId, ResultKind kind, string? query);   // count++, lastUsed=now, 记录 query 前缀关联（每项最多 10 个）
    public void RecordFolderVisit(string folder, FolderVisitSource source);       // Explorer/Dialog/Launcher；去重置顶，最多 100
    public double GetBoost(string pathOrId, string normalizedQuery);             // §6.4；内部按小写路径的 Dictionary，O(1)
    public IReadOnlyList<RecentFolder> GetRecentFolders(int max, bool onlyExisting = true);
    public IReadOnlyList<UsageRecord> GetTop(int max);
    public void Remove(string pathOrId); public void ClearRecent();
    public void ScheduleSave();   // 2s 防抖，原子写
}
public sealed record UsageRecord(string Key, ResultKind Kind, int Count, DateTime LastUsedUtc, Dictionary<string,int> Queries);
public sealed record RecentFolder(string Path, DateTime LastVisitUtc, FolderVisitSource Source);

public sealed record ExplorerWindowInfo(IntPtr Hwnd, string? Path, string Title, bool IsFileSystem);
public sealed record ExplorerActivation(IntPtr Hwnd, DateTime AtUtc);
public sealed class ExplorerService {   // 所有 COM 在 CL-Shell 线程
    public ExplorerActivation? LastActive { get; }                     // 由 UI 线程在前台事件中更新（无锁，volatile 引用替换）
    public void NotifyActivated(IntPtr hwnd);                          // 同时在 CL-Shell 上异步刷新该窗口缓存项
    public IReadOnlyList<ExplorerWindowInfo> Cached { get; }           // 热路径（菜单、Ctrl+G）直接读缓存，不发 COM 调用
    public Task<IReadOnlyList<ExplorerWindowInfo>> GetWindowsAsync(CancellationToken ct);  // 全量刷新：按 z 序（顶→底），每窗口取活动标签
    public Task<string?> GetFolderAsync(IntPtr hwnd, CancellationToken ct);                // 活动标签的文件系统路径；虚拟文件夹返回 null
    public Task<bool> NavigateAsync(IntPtr hwnd, string folder, CancellationToken ct);
    public Task<bool> NavigateAndSelectAsync(IntPtr hwnd, string filePath, CancellationToken ct);
    public event Action<string>? FolderVisited;                        // 供 HistoryStore
}

public sealed class HotkeyService : IDisposable {
    public void Apply(HotkeySettings s, bool paused);
    public event Action? LauncherRequested;                 // 双击 Ctrl 或 RegisterHotKey
    public event Action<IntPtr>? DialogJumpRequested;       // 对话框前台时 Ctrl+G（被吞掉）
}

public sealed class FileDialogInfo {
    public IntPtr Hwnd, FileNameEdit, OkButton, AddressToolbar, AddressEdit /*可能为 0，点开地址栏后才存在*/, ShellView;
    public FileDialogStyle Style;     // Modern(IFileDialog) / Legacy
    public bool IsFolderPicker;  public bool IsSaveDialog;
    public int ProcessId;  public bool TargetElevated;  public string ProcessName = "";
}
public sealed class FileDialogNavigator {   // 在 CL-Shell 线程执行
    public Task<NavigateResult> NavigateAsync(FileDialogInfo dlg, string folder, CancellationToken ct);
    public Task<NavigateResult> NavigateToFileAsync(FileDialogInfo dlg, string filePath, CancellationToken ct); // 导航到父目录并填文件名，不确认
    public string? TryGetCurrentFolder(FileDialogInfo dlg);
}
public enum NavigateResult { Ok, NotFound, Blocked /*UIPI*/, Timeout, Failed }
public sealed class QuickSwitchService : IDisposable {
    public void Apply(QuickSwitchSettings s, bool paused);
    public FileDialogInfo? ActiveDialog { get; }
    public Task NavigateActiveAsync(string folder);   // 供工具栏菜单与启动器上下文模式
    public event Action<FileDialogInfo?>? ActiveDialogChanged;
}
```

---

## 5. Win32 / COM API 一览（按模块）

| 模块 | API | 备注 |
|---|---|---|
| WinEventHub | `SetWinEventHook`、`UnhookWinEvent` | 全局（pid=0,tid=0）：`EVENT_SYSTEM_FOREGROUND`、`EVENT_OBJECT_DESTROY`、`EVENT_OBJECT_HIDE`、`EVENT_SYSTEM_DIALOGEND`、`EVENT_OBJECT_NAMECHANGE`（仅 `CabinetWClass`，500ms 防抖，捕获 Explorer 内导航）。**仅在工具栏附着期间**对对话框所在进程（`idProcess`+`idThread`）装 `EVENT_OBJECT_LOCATIONCHANGE`、`EVENT_SYSTEM_MINIMIZESTART/END`，脱离即卸载。所有回调首先过滤 `idObject==OBJID_WINDOW && idChild==CHILDID_SELF`，DESTROY/HIDE 再用 `HashSet<IntPtr>`（已跟踪对话框/Explorer）过滤，其余立即返回。flags：`WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS` |
| KeyboardHook | `SetWindowsHookEx(WH_KEYBOARD_LL)`、`CallNextHookEx`、`GetAsyncKeyState` | 忽略 `LLKHF_INJECTED` 且 `dwExtraInfo == 我们的标记` 的自注入按键；提升窗口在前台时收不到（UIPI） |
| HotkeyService | `RegisterHotKey`/`UnregisterHotKey`（`MOD_NOREPEAT`） | 注册失败（被占用）→ 托盘气泡 + 设置页标红 |
| ForegroundHelper | `GetForegroundWindow`、`GetWindowThreadProcessId`、`AttachThreadInput`、`BringWindowToTop`、`SetForegroundWindow`、`SendInput`（兜底：发一次空 Alt 抬起） | |
| WindowInfo | `GetClassName`、`GetWindowText`、`IsWindowVisible`、`GetAncestor(GA_ROOT)`、`GetGUIThreadInfo`、`OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`、`QueryFullProcessImageName`、`OpenProcessToken`+`GetTokenInformation(TokenElevation/TokenIntegrityLevel)` | 打不开目标令牌按“已提升”处理 |
| FileDialogDetector | `FindWindowEx`、`EnumChildWindows`、`GetDlgItem`、`GetDlgCtrlID` | 见 §6.1 |
| FileDialogNavigator | `SendMessageTimeout(WM_GETTEXT/WM_SETTEXT, SMTO_ABORTIFHUNG, 500)`、`PostMessage(WM_KEYDOWN/WM_KEYUP, VK_RETURN)`、`BM_CLICK`、`EM_SETSEL`；兜底：`GetAsyncKeyState`（等修饰键释放）、`SendInput`（Ctrl+L / Enter）、`GetGUIThreadInfo.hwndFocus` | 不用 UIA（慢、跨进程开销大）；`IFileDialog` COM 跨进程不可用 |
| ScreenUtil / 工具栏 | `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`、`MonitorFromWindow`、`GetMonitorInfo`、`GetDpiForWindow`、`SetWindowLongPtr(GWLP_HWNDPARENT, dlg)`（跨进程 owner）、`SetWindowPos(SWP_NOACTIVATE|SWP_NOZORDER)`、`GWL_EXSTYLE |= WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE` | 全部物理像素；不用 TOPMOST |
| ExplorerService | `ShellWindows`（CLSID `9BA05972-F6A8-11CF-A442-00A0C90A8F39`，`Activator.CreateInstance` + `dynamic`）→ 每项 `HWND`、`Document.Folder.Self.Path`（This PC/库等虚拟位置为 `::{GUID}`，视为不可导航）；`IServiceProvider.QueryService(SID_STopLevelBrowser, IID_IShellBrowser)` → `IShellBrowser.GetWindow` 与 `FindWindowEx(topHwnd, 0, "ShellTabWindowClass")`（首个即活动标签）比对确定 Win11 活动标签（直接取 `Document` 只得到第一个标签）；导航 `SHParseDisplayName` → `IShellBrowser.BrowseObject(pidl, SBSP_SAMEBROWSER)`，失败回退 `Navigate2`；选中 `Document.SelectItem`；`EnumWindows` 取 z 序 | `Marshal.ReleaseComObject`/`ILFree` 及时释放 |
| UsnIndexSource | `CreateFile("\\.\C:", GENERIC_READ, SHARE_READ|WRITE)`、`DeviceIoControl(FSCTL_QUERY_USN_JOURNAL / FSCTL_ENUM_USN_DATA(MFT_ENUM_DATA_V1, Min/Max 2/3) / FSCTL_READ_USN_JOURNAL(READ_USN_JOURNAL_DATA_V1))`、`GetVolumeInformation`（文件系统名、序列号）、`GetFileInformationByHandle`（根 FRN）、`CancelIoEx` | 枚举缓冲 1MB（`NativeMemory.Alloc`，复用）；按 `MajorVersion` 解析 V2/V3（V3 的 128 位 ID 高 64 位非 0 → 该卷改走爬取） |
| Crawler | `FileSystemEnumerable<T>` + `EnumerationOptions{IgnoreInaccessible=true, AttributesToSkip=0, RecurseSubdirectories=false, BufferSize=65536}`（内部即 `NtQueryDirectoryFile` 大缓冲，等价 `FindFirstFileExW(FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH)`；transform 中 `FileName` 是 span，直接拷入竞技场，零字符串分配）、`FileSystemWatcher` | |
| IconCache | 按扩展名：`SHGetFileInfo(".ext", FILE_ATTRIBUTE_NORMAL, SHGFI_ICON|SHGFI_USEFILEATTRIBUTES)`（不访问磁盘）；按路径（仅 exe/lnk/ico/url/appref-ms/特殊文件夹）：`SHCreateItemFromParsingName` + `IShellItemImageFactory.GetImage(SIIGBF_ICONONLY)`；`GetObject`/`DeleteObject`/`DestroyIcon` | HBITMAP 按 DIB 拷贝为 `BitmapSource.Create(Pbgra32)` 并 `Freeze()`；UI 线程绝不取图标 |
| ActionExecutor | `ShellExecuteEx`（`runas`、`properties` + `SEE_MASK_INVOKEIDLIST`）、`SHOpenFolderAndSelectItems` + `ILCreateFromPath`/`ILFree`、`Clipboard.SetFileDropList` | |
| Launcher 窗口 | `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE=33, DWMWCP_ROUND)`、`DWMWA_USE_IMMERSIVE_DARK_MODE=20` | 不用 `AllowsTransparency`（软件渲染慢） |

---

## 6. 核心算法

### 6.1 QuickSwitch（P1）

**对话框识别**（`FileDialogDetector.TryDetect(hwnd, out FileDialogInfo)`：Hook 线程只做类名初筛 `#32770` 后投递；完整解析在 UI 线程，全部为本地只读窗口查询，<1ms）：
```
if class(hwnd) != "#32770" or !IsWindowVisible(hwnd): return false
if processName ∈ settings.DisabledProcesses: return false
// 1) 现代对话框（IFileDialog）
if (c = GetDlgItem(hwnd, 0x047C)) && class(c) == "ComboBoxEx32":            // 打开对话框
    edit = GetDlgItem(GetDlgItem(c, 0x047C), 0x047C)                       // ComboBoxEx32 → ComboBox → Edit
    kind = Open
else if FindWindowEx(hwnd, 0, "DUIViewWndClassName") != 0:                // 保存对话框（及选择文件夹）
    dui = FindDescendant(hwnd, "DirectUIHWND")
    sink = FindWindowEx(dui, 0, "FloatNotifySink")                         // 可能有多个，逐个找含 ComboBox 的
    edit = GetDlgItem(FindWindowEx(sink, 0, "ComboBox"), 0x03E9)           // id 1001
    kind = Save
// 2) 旧式对话框启发式（AHK QuickSwitch 同款）：Button1 + Edit1，且 (ToolbarWindow32 + DirectUIHWND) 或 (SysListView32 + SysHeader32)
else if HasChild("Button") && (e = first "Edit") && ((Has("ToolbarWindow32") && Has("DirectUIHWND")) || (Has("SysListView32") && Has("SysHeader32"))):
    edit = GetDlgItem(hwnd, 1152 /*edt1*/) ?? e;  kind = Legacy
else return false                                                           // SysTreeView32 浏览文件夹框：不支持
ok = GetDlgItem(hwnd, IDOK=1)
addrToolbar = FindDescendant(FindDescendant(hwnd, "Breadcrumb Parent"), "ToolbarWindow32")   // 文本 "地址: D:\x"/"Address: D:\x"
addrEdit = WorkerW → ReBarWindow32 → "Address Band Root" → msctls_progress32 → ComboBoxEx32 → GetDlgItem(0xA205) → GetDlgItem(0xA205)  // 可能尚未创建
isFolderPicker = kind == Save && GetDlgItem(hwnd, 0x0470 /*文件类型 cmb1*/) == 0
TargetElevated = ElevationHelper.IsProcessElevated(pid) && !ElevationHelper.IsSelfElevated
```

**状态机**（UI 线程，`QuickSwitchService`；Explorer 激活序号 `activationSeq` 用于去重）：
```
state: Dictionary<IntPtr, DialogState{ Info, OpenedAt, Seen, LastAppliedSeq, Lock(SemaphoreSlim 1) }>
lastExplorer: (Hwnd, AtUtc, Seq)?

OnForeground(hwnd):                                   // Hook 线程已过滤 OBJID_WINDOW/CHILDID_SELF
  if paused or !Enabled: return
  if WindowInfo.IsExplorer(hwnd):                     // CabinetWClass / ExploreWClass
      lastExplorer = (hwnd, now, ++activationSeq); explorer.NotifyActivated(hwnd)   // 后者异步刷新缓存，不等待
      return                                          // 工具栏被对话框拥有，随 z 序自然被遮挡，无需隐藏
  if !FileDialogDetector.TryDetect(hwnd, out info): return
  d = state.GetOrAdd(hwnd); d.Info = info
  toolbarHost.AttachTo(info)                          // 首次：设 owner + 定位 + 显示；装进程级 LOCATIONCHANGE
  if autoProcessExcluded(info.ProcessName): return    // AutoSwitchExcludedProcesses：只关自动行为，保留工具栏
  if !d.Seen:
      d.Seen = true
      if AutoSwitchOnOpen && lastExplorer != null && now - lastExplorer.At < AutoSwitchMaxAge:
          d.LastAppliedSeq = lastExplorer.Seq; Fire(NavigateFromExplorer(d, lastExplorer.Hwnd))
  else if FollowExplorer && lastExplorer != null && lastExplorer.Seq != d.LastAppliedSeq
          && lastExplorer.At > d.OpenedAt:   // 只认“对话框打开期间切去的 Explorer”
      d.LastAppliedSeq = lastExplorer.Seq             // 去重：每次 Explorer 激活最多跟随一次
      Fire(NavigateFromExplorer(d, lastExplorer.Hwnd))

NavigateFromExplorer(d, explorerHwnd):
  folder = explorer.Cached[explorerHwnd]?.Path ?? await explorer.GetFolderAsync(explorerHwnd)   // 缓存优先
  if folder == null (虚拟文件夹) or PathEq(folder, navigator.TryGetCurrentFolder(d.Info)): return
  await NavigateDialog(d, folder)

NavigateDialog(d, folder):
  if !d.Lock.Wait(0): return                          // 同一对话框不并发导航
  r = await navigator.NavigateAsync(d.Info, folder)
  Ok → history.RecordFolderVisit(folder, Dialog);  Blocked → toolbar.ShowElevationHint();  其他 → 日志 + 工具栏错误闪烁

OnLocationChange(hwnd == attached dialog): toolbarHost.Reposition()   // 同一帧内多次合并为一次
OnMinimizeStart / OnHide / OnDialogEnd(hwnd): toolbarHost.Detach()    // 先解除 owner 再隐藏（见下）
OnMinimizeEnd: AttachTo 再次定位
OnDestroy(hwnd): state.Remove(hwnd); Detach（若尚未）；卸载进程级钩子
OnDialogJumpHotkey(hwnd): 目标 = lastExplorer 文件夹 ?? 最近文件夹第一项 → NavigateDialog
```

**导航**（`FileDialogNavigator.NavigateAsync`，CL-Shell 线程；主路径目标耗时 10–50ms）：
```
if !await PathUtil.DirectoryExistsAsync(folder, timeout: 1s): return NotFound  // 防止把不存在的路径当文件名“保存/打开”
if dlg.TargetElevated: return Blocked
if !IsWindow(dlg.Hwnd): return Failed
await WaitUntil(5ms, 1000ms, () => GetForegroundWindow() == dlg.Hwnd)        // 自动跳转发生在对话框刚激活时
if dlg.IsFolderPicker or dlg.FileNameEdit == 0: return ViaAddressBar(dlg, folder)  // 选择文件夹框按 Enter 会直接“选中并关闭”
before = ReadAddress(dlg)
// 主路径：文件名框（无焦点切换、无模拟按键）
orig = GetText(edit)                                         // SendMessageTimeout WM_GETTEXT
SetText(edit, NormalizeForDialog(folder))                    // 根目录保留 "C:\"，其余去尾部 "\"
if GetText(edit) != NormalizeForDialog(folder): SetText(edit, orig); return ViaAddressBar(dlg, folder)   // 写入未生效绝不按回车
PostMessage(edit, WM_KEYDOWN, VK_RETURN, 0x001C0001); PostMessage(edit, WM_KEYUP, VK_RETURN, 0xC01C0001)
changed = await PollUntil(10ms, 300ms, () => ReadAddress(dlg) != before || GetText(edit) != folder)
if !changed: SendMessageTimeout(dlg.OkButton, BM_CLICK); changed = await PollUntil(10ms, 700ms, 同上)
SetText(edit, orig); SendMessage(edit, EM_SETSEL, 0, LenWithoutExtension(orig))   // 恢复文件名（Save 关键）
return changed ? Ok : ViaAddressBar(dlg, folder)

ViaAddressBar(dlg, folder):                                  // 兜底，需要前台与模拟按键
  await WaitUntil(10ms, 500ms, () => 无 Ctrl/Alt/Shift/Win 按下 (GetAsyncKeyState))
  ForegroundHelper.Activate(dlg.Hwnd); SendInput(Ctrl+L)     // dwExtraInfo = 自定义标记，钩子据此忽略
  addr = await PollUntil(10ms, 1000ms, () => GetGUIThreadInfo(tid).hwndFocus 是 Edit 且祖先含 "Address Band Root")
  if addr == 0: return Timeout
  SetText(addr, folder); PostMessage(addr, WM_KEYDOWN/UP, VK_RETURN); poll ReadAddress 变化 ≤1000ms
  然后 SetFocus 回文件名框（经 AttachThreadInput，失败忽略）
```
`ReadAddress` 取地址工具栏文本，去掉第一个 `": "`（或全角冒号）之前的本地化前缀；已知文件夹可能只显示名称，所以成功判定用“与导航前不同”而非“等于目标”。不使用 UIA（慢且跨进程开销大），`IFileDialog` COM 不能跨进程调用。

**工具栏宿主**（`ToolbarHost` + `QuickSwitchToolbar`，见 §8.2）：窗口在独立的 CL-Toolbar 线程创建（启动时预创建并隐藏）。附着时 `SetWindowLongPtr(GWLP_HWNDPARENT, dlg)` 让对话框成为 owner：始终在对话框之上、随其最小化，被其他窗口遮挡时一起被遮挡，无需 TOPMOST。跨进程 owner 的两个副作用及对策：①输入队列被隐式附加 → 工具栏独占线程，目标卡死不影响主 UI；②owner 销毁时系统会连带销毁 owned 窗口 → 在 `EVENT_OBJECT_HIDE`/`EVENT_SYSTEM_DIALOGEND`/最小化时先 `GWLP_HWNDPARENT=0` 再隐藏；若仍收到 `Closed`，`ToolbarHost` 惰性重建。

**位置**（`ToolbarPlacement.Compute(dlgRect, workArea, sizePx)`）：`dlgRect` 取 DWM 扩展边框（物理像素，PMv2 下对 DPI 不感知的目标也是真实坐标）。默认贴在对话框**下沿外侧**、左对齐、间距 4px（与 Listary 一致）；下方超出工作区 → 上沿外侧；仍不够 → 对话框内底部左侧（按钮行之上）。最后夹紧到对话框所在显示器工作区。`scale = GetDpiForWindow(dlg)/96`。位置一律 `SetWindowPos(SWP_NOACTIVATE|SWP_NOZORDER)` 设物理像素（不用 `Window.Left/Top`）；`WM_DPICHANGED` 时忽略建议矩形，WPF 完成缩放后再 `Reposition()`。

**边界情况**
- Save 对话框：始终恢复原文件名并选中主名部分；若用户正在 Edit 中输入（焦点在 Edit 且 2s 内有按键），自动跳转/跟随延后到该次前台结束，不打断输入。
- 自动跳转只触发一次（`Seen`）；跟随按 Explorer 激活序号去重，每次激活最多一次；用户在对话框内手动导航后不会被覆盖，除非再次切到 Explorer。
- 旧式对话框（Legacy）：Edit id 1152 或启发式找到的 Edit1，同主路径；无地址工具栏时用“Edit 文本被清空/改变”判定，兜底不可用则返回 Failed。
- 选择文件夹对话框（`IsFolderPicker`）：只走地址栏方案。
- 提升进程（UIPI）：WinEvent 可收到、读文本可行，写入/输入被拦截 → 工具栏显示带警告徽标的单项“目标程序以管理员身份运行，点击以管理员身份重启 Hopkey”。LL 键盘钩子在提升窗口前台时收不到按键，Ctrl+G/双击 Ctrl 失效（文档说明，`RegisterHotKey` 仍可用）。
- 多显示器/DPI：所有坐标物理像素；DPI 不感知的目标进程，PMv2 调用方 `GetWindowRect` 返回真实物理坐标，无需换算。
- 网络路径：`DirectoryExistsAsync` 超时视为 NotFound，不卡 UI。
- 对话框被同一进程复用/重建：以 hwnd 为键，DESTROY 即清理；`IsWindow` 每次使用前复核。

### 6.2 排除（P2）

**构建期**（`IndexBuilder`，USN 枚举得到无序原始记录后）：
```
raw: keys[], parentKeys[], names, attrs[]  → keyToIdx 字典 → parentIdx[]（找不到父的记录=孤儿/元文件，丢弃）
state = byte[n]  // 0 未知 1 保留 2 排除
dirPath = string?[n]（仅目录，构建期临时）
for i in 0..n-1: Resolve(i)
Resolve(i):  // 迭代版：沿父链压栈直到遇到已知状态或根，再自顶向下计算
  stack = []; j = i
  while state[j]==0 and j != root: stack.push(j); j = parentIdx[j]
  parentState = (j==root) ? 1 : state[j]
  while stack 非空:
     k = stack.pop(); p = parentIdx[k]
     if parentState == 2: state[k] = 2
     else:
        parentPath = (p==root) ? rootPath : dirPath[p]
        state[k] = m.IsExcludedEntry(name[k], parentPath, isDir[k], attrs[k]) ? 2 : 1
        if isDir[k] and state[k]==1: dirPath[k] = Combine(parentPath, name[k])
     parentState = state[k]
保留 state==1 的条目，重映射下标，压实为 VolumeIndex；丢弃 dirPath
```
`IsExcludedEntry` 顺序（由便宜到贵）：属性开关（Hidden/System，根除外）→ Name 精确集（span 查找）→ Name 合并正则 → 仅目录：Path 集合（`Combine(parentPath,name)` 恰好等于）→ Glob/Regex 合并正则（目录完整路径；Target 含文件时才对文件拼路径，UI 提示其成本）。Path 规则只需“恰好等于”，子树靠父状态继承，因此字符串工作量 O(目录数)。构建期 `dirPath` 只为保留的目录生成，结束即释放。

**爬取期**（`DirectoryCrawler`）：目录在入队前调用 `IsExcludedEntry`，命中则既不记录也不递归（剪掉整个子树，这是爬取模式的主要提速点）；带 `ReparsePoint` 的目录记录但不递归。

**实时更新**（`VolumeIndex.ApplyBatch`）：
- Created：父不在索引中 → 丢弃（父被排除或未知）；否则 `IsExcludedEntry(name, GetFullPath(parent), …)` 命中则丢弃。
- Renamed：新位置被排除 → 删除该条目（目录则 `DeleteSubtree`：一次 O(n) 扫描，标记父链经过它的条目）；从排除区移入包含区（索引中不存在）→ 对 USN 卷标记 `dirty`，30s 防抖触发该卷重建；对爬取卷直接爬取该子树并插入。
- AttributesChanged：Hidden/System 位变化且开关相关 → 同上处理排除/恢复。

**规则变更**（`IndexManager.OnExclusionChanged(old, new)`，UI 改完后 1s 防抖）：
```
diff = new.Diff(old)
if diff.HasAdditions:
    foreach v in Volumes: 写锁内 v.Prune(new)    // 同 Resolve 记忆化，只对现存条目判定；被排除者置 Deleted
    Index.Changed()                                // 立即生效，200 万条约 0.5–1.5s
if diff.HasRemovals:
    foreach v in Volumes:
        if v.Kind == Usn: 后台 BuildAsync(new) 完成后 SetVolume 原子替换（MFT 枚举数秒）
        else if !diff.RemovedNonPathRules:
            foreach p in diff.RemovedPathRules where p 在 v.RootPath 下 且 父目录在索引中 且 !new.IsExcludedPath(p):
                 爬取 p 子树 → ApplyBatch(Created…)
        else: 后台全量重爬该根后替换
保存时写入 new.Fingerprint；启动加载缓存若 fingerprint 不同 → 先 Prune(new) 再按“有删除”路径重建
```
**搜索期**：`SearchEngine` 对最终候选（每个提供者 TopN 的 2 倍超取）统一调用 `IsExcludedPath`，覆盖应用目录、历史、最近、收藏，以及重建窗口期内的索引条目。代价仅为数百次字符串检查。

### 6.3 索引源细节
- **源选择**：根为卷根 + 文件系统 `NTFS` + 自身已提升 + `UseUsnWhenElevated` + 日志活动 → `UsnIndexSource`；其余（非管理员、FAT/exFAT/ReFS、子文件夹根）→ `CrawlerIndexSource`。日志未启用时不创建日志（不改系统），走爬取。子文件夹根若已被某卷根覆盖则忽略。默认根 = `DriveInfo.GetDrives()` 中 `Fixed && IsReady`。多卷**并行**构建。
- **USN 构建（Everything 同款）**：`FSCTL_QUERY_USN_JOURNAL` 取 `JournalId`、`NextUsn`、`LowestValidUsn`；`FSCTL_ENUM_USN_DATA` 循环（`MFT_ENUM_DATA_V1{StartFileReferenceNumber=0, LowUsn=0, HighUsn=NextUsn, MinMajor=2, MaxMajor=3}`，1MB 复用缓冲，输出前 8 字节为下一起点），按 `MajorVersion` 解析 V2/V3（FRN、ParentFRN、Attributes、FileName）。名称直接从记录 span 拷进原始竞技场，**不创建 string**；同时写临时 `ulong[] frn, parentFrn`。每个 FRN 只返回一个名称（硬链接的其他名称不收录，可接受）。根 FRN 由 `GetFileInformationByHandle` 打开卷根取得；父链到不了根的记录（`$Extend` 元文件、孤儿）丢弃。随后 `IndexBuilder` 解析父下标、做排除、并行计算 Lower 竞技场与拼音。直接解析 `$MFT` 原始记录（更快，需处理多 `$FILE_NAME` 属性并跳过 8.3 名）作为后续优化，放在同一 `IIndexSource` 后面，v1 不做。
- **USN 追赶/监视**：持久化每卷 `JournalId + NextUsn`。启动时若 `JournalId` 变化或 `NextUsn < LowestValidUsn` → 全量重建，否则从 `NextUsn` 回放。监视线程阻塞读 `FSCTL_READ_USN_JOURNAL`（`ReasonMask = FILE_CREATE|FILE_DELETE|RENAME_NEW_NAME|RENAME_OLD_NAME|BASIC_INFO_CHANGE`，`ReturnOnlyOnClose=0`，`BytesToWaitFor=1`），读到即投递，写线程按 300ms 合并；`ERROR_JOURNAL_ENTRY_DELETED`/ID 变化 → `RebuildRequired`。同一 FRN 在一批内多次出现按顺序应用。
- **爬取并行**：工作队列 + `min(ProcessorCount, 8)` 个 worker（`BelowNormal` 优先级），每目录一次 `FileSystemEnumerable`（`BufferSize=64KB`）；目录在入队前做排除判定（剪子树）；产出 `(parentIdx, name span, attrs)` 到 worker 本地竞技场块，最后合并（父下标在入队时分配，保证父先于子）。爬取源为目录建 `DirPaths` 字典供 FSW 定位。
- **FSW**：每根一个，`IncludeSubdirectories=true`，`NotifyFilter=FileName|DirectoryName|Attributes`，`InternalBufferSize=64KB`；事件转 `IndexChange(FullPath)`；`Error`（溢出）→ `RebuildRequired`（后台重爬并原子替换）。
- **写入合并**：`CL-IndexWriter` 从 Channel 读，300ms 或 2000 条一批，写锁内 `ApplyBatch`；写锁持有时间目标 <2ms，大批量拆分。
- **AppCatalog**：`%ProgramData%\Microsoft\Windows\Start Menu`、`%AppData%\Microsoft\Windows\Start Menu`、公共与用户桌面；`.lnk .url .appref-ms .exe`；显示名=去扩展名文件名；排除名称含 “uninstall/卸载” 的快捷方式；FSW 监视。UWP（可选）：CL-Shell 线程 `Shell.Application.NameSpace("shell:AppsFolder").Items()` 取 `Name` 与 `Path`（AUMID），启动 `explorer.exe shell:AppsFolder\{AUMID}`。条目数通常 <2000，作为普通 `List<AppEntry>` 存放（预小写名 + 首字母 + 缩写串）。
- **拼音**：`PinyinInitialsTable` 为 `U+4E00–U+9FFF` 每字一个字节（`'a'..'z'` 或 0）。生成法：对字符 `Encoding.GetEncoding(936).GetBytes`，两字节码值落在 GB2312 一级汉字 23 个字母分界（`0xB0A1…0xD7F9`）中即得首字母；其余（二级字/非 GB2312）为 0 → 若有嵌入的完整表则用之。条目入库时若名称含 CJK，设 `HasCjk` 并把“首字母串”（非汉字原样小写保留，便于 “bg报告” 混合匹配）写入 `PinyinArena`。全拼（可选）懒加载，只在 `FullPinyin` 开启时构建。

### 6.4 搜索：两阶段扫描、匹配与排序
- **解析**：按空白切分；`folder:`/`file:`/`app:`/`ext:pdf,docx` 为过滤器（扩展名用 `SearchValues<string>` 判尾部）；首词等于启用的 web 关键字且后跟空格 → Web 意图；以 `=` 开头或仅含数字/运算符/括号且含运算符 → 计算器意图（仍执行普通搜索）。关键词逐字符 `char.ToLowerInvariant`（与 Lower 竞技场同一规则）。
- **锚点关键词**：最后一个关键词为“名称锚点”（必须命中名称），其余关键词可命中名称或祖先路径（“proj readme” 找 projects 下的 readme）。若结果不足 `MaxResults` 且关键词 >1，再以第一个关键词为锚点补跑一次。
- **阶段 1（`ArenaScanner`，零分配）**：每卷按条目区间切成 `ProcessorCount×2` 个分区（每区对应 Lower 竞技场的连续区段 `[NameOffset[a], NameOffset[b])`），`Parallel.For` 执行：
  ```
  span = lower.Slice(start, end-start); pos = 0
  while (hit = span[pos..].IndexOf(anchor)) >= 0:                 // .NET 向量化 IndexOf（AVX2/AVX-512）
      e = EntryAtOffset(start+pos+hit)                            // 对 NameOffset 二分
      pos = NameOffset[e+1] - start                               // 跳到下一个名称，每条目最多命中一次
      if Flags[e] & Deleted or !TypeFilterOk(e) or !ExtOk(e): continue
      pre = PreScore(e, hitOffsetInName)                          // 相等/前缀/词边界/子串位置 + 名称长度，O(1)
      localTopK.Push(e, pre)                                      // 每 worker 有界堆 K = ceil(4096 / workers)
      每 4096 次循环检查 ct.IsCancellationRequested
  ```
  锚点为纯 ASCII 字母且启用拼音时，同样扫描 `PinyinArena`（命中映射回 entryId，若 Lower 名称已含锚点则跳过以去重）。堆、分区数组均来自 `ArrayPool`/线程静态缓存，热路径无 LINQ、无闭包分配、无 string。
- **阶段 2（≤ ~4–5k 幸存者）**：合并各 worker 堆 → 对每个候选：其余关键词先查名称，未命中再查祖先路径（每查询一个 `byte[]` 目录记忆表：0 未知/1 命中/2 未命中，沿 Parent 链懒计算，来自 ArrayPool）→ `NameMatcher` 精确打分（含高亮区间）→ 加分 → 取前 `MaxResults×2` → 仅此时 `GetFullPath` 生成 string → 排除后过滤 → 截取 `MaxResults`。
- **打分** `NameMatcher.Score(name, kw)`：完全相等 1000；前缀 800；词边界起始（前一字符为空格 `_ - . ( [`、小写→大写、字母↔数字）600；子串 300 − 位置×2；拼音命中按同等级 ×0.9；路径命中 150。**缩写（`vsc`→`Visual Studio Code`）与 fzf 式模糊子序列只用于 App/历史/收藏这些小集合**（文件索引上无法用 IndexOf 预筛，代价不可控）：缩写 500，模糊 100 − 间隔×5（下限 1）。
- **加分**：目录 +20（无过滤器时）；App +200；名称越短 +`(64 − min(len,64))`；路径越浅 +`(16 − min(depth,16))`；`HistoryStore.GetBoost = 120·ln(1+count) + 150·exp(−ageDays/7) + 300·(query 前缀关联命中)`（`UseHistoryBoost` 关闭为 0）。历史项另由 `HistorySearchProvider` 独立匹配，保证常用项即使预评分低也不会被阶段 1 截掉。
- **增量收窄**：若新查询是上次查询追加字符、锚点不变且上次阶段 1 未截断（命中数 < K），直接在上次命中集合上复核，跳过全量扫描。
- **空查询**：最近使用 8 项 + 收藏全部 + 最近文件夹 8 项，分组显示（纯内存，无扫描）。

---

## 7. 功能与交互细节

### 7.1 启动器（`LauncherWindow`）
- **唤起**：双击 Ctrl（两次 Ctrl 按下→抬起，每次按住 <250ms，两次间隔 <300ms，期间无其他键，且无鼠标点击）；或 `LauncherAlt` 组合键（`RegisterHotKey`，默认 `Alt+Space`，冲突时提示）。再次触发时若已显示则隐藏。
- **窗口**：`WindowStyle=None`、`ResizeMode=NoResize`、`ShowInTaskbar=false`、`Topmost=true`，DWM 圆角与阴影。宽 680 DIP；输入框高 52；结果行高 46（图标 32、标题 14pt、副标题 11pt 灰色路径中间省略），最多可见 9 行，列表 `VirtualizingStackPanel`（Recycling）；底部状态栏 26 高：左侧索引状态（“正在建立索引… 1,234,567 项”/“已索引 2,103,455 项”），右侧按键提示。位置：鼠标所在显示器工作区，水平居中，顶部在高度 22% 处。上下文模式：锚定目标窗口，水平居中于其顶部下方 80px，夹紧到该显示器。
- **激活**：`Show()` 后 `ForegroundHelper.Activate`，`Keyboard.Focus(textbox)`；`Deactivated` 即隐藏（动作菜单、属性对话框打开期间除外）。隐藏时保留上次查询并全选（下次打开可直接覆盖），上下文模式例外（清空并预填触发字符）。
- **输入**：`TextChanged` → 30ms 防抖 → 取消上一 `CancellationTokenSource` → `SearchEngine.SearchAsync` → UI 线程替换结果（保持选中项若仍存在，否则选第一项）。
- **键盘**：↑/↓/PgUp/PgDn/Home/End（Ctrl+Home）选择；`Enter` 打开；`Ctrl+Enter` 打开所在位置；`Shift+Enter` 管理员运行；`Alt+1..9` 直接打开第 N 项；`Tab` 或 `→`（光标在末尾时）打开动作菜单，`Esc`/`←` 返回；`Esc` 在主列表：有文本先清空，无文本隐藏；`Ctrl+C`（无文本选区时）复制路径。
- **动作菜单**（`ResultActions`）：文件：打开、打开所在文件夹、以管理员身份运行（exe/lnk/bat/cmd/ps1/msi）、复制路径、复制文件、在终端中打开（所在目录）、属性、排除所在文件夹、添加到收藏（所在文件夹）；文件夹：打开、复制路径、复制文件夹、在终端中打开、属性、排除此文件夹、添加/移除收藏；App：打开、管理员运行、打开文件位置；Web/计算器：执行/复制结果。终端：`wt.exe -d "<dir>"`（存在于 PATH/WindowsApps）否则 `powershell.exe -NoExit -Command Set-Location -LiteralPath '<dir>'`。排除操作弹确认框（显示路径与“将立即从索引中移除”）。
- **网页搜索**：`g 关键词` 结果行“在 Google 中搜索 ‘关键词’”，`Enter` 以 `Uri.EscapeDataString` 替换 `{q}` 后 `ShellExecute`。默认：`g` Google、`bd` 百度、`bing` 必应。
- **计算器**：结果作为首行“= 结果”，`Enter` 复制结果到剪贴板；除零/语法错误不显示该行。
- **状态**：空查询 → 分组（收藏/最近使用/最近文件夹），全部为空时显示提示“输入关键词开始搜索；双击 Ctrl 随时唤起”；索引未就绪 → 结果照常（已加载部分）+ 状态栏进度；无结果 → 灰色“无匹配结果” + 默认网页搜索建议行；索引错误 → 状态栏红字“索引出错，点击查看日志”。
- **执行后**：记录 `HistoryStore.RecordLaunch`；文件夹/文件所在目录记入最近文件夹；隐藏窗口。失败（文件不存在、`Win32Exception` 用户取消 UAC 1223 忽略）→ 状态栏提示 3s，不隐藏。

### 7.2 资源管理器集成（打字即搜）
- **触发**（KeyboardHook 过滤器，Hook 线程内判定）：`TypeToSearch` 开启且未暂停；按键为可打印字符（字母/数字/常用符号，经 `ToUnicodeEx` 转换，且无 Ctrl/Alt/Win）；前台为 Explorer（`CabinetWClass`）或已识别的文件对话框；`GetGUIThreadInfo` 的 `hwndFocus` 类名为 `DirectUIHWND` 或 `SysListView32`（文件视图），焦点不在 `Edit`/`RichEdit*`/`Windows.UI.Input.InputSite.WindowClass`（Win11 地址栏/搜索框）/重命名框；`GUI_INMENUMODE` 时不触发。满足则吞键并 `BeginInvoke(ShowContext(fgHwnd, ch))`。
- **上下文模式**：启动器头部显示一枚标签“在此窗口中打开：<窗口标题>”。选择文件夹 `Enter` → Explorer：`ExplorerService.NavigateAsync(hwnd)`（活动标签 `Navigate2`）；对话框：`QuickSwitchService.NavigateActiveAsync`。选择文件 → Explorer：导航到父目录并 `SelectItem(item, 1|4|8|16)`；对话框：`NavigateToFileAsync`（导航后填入文件名，不自动确认）。`Ctrl+Enter`/动作菜单仍按普通方式执行。导航失败 → 回退为普通打开并在状态栏提示。

### 7.3 收藏与最近文件夹
- 收藏：设置页管理（添加文件夹/文件、别名、上移/下移、删除），启动器动作菜单添加/移除；别名参与搜索匹配（匹配别名时 +400）。
- 最近文件夹来源：Explorer 前台与 NAMECHANGE 防抖后读取当前路径、对话框导航成功、启动器打开项。仅记录文件系统路径，去重置顶，上限 100；QuickSwitch 菜单显示前 15 个存在的路径（存在性检查在后台，带 200ms 总超时，超时则不过滤）。

### 7.4 设置窗口（即时生效）
- 窗口 780×560 DIP（最小 680×480），左侧导航列表（`ListBox` 140 宽）+ 右侧内容区；无“确定/取消”，修改即 300ms 防抖写入 `SettingsStore.Update`，底部显示“已保存”淡出提示。
- **常规**：开机自启动（复选）、以管理员身份开机自启动（复选，需管理员，走计划任务）、语言（简体中文/English）、主题（跟随系统/浅色/深色）、启动器热键（`HotkeyBox` + “双击 Ctrl”单选）、备用组合键、打字即搜开关、当前权限状态 +“以管理员身份重启”按钮（已提升时禁用并显示“已以管理员身份运行”）。
- **QuickSwitch**：启用、显示工具栏、打开对话框时自动跳转（+最长间隔分钟数）、切换到资源管理器后返回时跟随、Ctrl+G 跳转（可改键）、禁用的程序列表（添加进程名/从运行中进程选择）。
- **索引与排除**：索引根目录列表（默认“所有固定磁盘”复选，取消后可添加/移除文件夹）、使用 MFT（需管理员，非管理员时禁用并说明）、索引开始菜单应用、UWP 应用；状态行（每根：方式 USN/爬取、条目数、上次更新）+“重建索引”。排除规则 `DataGrid`：启用、类型（路径/名称/正则）、模式、作用对象、内置标记；按钮：添加文件夹（`FolderBrowserDialog`）、添加名称模式、添加正则、删除（内置项禁用删除仅可停用）、导入、导出、恢复默认；排除隐藏/系统文件复选。输入校验：路径必须为绝对路径；名称模式不得含 `\ / :`；正则需能编译，否则行内红框 + ToolTip。
- **收藏**：列表（图标、别名、路径），添加/编辑/删除/排序。
- **网页搜索**：表格（启用、关键字、名称、URL 模板），校验关键字唯一且无空格、模板含 `{q}`；恢复默认。
- **关于**：版本、数据目录（打开）、日志目录（打开）、索引统计、许可说明。

### 7.5 托盘与基础设施
- 托盘菜单：搜索、设置、重建索引、暂停/恢复（暂停 = 卸载所有钩子与热键、停止索引监视；图标换 `tray-paused.ico`）、退出。左键单击=搜索，双击=设置。
- 单实例：`Mutex("Local\\Hopkey.Instance")`；第二实例 `EventWaitHandle.OpenExisting("Local\\Hopkey.Activate").Set()` 后退出（首实例后台线程等待该事件 → UI 打开启动器）。`--wait-pid N`：先等待该进程退出（最多 10s）再抢锁（用于以管理员身份重启）。打开事件遭拒（提升/非提升混用）→ 直接退出并写日志。
- 以管理员身份重启：`ShellExecuteEx(runas, 自身路径, "--wait-pid <pid> --elevated-restart")`，成功后当前实例保存索引/历史并退出；用户取消 UAC → 保持运行。
- 自启动：HKCU `Software\Microsoft\Windows\CurrentVersion\Run` 值 `Hopkey` = `"<exe>" --autostart`。管理员自启动：`schtasks /Create /TN Hopkey /TR "\"<exe>\" --autostart" /SC ONLOGON /RL HIGHEST /F`（需当前已提升），启用时同时删除 Run 值，反之亦然。`--autostart` 时不弹任何窗口。
- 日志：`Logger.Info/Warn/Error(string, Exception?)`，后台队列写 `%LocalAppData%\Hopkey\logs\yyyy-MM-dd.log`，保留 7 天，单文件 >10MB 截断滚动。
- 崩溃：`AppDomain.UnhandledException`（写 `crash-*.log` 后让进程结束）、`Dispatcher.UnhandledException`（记录，`Handled=true`，同一异常 1 分钟内 >5 次则提示并退出）、`TaskScheduler.UnobservedTaskException`（记录并 `SetObserved`）。退出路径统一 `AppHost.ShutdownAsync`：卸钩子→停监视→保存索引（最多 5s）→保存历史→释放托盘。
- 主题：`ThemeManager` 读 `HKCU\...\Themes\Personalize\AppsUseLightTheme`，监听 `SystemEvents.UserPreferenceChanged`；替换 `Application.Resources.MergedDictionaries[0]`；所有颜色用 `DynamicResource`。

---

## 8. UI 规范

### 8.1 视觉
- 字体：`Segoe UI Variable, Segoe UI, Microsoft YaHei UI`；基准 13px。浅色：背景 `#F9F9F9`、卡片 `#FFFFFF`、选中 `#E5F1FB`、强调 `#0067C0`、次要文字 `#5F5F5F`；深色：背景 `#202020`、卡片 `#2B2B2B`、选中 `#2D3E50`、强调 `#4CC2FF`、次要文字 `#A0A0A0`。圆角 8。
- 结果项高亮匹配片段：强调色 + SemiBold。

### 8.2 QuickSwitch 工具栏
- 横向条，贴对话框下沿（高 36 DIP，圆角 8，1px 边框，DWM 阴影），从左到右：“搜索”按钮（打开**上下文模式**启动器，锚定该对话框，即 Listary 面板的搜索框；选中文件夹即导航对话框）｜“推荐”胶囊（显示 Ctrl+G 的目标文件夹名，单击即跳转，无目标时隐藏）｜“已打开的文件夹”（资源管理器图标）｜“收藏”（星）｜“最近”（时钟）｜“更多”（⋯：QuickSwitch 设置、对此程序禁用自动跳转、对此程序禁用 QuickSwitch）。按钮 32×32，间距 2。
- 菜单（WPF `ContextMenu`，`Placement=Bottom/Top` 跟随工具栏位置）：每项“文件夹名（粗）+ 路径（灰，中间省略）+ 扩展名缓存的文件夹图标”，最多 20 项；已打开文件夹按 z 序并标出对话框当前所在目录（对勾）。空状态：灰色禁用项“没有打开的资源管理器窗口”/“暂无收藏”/“暂无最近文件夹”。数据来自缓存，正常无加载态；缓存为空且同步查询 >150ms 时显示禁用项“正在获取…”，完成后替换。
- 反馈：导航进行中按钮显示细进度环；失败按钮红色闪烁 + ToolTip 原因 2s；UIPI 时整条显示警告徽标，点击弹出说明与“以管理员身份重启”。
- 悬停 ToolTip 显示按钮名与快捷键（Ctrl+G）。

---

## 9. 性能设计与指标

### 9.1 指标（Release，参考机：8 核、NVMe SSD、Win11；`PerfLog` 打点验收）
| 项目 | 目标 | 说明 |
|---|---|---|
| 冷启动到托盘可用 | < 400ms | 进程启动 → 托盘图标出现；索引加载在后台进行，不阻塞 |
| 缓存加载到可搜索 | 100 万条 < 0.5s，200 万条 < 1s | 读 index.bin + 并行重算 Lower 竞技场 |
| MFT 首次构建 | 100 万条 < 3s，200 万条 < 6s | 枚举 + 父链 + 排除 + 压实 |
| 非管理员爬取首次构建 | 100 万条 < 60s（受磁盘限制） | 构建期间已爬取部分可搜 |
| 实时更新延迟 | USN < 1.5s，FSW < 1s | 文件变化 → 可搜到 |
| 搜索延迟（按键 → 结果集） | 200 万条 P95 < 10ms，P99 < 25ms；500 万条 < 30ms | 阶段 1 + 阶段 2，不含渲染 |
| 按键 → 列表渲染完成 | P95 < 30ms | 含 30ms 防抖之外的部分 |
| 热键触发 → 启动器可见并获焦 | < 50ms（目标 30ms） | 从双击 Ctrl 判定成立/`WM_HOTKEY` 起算 |
| 对话框激活 → 工具栏可见 | < 100ms | 前台事件 → 定位完成 |
| QuickSwitch 主路径导航 | < 150ms（典型 10–50ms） | 菜单点击/Ctrl+G → 对话框目录切换完成 |
| 常驻内存（工作集） | 空闲无索引 < 60MB；100 万条 < 160MB；200 万条 < 280MB | 索引 ≈ 94 B/条 + 运行时/WPF 基线 |
| 空闲 CPU | ≈ 0% | 无轮询；仅事件驱动 |

### 9.2 实现手段（必须）
- **数据布局**：SoA 数组 + 两份 `char[]` 竞技场（原名、预小写），**每条目零对象**；条目只追加，`NameOffset` 递增使“命中偏移→条目”为二分；`byte` 名长与标志位。Lower 竞技场与拼音不落盘，加载时并行重算。大数组一次性按容量分配（构建期已知总数），避免反复扩容拷贝。
- **搜索**：向量化 `MemoryExtensions.IndexOf` 跨整个分区连续扫描（不逐条目调用）；按 `ProcessorCount` 分区并行；每 worker 本地 TopK，合并时才比较；仅对 ≤5k 幸存者做精确打分与路径拼接；每次按键新 `CancellationTokenSource`，旧查询在下一次 4096 步检查点退出；`ArrayPool`/`[ThreadStatic]` 复用缓冲；热路径禁止 LINQ、`foreach` over 接口、lambda 捕获、boxing、string 拼接。
- **索引构建**：1MB 复用非托管缓冲解析 USN 记录（`MemoryMarshal.Read`/指针）；名称直接拷入竞技场；父链解析用排序 FRN 数组二分而不是 `Dictionary<ulong,int>`（构建期 MFT 输出天然按 FRN 递增）；排除判定只在目录上做字符串工作（文件只查名称集合），代价 O(目录数)；多卷并行。
- **持久化**：`RandomAccess.Write/Read` + `MemoryMarshal.AsBytes` 整块读写，无序列化器；保存在后台线程且只在有变更时进行；保存期间持读锁（不阻塞搜索）。
- **启动**：ReadyToRun 预编译 + TieredPGO；`AppHost` 只同步做“单实例 → 读设置 → 托盘 → 钩子”，其余（索引加载、AppCatalog、ShellWindows 缓存、图标线程）全部 `Dispatcher.BeginInvoke(ApplicationIdle)` 或后台启动；设置 JSON 用源生成上下文（无反射）。
- **启动器显示**：窗口启动时预创建（`EnsureHandle` + 屏幕外以 `Opacity=0` 显示一次再隐藏，完成首次布局与模板实例化），之后只 `Show/Hide`，从不重建；`Warmup` 在空闲时执行一次假搜索与结果列表绑定（JIT 预热）；不用 `AllowsTransparency`、`DropShadowEffect`、`BlurEffect`（圆角/阴影交给 DWM）；列表 `VirtualizingStackPanel.VirtualizationMode=Recycling`、`ScrollUnit=Item`，可见约 9 行；结果项模板扁平（Grid + Image + 2 TextBlock），高亮用 `Run` 而非嵌套面板；结果替换整体换 `ItemsSource`（单次通知）而非逐项 Add。
- **图标**：永不在 UI 线程取；按扩展名缓存（`SHGFI_USEFILEATTRIBUTES` 不访问磁盘），只有 exe/lnk/ico/url/appref-ms 与特殊文件夹按路径后台取；先显示占位图标，加载完成后替换；`ImageSource` 全部 `Freeze()`；只为可见行请求。
- **钩子**：LL 键盘钩子只用于双击 Ctrl、对话框内 Ctrl+G、打字即搜；普通组合键一律 `RegisterHotKey`（零钩子开销）。钩子回调只做“读 vkCode/标志 → 状态机 → 必要时 `BeginInvoke`”，目标 <50µs，绝不分配或访问 COM；`LocationChange` 钩子只在附着期间、只对目标进程线程安装。
- **Explorer 信息**：`ShellWindows` 在 CL-Shell 缓存，Explorer 前台/NAMECHANGE 事件时增量刷新单个窗口；菜单打开、Ctrl+G、自动跳转都直接读缓存（缓存缺失才同步查询）。
- **GC/内存**：Workstation + Concurrent GC；索引构建完成后执行一次 `GCSettings.LargeObjectHeapCompactionMode = CompactOnce; GC.Collect()` 回收构建期临时数组；启动器隐藏 30s 后清空结果集合与图标 LRU 的冷端（保留扩展名缓存）；`SetProcessWorkingSetSize` 不使用（会引发缺页抖动）。
- **度量**：`PerfLog` 对 §9.1 各项打点（`Stopwatch.GetTimestamp`），超过目标 2 倍时写 Warn 日志；“关于”页显示最近一次的构建/加载/搜索耗时，便于测试轮验收。

---

## 10. 开发阶段

| 里程碑 | 交付物 | 完成标准 |
|---|---|---|
| **M1 骨架与基础设施** | sln/两工程/manifest；`Program`、`App`、`AppHost`、`SingleInstance`、`CrashHandler`、`Logger`、`AppPaths`、`SettingsStore`+`AppSettings`+`JsonContext`、`TrayIconService`、`DispatcherThread`、`WinEventHub`、`KeyboardHook`、`ForegroundHelper`、`WindowInfoCache`、`ElevationHelper`、`ElevationService`、`AutostartService`、`ThemeManager`/`LocalizationManager` 基础、Native 声明首批 | 托盘可用；重复启动唤醒首实例；settings.json 生成；日志写入；以管理员身份重启可用；钩子线程运行并在日志中打印前台窗口类名 |
| **M2 QuickSwitch** | `ExplorerService`（缓存、z 序、活动标签、读路径、BrowseObject 导航）、`FileDialogDetector`、`FileDialogNavigator`（文件名框主路径 + 地址栏兜底）、`QuickSwitchService`、`ToolbarHost`+`QuickSwitchToolbar`（工具栏“搜索”按钮在 M5 接通）、`ToolbarPlacement`、`FolderMenuBuilder`、`HotkeyService`（Ctrl+G 部分）、`HistoryStore`（最近文件夹部分）、`FavoritesService`；临时最简设置项（直接读 JSON） | 记事本/Chrome/VS Code 的打开与另存为对话框中工具栏可见并跟随；三类菜单可导航；自动跳转、跟随 Explorer、Ctrl+G 可用；Save 文件名保留；提升目标显示提示；工具栏出现 <100ms、主路径导航 <150ms |
| **M3 排除与索引** | `ExclusionRule/Matcher/WildcardPattern/DefaultExclusions/RuleIO`、`VolumeIndex`、`FileIndex`、`IndexBuilder`、`UsnIndexSource`、`DirectoryCrawler`、`CrawlerIndexSource`、`IndexPersistence`、`IndexManager`、`NameArena`、`KeyMap`、`AppCatalog`、`PinyinHelper`/`PinyinInitialsTable`、`PerfLog` | 非管理员爬取、管理员 USN 均可完成构建并实时更新；达到 §9.1 的构建/加载/内存指标；规则增删即时剪枝/恢复 |
| **M4 启动器** | `QueryParser`、`ArenaScanner`、`NameMatcher`、`TopN`、`SearchEngine` 与五个 Provider、`Calculator`、`LauncherWindow/ViewModel/ResultItemViewModel`、`ResultActions`、`ActionExecutor`、`IconCache`、`HighlightText`、`HotkeyService`（双击 Ctrl + RegisterHotKey）、历史加权、`Warmup`（预创建窗口） | 达到 §9.1 搜索与显示延迟指标；所有动作可用；“排除此文件夹”联动 M3 |
| **M5 资源管理器集成** | `TypeToSearchService`、`LauncherContext`、上下文导航（Explorer/对话框、文件选中）、工具栏“搜索”按钮、NAMECHANGE 最近文件夹跟踪 | Explorer 文件视图中打字弹出启动器并在原窗口导航；地址栏/重命名中打字不受影响 |
| **M6 设置与打磨** | `SettingsWindow` + 6 页 + `HotkeyBox`、即时应用全链路、主题/语言完整、托盘暂停、错误提示、空/加载/错误状态、可选全拼与 UWP | 所有设置项修改后无需重启生效；中英文切换完整；深浅色无遗漏硬编码色 |

本轮只开发，不写自动化测试；但 Core 中的 `ExclusionMatcher`、`NameMatcher`、`Calculator`、`QueryParser`、`HotkeySpec` 必须保持纯函数风格、无 UI 依赖，便于后续补单元测试。

---

## 11. 后续手工测试清单（供测试轮使用）
1. 启动/单实例：重复启动只唤醒；`--autostart` 无窗口；以管理员重启后旧进程退出、新进程标题栏/设置页显示已提升。
2. QuickSwitch 对话框覆盖：记事本 打开/另存为、Chrome 上传、VS Code 打开文件夹（选择文件夹对话框）、Office 另存为“浏览”、旧式对话框（如 regedit 导入）。每个检查：工具栏出现、跟随移动/缩放/最小化/跨显示器（100%↔150% DPI）、关闭后消失。
3. 导航：三种菜单各导航一次；另存为中文件名 `报告.docx` 导航后仍在且主名被选中；不存在的收藏路径提示而不保存；网络路径断开时不卡。
4. 自动跳转/跟随：打开 Explorer 到 D:\A，再开对话框 → 自动到 D:\A（开关关闭时不跳）；对话框打开中切到 Explorer 导航到 D:\B 再切回 → 跟随；Ctrl+G 生效；Win11 多标签取活动标签。
5. UIPI：以管理员运行记事本，未提升的本程序显示警告与重启入口；提升后可导航。
6. 排除：添加 `node_modules` → 搜索立即无结果；删除规则 → 重新出现（USN 与爬取两种模式）；添加/删除路径规则；正则规则；隐藏/系统开关；导入导出往返一致；启动器“排除此文件夹”。
7. 索引：首次构建时间与条目数记录；新建/删除/重命名/移动文件 2s 内反映；重启后缓存加载；USN 日志删除（`fsutil usn deletejournal`）后自动重建；FSW 大批量复制后结果一致。
8. 搜索：前缀/缩写/子串/多关键词/路径关键词/`folder:`/`app:`/`ext:pdf`/拼音首字母（“bg”→“报告”）/计算器/`g xxx`；历史加权使常用项上浮；空查询分组；所有动作逐项执行；UAC 取消无报错。
9. 打字即搜：Explorer 文件视图、对话框文件视图触发；地址栏、搜索框、重命名、菜单中不触发；上下文导航文件夹与选中文件；关闭开关后恢复系统默认行为。
10. 设置：每项即时生效；热键冲突提示；主题与语言切换；损坏 settings.json 自动恢复默认并备份。
11. 稳定性：Explorer 重启（任务管理器）后 QuickSwitch/打字即搜继续工作；睡眠唤醒后热键与钩子仍在；暂停/恢复；目标程序卡死（调试器暂停）时启动器与托盘仍响应；关闭对话框后工具栏不残留且下次可再次出现。
12. 性能：按 §9.1 逐项读取 `PerfLog`（关于页/日志）与任务管理器工作集，记录实测值；200 万条下连续快速输入无卡顿、无结果闪烁。

---

## 12. 风险与对策
| 风险 | 对策 |
|---|---|
| LL 钩子因超时被系统静默卸载 | 钩子独立线程、回调只做判定；`WM_POWERBROADCAST`(恢复)/会话解锁/设置变更时重装；托盘菜单“暂停→恢复”亦重装 |
| Explorer/目标进程卡死导致我们卡死 | 所有跨进程调用走 CL-Shell 或 `SendMessageTimeout(SMTO_ABORTIFHUNG)`；跨进程 owner 的工具栏放在独立 CL-Toolbar 线程；Shell 线程超时重建 |
| 跨进程 owner：对话框销毁连带销毁工具栏 | HIDE/DIALOGEND/最小化时先解除 owner；`Closed` 后惰性重建；测试清单第 11 项覆盖 |
| 回车方案在个别对话框中误“确认”（打开/保存） | 先校验目录存在；`WM_SETTEXT` 后回读确认才回车；文件夹选择器只走地址栏；导航后轮询验证并恢复文件名；`DisabledProcesses`/`AutoSwitchExcludedProcesses` 逐程序关闭 |
| 自动跟随把对话框带到错误目录导致存错位置（Listary 用户常见抱怨） | 打开时自动跳转默认关闭；跟随只认对话框打开后的 Explorer 激活、每次激活最多一次；按程序排除；用户手动导航后不再覆盖 |
| Win11 Explorer 标签与 XAML 地址栏变化 | 活动标签用 `IShellBrowser` 比对；焦点类名白名单而非黑名单；失败退回“该窗口第一个标签” |
| USN 仅管理员可用，非管理员全盘爬取慢 | 默认排除规则剪枝；缓存优先可搜；后台低优先级（`ThreadPriority.BelowNormal`）；设置页提示“以管理员运行可显著加速” |
| 内存占用（200 万条约 190MB 索引） | SoA + 竞技场、零对象；Lower 不落盘；压实；不存大小/时间；后续可加“仅索引目录”或用 `byte[]`(Latin-1 快路径) 存 Lower 的优化 |
| 搜索指标依赖 CPU 向量宽度与核数 | `PerfLog` 实测；低端机自动把分区数降到核数、K 降到 2048；增量收窄减少全量扫描次数 |
| UIPI：提升窗口前台时 LL 钩子收不到按键 | 文档化；`RegisterHotKey` 备用热键不受影响；提供以管理员运行 |
| 拼音数据无内置来源 | v1 首字母由 GB2312 一级字（3755 个常用字）运行时生成；完整表/全拼见待定问题 |
| 双击 Ctrl 与其他软件冲突/误触 | 严格时序 + 中间任意键或鼠标即取消；可切换为组合键 |
| 前台抢占失败导致启动器不获焦 | `ForegroundHelper` 三级兜底；仍失败时窗口闪烁并记录日志 |

## 13. 需要用户拍板的问题
1. **拼音数据来源**：是否允许内置由 Unicode Unihan `kMandarin` 生成的 `pinyin-initials.bin`（20,992 字节，覆盖全部 `4E00–9FFF`）与可选 `pinyin-full.txt`（约 200KB，Unicode 许可）？否则 v1 仅支持 GB2312 一级字首字母。
2. **默认备用热键**：`Alt+Space` 与 PowerToys Run 冲突，是否改为 `Ctrl+Alt+Space` 或默认不设备用键？
3. **工具栏形态**：本方案为 Listary 式“贴在对话框下沿、被对话框拥有”的横向条（含搜索按钮），而非完整的内嵌搜索面板；是否需要 Listary 那样展开的“推荐文件夹列表”面板（v2 可加）？
4. **打字即搜默认开启**：它会替代 Explorer 自带的“按字母定位文件”，是否默认关闭更稳妥？（方案默认开启，按 Listary 习惯）
5. **自动跳转默认值**：方案默认关闭“打开对话框时自动跳转”、默认开启“切到资源管理器再切回时跟随”，两者独立开关并支持按程序排除，是否同意？
6. **管理员自启动**：是否接受通过 `schtasks.exe` 创建计划任务实现？
