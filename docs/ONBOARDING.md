# Hopkey 新用户引导（Onboarding）设计

> 状态：已在 v0.4.0 实施。
> 视觉语言沿用 `docs/UI-REDESIGN.md`「弧光键帽」，所有画刷、字体、键帽、按钮、开关都复用现有资源，不新增颜色令牌。
> 文中 `file:line` 指本 worktree 当前状态（包含尚未提交的「用户数据导出/导入」改动）。行号会漂移，以文中锚点代码为准。

---

## 1. 问题与目标

| # | 现象 | 原因 |
|---|---|---|
| P1 | 双击 exe 后"什么都没出现"，以为没启动 | Hopkey 没有主窗口；Win11 默认把新托盘图标收进 `^` 溢出区 |
| P2 | 不知道怎么用 | 双击 Ctrl、Ctrl+Alt+Space、文件对话框里的 Ctrl+G 和 QuickSwitch 工具条、资源管理器里直接打字搜索、`folder:`/`ext:` 前缀、`=` 计算、`g 关键词`网页搜索，这些全都看不见 |

目标：

1. 新用户**第一次手动启动**后 1 秒内看到一个窗口，确认程序已在运行。
2. 最多 3 页、每页都能一键跳过，并且有一个**当场能试的**动作（双击 Ctrl）。
3. 讲清楚"它住在托盘里"。Win11 上能检测图标是否被收起，并给出固定方法。
4. 已有用户升级、开机自启、管理员重启这三种情况**永远不自动弹出**。
5. 可以随时从托盘菜单或设置再打开；启动性能不受影响（窗口懒创建）。

---

## 2. 方案选择

### 2.1 推荐：「欢迎窗口（3 页）」+「关闭时的托盘气泡」+「启动器空状态提示」+「托盘/设置里的再次打开入口」

| 组成 | 解决 | 打扰程度 |
|---|---|---|
| **A. 欢迎窗口**：首次手动启动时弹出一次，3 页，可跳过，第 1 页可当场试双击 Ctrl 并实时打勾 | P1 + P2 | 一生一次 |
| **B. 关闭气泡**：欢迎窗口关闭时，如果检测到托盘图标没有固定（或无法检测），弹一次托盘气泡"Hopkey 仍在后台运行……" | P1（窗口关掉后再次确认还在） | 一生一次 |
| **C. 启动器空状态提示**：空查询且没有任何历史时，在现有 EmptyHint 下方轮播一条小提示 | P2（长尾技巧） | 零。有历史后空状态本来就不显示 |
| **D. 再次打开入口**：托盘菜单"新手指南"、设置 → 关于 →"新手指南"按钮 | 忘了 / 想再看 | 零 |
| **E. 第二次启动 exe**：欢迎窗口开着时激活它；否则保持现状（打开启动器） | P1（用户再点一次 exe 时立即有反馈） | 零 |

### 2.2 被否决的方案

| 方案 | 否决理由 |
|---|---|
| 只发一个托盘气泡/Toast，不弹窗 | Win11 上气泡会变成通知中心 Toast，开着"勿扰/专注"时直接被吞；而且图标在 `^` 里，气泡指向的位置用户看不到。只能作为补充（B）。 |
| 5 页以上的完整教程、动画/截图 | 对这类效率工具太重。截图需要随 UI 更新，还会让 exe 变大。用矢量键帽和文字就够了。 |
| 覆盖在启动器上的"教练气泡"（coach marks） | 启动器是 Topmost、失焦即隐藏的弹出层，叠加引导层会干扰现有的激活/隐藏逻辑（`LauncherWindow.xaml.cs:64-84` 的重新激活逻辑很脆弱），风险大、收益小。 |
| 首次启动直接弹出启动器 | 空白的搜索框不解释任何事，用户按 Esc 后又回到"什么都没有"的状态。 |
| 程序自动写注册表 `NotifyIconSettings\…\IsPromoted=1`，强行把图标固定 | 这是用户自己的系统设置。未公开的行为，Explorer 不一定实时生效，还会让 Hopkey 显得像流氓软件。改为检测 + 打开 `ms-settings:taskbar` + 文字指导。 |
| 每次启动都弹，或带"不再显示"复选框 | 打扰。只弹一次，之后从入口 D 再打开。 |
| 首次运行打开设置窗口 | 设置有 6 页、几十个选项，新用户会迷失，也没解决"怎么用"的问题。 |

---

## 3. 触发与持久化

### 3.1 数据结构（Core）

`src/Hopkey.Core/Config/AppSettings.cs`

- 在 `AppSettings`（约 `:10-23`）中 `WebSearch` 属性之后加：

```csharp
/// <summary>Null only while deserializing a settings.json written before onboarding existed (= existing user).</summary>
public OnboardingState? Onboarding { get; set; }
```

- 文件末尾新增：

```csharp
public sealed class OnboardingState
{
    /// <summary>Bump when the guide gains content worth showing to people who saw an older one (not planned yet).</summary>
    public const int CurrentVersion = 1;
    /// <summary>Highest guide version the user has been shown automatically; 0 = never.</summary>
    public int SeenVersion { get; set; }
    /// <summary>The one-time "still running in the tray" balloon has been shown.</summary>
    public bool TrayBalloonShown { get; set; }

    public static OnboardingState ForNewUser() => new();
    public static OnboardingState ForExistingUser() => new() { SeenVersion = CurrentVersion, TrayBalloonShown = true };
}
```

**为什么用可空、默认 null：** 用 System.Text.Json 源生成反序列化时，JSON 里缺少的属性会保留属性初始值。如果写成 `= new()`，老的 settings.json 读出来也会是 `SeenVersion=0`，升级用户就会看到教程。默认 null 才能区分"老文件里没有这个字段"和"新用户"。

**为什么放在顶层而不是 `General` 里：** `SettingsStore.ReplaceAll`（导入用户数据，`SettingsStore.cs:90` 起）是逐个段落复制的，它不复制 `Onboarding`。所以导入别人的配置不会重置或改写本机的引导状态，这符合预期。不需要改 `ReplaceAll`，但要写测试锁住这个行为（§8）。导出包里会带上这个字段，无害。

### 3.2 新用户判定（Core，`SettingsStore.cs`）

规则：**settings.json 和同目录的 history.json 都不存在 → 新用户。** 其余情况（任一文件存在、settings.json 损坏、从 CopyListary 迁移来）一律算老用户。

为什么要同时看 history.json：用户手动删掉 settings.json 来"重置设置"时，history.json 往往还在，这种人不是新手。`AppPaths.MigrateFromLegacy()`（`Program.cs` 中 `Main` 第一步）会在 `SettingsStore.Load` 之前把旧目录搬过来，所以从旧版改名升级的用户自然会被判为老用户。

修改 `SettingsStore.Load`（`:23-50`）：

```csharp
/// <summary>True when neither settings.json nor history.json existed before this load (brand-new user).</summary>
public bool IsFirstRun { get; private set; }

public static SettingsStore Load(string? path = null)
{
    path ??= AppPaths.SettingsFile;
    // history.json lives next to settings.json (AppPaths); temp paths in tests follow the same layout.
    var historyPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "history.json");
    bool firstRun = OnboardingPolicy.IsNewUser(File.Exists(path), File.Exists(historyPath));
    AppSettings settings;
    try
    {
        if (File.Exists(path)) { /* 原样：读取 + Migrate */ }
        else
        {
            settings = new AppSettings();
            settings.Onboarding = firstRun ? OnboardingState.ForNewUser() : OnboardingState.ForExistingUser();
            Migrate(settings);   // 新增：保证各段非 null，和文件分支一致
        }
    }
    catch (Exception ex)
    {
        /* 原样 */ settings = new AppSettings();
        settings.Onboarding = OnboardingState.ForExistingUser(); // 损坏 ≠ 新用户
        Migrate(settings);
    }
    var store = new SettingsStore(path, settings) { IsFirstRun = firstRun };
    store.Save();
    return store;
}
```

`Migrate`（`:55` 起）在 `s.SchemaVersion = 1;` 之前加一行：

```csharp
s.Onboarding ??= OnboardingState.ForExistingUser();   // 老文件没有这个字段 = 升级用户
```

这样 `Load` 返回后 `Onboarding` 永远非 null。App 层统一用 `s.Onboarding!`。`Normalize`（导入）也走 `Migrate`，同样安全。

### 3.3 自动弹出判定（Core，新文件 `src/Hopkey.Core/Config/OnboardingPolicy.cs`）

```csharp
namespace Hopkey.Core.Config;

public enum TrayPromotion { Unknown, Hidden, Promoted }

/// <summary>Pure rules for the first-run guide (unit-tested in Hopkey.SelfTest).</summary>
public static class OnboardingPolicy
{
    public static bool IsNewUser(bool settingsExisted, bool historyExisted) => !settingsExisted && !historyExisted;

    /// <param name="autostart">--autostart (login): never pop a window at login.</param>
    /// <param name="elevatedRestart">--elevated-restart: the user is mid-task; settings opens instead.</param>
    /// <param name="forced">--onboarding (manual/testing).</param>
    public static bool ShouldAutoShow(OnboardingState s, bool autostart, bool elevatedRestart, bool forced)
    {
        if (forced) return true;
        if (autostart || elevatedRestart) return false;
        return s.SeenVersion < OnboardingState.CurrentVersion;
    }

    /// <summary>Keycaps for a launcher hotkey spec: DoubleCtrl → [Ctrl, Ctrl]; "Ctrl+Alt+Space" → [Ctrl, Alt, Space]; "" → [].</summary>
    public static IReadOnlyList<string> Keycaps(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Array.Empty<string>();
        if (HotkeySpec.Parse(spec).IsDoubleCtrl) return new[] { "Ctrl", "Ctrl" };
        return spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Win11 HKCU\Control Panel\NotifyIconSettings\* entries → is our icon pinned to the taskbar?
    /// Match on full path first (known-folder GUID prefixes expanded by the caller); else a unique file-name match; else Unknown.
    /// </summary>
    public static TrayPromotion EvaluatePromotion(IEnumerable<(string ExePath, bool Promoted)> entries, string selfExePath)
    {
        var list = entries.ToList();
        var full = list.Where(e => string.Equals(e.ExePath, selfExePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (full.Count == 0)
        {
            var name = Path.GetFileName(selfExePath);
            full = list.Where(e => string.Equals(Path.GetFileName(e.ExePath), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (full.Count != 1) return TrayPromotion.Unknown;
        }
        return full.Any(e => e.Promoted) ? TrayPromotion.Promoted : TrayPromotion.Hidden;
    }
}
```

**"已看过"在什么时候写入：在欢迎窗口自动弹出（`Show()` 之后）时立即写入 `SeenVersion = CurrentVersion`**，而不是等用户点"完成"。

- 理由：这类工具的引导只该自动出现一次。用户中途关掉，就是在表态"不用了"。下次启动再弹会很烦，想看可以从入口 D 打开。
- 备选："点完成才算看过"。否决：中途关闭、从托盘退出、管理员重启都会让它下次再弹，等于变相的每次都弹。
- 从入口 D 手动打开不改任何状态。

### 3.4 启动参数

`src/Hopkey.App/Program.cs`

- `:455` 记录加一个字段：`public sealed record StartupOptions(bool Autostart, int WaitPid, bool ElevatedRestart, bool ForceOnboarding);`
- `Parse`（`:496-512`）加 `case "--onboarding": onboarding = true; break;`，供测试和开发用。也可以从命令行给用户看。
- `:489` 改为 `instance.Listen(() => app.Dispatcher.BeginInvoke(() => AppHost.Instance?.OnSecondInstance()));`

---

## 4. App 层流程

### 4.1 `AppHost`（`src/Hopkey.App/Bootstrap/AppHost.cs`）

新增字段（`:51-52` 附近）：

```csharp
private OnboardingWindow? _onboarding;
```

**Start()：**

- `:72-78` 托盘事件后加 `_tray.GuideRequested += () => ShowOnboarding(auto: false);`
- `:89` 改为：

```csharp
_hotkeys.LauncherRequested += () => { ToggleLauncher(); _onboarding?.NotifyLauncherHotkey(); };
```

- `:115-116` 改为（顺序很重要）：

```csharp
_ui.BeginInvoke(DispatcherPriority.ApplicationIdle, StartDeferred);
if (_options.ElevatedRestart) _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ShowSettings());
else if (OnboardingPolicy.ShouldAutoShow(s.Onboarding!, _options.Autostart, _options.ElevatedRestart, _options.ForceOnboarding))
    _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () => ShowOnboarding(auto: true));
Logger.Info($"First run: {_settings.IsFirstRun}");
```

欢迎窗口排在 `StartDeferred` **之后**：`StartDeferred` 会做 `EnsureLauncher()` 和 `Prewarm()`，这样用户在第 1 页按双击 Ctrl 时启动器已经预热好，能瞬开。代价是窗口晚出现约 100-200ms，可以接受。`startup.tray` 指标不受影响，因为它在 `:113` 就停表了。

**新方法（放在 settings 区块后）：**

```csharp
// ------------------------------------------------------------------ onboarding

private void ShowOnboarding(bool auto)
{
    if (_shutdown) return;
    if (_onboarding != null) { _onboarding.ActivateWindow(); return; }
    var t0 = PerfLog.Start();
    var vm = new OnboardingViewModel(_settings, isFirstRun: auto && _settings.IsFirstRun, isPaused: () => _paused);
    vm.OpenTaskbarSettingsRequested += OpenTaskbarSettings;
    _onboarding = new OnboardingWindow(vm);
    _onboarding.Closed += (_, _) => { var v = _onboarding?.ViewModel; _onboarding = null; OnOnboardingClosed(v); };
    _onboarding.Show();
    _onboarding.ActivateWindow();
    if (auto) _settings.Update(x => x.Onboarding!.SeenVersion = OnboardingState.CurrentVersion);
    PerfLog.Stop("onboarding.show", t0); // listed in About → Performance
}

private void OnOnboardingClosed(OnboardingViewModel? vm)
{
    if (_shutdown || _app.Dispatcher.HasShutdownStarted) return;   // tray Exit while the window is open: no balloon
    if (_settings.Current.Onboarding!.TrayBalloonShown) return;
    if (TrayPromotionProbe.Read() == TrayPromotion.Promoted) { MarkBalloon(); return; }
    _tray.ShowBalloon(LocalizationManager.T("Onboarding.Balloon"));
    MarkBalloon();
    void MarkBalloon() => _settings.Update(x => x.Onboarding!.TrayBalloonShown = true);
}

/// <summary>Second launch of the exe: bring the guide forward if it's open, else the launcher (as before).</summary>
public void OnSecondInstance()
{
    if (_onboarding != null) _onboarding.ActivateWindow();
    else ShowLauncher();
}

private static void OpenTaskbarSettings()
{
    try { Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true }); }
    catch (Exception ex) { Logger.Warn("Opening taskbar settings failed", ex); }
}
```

**ShowSettings()（`:240-261`）：** 加 `_settingsVm.GuideRequested += () => ShowOnboarding(auto: false);`

**OnSettingsChanged()（`:265` 起）：** 语言变化分支 `:269-275` 里不需要额外处理。窗口的 XAML 全部用 `DynamicResource S.*`，代码拼出来的文字由 VM 订阅 `LocalizationManager.LanguageChanged` 自行刷新（§5.4）。热键或 QuickSwitch 变化时，如果 `_onboarding != null`，调用 `_onboarding.ViewModel.RefreshHotkeys()`，让第 1、3 页显示的键帽跟着设置变。

**Shutdown()（`:339` 起）：** 无需改动。`Application.Shutdown` 会关闭所有窗口，`OnOnboardingClosed` 里已经用 `_shutdown` / `HasShutdownStarted` 挡住了气泡。

### 4.2 托盘检测（App，新文件 `src/Hopkey.App/Tray/TrayPromotionProbe.cs`）

```csharp
/// <summary>Reads Win11's per-icon "show in taskbar" flag. Win10 / missing key / any error → Unknown.</summary>
internal static class TrayPromotionProbe
{
    public static TrayPromotion Read()
    {
        if (!Dwmapi.IsWin11) return TrayPromotion.Unknown;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return TrayPromotion.Unknown;
            var entries = new List<(string, bool)>();
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("ExecutablePath") is not string p) continue;
                entries.Add((ExpandKnownFolder(p), k.GetValue("IsPromoted") is int v && v == 1));
            }
            return OnboardingPolicy.EvaluatePromotion(entries, AppPaths.ExecutablePath);
        }
        catch { return TrayPromotion.Unknown; }
    }

    // Explorer stores some paths as "{KNOWNFOLDERID}\rest": expand the common ones.
    private static string ExpandKnownFolder(string p) { /* {6D809377-6AF0-444B-8957-A3773F02200E}=ProgramFiles,
       {7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}=ProgramFilesX86, {F38BF404-1D43-42F2-9305-67DE0B28FC23}=Windows;
       other GUIDs → return as-is (file-name fallback in EvaluatePromotion handles them) */ }
}
```

- 只在欢迎窗口第 2 页可见时，每 1 秒用 `DispatcherTimer` 轮询一次，离开该页或关闭窗口就 `Stop()`。另外在关闭气泡判断时读一次。单次读取不到 1ms，而且不在启动路径上。
- 托盘图标刚创建时，Explorer 可能稍后才写入这个键。轮询会自然追上：前几秒显示 Unknown 文案，之后切换成 Hidden 或 Promoted。

### 4.3 托盘菜单（`src/Hopkey.App/Tray/TrayIconService.cs`）

- `:22-27` 加 `public event Action? GuideRequested;`
- `:14-19` 加字段 `_guideItem`。`:35-40` 之间新建：
  `_guideItem = new WinForms.ToolStripMenuItem("", null, (_, _) => GuideRequested?.Invoke()) { Tag = "" };`（Segoe Fluent "Lightbulb"。实施时目测一下，如果字形不合适就用 `` Help）
- `:41-45` 菜单顺序：`_searchItem, _settingsItem, _guideItem, ─, _rebuildItem, _pauseItem, _adminItem, ─, _exitItem`
- `UpdateTexts`（`:54-66`）加 `_guideItem.Text = LocalizationManager.T("Tray.Guide");`
- `TrayMenuRenderer.Apply` 会按 Tag 自动渲染图标，不用改 renderer。

### 4.4 设置 → 关于（入口 D）

- `src/Hopkey.App/Settings/SettingsWindow.xaml:313-316` 的按钮行（"打开数据目录 / 打开日志目录"）**之前**插一个主按钮：
  `<Button Content="{DynamicResource S.Onboarding.Open}" Style="{StaticResource Hk.Button.Primary}" Click="OnOpenGuide" />`
- `SettingsWindow.xaml.cs` 加 `private void OnOpenGuide(object s, RoutedEventArgs e) => _vm.RequestGuide();`
- `SettingsViewModel.cs:86-88` 事件区加 `public event Action? GuideRequested;`，并加 `public void RequestGuide() => GuideRequested?.Invoke();`（与 `:242 RequestRestartElevated` 并列）。

### 4.5 启动器空状态提示（C）

- `LauncherViewModel.cs`：新增 `EmptyTip` 属性（string）和静态数组 `s_tipKeys = { "Launcher.Tip.Filter", "Launcher.Tip.Calc", "Launcher.Tip.Web", "Launcher.Tip.Actions", "Launcher.Tip.Pinyin" }`，以及 `private int _tipIndex`。在 `Reset(...)` 里（每次 `ShowLauncher` 都会调用）执行 `_tipIndex = (_tipIndex + 1) % s_tipKeys.Length; EmptyTip = LocalizationManager.T(s_tipKeys[_tipIndex]);`。`RefreshLocalization()` 里也重算一次。
- `LauncherWindow.xaml:286-287` 的 `EmptyHint` TextBlock 之后加：

```xml
<TextBlock Text="{Binding EmptyTip}" Margin="0,6,0,0" HorizontalAlignment="Center"
           FontFamily="{DynamicResource Font.Small}" FontSize="11.5" Foreground="{DynamicResource Brush.TextFaint}" />
```

- 只在 `ShowEmptyHint`（`LauncherViewModel.cs:265`：空查询且没有任何默认结果）时可见，也就是只有没有历史、没有收藏的新用户能看到。这里零分配：字符串来自字典。

---

## 5. 欢迎窗口 UI

### 5.1 文件

| 文件 | 内容 |
|---|---|
| `src/Hopkey.App/Onboarding/OnboardingWindow.xaml` | 窗口 + 3 页 + 底栏 |
| `src/Hopkey.App/Onboarding/OnboardingWindow.xaml.cs` | 翻页动画、键盘、Chrome、`ActivateWindow()`、`NotifyLauncherHotkey()` |
| `src/Hopkey.App/Onboarding/OnboardingViewModel.cs` | 页码、键帽列表、托盘状态、自启开关、语言 |

只在 `ShowOnboarding` 里 `new`，不进入启动路径。它的资源和设置窗口一样，在 `Window.Resources` 里合并 `/Hopkey;component/Themes/SettingsControls.xaml`（按钮、开关、分段控件），**不进 App 资源**，所以不影响启动器。键帽（`Hk.Keycap`、`Hk.KeyTemplate`、`KeyChord`）和 `Img.Logo`、`Geo.HopArc` 已经在 App 级的 `Controls.xaml` 里。

### 5.2 窗口属性

```xml
<Window x:Class="Hopkey.App.Onboarding.OnboardingWindow" …
        Title="{DynamicResource S.Onboarding.Title}" Width="600" Height="520"
        ResizeMode="CanMinimize" WindowStartupLocation="CenterScreen" ShowInTaskbar="True"
        Background="{DynamicResource Brush.WindowBg}" Foreground="{DynamicResource Brush.Text}"
        FontFamily="{DynamicResource Font.Family}" FontSize="13" UseLayoutRounding="True">
```

- **标准标题栏**（与设置窗口一致）：可拖动、可关闭、可最小化；DPI 和多显示器的行为交给系统处理。`SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);` 给标题栏着色（framed 分支）。`ThemeManager.Apply` 遍历 `app.Windows`，切换主题时会自动重新着色。
- **ShowInTaskbar=True**：窗口开着的时候任务栏上有 Hopkey 按钮，这本身就是"已经启动"的明确信号。
- `CenterScreen` 在 WPF 中居中到鼠标所在的显示器；app.manifest 已经声明了 PerMonitorV2，跨显示器拖动时会自动重新缩放。
- 固定 600×520（以 96 DPI 计）。在 1366×768、150% 缩放下约 780 物理像素高，能放下。所有文字 `TextWrapping="Wrap"`；英文比中文长约 30%，§5.5 的文案已按 520 高度控制。
- 不设 Topmost：不抢其他窗口的层级。启动器是 Topmost，试双击 Ctrl 时会正常盖在欢迎窗口上。
- Win10：标题栏着色调用会静默失败，退回系统标题栏，可以接受。

### 5.3 布局

```
┌ 欢迎使用 Hopkey ──────────────────────────────── _  × ┐  DWM 标题栏 = Brush.Background 色
│ (Brush.Aurora 径向光晕铺在顶部 160px 背后)                    │
│  [Logo 36] Hopkey                      [ 中文 | English ]   │ Header  margin 28,22,28,0
│  ━━━━━━ (Brush.Hop 3px, 宽 96)                              │
│                                                            │
│  页标题（Font.Display 22 SemiBold）                          │ Body  margin 28,18,28,0
│  页说明（13, SubText, Wrap, MaxWidth 520）                  │
│  ╭──────────────────────────────────────────────────╮      │
│  │ 页面主体卡片 Hk.Section                            │      │
│  ╰──────────────────────────────────────────────────╯      │
│                                                            │
├────────────────────────────────────────────────────────────┤ 1px Divider
│  ▬ • •                              [跳过]   [下一步 →]   │ Footer h60 Brush.Footer padding 28,0
└────────────────────────────────────────────────────────────┘
```

- **Header**：`Image Source={StaticResource Img.Logo}` 36×36；字标 "Hopkey" 用 `Font.Numeric` 20 SemiBold。弧光线：`Path Data="M0,0 H1" Stretch="Fill" Width="96" Height="3" Stroke=Brush.Hop`，写法同关于页 `SettingsWindow.xaml` 的弧光线。右侧是语言分段：`Border Style=Hk.SegmentHost` 内放两个 `RadioButton Style=Hk.Segment GroupName="Lang"`，内容固定为"中文"和"English"（不本地化，各自按本语言显示），`IsChecked` 绑定 `VM.IsZh` / `VM.IsEn`。
  - 为什么把语言放在引导里：默认语言是 zh-CN（`AppSettings.cs` `GeneralSettings.Language`），英文用户在第一屏就能切换，不用先去找设置。
- **Body**：三个 `Grid`（Page1/2/3），按 `VM.Page` 切换 `Visibility`。不用 TabControl，免得它的隐式样式和焦点行为干扰。
- **Footer**：
  - 左侧步骤点：3 个 `Border`。当前页宽 18、高 6、`CornerRadius=3`、`Background=Brush.Hop`；其他页 6×6、`Brush.Divider`。可点击跳页（`MouseLeftButtonUp`），`Cursor=Hand`。
  - 右侧按钮：`[跳过]`（`Hk.Button`，`IsCancel=True`，第 3 页隐藏）；`[上一步]`（`Hk.Button`，只在第 2、3 页显示，放在跳过左边）；`[下一步]` / 第 3 页 `[开始使用]`（`Hk.Button.Primary`，`IsDefault=True`）。
- **翻页动效**（遵循 UI-REDESIGN §10）：新页 `Opacity` 0→1（110ms，CubicEase Out），同时 `TranslateTransform.X` 12→0（120ms），在 Back 时 -12→0。动画对象 static frozen。`SystemParameters.ClientAreaAnimation == false` 时不播放。

### 5.4 ViewModel（`OnboardingViewModel : ObservableObject, IDisposable`）

```csharp
public int Page { get; set; }                       // 0..2，set 时 OnPropertyChanged(IsFirst/IsLast/NextText…)
public bool IsLast => Page == 2;
public string NextText => T(IsLast ? "Onboarding.Finish" : "Onboarding.Next");
public bool IsFirstRun { get; }                     // 控制第 1 页"首次建立索引"提示
public bool IsPaused => _isPaused();                // 第 1 页显示"已暂停"提示

// Page 1
public IReadOnlyList<string> LauncherKeys { get; private set; }   // OnboardingPolicy.Keycaps(Hotkeys.Launcher)
public bool LauncherIsDoubleTap { get; private set; }             // 决定 "快速按两次" / "按下"
public IReadOnlyList<string> AltKeys { get; private set; }        // 空列表 → 隐藏 "或 …" 行
public bool TriedLauncher { get; private set; }                   // NotifyLauncherHotkey() 置 true

// Page 2
public TrayPromotion Tray { get; private set; }                   // 轮询更新
public bool Autostart { get => _settings.Current.General.Autostart; set => _settings.Update(s => s.General.Autostart = value); }
public event Action? OpenTaskbarSettingsRequested;

// Page 3
public IReadOnlyList<string> DialogJumpKeys { get; private set; } // QuickSwitch.Enabled && DialogJumpHotkey 才非空
public bool ShowToolbarTip { get; private set; }                  // QuickSwitch.Enabled && ShowToolbar
public bool ShowTypeToSearchTip { get; private set; }             // Hotkeys.TypeToSearch

// Language
public bool IsZh { get; set; }  // set true → _settings.Update(s => s.General.Language = "zh-CN")
public bool IsEn { get; set; }  // set true → "en"
public void RefreshHotkeys();   // 由 AppHost 在热键/QS 设置变化时调用；构造函数里也调用一次
```

- 订阅 `LocalizationManager.LanguageChanged` 刷新 `NextText` 等代码拼出的文字，以及 `IsZh/IsEn`；`Dispose` 时退订。窗口 `Closed` 里调用 `Dispose`。
- 自启开关直接写 `SettingsStore`。`AppHost.OnSettingsChanged`（`:281-282`）已经负责调用 `AutostartService.Apply`，不用重复实现。设置窗口同时开着时，它的 VM 有自己的 draft，两边一起改的情况可以忽略，和现在托盘、设置并存时的处理一致。
- 托盘轮询：`DispatcherTimer`（1s）在 `Page == 1` 时 `Start`，否则 `Stop`；`Dispose` 时停止。

### 5.5 各页内容与文案

> 键帽统一用 `ItemsControl ItemsSource=… ItemTemplate={StaticResource Hk.KeyTemplate} ItemsPanel={StaticResource Hk.HPanel}`。第 1 页的大键帽用 `Hk.Keycap` + `Height=34 MinWidth=52 CornerRadius=7` 的局部样式（`BasedOn`），字号 14。

#### 第 1 页：已在运行 + 当场试一下

| 元素 | zh-CN | en |
|---|---|---|
| 标题 `Onboarding.P1.Title` | Hopkey 已经在后台运行了 | Hopkey is running |
| 说明 `Onboarding.P1.Body` | 它没有主窗口。在任何地方按下快捷键，就能搜索文件、文件夹和应用，并在当前窗口里直接打开。 | It has no main window. Press a shortcut anywhere to search files, folders and apps, and open them right where you are. |
| 卡片标签（双击）`Onboarding.P1.DoubleTap` | 快速按两次 | Tap twice quickly |
| 卡片标签（组合键）`Onboarding.P1.Press` | 按下 | Press |
| 备用热键行前缀 `Onboarding.P1.Or` | 或者 | or |
| 试一试提示 `Onboarding.P1.Try` | 现在就试试：唤出搜索框，输入几个字母，按 Esc 关闭。 | Try it now: open the search box, type a few letters, press Esc to close. |
| 成功 `Onboarding.P1.Done` | 成功！以后在任何地方都可以这样唤出 Hopkey。 | Nice! That works from anywhere. |
| 暂停提示 `Onboarding.P1.Paused` | Hopkey 当前已暂停，请先在托盘菜单中选择“恢复”。 | Hopkey is paused. Choose "Resume" in the tray menu first. |
| 首次索引 `Onboarding.P1.Indexing` | 首次运行会在后台建立文件索引，通常不到一分钟，期间搜索结果可能不完整。 | The first file index builds in the background (usually under a minute); results may be incomplete until then. |

卡片布局（`Hk.Section`，内容水平居中，`Padding 20,22`）：

```
        快速按两次
     [ Ctrl ]  [ Ctrl ]          ← 大键帽
     或者 [Ctrl][Alt][Space]      ← 小键帽，AltKeys 为空时整行隐藏
  ─────────────────────────────
  ◎ 现在就试试：唤出搜索框……       ← TriedLauncher=false：图标 E768(Play) Accent
  ✓ 成功！以后在任何地方……         ← TriedLauncher=true：图标 E73E(CheckMark) Brush.Success，文字 Brush.Success
```

- `TriedLauncher` 从 false 变为 true 时，大键帽的 `BorderThickness` 从 `1,1,1,2` 变为 `1,2,1,1`（键帽"按下"），背景变为 `Brush.AccentFill`、文字变为 `Brush.AccentText`。这是 UI-REDESIGN 中"激活键帽"的同一种处理，作为成功反馈。
- 页底 `Hk.Hint`：`IsPaused` 时显示暂停提示（`Brush.Warning`），否则 `IsFirstRun` 时显示首次索引提示。
- **交互检测**：热键回调 `AppHost` → `_onboarding?.NotifyLauncherHotkey()` → `VM.TriedLauncher = true`（幂等）。只有热键才算，托盘单击不算，因为要教的就是热键。启动器打开时欢迎窗口失去焦点，用户按 Esc 后焦点回到欢迎窗口，这时打勾已经显示好了。如果用户停在第 2、3 页时按了热键，同样置 true，但不跳页。

#### 第 2 页：住在托盘里

| 元素 | zh-CN | en |
|---|---|---|
| 标题 `Onboarding.P2.Title` | 在任务栏右下角找到 Hopkey | Find Hopkey in the taskbar corner |
| 说明 `Onboarding.P2.Body` | Hopkey 只在通知区域（托盘）显示一个图标：单击打开搜索，右键打开菜单（设置、暂停、退出）。关闭这个窗口后它会继续运行。 | Hopkey lives in the notification area (system tray): click the icon to search, right-click for the menu (settings, pause, exit). It keeps running after you close this window. |
| 状态·已固定 `Onboarding.P2.Promoted` | 图标已显示在任务栏上。 | The icon is pinned to the taskbar. |
| 状态·被收起 标题 `Onboarding.P2.Hidden` | 图标目前收在 ^ 隐藏区里 | The icon is hidden behind ^ |
| 状态·被收起 说明 `Onboarding.P2.HiddenHelp` | Windows 11 默认会收起新图标。点击下方按钮，在“其他系统托盘图标”里打开 Hopkey；也可以直接把图标从 ^ 里拖到任务栏上。 | Windows 11 hides new icons by default. Click below and turn on Hopkey under "Other system tray icons", or drag the icon out of ^ onto the taskbar. |
| 状态·未知 `Onboarding.P2.Unknown` | 看不到图标时，点击任务栏右下角的 ^ 查看隐藏的图标，可以把它拖到任务栏上固定。 | Can't see the icon? Click ^ in the taskbar corner to show hidden icons, then drag it onto the taskbar. |
| 按钮 `Onboarding.P2.OpenTaskbar` | 打开任务栏设置 | Open taskbar settings |
| 开关 `Settings.Autostart`（复用） | 开机自动启动 | Start with Windows |
| 开关说明 `Onboarding.P2.AutostartHint` | 开机后自动在后台运行，无需每次手动打开。 | Runs in the background after you sign in, so it's always ready. |

布局：说明下面放**插图卡片**：一条 `Brush.Plate` 底、`CornerRadius=8`、高 44 的"迷你任务栏"。右侧依次是 `^` 字形（`E70E` ChevronUp）、Hopkey 小 Logo（`Img.Logo` 20×20）、时钟文字 "12:30"（Font.Numeric，TextFaint）。Logo 外画一个 `Brush.Hop` 描边 2px 的圆角框做高亮。这是示意，不随真实状态变化。

插图下面是**状态卡片**（`Hk.Section`），按 `Tray` 显示三种状态之一：

| Tray | 图标 | 文本 | 按钮 |
|---|---|---|---|
| Promoted | `E73E` Brush.Success | P2.Promoted | 隐藏 |
| Hidden | `E7BA`(Warning) Brush.Warning；卡片 `Background=Brush.WarningSoft` | P2.Hidden（SemiBold）+ P2.HiddenHelp | `Hk.Button.Primary` P2.OpenTaskbar |
| Unknown（Win10 / 读不到） | `E946`(Info) Brush.Accent | P2.Unknown | `Hk.Button` P2.OpenTaskbar |

用户在系统设置里打开开关后，1 秒内卡片从 Hidden 变为 Promoted（绿色）。这是第二个"实时反馈"点。

最下面是自启开关行：`CheckBox`（隐式 Toggle 样式）`Content={DynamicResource S.Settings.Autostart}`，下方 `Hk.Hint` 显示 P2.AutostartHint，并加 `Margin 52,0,0,0` 子项缩进。

#### 第 3 页：更多用法

| 元素 | zh-CN | en |
|---|---|---|
| 标题 `Onboarding.P3.Title` | 几个顺手的用法 | A few handy tricks |
| 分组·文件对话框 `Onboarding.P3.Dialogs` | 在“打开 / 保存”对话框和资源管理器里 | In Open/Save dialogs and Explorer |
| 对话框跟随（重点，第一行）`Onboarding.P3.Follow` | 在“打开 / 保存”对话框里，切到资源管理器打开某个文件夹，再切回对话框——对话框会自动跳到那个文件夹。 | In an Open/Save dialog, switch to Explorer and open a folder, then switch back: the dialog jumps to that folder. |
| 工具条 `Onboarding.P3.Toolbar` | 对话框下方会出现 Hopkey 工具条：一键跳到资源管理器里打开着的文件夹、收藏或最近的文件夹。 | A Hopkey toolbar appears under file dialogs: jump to a folder open in Explorer, a favorite or a recent folder. |
| Ctrl+G `Onboarding.P3.DialogJump` | 在对话框里直接跳到推荐的文件夹。 | Jump the dialog straight to the suggested folder. |
| 直接打字 `Onboarding.P3.TypeToSearch` | 在资源管理器或对话框里直接打字，就会开始搜索，结果在当前窗口打开。 | Just start typing in Explorer or a dialog to search; results open in that window. |
| 分组·搜索框 `Onboarding.P3.Launcher` | 在搜索框里 | In the search box |
| 类型标签 `Onboarding.P3.Filter` | 搜索后点击结果上方的类型标签，只看这一类结果；也可以按 Ctrl+Tab 切换。 | After searching, click a type chip above the results to show only that kind; Ctrl+Tab cycles them. |
| 计算 `Onboarding.P3.Calc` | 直接计算 | Calculate |
| 网页 `Onboarding.P3.Web` | 用 Google 搜索（bd 百度、bing、gh GitHub） | Search Google (also bd, bing, gh) |
| Tab `Onboarding.P3.Actions` | 更多操作：打开位置、复制路径、以管理员运行… | More actions: open location, copy path, run as admin… |
| Alt+数字 `Onboarding.P3.Direct` | 直接打开第 1–9 个结果 | Open result 1–9 directly |
| 页脚 `Onboarding.P3.Footer` | 随时可以从托盘菜单 →“新手指南”再次打开本页。 | Reopen this guide any time from the tray menu → "Getting started". |
| 完成按钮 `Onboarding.Finish` | 开始使用 | Get started |

布局：两个 `Hk.Section` 卡片，每个卡片里是 `Grid`，两列（左列 `Width=150` 放键帽或代码片段，右列 `*` 放说明，`MinHeight=32`，行间 1px Divider，规则同 `Hk.Row`）：

- 卡片 1（文件对话框与资源管理器）：
  - 左 `E8AB` 图标 + "QuickSwitch"（12 SemiBold）→ P3.Toolbar（`ShowToolbarTip`）
  - 左 `DialogJumpKeys` 键帽 → P3.DialogJump（`DialogJumpKeys` 非空）
  - 左 `[A][B][C]` 三个小键帽 → P3.TypeToSearch（`ShowTypeToSearchTip`）
- 卡片 2（搜索框）：左列用 `Font.Mono` 12、`Brush.Accent`、`Brush.AccentSoft` 底、`CornerRadius=4`、`Padding 6,1` 的"代码胶囊"：
  - 仿启动器类型标签的小芯片「文件夹（选中态）/ 应用 / PDF」→ P3.Filter（不再介绍 `folder:` 等前缀）
  - `=12*3` → P3.Calc
  - `g hopkey` → P3.Web
  - 键帽 `[Tab]`，以及 `[Ctrl][Enter]` 合并成一行 → P3.Actions（Ctrl+Enter 包含在"打开位置"里，不再单列）
  - 键帽 `[Alt][1…9]` → P3.Direct
- 页脚 `Hk.Hint` 显示 P3.Footer。

#### 共用文案

| 键 | zh-CN | en |
|---|---|---|
| `Onboarding.Title` | 欢迎使用 Hopkey | Welcome to Hopkey |
| `Onboarding.Next` | 下一步 | Next |
| `Onboarding.Back` | 上一步 | Back |
| `Onboarding.Skip` | 跳过 | Skip |
| `Onboarding.Finish` | 开始使用 | Get started |
| `Onboarding.Open`（设置·关于按钮） | 新手指南 | Getting started |
| `Tray.Guide`（托盘菜单） | 新手指南 | Getting started |
| `Onboarding.Balloon`（关闭气泡） | Hopkey 仍在后台运行。双击 Ctrl 即可搜索；图标在任务栏右下角的 ^ 里。 | Hopkey is still running. Double-tap Ctrl to search; the icon is under ^ in the taskbar corner. |

> 气泡文案里的"双击 Ctrl"在用户已改热键时不准确。可以改为 `Onboarding.BalloonCombo` =（"Hopkey 仍在后台运行。按 {0} 即可搜索；图标在任务栏右下角的 ^ 里。" / "Hopkey is still running. Press {0} to search; the icon is under ^ in the taskbar corner."），按 `HotkeySpec.IsDoubleCtrl` 选择用哪条。新用户必然是默认热键，所以这只是严谨性问题，建议一并实现（多 1 行代码）。

#### 启动器提示（入口 C）

| 键 | zh-CN | en |
|---|---|---|
| `Launcher.Tip.Filter` | 提示：搜索后点击结果上方的类型标签，只看文件夹、应用或某种文件（Ctrl+Tab 也能切换） | Tip: after searching, click a type chip above the results to see only folders, apps or one file type (Ctrl+Tab cycles them) |
| `Launcher.Tip.Calc` | 提示：输入 =12*3 直接计算 | Tip: type =12*3 to calculate |
| `Launcher.Tip.Web` | 提示：g 关键词 用 Google 搜索（还有 bd、bing、gh） | Tip: g <words> searches Google (also bd, bing, gh) |
| `Launcher.Tip.Actions` | 提示：按 Tab 查看更多操作，Ctrl+Enter 打开所在位置 | Tip: Tab for more actions, Ctrl+Enter opens the location |
| `Launcher.Tip.Pinyin` | 提示：支持拼音首字母，如 wd 可以找到“文档” | Tip: pinyin initials work too, e.g. wd finds 文档 |

所有键加到 `LocalizationManager.cs` 的 `s_strings`：`Launcher.Tip.*` 加在 `// Launcher` 区块末尾（`:62` 之后）；`Tray.Guide` 加在 `// Tray` 区块（`:101-105`）；新建 `// Onboarding` 区块放其余键，位置在 `// Settings` 之前。XAML 用 `{DynamicResource S.Onboarding.…}`，代码用 `LocalizationManager.T(...)`。

### 5.6 键盘与可访问性

- `Enter` = 下一步 / 开始使用（`IsDefault`）；`Esc` = 跳过（`IsCancel`，关闭窗口）。第 3 页没有跳过按钮，所以在 `PreviewKeyDown` 里处理 Esc → `Close()`。
- `Ctrl+Tab`/`Ctrl+Shift+Tab` 或 `Alt+→`/`Alt+←`：翻页。普通方向键留给焦点控件。
- 打开窗口和翻页后，焦点放在主按钮上，这样按 Enter 就能一路走完。
- 页面切换时调用 `AutomationProperties.Name`（页标题）。所有图标字形加 `AutomationProperties.Name` 或 `IsHitTestVisible=False` + 文字兜底。
- 颜色对比度沿用已经校验过的令牌（UI-REDESIGN §3.7）。成功和警告状态除了颜色还有图标与文字，不只靠颜色区分。

### 5.7 `OnboardingWindow.xaml.cs` 要点

```csharp
public OnboardingViewModel ViewModel { get; }
public OnboardingWindow(OnboardingViewModel vm)
{
    ViewModel = vm; DataContext = vm; InitializeComponent();
    SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
    vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.Page)) PlayPageAnimation(); };
    Closed += (_, _) => { DataContext = null; vm.Dispose(); };
}
public void ActivateWindow()
{
    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
    ForegroundHelper.Activate(new WindowInteropHelper(this).Handle);
    Activate();
}
public void NotifyLauncherHotkey() => ViewModel.MarkLauncherTried();
// 按钮：Next → Page < 2 ? Page++ : Close(); Back → Page--; Skip → Close(); OpenTaskbar → vm.RequestOpenTaskbarSettings()
```

---

## 6. 边界情况

| 情况 | 行为 | 依据 |
|---|---|---|
| 开机自启（`--autostart`），且新用户从未看过引导 | 不弹。`SeenVersion` 不变，下次**手动**启动时再弹 | `ShouldAutoShow` |
| 管理员重启（`--elevated-restart`） | 不弹，照旧打开设置。之前那次启动时已经标记为看过 | `Start()` 中 `if/else if` |
| 欢迎窗口打开时从托盘"以管理员身份重启" | 旧进程退出（窗口随之关闭，不弹气泡），新进程不弹引导 | `HasShutdownStarted` 检查 |
| 用户第 1 页就点 × / Alt+F4 / Esc | 视为看过。如果图标未固定且没弹过气泡，就弹一次气泡 | §3.3、`OnOnboardingClosed` |
| 托盘"退出"时窗口还开着 | 直接退出，不弹气泡 | 同上 |
| 窗口开着时再次双击 exe | 激活欢迎窗口（最小化时先还原） | `OnSecondInstance` |
| 窗口开着时从托盘或设置再点"新手指南" | 激活已有窗口，不新建第二个 | `ShowOnboarding` 开头 |
| 窗口开着时在设置里改语言或主题 | 立即生效（DynamicResource + `ApplyWindowChrome`） | §5.2、§5.4 |
| 窗口开着时在设置里改热键 | `RefreshHotkeys` 更新键帽 | §4.1 |
| 已暂停时打开引导 | 第 1 页显示暂停提示；热键不会触发，不打勾 | `IsPaused` |
| 热键被占用（如 Ctrl+Alt+Space 冲突） | 照常弹出原有的冲突气泡，与引导无关 | `AppHost.cs:90` |
| 前台是管理员窗口（UIPI），非管理员 Hopkey 收不到双击 Ctrl | 用户可能试不出来，这是已知限制。第 1 页不额外说明，避免信息过载 | — |
| 勿扰模式吞掉气泡 | 可以接受，因为窗口已经说明过托盘的位置 | — |
| Win10 | 托盘状态永远是 Unknown（显示通用说明）；`ms-settings:taskbar` 仍然可用（Win10 的"选择哪些图标显示在任务栏上"在同一页） | `TrayPromotionProbe` |
| 多个不同路径的 Hopkey.exe（开发者） | 先按完整路径匹配；匹配不到且文件名同名项不唯一 → Unknown | `EvaluatePromotion` |
| 用户删除 settings.json 但保留 history.json | 算老用户，不弹 | `IsNewUser` |
| settings.json 损坏 | 算老用户，不弹 | `Load` 的 catch 分支 |
| 导入用户数据 | 不改变本机的 `Onboarding` | `ReplaceAll` 不复制该段 |
| 高 DPI / 跨显示器拖动 | PerMonitorV2 + WPF 自动缩放；固定尺寸按 DIP 计算 | app.manifest |
| 窗口在小屏上放不下 | 520 DIP 高在 768p/150% 下刚好能放下；更极端时 Body 外层包 `ScrollViewer VerticalScrollBarVisibility=Auto` 兜底 | — |

---

## 7. 性能

- 启动路径上只新增：`File.Exists(history.json)`（约 0.05ms）、`ShouldAutoShow`（纯计算）和一次 `BeginInvoke`。`startup.tray` 不变。
- 窗口、ViewModel、SettingsControls.xaml 只在需要时创建；老用户永远不会加载。
- 新增 `PerfLog` 项 `onboarding.show`（`ShowOnboarding` 入口到 `Show()` 返回），目标 < 150ms（冷，含 SettingsControls.xaml 解析）。设置窗口首开耗时与它同一量级，可以作为参照。
- 托盘轮询只在第 2 页可见时运行，1Hz，单次不到 1ms。
- 启动器提示：每次显示时多一次字典查找和一次属性赋值，无分配。

---

## 8. 测试

### 8.1 SelfTest（Core，`tests/Hopkey.SelfTest/Program.cs`）

在 `Main` 的 Section 列表（`:42-53`）末尾加 `Section("Onboarding", OnboardingTests);`。所有用例都在临时目录里进行，跟 `UserDataTests`（`:450` 起）的写法一样。

| 用例 | 断言 |
|---|---|
| 空目录 `SettingsStore.Load(tmp/settings.json)` | `IsFirstRun == true`；`Onboarding.SeenVersion == 0`；`ShouldAutoShow(…, false, false, false) == true` |
| 同上，但传 autostart=true / elevatedRestart=true | `ShouldAutoShow == false` |
| forced=true 且已看过 | `ShouldAutoShow == true` |
| 写一个不含 `onboarding` 字段的旧 settings.json 再 Load | `IsFirstRun == false`；`SeenVersion == CurrentVersion`；`TrayBalloonShown == true` |
| 只有 history.json，没有 settings.json | `IsFirstRun == false`；`SeenVersion == CurrentVersion` |
| settings.json 内容是垃圾 | `SeenVersion == CurrentVersion`（不弹） |
| 新用户 `Update(SeenVersion = 1)` 后重新 Load | 值被持久化；`ShouldAutoShow == false` |
| `ReplaceAll(另一个 SeenVersion=0 的 settings)` | 本机 `Onboarding` 不变 |
| `UserDataService.Read` 读一个不含 onboarding 的包 → Normalize | `Onboarding` 非 null |
| `Keycaps("DoubleCtrl")` / `("Ctrl+Alt+Space")` / `("")` / `(null)` | `[Ctrl,Ctrl]` / `[Ctrl,Alt,Space]` / `[]` / `[]` |
| `EvaluatePromotion`：完整路径匹配且 promoted / 完整路径匹配且未 promoted / 只有唯一同名项 / 两个同名项 / 空列表 / 大小写不同 | Promoted / Hidden / 按该项 / Unknown / Unknown / 视为匹配 |

### 8.2 手工验收清单（App）

1. 删除（备份）`%APPDATA%\Hopkey\settings.json` 和 `history.json`，启动 exe → 1 秒内出现欢迎窗口，居中于鼠标所在显示器，任务栏上有 Hopkey 按钮。
2. 第 1 页双击 Ctrl → 启动器弹出；按 Esc → 欢迎窗口显示绿色勾，键帽变成按下态。
3. 右上角切换到 English → 全部文案立即变为英文，托盘菜单也是英文；切回中文同理。
4. 设置里切换深色/浅色 → 欢迎窗口和标题栏颜色同步变化。
5. 第 2 页（Win11，图标未固定）→ 黄色"收在 ^ 里"卡片；点"打开任务栏设置"→ 系统设置打开；打开 Hopkey 开关 → 1 秒内变为绿色"已显示"。
6. 第 2 页打开"开机自动启动"→ 设置 → 常规里同一开关为开；HKCU Run 键里有值。
7. 第 3 页 → 按钮变为"开始使用"，没有"跳过"；Enter 关闭。图标未固定时弹出一次气泡，再次打开并关闭不再弹气泡。
8. 关闭后再次启动 exe（进程已退出）→ 不再弹引导。
9. 进程运行中再次双击 exe：引导开着时激活引导，引导关着时打开启动器。
10. 托盘菜单出现"新手指南"，带图标；点击后打开引导，`settings.json` 中 `onboarding` 不变。设置 → 关于 → "新手指南"按钮效果相同。
11. 用 `--autostart` 启动一个全新配置 → 不弹；随后不带参数手动启动 → 弹出。
12. 引导开着时托盘 → 退出 → 进程退出，没有气泡。
13. 用旧版本生成的 settings.json（没有 onboarding 字段）启动 → 不弹；文件被写回后带有 `"onboarding": { "seenVersion": 1, "trayBalloonShown": true }`。
14. 新用户空历史时打开启动器 → EmptyHint 下方出现一条提示，每次打开换一条；有历史后提示和 EmptyHint 都不显示。
15. 125% / 150% / 175% 缩放，以及把窗口拖到另一块不同 DPI 的显示器上 → 文字不截断，布局不溢出（英文为最长的情况）。
16. `dotnet run --project tests\Hopkey.SelfTest --no-build` 全部通过，性能指标无回退；关于页能看到 `onboarding.show`。

---

## 9. 改动清单

| 文件 | 改动 | 估算行数 |
|---|---|---|
| `src/Hopkey.Core/Config/AppSettings.cs` | `Onboarding` 属性 + `OnboardingState` 类 | +20 |
| `src/Hopkey.Core/Config/OnboardingPolicy.cs`（新） | `TrayPromotion`、`IsNewUser`、`ShouldAutoShow`、`Keycaps`、`EvaluatePromotion` | +50 |
| `src/Hopkey.Core/Config/SettingsStore.cs` | `IsFirstRun`；`Load` 三个分支设置 Onboarding；`Migrate` 加 `??=` | +15 |
| `src/Hopkey.App/Program.cs` | `ForceOnboarding`、`--onboarding`、`OnSecondInstance` | +5 |
| `src/Hopkey.App/Bootstrap/AppHost.cs` | 字段、Start 调度、热键通知、`ShowOnboarding` / `OnOnboardingClosed` / `OnSecondInstance` / `OpenTaskbarSettings`，以及设置和热键变化的转发 | +60 |
| `src/Hopkey.App/Tray/TrayIconService.cs` | `GuideRequested` + 菜单项 | +6 |
| `src/Hopkey.App/Tray/TrayPromotionProbe.cs`（新） | 读取注册表 | +45 |
| `src/Hopkey.App/Onboarding/OnboardingWindow.xaml`（新） | 窗口与 3 页 | +230 |
| `src/Hopkey.App/Onboarding/OnboardingWindow.xaml.cs`（新） | 动画、键盘、激活 | +70 |
| `src/Hopkey.App/Onboarding/OnboardingViewModel.cs`（新） | §5.4 | +130 |
| `src/Hopkey.App/Settings/SettingsWindow.xaml` / `.xaml.cs` / `SettingsViewModel.cs` | 关于页按钮 + 事件 | +6 |
| `src/Hopkey.App/Launcher/LauncherViewModel.cs` / `LauncherWindow.xaml` | `EmptyTip` | +12 |
| `src/Hopkey.App/Infrastructure/LocalizationManager.cs` | 约 45 个键，中英双语 | +50 |
| `tests/Hopkey.SelfTest/Program.cs` | `OnboardingTests` | +70 |

合计约 750 行，其中约 300 行是 XAML 和文案；C# 逻辑约 400 行。如果需要压缩到一次做完，**入口 C（启动器提示）可以单独推迟**，它与其他部分没有依赖。

---

## 10. 风险

1. **`NotifyIconSettings` 是未公开的注册表结构**，未来的 Windows 版本可能改名或改格式。缓解措施：任何异常或匹配不到都返回 Unknown，界面退回通用说明，功能不会坏。
2. **Win11 设置页里"其他系统托盘图标"的名称和位置**在 22H2、23H2、24H2 之间略有差异，文案里用的是当前名称。如果后续版本改名，只需要改一条字符串。
3. **欢迎窗口与启动器的焦点交互**：启动器的 `Deactivated` 重新激活逻辑只针对它自己显示后的 300ms 内，与欢迎窗口互不干扰。但第 1 页的"试一试"必须实测：启动器关闭后焦点要回到欢迎窗口，不能落到其他程序（见验收第 2 项）。
4. **首次弹窗晚了约 0.2s**（排在 Prewarm 之后）。如果实测超过 500ms，就改为先 `ShowOnboarding` 再 `StartDeferred`，并接受第 1 页首次双击 Ctrl 时启动器稍慢。

---

## 11. 需要拍板的问题

1. **"看过"的时机**：推荐"自动弹出即算看过"（§3.3）。如果希望"中途关闭的人下次还能看到"，就改为在 `Closed` 时判断 `Page == 2` 或点了完成；代价是更容易打扰用户。
2. **默认语言**：目前新用户默认 zh-CN。是否改为首次运行时按 `CultureInfo.CurrentUICulture` 自动选择（zh-* → zh-CN，其余 → en）？改动约 3 行，放在 `Load` 的新用户分支里。推荐做，这样欢迎页的语言切换就成了兜底而不是必需。
3. **自启开关在引导里的默认状态**：推荐保持关（与现状一致，由用户主动打开）。是否改为在第 2 页默认勾选？不推荐：未经用户同意就开机自启会降低信任。
4. **第 3 页内容是否需要按当前设置隐藏行**（例如关闭了 QuickSwitch 就不显示工具条那一行）：本文按"隐藏"设计。如果希望始终完整展示功能（用于"发现"），就去掉这几个可见性绑定。
5. **以后升级时是否要做"新功能"提示**（把 `CurrentVersion` 加到 2）：本次不做，只预留字段。
