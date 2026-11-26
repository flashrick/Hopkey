# Hopkey UI 重设计规范 —「弧光键帽」(Arc & Keycap)

> 适用范围：Launcher、QuickSwitch 工具条（含其弹出菜单）、设置窗口、托盘菜单。
> 约束：纯 WPF + C#，零第三方依赖；只用 Windows 11 自带字体；Light / Dark / System 三种主题、中英文 UI 都必须正常；
> 所有现有 `x:Name`、绑定、事件处理器保持可用（下文 XAML 已按此编写）。

---

## 1. 设计概念

**名称：「弧光键帽」(Arc & Keycap)**

Hopkey 的图标是一枚正在“跳起”的键帽，上面画着一道带箭头的弧线。重设计从图标里取出两个母题，把它们贯穿到整个 UI：

- **键帽（Keycap）——触感。** 所有“可以按”的东西都画成实体键帽：搜索框是一块带底部“唇边”的大键帽面板，类型筛选是一排小键帽，结果行右侧的 1–9 直达序号、底栏快捷键提示、动作菜单里的快捷键也都是键帽。按下态的做法是把唇边从底部挪到顶部（`BorderThickness 1,1,1,2 → 1,2,1,1`）。这样有“按下去”的感觉，又不改变布局尺寸。
- **弧光（Hop Arc）——动感。** 一条 靛蓝 → 紫 → 青 的渐变（`Brush.Hop`），只用在线条和图形上：搜索框左侧的跳跃弧图标、搜索面板唇边上的“弧光线”、选中行左侧的指示条、设置导航指示条、输入框聚焦下划线、工具条顶边。启动器每次弹出时，弧光线从左向右扫过（150ms），弧图标轻轻跳一下。这就是 Hopkey 的签名动作。

底色不用中性灰白，改用带靛蓝色调的「墨色」（Dark）和「薰衣草纸」（Light）。

**为什么不会再像 Listary（逐条对照）：**

| Listary / 当前实现 | 弧光键帽 |
|---|---|
| 纯白/中性灰卡片，扁平 | 靛蓝调墨色渐变底 + 顶部微光边 + 左上角极光晕，有品牌色温 |
| 一行细线搜索框 + 放大镜 | 56px 高的“键帽面板”搜索区，左侧是品牌跳跃弧图标，底部唇边上有渐变弧光线 |
| 选中 = 整行蓝色填充 | 靛蓝浅底 + 左侧 3px 渐变弧光条（有弹出动画）+ 右侧序号键帽变成“按下”的实心态 |
| 匹配字符蓝色加粗 | 荧光笔式半透明靛蓝底 + 半粗体 |
| `Alt+1` 灰字 | 1–9 数字键帽列（键盘数字行的隐喻） |
| 灰色状态栏 + 一串文字快捷键 | 状态圆点 + 消息胶囊 + 键帽化快捷键提示 |
| 筛选胶囊 = 圆角灰标签 | 一排小键帽，激活态是靛蓝实心且“按下” |
| 设置 = 普通 Tab | 品牌导航栏（Logo + 字标 + 弧光指示条）+ 圆角内容页 + 分组卡片 + 键帽式按钮/开关 |

---

## 2. 窗口外观决策：不用 AllowsTransparency，用 DWM

**推荐：`AllowsTransparency=False` + DWM 圆角 + DWM 边框色 + DWM 阴影；不启用 Mica/Acrylic 背景。**

理由：
1. `AllowsTransparency=True` 会把窗口变成分层窗口（`UpdateLayeredWindow`），每帧都要把整个窗口位图从显存拷回系统内存再交给 DWM。首次显示更慢，滚动和动画时 CPU 更高，而且会失去 DWM 的原生阴影和圆角，只能用 `DropShadowEffect` 自己画，这又是一项昂贵的效果。启动器的卖点是“瞬开”，这个代价不值得。
2. Win11 DWM 已经能做到我们需要的：`DWMWA_WINDOW_CORNER_PREFERENCE`（项目已在用）负责圆角；`DWMWA_BORDER_COLOR`（34）画一条贴合圆角的 1px 品牌色边框。现在 WPF 的 `BorderThickness=1` 是方形边框，被 DWM 圆角裁掉后四角会断线，这一项正好修掉。DWM 也负责阴影。
3. 不用 Acrylic/Mica（`DWMWA_SYSTEMBACKDROP_TYPE`）。它们会让外观随壁纸漂移，品牌色不可控，看起来和所有 Fluent 应用一样，正是我们要摆脱的；在 WPF 里还要把客户区背景打透（`DwmExtendFrameIntoClientArea` + 透明 `CompositionTarget`），文字对比度不再稳定。保留为未来可选项，见 §10。

**DWM 调用（加到 `src/Hopkey.Core/Native/Shell32.cs` 的 `Dwmapi`）：**

```csharp
public const int DWMWA_NCRENDERING_POLICY = 2;
public const int DWMWA_BORDER_COLOR  = 34;   // Win11 22000+
public const int DWMWA_CAPTION_COLOR = 35;
public const int DWMWA_TEXT_COLOR    = 36;

[StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int L, R, T, B; }
[DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

/// <summary>colorRef = 0x00BBGGRR</summary>
public static void SetColor(IntPtr hwnd, int attr, uint colorRef)
{
    int v = unchecked((int)colorRef);
    DwmSetWindowAttribute(hwnd, attr, ref v, sizeof(int));
}

/// <summary>Borderless popups: make DWM draw its shadow. Call once after SourceInitialized.</summary>
public static void EnableShadow(IntPtr hwnd)
{
    int policy = 2; // DWMNCRP_ENABLED
    DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
    var m = new MARGINS { B = 1 };          // 1px hidden under WPF's opaque content
    DwmExtendFrameIntoClientArea(hwnd, ref m);
}
```

- Launcher：`SetRoundCorners(hwnd)`（8px）+ `EnableShadow` + `BORDER_COLOR`。
- QuickSwitch 工具条：改成 `SetRoundCorners(Handle)`（8px，不再用 small）+ `EnableShadow` + `BORDER_COLOR`。
- 设置窗口（标准标题栏）：`CAPTION_COLOR` 和 `TEXT_COLOR` 设成导航栏底色，标题栏和左侧导航融为一体；`BORDER_COLOR` 设成 `Border` 色。
- **Win10 回退**：`Environment.OSVersion.Version.Build < 22000` 时，这些属性调用会静默失败。此时 Launcher 和工具条在代码里恢复 `BorderThickness=1` + `Brush.Border`。
- **验证点**：如果某台 Win11 机器上 Launcher 仍没有阴影，先确认 `EnableShadow` 已调用；仍不行，就接受无阴影，靠 DWM 边框色区分边界。

---

## 3. 颜色令牌

所有画刷都在 `ThemeManager.CreateDictionary(bool dark)` 里用代码构建并 `Freeze()`。保持代码构建，是因为工具条运行在 `CL-Toolbar` 线程，每个线程都要有自己的副本。

**规则：`Brush.Hop` / `Brush.HopV` 渐变只用于线条和图形，绝不当承载文字的底色。** 白字放在青色端上对比度只有约 1.8。

### 3.1 现有键（重新取值）

| Key | Dark | Light | 用途 |
|---|---|---|---|
| `Brush.Background` | `#0D0C1A` | `#F3F2FA` | 设置导航栏 / 对话框底 / 设置标题栏 |
| `Brush.Card` | `#17162C` | `#FFFFFF` | 设置分组卡片、工具条、菜单、Launcher 纯色回退 |
| `Brush.CardAlt` | `#1D1C36` | `#F1F0F9` | DataGrid 表头 |
| `Brush.Selected` | `#2A2766` | `#E6E4FF` | 选中行 / 选中导航 / 按下 |
| `Brush.Hover` | `#211F40` | `#EEEDFA` | 悬停 |
| `Brush.Accent` | `#A5B4FC` | `#4F46E5` | **文字/图标**强调色（徽章、消息、激活图标） |
| `Brush.AccentText` | `#FFFFFF` | `#FFFFFF` | AccentFill 上的文字（原 Dark 值 `#000000` 改为白） |
| `Brush.Text` | `#ECEBFF` | `#17162B` | 主文字 |
| `Brush.SubText` | `#9D9BC7` | `#5E5B7E` | 次级文字（路径、说明） |
| `Brush.Border` | `#2C2A4F` | `#E0DEF0` | 卡片/输入框边框 |
| `Brush.Input` | `#1B1A33` | `#FFFFFF` | 输入框底 |
| `Brush.Error` | `#FF7A8A` | `#C42B1C` | 错误 |
| `Brush.Warning` | `#FBBF24` | `#9D5D00` | 警告 |
| `Brush.Highlight` | `#C7D2FE` | `#3730A3` | 匹配字符前景 |

### 3.2 新增纯色键

| Key | Dark | Light | 用途 |
|---|---|---|---|
| `Brush.Surface` | `#121124` | `#F8F7FD` | 设置内容页底 |
| `Brush.Footer` | `#121124` | `#F4F3FB` | Launcher 底栏 |
| `Brush.Divider` | `#232142` | `#E9E7F5` | 细分隔线 |
| `Brush.TextFaint` | `#7C7AA8` | `#706E94` | 占位符、详情、禁用（仅非关键信息） |
| `Brush.Plate` | `#1E1D3A` | `#FFFFFF` | 搜索键帽面板 |
| `Brush.PlateEdge` | `#34315F` | `#D9D6F2` | 搜索面板/弹出层边、唇边 |
| `Brush.KeyFace` | `#232144` | `#FFFFFF` | 键帽面、次按钮底 |
| `Brush.KeyEdge` | `#3A3768` | `#D4D1EC` | 键帽边/唇边 |
| `Brush.KeyText` | `#C9C7EE` | `#4A4766` | 键帽文字 |
| `Brush.AccentFill` | `#5558E8` | `#4F46E5` | 实心强调底（主按钮、激活筛选、按下键帽、开关开） |
| `Brush.AccentFillHover` | `#6366F1` | `#4338CA` | 实心强调悬停 |
| `Brush.AccentFillEdge` | `#3B37B5` | `#3730A3` | 实心强调唇边 |
| `Brush.AccentSoft` | `#3D6366F1` | `#1A4F46E5` | 半透明强调底（徽章、上下文胶囊、消息胶囊） |
| `Brush.HighlightBg` | `#4D6366F1` | `#2E6366F1` | 匹配字符荧光底 |
| `Brush.Success` | `#34D399` | `#0F7B4F` | 状态点：就绪 |
| `Brush.ErrorSoft` | `#33FF7A8A` | `#1AC42B1C` | 错误消息胶囊底 |
| `Brush.WarningSoft` | `#33FBBF24` | `#1A9D5D00` | 工具条权限警告按钮底 |
| `Brush.Focus` | `#A5B4FC` | `#4F46E5` | 键盘焦点环 |
| `Brush.ScrollThumb` | `#3A3768` | `#C9C6E4` | 滚动条滑块 |
| `Brush.ScrollThumbHover` | `#4C4889` | `#A9A5D4` | 滚动条滑块悬停 |
| `Brush.ToggleTrack` | `#1B1A33` | `#FFFFFF` | 开关关闭态轨道 |
| `Brush.ToggleEdge` | `#5A5790` | `#8A88A8` | 开关关闭态边 |
| `Brush.ToggleKnob` | `#9D9BC7` | `#5E5B7E` | 开关关闭态滑块 |

### 3.3 新增渐变键

| Key | 类型 | Dark stops | Light stops |
|---|---|---|---|
| `Brush.Hop` | Linear 水平 (0,0)→(1,0) | 0 `#6366F1` · 0.55 `#A78BFA` · 1 `#22D3EE` | 0 `#4F46E5` · 0.55 `#8B5CF6` · 1 `#06B6D4` |
| `Brush.HopV` | Linear 垂直 (0,0)→(0,1) | 0 `#22D3EE` · 0.5 `#818CF8` · 1 `#6366F1` | 0 `#06B6D4` · 0.5 `#6366F1` · 1 `#4F46E5` |
| `Brush.WindowBg` | Linear 垂直 | 0 `#1A1934` · 1 `#141328` | 0 `#FDFDFF` · 1 `#F6F5FD` |
| `Brush.RimLight` | Linear 水平 | 0 `#00C7D2FE` · 0.3 `#26C7D2FE` · 0.7 `#26C7D2FE` · 1 `#00C7D2FE` | 全透明（`Transparent`） |
| `Brush.Aurora` | Radial，Center = GradientOrigin = (0.12, 0)，RadiusX 0.6，RadiusY 1.3 | 0 `#2E6366F1` · 1 `#006366F1` | 0 `#1A6366F1` · 1 `#006366F1` |

### 3.4 字体键

| Key | 值 |
|---|---|
| `Font.Family`（已有） | `Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI` |
| `Font.Display` | `Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI` |
| `Font.Small` | `Segoe UI Variable Small, Segoe UI, Microsoft YaHei UI` |
| `Font.Mono` | `Cascadia Mono, Consolas, Microsoft YaHei UI` |
| `Font.Numeric` | `Bahnschrift, Segoe UI, Microsoft YaHei UI` |
| `Font.Icon` | `Segoe Fluent Icons, Segoe MDL2 Assets` |

### 3.5 DWM 颜色（ThemeManager 中的 `uint` 常量，格式 0x00BBGGRR）

| 用途 | Dark | Light |
|---|---|---|
| 无边框窗口边框（Launcher/工具条） | `#34315F` → `0x005F3134` | `#D4D1EC` → `0x00ECD1D4` |
| 设置标题栏底 | `#0D0C1A` → `0x001A0C0D` | `#F3F2FA` → `0x00FAF2F3` |
| 设置标题栏文字 | `#ECEBFF` → `0x00FFEBEC` | `#17162B` → `0x002B1617` |
| 设置窗口边框 | `#2C2A4F` → `0x004F2A2C` | `#E0DEF0` → `0x00F0DEE0` |

### 3.6 ThemeManager 实现要点

```csharp
private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
private static LinearGradientBrush LG(bool vertical, params (double O, string Hex)[] stops)
{
    var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = vertical ? new Point(0, 1) : new Point(1, 0) };
    foreach (var (o, h) in stops) b.GradientStops.Add(new GradientStop(C(h), o));
    b.Freeze(); return b;
}
private static RadialGradientBrush Aurora(string from, string to)
{
    var b = new RadialGradientBrush(C(from), C(to))
        { Center = new Point(0.12, 0), GradientOrigin = new Point(0.12, 0), RadiusX = 0.6, RadiusY = 1.3 };
    b.Freeze(); return b;
}
// d["Font.Display"] = new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"); …（其余字体同理）

public static void ApplyWindowChrome(Window w)
{
    var hwnd = new WindowInteropHelper(w).Handle;
    if (hwnd == IntPtr.Zero) return;
    Dwmapi.SetDarkMode(hwnd, IsDark);
    if (w.WindowStyle == WindowStyle.None)
        Dwmapi.SetColor(hwnd, Dwmapi.DWMWA_BORDER_COLOR, IsDark ? 0x005F3134u : 0x00ECD1D4u);
    else
    {
        Dwmapi.SetColor(hwnd, Dwmapi.DWMWA_CAPTION_COLOR, IsDark ? 0x001A0C0Du : 0x00FAF2F3u);
        Dwmapi.SetColor(hwnd, Dwmapi.DWMWA_TEXT_COLOR,    IsDark ? 0x00FFEBECu : 0x002B1617u);
        Dwmapi.SetColor(hwnd, Dwmapi.DWMWA_BORDER_COLOR,  IsDark ? 0x004F2A2Cu : 0x00F0DEE0u);
    }
}
```
`QuickSwitchToolbar.ApplyResources()` 末尾，在工具条线程上（`Handle != 0` 时）补一次 `BORDER_COLOR` 和 `SetDarkMode`。

### 3.7 对比度核对（WCAG 相对亮度）

| 前景 / 背景 | Dark | Light |
|---|---|---|
| Text / 窗口底 | `#ECEBFF`/`#1A1934` **14.5** | `#17162B`/`#F6F5FD` **16.4** |
| SubText / 卡片 | `#9D9BC7`/`#17162C` **6.9** | `#5E5B7E`/`#FFFFFF` **6.3** |
| SubText / 选中行 | `#9D9BC7`/`#2A2766` **5.0** | `#5E5B7E`/`#E6E4FF` **5.2** |
| TextFaint / 窗口底 | `#7C7AA8`/`#141328` **4.5** | `#706E94`/`#FFFFFF` ≈**4.9** |
| Accent / 卡片 | `#A5B4FC`/`#17162C` **9.1** | `#4F46E5`/`#FFFFFF` **6.1** |
| Accent / AccentSoft（混合后） | `#A5B4FC`/`#29295B` **6.7** | `#4F46E5`/`#EDECFC` **5.4** |
| Highlight / HighlightBg（普通行） | `#C7D2FE`/`#2C2C64` **8.5** | `#3730A3`/`#E3E3FC` **7.9** |
| Highlight / HighlightBg（选中行） | `#C7D2FE`/`#3B3A90` **6.4** | `#3730A3`/`#CECDFC` **6.5** |
| AccentText / AccentFill | `#FFFFFF`/`#5558E8` **5.3** | `#FFFFFF`/`#4F46E5` **6.3** |
| KeyText / KeyFace | `#C9C7EE`/`#232144` **9.4** | `#4A4766`/`#FFFFFF` **8.8** |
| Error / 卡片 | `#FF7A8A` **7.3** | `#C42B1C` **5.5** |

所有正文都 ≥ 4.5，最低一项是 TextFaint，约 4.5，只用于非关键信息。

---

## 4. 字体排印

| 元素 | 字体键 | 字号 | 字重 | 颜色 |
|---|---|---|---|---|
| 查询输入 / 占位符 | Font.Display | 22 | SemiLight（无则回落 Regular） | Text / TextFaint |
| 结果标题 | Font.Family | 14 | Regular；匹配段 SemiBold | Text / Highlight |
| 结果副标题（路径） | Font.Small | 11.5 | Regular | SubText |
| 结果详情（日期/大小） | Font.Numeric | 11 | Regular | TextFaint |
| 徽章 | Font.Small | 10.5 | SemiBold | Accent |
| 键帽 | Font.Mono | 10.5（底栏 10） | Regular | KeyText |
| 筛选键帽 | Font.Family | 12 | Regular（激活 SemiBold 不变宽，故保持 Regular） | SubText / AccentText |
| 上下文胶囊 | Font.Family | 12 | Regular | Text |
| 动作名 | Font.Family | 13.5 | Regular | Text |
| 底栏状态/标签 | Font.Small | 11.5 | Regular | SubText |
| 设置页标题 | Font.Display | 26 | SemiBold | Text |
| 设置分组标题 | Font.Family | 14 | SemiBold | Text |
| 设置正文 / 说明 | Font.Family | 13 / 12 | Regular | Text / SubText |
| 设置导航 | Font.Family | 13.5 | Regular | SubText（选中 Text） |
| 品牌字标 “Hopkey” | Font.Numeric | 18（关于页 30） | SemiBold | Text |

- Launcher 根节点保留 `TextOptions.TextFormattingMode="Display"`（小字锐利）；`QueryBox` 和 `Placeholder` 单独设 `Ideal`（大字更平滑）。
- 中文统一回落到 Microsoft YaHei UI（每个字体键都带这个回退）。

---

## 5. Launcher

### 5.1 结构与尺寸

```
宽 720（原 680），SizeToContent=Height，DWM 圆角 8，背景 Brush.WindowBg
┌──────────────────────────────────────────────────────────┐ ← 1px RimLight（顶）
│ (Aurora 径向光晕铺在 Row0-1 背后)                          │
│ [⤴ 在此窗口中打开：Downloads]   上下文胶囊 h26，margin 12,12,12,0 │ Row0
│ ╭────────────────────────────────────────────────────╮   │
│ │ ⤴  输入关键词开始搜索                    h56 r10   │   │ Row1 查询键帽面板 margin 10,10,10,0
│ ╰═════════════ 弧光线 2px ══════════════════════════╯   │      底唇 2px + 弧光线
│ [全部 128] [文件夹 12] [应用 3] [.pdf 9]      Ctrl Tab   │ Row2 筛选键帽 margin 12,10,12,0
│ ▍[ic] 标题标题标题                     [收藏]   [1]      │ Row3 结果 行高 48，item margin 8,1
│   [ic] 标题                          修改 2024…   [2]      │      ListBox MaxHeight 432 (=9 行)
│ ...                                                      │      margin 0,8,0,6
├──────────────────────────────────────────────────────────┤ ← 1px Divider
│ ● 已索引 1,234,567 项  (已复制)   Enter 打开 Ctrl Enter 打开位置 Tab 更多操作 │ Row4 底栏 h34
└──────────────────────────────────────────────────────────┘
```

### 5.2 窗口外框（替换 `<Window>` 头部和根 Grid 的开头）

```xml
<Window x:Class="Hopkey.App.Launcher.LauncherWindow" …同前 xmlns…
        Title="Hopkey" Width="720" SizeToContent="Height"
        WindowStyle="None" ResizeMode="NoResize" ShowInTaskbar="False" Topmost="True"
        WindowStartupLocation="Manual" Left="-10000" Top="-10000"
        Background="{DynamicResource Brush.WindowBg}" Foreground="{DynamicResource Brush.Text}"
        BorderThickness="0" FontFamily="{DynamicResource Font.Family}" FontSize="13"
        UseLayoutRounding="True" TextOptions.TextFormattingMode="Display">
  …
  <Grid x:Name="Root">
    <Grid.RowDefinitions> <!-- 5 × Auto，同前 --> </Grid.RowDefinitions>
    <Border Grid.RowSpan="2" Background="{DynamicResource Brush.Aurora}" IsHitTestVisible="False" />
    <Rectangle Grid.RowSpan="5" Height="1" VerticalAlignment="Top" Fill="{DynamicResource Brush.RimLight}" IsHitTestVisible="False" />
    …各行…
  </Grid>
</Window>
```

在 `SourceInitialized` 中，原有 `SetRoundCorners` 之后追加 `Dwmapi.EnableShadow(_hwnd)`；`ThemeManager.ApplyWindowChrome(this)` 会负责边框色。

### 5.3 上下文胶囊（Row 0）

```xml
<Border Grid.Row="0" Visibility="{Binding IsContextMode, Converter={StaticResource BoolToVis}}"
        Margin="12,12,12,0" HorizontalAlignment="Left" Height="26" CornerRadius="13" Padding="10,0,12,0"
        Background="{DynamicResource Brush.AccentSoft}">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="&#xE8A7;" FontFamily="{DynamicResource Font.Icon}" FontSize="12"
                   Foreground="{DynamicResource Brush.Accent}" VerticalAlignment="Center" />
        <TextBlock Text="{Binding ContextText}" Margin="8,0,0,0" FontSize="12" MaxWidth="620"
                   TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
    </StackPanel>
</Border>
```

### 5.4 查询键帽面板（Row 1）

```xml
<Grid Grid.Row="1" Margin="10,10,10,0">
    <Border Height="56" CornerRadius="10" BorderThickness="1,1,1,2"
            Background="{DynamicResource Brush.Plate}" BorderBrush="{DynamicResource Brush.PlateEdge}">
        <Grid Margin="16,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>
            <!-- 品牌跳跃弧：取自 hopkey.svg 的两条路径 -->
            <Path x:Name="HopGlyph" Width="22" Height="16" Stretch="Uniform" Margin="0,0,14,0" VerticalAlignment="Center"
                  Data="M94,132 Q128,84 162,132 M146,128 L162,134 L168,116"
                  Stroke="{DynamicResource Brush.Hop}" StrokeThickness="2.4"
                  StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round"
                  RenderTransformOrigin="0.5,0.5">
                <Path.RenderTransform>
                    <TransformGroup>
                        <RotateTransform Angle="-9" />
                        <TranslateTransform x:Name="HopGlyphShift" />
                    </TransformGroup>
                </Path.RenderTransform>
            </Path>
            <TextBlock x:Name="Placeholder" Grid.Column="1" Text="{DynamicResource S.Launcher.Placeholder}"
                       FontFamily="{DynamicResource Font.Display}" FontSize="22" FontWeight="SemiLight"
                       TextOptions.TextFormattingMode="Ideal" VerticalAlignment="Center" Margin="3,0,0,0"
                       Foreground="{DynamicResource Brush.TextFaint}" IsHitTestVisible="False" />
            <TextBox x:Name="QueryBox" Grid.Column="1" FontFamily="{DynamicResource Font.Display}" FontSize="22"
                     FontWeight="SemiLight" TextOptions.TextFormattingMode="Ideal"
                     VerticalAlignment="Center" BorderThickness="0" Background="Transparent" Padding="0"
                     Foreground="{DynamicResource Brush.Text}" CaretBrush="{DynamicResource Brush.Accent}"
                     SelectionBrush="{DynamicResource Brush.AccentFill}" SelectionOpacity="0.45"
                     Template="{StaticResource Hk.BareTextBox}"
                     Text="{Binding Query, UpdateSourceTrigger=PropertyChanged}" TextChanged="OnQueryTextChanged" />
        </Grid>
    </Border>
    <!-- 弧光线：压在底唇上 -->
    <Rectangle x:Name="HopLine" Height="2" Margin="18,0" VerticalAlignment="Bottom" RadiusX="1" RadiusY="1"
               Fill="{DynamicResource Brush.Hop}" IsHitTestVisible="False" RenderTransformOrigin="0,0.5">
        <Rectangle.RenderTransform><ScaleTransform x:Name="HopLineScale" /></Rectangle.RenderTransform>
    </Rectangle>
</Grid>
```

`Hk.BareTextBox` 定义在 Controls.xaml 中：只含 `PART_ContentHost`，没有边框和焦点视觉。这样做是为了不被设置页的隐式 TextBox 样式影响，同时去掉 Aero2 的焦点边框：

```xml
<ControlTemplate x:Key="Hk.BareTextBox" TargetType="TextBox">
    <ScrollViewer x:Name="PART_ContentHost" Focusable="False" Background="Transparent" />
</ControlTemplate>
```

### 5.5 筛选键帽（Row 2；删除原来的 1px 分隔 `Rectangle`）

```xml
<Grid Grid.Row="2" Margin="12,10,12,0" Visibility="{Binding HasFacets, Converter={StaticResource BoolToVis}}">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*" />
        <ColumnDefinition Width="Auto" />
    </Grid.ColumnDefinitions>
    <ItemsControl x:Name="FacetBar" ItemsSource="{Binding Facets}" Focusable="False">
        <ItemsControl.ItemsPanel><ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate></ItemsControl.ItemsPanel>
        <ItemsControl.ItemTemplate>
            <DataTemplate DataType="{x:Type local:FacetViewModel}">
                <Border x:Name="Chip" Height="24" CornerRadius="6" Padding="10,0" Margin="0,0,6,6" Cursor="Hand"
                        BorderThickness="1,1,1,2" Background="{DynamicResource Brush.KeyFace}"
                        BorderBrush="{DynamicResource Brush.KeyEdge}" MouseLeftButtonUp="OnFacetClick">
                    <TextBlock x:Name="ChipText" Text="{Binding Text}" FontSize="12" VerticalAlignment="Center"
                               Foreground="{DynamicResource Brush.SubText}" />
                </Border>
                <DataTemplate.Triggers>
                    <Trigger SourceName="Chip" Property="IsMouseOver" Value="True">
                        <Setter TargetName="Chip" Property="Background" Value="{DynamicResource Brush.Hover}" />
                        <Setter TargetName="ChipText" Property="Foreground" Value="{DynamicResource Brush.Text}" />
                    </Trigger>
                    <DataTrigger Binding="{Binding IsActive}" Value="True">
                        <Setter TargetName="Chip" Property="Background" Value="{DynamicResource Brush.AccentFill}" />
                        <Setter TargetName="Chip" Property="BorderBrush" Value="{DynamicResource Brush.AccentFillEdge}" />
                        <Setter TargetName="Chip" Property="BorderThickness" Value="1,2,1,1" />
                        <Setter TargetName="ChipText" Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
                    </DataTrigger>
                </DataTemplate.Triggers>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
    <!-- Ctrl+Tab 提示 -->
    <ItemsControl Grid.Column="1" Margin="8,3,0,0" VerticalAlignment="Top" Opacity="0.75" Focusable="False"
                  ItemsSource="{Binding Source='Ctrl+Tab', Converter={StaticResource KeyChord}}"
                  ItemTemplate="{StaticResource Hk.KeyTemplate}" ItemsPanel="{StaticResource Hk.HPanel}" />
</Grid>
```

### 5.6 结果行（Row 3）——容器样式 + 数据模板

结果行的结构，从左到右依次是：3px 弧光指示条（只在选中时出现）、42px 图标列、标题和副标题、右侧元信息（徽章和详情）、30px 数字键帽列。

```xml
<Style x:Key="ResultItemStyle" TargetType="ListBoxItem">
    <Setter Property="Padding" Value="0" />
    <Setter Property="Margin" Value="8,1" />
    <Setter Property="Focusable" Value="False" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ListBoxItem">
                <Grid>
                    <Border x:Name="Bd" Background="Transparent" CornerRadius="8" />
                    <Rectangle x:Name="Bar" Width="3" Height="24" RadiusX="1.5" RadiusY="1.5" Margin="3,0,0,0"
                               HorizontalAlignment="Left" Fill="{DynamicResource Brush.HopV}" Opacity="0"
                               RenderTransformOrigin="0.5,0.5">
                        <Rectangle.RenderTransform><ScaleTransform ScaleY="0.3" /></Rectangle.RenderTransform>
                    </Rectangle>
                    <ContentPresenter Margin="12,0,8,0" />
                </Grid>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Hover}" />
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Selected}" />
                        <Setter TargetName="Bar" Property="Opacity" Value="1" />
                        <Trigger.EnterActions>
                            <BeginStoryboard x:Name="SelPop">
                                <Storyboard>
                                    <DoubleAnimation Storyboard.TargetName="Bar"
                                                     Storyboard.TargetProperty="(UIElement.RenderTransform).(ScaleTransform.ScaleY)"
                                                     From="0.3" To="1" Duration="0:0:0.09">
                                        <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut" /></DoubleAnimation.EasingFunction>
                                    </DoubleAnimation>
                                </Storyboard>
                            </BeginStoryboard>
                        </Trigger.EnterActions>
                        <Trigger.ExitActions><RemoveStoryboard BeginStoryboardName="SelPop" /></Trigger.ExitActions>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<DataTemplate x:Key="ResultTemplate" DataType="{x:Type local:ResultItemViewModel}">
    <Grid Height="48">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="42" />
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
            <ColumnDefinition Width="30" />
        </Grid.ColumnDefinitions>
        <Image Source="{Binding Icon}" Width="32" Height="32" HorizontalAlignment="Left" VerticalAlignment="Center"
               RenderOptions.BitmapScalingMode="HighQuality" />
        <StackPanel Grid.Column="1" VerticalAlignment="Center">
            <TextBlock local:HighlightText.Item="{Binding}" FontSize="14" TextTrimming="CharacterEllipsis" />
            <TextBlock x:Name="Sub" Text="{Binding Subtitle}" FontFamily="{DynamicResource Font.Small}" FontSize="11.5"
                       Margin="0,1,0,0" Foreground="{DynamicResource Brush.SubText}" TextTrimming="CharacterEllipsis" />
        </StackPanel>
        <StackPanel Grid.Column="2" VerticalAlignment="Center" Margin="12,0,8,0">
            <Border x:Name="BadgePill" HorizontalAlignment="Right" CornerRadius="4" Padding="6,1"
                    Background="{DynamicResource Brush.AccentSoft}">
                <TextBlock Text="{Binding Badge}" FontFamily="{DynamicResource Font.Small}" FontSize="10.5"
                           FontWeight="SemiBold" Foreground="{DynamicResource Brush.Accent}" />
            </Border>
            <TextBlock x:Name="Det" Text="{Binding Details}" FontFamily="{DynamicResource Font.Numeric}" FontSize="11"
                       Margin="0,2,0,0" HorizontalAlignment="Right" Foreground="{DynamicResource Brush.TextFaint}" />
        </StackPanel>
        <Border x:Name="Cap" Grid.Column="3" Style="{StaticResource Hk.Keycap}" HorizontalAlignment="Right" VerticalAlignment="Center">
            <TextBlock x:Name="CapText" Text="{Binding IndexKey}" Style="{StaticResource Hk.KeycapText}" />
        </Border>
    </Grid>
    <DataTemplate.Triggers>
        <DataTrigger Binding="{Binding Subtitle}" Value="{x:Null}"><Setter TargetName="Sub" Property="Visibility" Value="Collapsed" /></DataTrigger>
        <DataTrigger Binding="{Binding Badge}" Value="{x:Null}"><Setter TargetName="BadgePill" Property="Visibility" Value="Collapsed" /></DataTrigger>
        <DataTrigger Binding="{Binding Details}" Value="{x:Null}"><Setter TargetName="Det" Property="Visibility" Value="Collapsed" /></DataTrigger>
        <DataTrigger Binding="{Binding Details}" Value=""><Setter TargetName="Det" Property="Visibility" Value="Collapsed" /></DataTrigger>
        <DataTrigger Binding="{Binding IndexKey}" Value=""><Setter TargetName="Cap" Property="Visibility" Value="Hidden" /></DataTrigger>
        <DataTrigger Binding="{Binding IsSelected, RelativeSource={RelativeSource AncestorType=ListBoxItem}}" Value="True">
            <Setter TargetName="Cap" Property="Background" Value="{DynamicResource Brush.AccentFill}" />
            <Setter TargetName="Cap" Property="BorderBrush" Value="{DynamicResource Brush.AccentFillEdge}" />
            <Setter TargetName="Cap" Property="BorderThickness" Value="1,2,1,1" />
            <Setter TargetName="CapText" Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
        </DataTrigger>
    </DataTemplate.Triggers>
</DataTemplate>
```

`ResultList` 保持原有属性，只改两处：`MaxHeight="432"`，外层 Grid `Margin="0,8,0,6"`。虚拟化（`Recycling`、`ScrollUnit=Item`）保持不变。

**ViewModel 补充（`LauncherModels.cs`）：**
```csharp
public string IndexKey => Index < 9 ? (Index + 1).ToString() : "";   // 原 Shortcut 保留不删
```

**匹配高亮（`HighlightText.cs`）：** 在已有的 `run.FontWeight` 和 `Foreground` 两行之后，加一行：
```csharp
run.SetResourceReference(TextElement.BackgroundProperty, "Brush.HighlightBg");
```
Run 的背景是按行高绘制的矩形，效果是“荧光笔”，成本与现在相同。

### 5.7 动作菜单（ActionPanel）

- `ActionPanel` 的 `Background` 改为 `Transparent`：动作模式下 ResultList 已被代码设为 Hidden，不需要遮挡。
- 头部：左侧是返回链接，右侧是当前目标的标题。

```xml
<StackPanel x:Name="ActionPanel" Visibility="{Binding ActionMode, Converter={StaticResource BoolToVis}}">
    <StackPanel.RenderTransform><TranslateTransform x:Name="ActionShift" /></StackPanel.RenderTransform>
    <Grid Margin="20,2,20,6">
        <TextBlock Text="{DynamicResource S.Launcher.Back}" FontSize="12" Padding="0,2" Cursor="Hand"
                   Background="Transparent" MouseLeftButtonUp="OnBackClick" HorizontalAlignment="Left">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Setter Property="Foreground" Value="{DynamicResource Brush.SubText}" />
                    <Style.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter Property="Foreground" Value="{DynamicResource Brush.Accent}" />
                        </Trigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
        <TextBlock Text="{Binding Selected.Title}" HorizontalAlignment="Right" MaxWidth="460" FontWeight="SemiBold"
                   FontSize="12" TextTrimming="CharacterEllipsis" Foreground="{DynamicResource Brush.Text}" />
    </Grid>
    <ListBox x:Name="ActionList" …属性全部同前… />
</StackPanel>
```
> `LauncherViewModel.SelectedIndex` 的 setter 目前不会通知 `Selected`，需要补一行 `OnPropertyChanged(nameof(Selected))`。

```xml
<DataTemplate x:Key="ActionTemplate" DataType="{x:Type local:ActionItemViewModel}">
    <Grid Height="40">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="40" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <Border Width="28" Height="28" CornerRadius="7" HorizontalAlignment="Left" BorderThickness="1"
                Background="{DynamicResource Brush.KeyFace}" BorderBrush="{DynamicResource Brush.KeyEdge}">
            <TextBlock Text="{Binding Glyph}" FontFamily="{DynamicResource Font.Icon}" FontSize="14"
                       Foreground="{DynamicResource Brush.Accent}" HorizontalAlignment="Center" VerticalAlignment="Center" />
        </Border>
        <TextBlock Grid.Column="1" Text="{Binding Name}" FontSize="13.5" VerticalAlignment="Center" />
        <ItemsControl Grid.Column="2" VerticalAlignment="Center" Focusable="False"
                      ItemsSource="{Binding Shortcut, Converter={StaticResource KeyChord}}"
                      ItemTemplate="{StaticResource Hk.KeyTemplate}" ItemsPanel="{StaticResource Hk.HPanel}" />
    </Grid>
</DataTemplate>
```
ActionList 继续使用 `ResultItemStyle`，所以同样有弧光指示条。

### 5.8 空状态

把 `EmptyHint` 包进一个居中的块里。`x:Name="EmptyHint"` 保留在 TextBlock 上，code-behind 没有引用它，可见性绑定挪到外层：

```xml
<StackPanel Margin="0,24,0,28" HorizontalAlignment="Center"
            Visibility="{Binding ShowEmptyHint, Converter={StaticResource BoolToVis}}">
    <Path Width="46" Height="30" Stretch="Uniform" HorizontalAlignment="Center"
          Data="M94,132 Q128,84 162,132 M146,128 L162,134 L168,116" Stroke="{DynamicResource Brush.Hop}"
          StrokeThickness="3" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" />
    <TextBlock x:Name="EmptyHint" Text="{DynamicResource S.Launcher.EmptyHint}" Margin="0,14,0,0"
               HorizontalAlignment="Center" Foreground="{DynamicResource Brush.SubText}" />
</StackPanel>
```

### 5.9 底栏（Row 4）

```xml
<Border Grid.Row="4" Height="34" Padding="14,0" Background="{DynamicResource Brush.Footer}"
        BorderBrush="{DynamicResource Brush.Divider}" BorderThickness="0,1,0,0">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
            <Ellipse Width="6" Height="6" Margin="0,0,8,0" VerticalAlignment="Center">
                <Ellipse.Style>
                    <Style TargetType="Ellipse">
                        <Setter Property="Fill" Value="{DynamicResource Brush.Success}" />
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding IndexState}" Value="Loading"><Setter Property="Fill" Value="{DynamicResource Brush.Warning}" /></DataTrigger>
                            <DataTrigger Binding="{Binding IndexState}" Value="Building"><Setter Property="Fill" Value="{DynamicResource Brush.Warning}" /></DataTrigger>
                            <DataTrigger Binding="{Binding IndexState}" Value="Paused"><Setter Property="Fill" Value="{DynamicResource Brush.TextFaint}" /></DataTrigger>
                            <DataTrigger Binding="{Binding IndexError}" Value="True"><Setter Property="Fill" Value="{DynamicResource Brush.Error}" /></DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Ellipse.Style>
            </Ellipse>
            <TextBlock x:Name="StatusText" Text="{Binding IndexStatus}" FontFamily="{DynamicResource Font.Small}" FontSize="11.5"
                       VerticalAlignment="Center" Foreground="{DynamicResource Brush.SubText}" MouseLeftButtonUp="OnStatusClick" />
        </StackPanel>
        <Border x:Name="MessagePill" Grid.Column="1" HorizontalAlignment="Left" VerticalAlignment="Center" Margin="12,0"
                CornerRadius="10" Padding="8,1" Visibility="{Binding HasMessage, Converter={StaticResource BoolToVis}}">
            <Border.Style>
                <Style TargetType="Border">
                    <Setter Property="Background" Value="{DynamicResource Brush.AccentSoft}" />
                    <Setter Property="TextElement.Foreground" Value="{DynamicResource Brush.Accent}" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding MessageIsError}" Value="True">
                            <Setter Property="Background" Value="{DynamicResource Brush.ErrorSoft}" />
                            <Setter Property="TextElement.Foreground" Value="{DynamicResource Brush.Error}" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </Border.Style>
            <TextBlock Text="{Binding Message}" FontSize="11.5" TextTrimming="CharacterEllipsis" />
        </Border>
        <ItemsControl Grid.Column="2" ItemsSource="{Binding KeyHintItems}" VerticalAlignment="Center" Focusable="False"
                      ItemsPanel="{StaticResource Hk.HPanel}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <StackPanel Orientation="Horizontal" Margin="14,0,0,0">
                        <ItemsControl ItemsSource="{Binding Keys}" ItemTemplate="{StaticResource Hk.KeyTemplateSmall}"
                                      ItemsPanel="{StaticResource Hk.HPanel}" Focusable="False" />
                        <TextBlock Text="{Binding Label}" Margin="3,0,0,0" FontFamily="{DynamicResource Font.Small}" FontSize="11.5"
                                   VerticalAlignment="Center" Foreground="{DynamicResource Brush.SubText}" />
                    </StackPanel>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </Grid>
</Border>
```

**ViewModel 补充（`LauncherViewModel.cs`）：**
```csharp
public sealed record KeyHint(string[] Keys, string Label);
private IReadOnlyList<KeyHint>? _keyHintItems;
public IReadOnlyList<KeyHint> KeyHintItems => _keyHintItems ??= ParseHints(KeyHints);
// "Enter 打开 · Ctrl+Enter 打开位置 · Tab 更多操作" → [Enter|打开] [Ctrl,Enter|打开位置] [Tab|更多操作]
private static IReadOnlyList<KeyHint> ParseHints(string s) => s.Split(" · ").Select(p =>
{ int i = p.IndexOf(' '); return i < 0 ? new KeyHint(p.Split('+'), "") : new KeyHint(p[..i].Split('+'), p[(i + 1)..]); }).ToArray();

private IndexState _indexState;
public IndexState IndexState { get => _indexState; private set => Set(ref _indexState, value); }
```
- 语言切换时，现在在第 151 行 `OnPropertyChanged(nameof(KeyHints))`。在它旁边置 `_keyHintItems = null;`，并通知 `KeyHintItems`。
- 在 `UpdateIndexStatus` 中写入 `IndexState = s.State;`。
- 不新增本地化字符串。英文串 "Enter open · Ctrl+Enter location · Tab actions" 同样可以解析。
- code-behind 对 `StatusText.Foreground` 的错误态处理保持不变。

---

## 6. 共享资源字典

### 6.1 文件划分（性能考虑）

| 文件 | 合并到 | 内容 |
|---|---|---|
| `src/Hopkey.App/Themes/Controls.xaml`（新，小） | `App.xaml` 的 `Application.Resources.MergedDictionaries`；工具条在 `ApplyResources()` 里每线程 `new ResourceDictionary { Source = new Uri("pack://application:,,,/Hopkey;component/Themes/Controls.xaml") }` 单独加载 | `Hk.Keycap`、`Hk.KeycapText`、`Hk.KeyTemplate(Small)`、`Hk.HPanel`、`Hk.BareTextBox`、`KeyChord` 转换器、`Img.Logo`、隐式 `ScrollBar`、隐式 `ToolTip`、`Hk.ContextMenu`、`Hk.MenuItem`、菜单 `Separator` |
| `src/Hopkey.App/Themes/SettingsControls.xaml`（新） | **只合并进 `SettingsWindow.Resources`**，不进 App，避免拖慢启动器启动，也避免隐式 TextBox 样式影响 QueryBox | 隐式 CheckBox(Toggle)、Button、TextBox、ComboBox(+Item)、RadioButton(Segment)、DataGrid 系列、ListBox、TabControl/TabItem 导航、`Hk.Section` 等 |

`ThemeManager.ReplaceDictionary` 把主题字典插在 index 0，Controls.xaml 位于其后。两者互不冲突，样式里一律用 `DynamicResource` 引用画刷。

### 6.2 键帽与转换器（Controls.xaml）

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:hk="clr-namespace:Hopkey.App.Infrastructure">
    <hk:KeyChordConverter x:Key="KeyChord" />
    <ItemsPanelTemplate x:Key="Hk.HPanel"><StackPanel Orientation="Horizontal" /></ItemsPanelTemplate>

    <Style x:Key="Hk.Keycap" TargetType="Border">
        <Setter Property="MinWidth" Value="20" />
        <Setter Property="Height" Value="20" />
        <Setter Property="Padding" Value="5,0" />
        <Setter Property="CornerRadius" Value="5" />
        <Setter Property="BorderThickness" Value="1,1,1,2" />
        <Setter Property="Background" Value="{DynamicResource Brush.KeyFace}" />
        <Setter Property="BorderBrush" Value="{DynamicResource Brush.KeyEdge}" />
        <Setter Property="SnapsToDevicePixels" Value="True" />
    </Style>
    <Style x:Key="Hk.KeycapText" TargetType="TextBlock">
        <Setter Property="FontFamily" Value="{DynamicResource Font.Mono}" />
        <Setter Property="FontSize" Value="10.5" />
        <Setter Property="Foreground" Value="{DynamicResource Brush.KeyText}" />
        <Setter Property="HorizontalAlignment" Value="Center" />
        <Setter Property="VerticalAlignment" Value="Center" />
    </Style>
    <DataTemplate x:Key="Hk.KeyTemplate">
        <Border Style="{StaticResource Hk.Keycap}" Margin="0,0,3,0">
            <TextBlock Text="{Binding}" Style="{StaticResource Hk.KeycapText}" />
        </Border>
    </DataTemplate>
    <DataTemplate x:Key="Hk.KeyTemplateSmall">
        <Border Style="{StaticResource Hk.Keycap}" Height="18" MinWidth="18" Padding="4,0" Margin="0,0,3,0">
            <TextBlock Text="{Binding}" Style="{StaticResource Hk.KeycapText}" FontSize="10" />
        </Border>
    </DataTemplate>
    …
</ResourceDictionary>
```

`src/Hopkey.App/Infrastructure/KeyChordConverter.cs`：
```csharp
public sealed class KeyChordConverter : IValueConverter
{
    public object Convert(object? v, Type t, object? p, CultureInfo c) =>
        v is string s && s.Length > 0 ? s.Split('+') : Array.Empty<string>();
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
```

### 6.3 品牌 Logo（矢量，Controls.xaml）

```xml
<DrawingImage x:Key="Img.Logo">
    <DrawingImage.Drawing>
        <DrawingGroup>
            <GeometryDrawing Geometry="M56,0 H200 A56,56 0 0 1 256,56 V200 A56,56 0 0 1 200,256 H56 A56,56 0 0 1 0,200 V56 A56,56 0 0 1 56,0 Z">
                <GeometryDrawing.Brush>
                    <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                        <GradientStop Color="#6366F1" Offset="0" /><GradientStop Color="#4338CA" Offset="1" />
                    </LinearGradientBrush>
                </GeometryDrawing.Brush>
            </GeometryDrawing>
            <DrawingGroup>
                <DrawingGroup.Children>
                    <GeometryDrawing Brush="#A5B4FC" Geometry="M88,52 H168 A30,30 0 0 1 198,82 V154 A30,30 0 0 1 168,184 H88 A30,30 0 0 1 58,154 V82 A30,30 0 0 1 88,52 Z" />
                    <GeometryDrawing Brush="#FFFFFF" Geometry="M94,56 H162 A24,24 0 0 1 186,80 V136 A24,24 0 0 1 162,160 H94 A24,24 0 0 1 70,136 V80 A24,24 0 0 1 94,56 Z" />
                    <GeometryDrawing Geometry="M94,132 Q128,84 162,132 M146,128 L162,134 L168,116">
                        <GeometryDrawing.Pen><Pen Brush="#4F46E5" Thickness="14" StartLineCap="Round" EndLineCap="Round" LineJoin="Round" /></GeometryDrawing.Pen>
                    </GeometryDrawing>
                </DrawingGroup.Children>
                <DrawingGroup.Transform><RotateTransform Angle="-9" CenterX="128" CenterY="120" /></DrawingGroup.Transform>
            </DrawingGroup>
        </DrawingGroup>
    </DrawingImage.Drawing>
</DrawingImage>
```
（画法与 `assets/icon/hopkey.svg` 一致，省略了两个阴影椭圆。）

### 6.4 弹出层圆角：`PopupChrome` 附加属性（新，`Infrastructure/PopupChrome.cs`）

ContextMenu、ComboBox 下拉、ToolTip 一律 **不用 AllowsTransparency**，模板内 `CornerRadius=0`，由 DWM 给弹出层的 HWND 加圆角、阴影和边框色：

```csharp
public static class PopupChrome
{
    public static readonly DependencyProperty RoundedProperty = DependencyProperty.RegisterAttached(
        "Rounded", typeof(bool), typeof(PopupChrome), new PropertyMetadata(false, OnChanged));
    public static void SetRounded(DependencyObject d, bool v) => d.SetValue(RoundedProperty, v);
    public static bool GetRounded(DependencyObject d) => (bool)d.GetValue(RoundedProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        switch (d)
        {
            case Popup p: p.Opened += (_, _) => Apply(p.Child); break;
            case ContextMenu m: m.Opened += (_, _) => Apply(m); break;
            case ToolTip t: t.Opened += (_, _) => Apply(t); break;
        }
    }
    private static void Apply(Visual? v)
    {
        if (v == null || PresentationSource.FromVisual(v) is not HwndSource src) return;
        Dwmapi.SetRoundCorners(src.Handle);
        Dwmapi.SetColor(src.Handle, Dwmapi.DWMWA_BORDER_COLOR, ThemeManager.IsDark ? 0x005F3134u : 0x00ECD1D4u);
    }
}
```

### 6.5 菜单样式（工具条的弹出菜单；Controls.xaml）

```xml
<Style x:Key="Hk.ContextMenu" TargetType="ContextMenu">
    <Setter Property="hk:PopupChrome.Rounded" Value="True" />
    <Setter Property="HasDropShadow" Value="False" />
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="FontFamily" Value="{DynamicResource Font.Family}" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="ContextMenu">
                <Border Background="{DynamicResource Brush.Card}" Padding="4" MinWidth="220">
                    <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle" />
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<Style x:Key="Hk.MenuItem" TargetType="MenuItem">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="MenuItem">
                <Grid x:Name="Root" MinHeight="32" Margin="0,1">
                    <Border x:Name="Bd" CornerRadius="6" Background="Transparent" />
                    <Rectangle x:Name="Bar" Width="3" Height="16" RadiusX="1.5" RadiusY="1.5" HorizontalAlignment="Left"
                               Margin="2,0,0,0" Fill="{DynamicResource Brush.HopV}" Visibility="Collapsed" />
                    <Grid Margin="10,6,12,6">
                        <Grid.ColumnDefinitions><ColumnDefinition Width="26" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
                        <ContentPresenter ContentSource="Icon" VerticalAlignment="Center" />
                        <ContentPresenter Grid.Column="1" ContentSource="Header" VerticalAlignment="Center" RecognizesAccessKey="True" />
                    </Grid>
                </Grid>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Hover}" /></Trigger>
                    <Trigger Property="IsChecked" Value="True">
                        <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Selected}" />
                        <Setter TargetName="Bar" Property="Visibility" Value="Visible" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Root" Property="Opacity" Value="0.4" /></Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

<Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator">
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Separator">
        <Rectangle Height="1" Margin="10,4" Fill="{DynamicResource Brush.Divider}" />
    </ControlTemplate></Setter.Value></Setter>
</Style>

<Style TargetType="ToolTip">
    <Setter Property="hk:PopupChrome.Rounded" Value="True" />
    <Setter Property="HasDropShadow" Value="False" />
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToolTip">
        <Border Background="{DynamicResource Brush.Card}" Padding="10,6" MaxWidth="420">
            <ContentPresenter TextElement.Foreground="{DynamicResource Brush.Text}" TextElement.FontSize="12" />
        </Border>
    </ControlTemplate></Setter.Value></Setter>
</Style>
```
`QuickSwitchToolbar.CreateMenu()` 改为 `menu.SetResourceReference(StyleProperty, "Hk.ContextMenu")`，删掉手写的 Background/Foreground/BorderBrush；`MakeItem()` 中对每个 `MenuItem` 设 `item.SetResourceReference(StyleProperty, "Hk.MenuItem")`。代码创建的菜单不在逻辑树里，不能依赖隐式样式。

### 6.6 滚动条（隐式，Controls.xaml）

```xml
<Style x:Key="Hk.ScrollThumb" TargetType="Thumb">
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb">
        <Border x:Name="B" CornerRadius="3" Margin="3" Background="{DynamicResource Brush.ScrollThumb}" />
        <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="B" Property="Margin" Value="2" />
                <Setter TargetName="B" Property="Background" Value="{DynamicResource Brush.ScrollThumbHover}" />
            </Trigger>
            <Trigger Property="IsDragging" Value="True">
                <Setter TargetName="B" Property="Background" Value="{DynamicResource Brush.Accent}" />
            </Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
</Style>
<Style TargetType="ScrollBar">
    <Setter Property="Width" Value="10" /><Setter Property="MinWidth" Value="10" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
        <Track x:Name="PART_Track" IsDirectionReversed="True">
            <Track.Thumb><Thumb Style="{StaticResource Hk.ScrollThumb}" /></Track.Thumb>
        </Track>
    </ControlTemplate></Setter.Value></Setter>
    <Style.Triggers>
        <Trigger Property="Orientation" Value="Horizontal">
            <Setter Property="Width" Value="Auto" /><Setter Property="MinWidth" Value="0" />
            <Setter Property="Height" Value="10" /><Setter Property="MinHeight" Value="10" />
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
                <Track x:Name="PART_Track" IsDirectionReversed="False">
                    <Track.Thumb><Thumb Style="{StaticResource Hk.ScrollThumb}" /></Track.Thumb>
                </Track>
            </ControlTemplate></Setter.Value></Setter>
        </Trigger>
    </Style.Triggers>
</Style>
```
滑块默认 4px 宽，悬停时 6px，没有箭头按钮。点击轨道空白处不翻页，这是有意的简化。

---

## 7. QuickSwitch 工具条

**布局：** 高 40，包括顶部 2px 弧光边、4px 内边距、30px 按钮。DWM 8px 圆角，DWM 边框色，`EnableShadow`，Window `BorderThickness=0`。

```
╭═══════════ 2px Brush.Hop ═══════════╮
│ [⤴] [★ Downloads] │ [▣] [☆] [⟲] [⋯] [!] ◌ │
╰──────────────────────────────────────╯
```

```xml
<Window … Background="{DynamicResource Brush.Card}" BorderThickness="0" FontFamily="{DynamicResource Font.Family}" FontSize="12" …>
  <Window.Resources>
    <Style x:Key="TbButton" TargetType="Button">
      <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
      <Setter Property="Background" Value="Transparent" />
      <Setter Property="Height" Value="30" /><Setter Property="MinWidth" Value="30" />
      <Setter Property="Margin" Value="1,0" /><Setter Property="Padding" Value="6,0" />
      <Setter Property="Focusable" Value="False" /><Setter Property="Cursor" Value="Hand" />
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
        <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="7" Padding="{TemplateBinding Padding}">
          <Border.RenderTransform><TranslateTransform x:Name="Press" /></Border.RenderTransform>
          <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Hover}" /></Trigger>
          <Trigger Property="IsPressed" Value="True">
            <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Selected}" />
            <Setter TargetName="Press" Property="Y" Value="1" />
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="Glyph" TargetType="TextBlock"> <!-- 同前，FontFamily 改 {DynamicResource Font.Icon} --> </Style>
  </Window.Resources>
  <Grid>
    <Rectangle Height="2" VerticalAlignment="Top" Fill="{DynamicResource Brush.Hop}" IsHitTestVisible="False" />
    <Border Padding="4,6,4,4">
      <StackPanel Orientation="Horizontal">
        <!-- 搜索：品牌主按钮 -->
        <Button x:Name="SearchButton" Style="{StaticResource TbButton}" Background="{DynamicResource Brush.AccentSoft}"
                ToolTip="{DynamicResource S.QS.Search}" Click="OnSearch">
          <Path Width="16" Height="11" Stretch="Uniform" Data="M94,132 Q128,84 162,132 M146,128 L162,134 L168,116"
                Stroke="{DynamicResource Brush.Hop}" StrokeThickness="2" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" />
        </Button>
        <Button x:Name="RecommendButton" Style="{StaticResource TbButton}" ToolTip="{DynamicResource S.QS.Recommended}" Click="OnRecommend" Visibility="Collapsed">
          <StackPanel Orientation="Horizontal">
            <TextBlock Style="{StaticResource Glyph}" FontSize="13" Text="&#xE8B7;" Foreground="{DynamicResource Brush.Accent}" />
            <TextBlock x:Name="RecommendText" Margin="6,0,0,0" MaxWidth="200" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
          </StackPanel>
        </Button>
        <Rectangle Width="1" Height="18" Margin="4,0" Fill="{DynamicResource Brush.Divider}" VerticalAlignment="Center" />
        <!-- ExplorerButton / FavoritesButton / RecentButton / MoreButton：同前，仅依赖新 TbButton -->
        <Button x:Name="WarningButton" Style="{StaticResource TbButton}" Background="{DynamicResource Brush.WarningSoft}"
                ToolTip="{DynamicResource S.QS.ElevatedHint}" Click="OnWarning" Visibility="Collapsed">
          <TextBlock Style="{StaticResource Glyph}" Text="&#xE7BA;" Foreground="{DynamicResource Brush.Warning}" />
        </Button>
        <Grid x:Name="BusyIndicator" Width="16" Height="16" Margin="6,0" Visibility="Collapsed" VerticalAlignment="Center">
          <Ellipse Stroke="{DynamicResource Brush.Divider}" StrokeThickness="2" />
          <Ellipse x:Name="Spinner" Stroke="{DynamicResource Brush.Hop}" StrokeThickness="2" StrokeDashArray="6 20" RenderTransformOrigin="0.5,0.5">
            <Ellipse.RenderTransform><RotateTransform x:Name="SpinnerRotate" /></Ellipse.RenderTransform>
          </Ellipse>
        </Grid>
      </StackPanel>
    </Border>
  </Grid>
</Window>
```
- SearchButton 的悬停态会被模板触发器盖成 `Hover`，这是可以接受的。
- 推荐按钮去掉内层的 `Selected` 胶囊，按钮本身就是胶囊。
- `FlashError` 逻辑不变：把 Window.Background 临时设为 `Brush.Error`。
- `ApplyResources()`：在 `ReplaceDictionary` 后面加载 Controls.xaml 的新实例（每个线程只加载一次，用字段缓存“已加载”），然后刷新 DWM 边框色和暗色模式。

---

## 8. 设置窗口

### 8.1 整体布局

- 窗口 900×640，`MinWidth=720`、`MinHeight=500`，保留标准标题栏。标题栏经 DWM 着色成 `Brush.Background` 的颜色，与左侧导航连成一体。
- `Window.Background = Brush.Background`。
- 左侧是 232px 品牌导航栏；右侧内容页 `Brush.Surface`，只有左上角是 12px 圆角，带 1px 的左边和上边 Divider，像一张纸压在导航栏上。
- 底部原有的“已保存”状态条（Grid.Row=1）删除，改为内容页右下角的浮动胶囊。

```xml
<Window.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="/Hopkey;component/Themes/SettingsControls.xaml" />
        </ResourceDictionary.MergedDictionaries>
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
        <!-- 原 Header/SubHeader/Hint 样式改名为 Hk.PageTitle/Hk.SectionTitle/Hk.Hint 并移入 SettingsControls.xaml；保留旧 key 作为 BasedOn 别名 -->
    </ResourceDictionary>
</Window.Resources>
<Grid>
    <TabControl x:Name="Tabs" Style="{StaticResource Hk.NavTabs}"> …TabItem… </TabControl>
    <Border HorizontalAlignment="Right" VerticalAlignment="Bottom" Margin="0,0,24,20" CornerRadius="14" Padding="12,5"
            Background="{DynamicResource Brush.AccentSoft}" IsHitTestVisible="False">
        <Border.Style><Style TargetType="Border">
            <Style.Triggers>
                <DataTrigger Binding="{Binding SavedText}" Value=""><Setter Property="Visibility" Value="Collapsed" /></DataTrigger>
                <DataTrigger Binding="{Binding SavedText}" Value="{x:Null}"><Setter Property="Visibility" Value="Collapsed" /></DataTrigger>
            </Style.Triggers>
        </Style></Border.Style>
        <StackPanel Orientation="Horizontal">
            <TextBlock Text="&#xE73E;" FontFamily="{DynamicResource Font.Icon}" FontSize="12" Foreground="{DynamicResource Brush.Accent}" VerticalAlignment="Center" />
            <TextBlock Text="{Binding SavedText}" Margin="6,0,0,0" Foreground="{DynamicResource Brush.Accent}" />
        </StackPanel>
    </Border>
</Grid>
```

### 8.2 导航（TabControl + TabItem；SettingsControls.xaml）

每个 TabItem 加 `Tag` 作为图标字形：常规 `&#xE713;`、QuickSwitch `&#xE8AB;`、索引与排除 `&#xE71C;`、收藏 `&#xE734;`、网页搜索 `&#xE774;`、关于 `&#xE946;`。

```xml
<Style x:Key="Hk.NavTabs" TargetType="TabControl">
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabControl">
        <Grid>
            <Grid.ColumnDefinitions><ColumnDefinition Width="232" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
            <DockPanel Margin="12,14,12,14">
                <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="10,0,0,20">
                    <Image Source="{StaticResource Img.Logo}" Width="28" Height="28" />
                    <TextBlock Text="Hopkey" Margin="10,0,0,0" FontFamily="{DynamicResource Font.Numeric}" FontSize="18"
                               FontWeight="SemiBold" VerticalAlignment="Center" Foreground="{DynamicResource Brush.Text}" />
                </StackPanel>
                <TextBlock DockPanel.Dock="Bottom" Text="{Binding Version}" Margin="12,0" FontFamily="{DynamicResource Font.Mono}"
                           FontSize="11" Foreground="{DynamicResource Brush.TextFaint}" />
                <StackPanel IsItemsHost="True" />
            </DockPanel>
            <Border Grid.Column="1" Background="{DynamicResource Brush.Surface}" CornerRadius="12,0,0,0"
                    BorderBrush="{DynamicResource Brush.Divider}" BorderThickness="1,1,0,0">
                <ContentPresenter x:Name="PART_SelectedContentHost" ContentSource="SelectedContent" />
            </Border>
        </Grid>
    </ControlTemplate></Setter.Value></Setter>
</Style>

<Style TargetType="TabItem">
    <Setter Property="Foreground" Value="{DynamicResource Brush.SubText}" />
    <Setter Property="FocusVisualStyle" Value="{x:Null}" />
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem">
        <Grid Height="38" Margin="0,1" Background="Transparent">
            <Border x:Name="Bd" CornerRadius="8" Background="Transparent" />
            <Rectangle x:Name="Bar" Width="3" Height="18" RadiusX="1.5" RadiusY="1.5" HorizontalAlignment="Left"
                       Fill="{DynamicResource Brush.HopV}" Opacity="0" />
            <StackPanel Orientation="Horizontal" Margin="14,0,10,0" VerticalAlignment="Center">
                <TextBlock x:Name="Ico" Text="{TemplateBinding Tag}" FontFamily="{DynamicResource Font.Icon}" FontSize="16"
                           Width="20" VerticalAlignment="Center" Foreground="{TemplateBinding Foreground}" />
                <ContentPresenter ContentSource="Header" Margin="12,0,0,0" VerticalAlignment="Center" TextElement.FontSize="13.5" />
            </StackPanel>
        </Grid>
        <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Hover}" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
            </Trigger>
            <Trigger Property="IsSelected" Value="True">
                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Selected}" />
                <Setter TargetName="Bar" Property="Opacity" Value="1" />
                <Setter TargetName="Ico" Property="Foreground" Value="{DynamicResource Brush.Accent}" />
                <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
            </Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
</Style>
```

### 8.3 页面与分组卡片

- 每页：`ScrollViewer Padding="32,28,32,32"`，内容 `MaxWidth="760" HorizontalAlignment="Left"`。
- 页标题：`Hk.PageTitle`（Font.Display 26 SemiBold，下边距 20）。
- 分组卡片 `Hk.Section`：`Border Background=Brush.Card, BorderBrush=Brush.Border, BorderThickness=1, CornerRadius=10, Padding=20,16, Margin=0,0,0,12`。卡片标题 `Hk.SectionTitle`（14 SemiBold，下边距 10）。
- 行模式 `Hk.Row`：`Grid MinHeight=40`，左列是标签（13 Text），右列 Auto 放控件（ComboBox 宽 220）。行与行之间用 1px `Divider` 分隔，左右各缩进 -20 贴边。
- 子选项缩进：开关宽 40 加间距 12，所以子项 `Margin="52,0,0,0"`（原来是 22）。

**分组（不新增字符串，标题都复用现有 SubHeader 文案）：**

| 页 | 卡片 |
|---|---|
| 常规 | ①（无标题）开机启动、管理员启动（子项）②行：语言 / 主题 ③“唤起热键”：分段选择 [双击 Ctrl / 组合键] + HotkeyBox；备用热键行；提示；错误；类型即搜开关 ④“搜索”：拼音、历史加权、最大结果数行 + 清除历史按钮、详情三开关（WrapPanel）、排序行、提示 ⑤“权限”：说明 + `ElevateButton`（Primary） |
| QuickSwitch | ①主开关 QsEnabled ②（`IsEnabled=QsEnabled`）工具条、跟随、自动跳转 + 最大时长、Dialog Jump + HotkeyBox ③禁用应用（多行 TextBox）④自动排除应用 |
| 索引 | ①索引范围：全部驱动器、RootsList、添加/移除 ②USN、应用、UWP ③状态：文字 + 重建（Primary）④排除：两个开关、RulesGrid、错误、按钮组 |
| 收藏 / 网页搜索 | 页标题 + 一张撑满剩余高度的卡片（Padding 0，内放 DataGrid）+ 下方按钮组 |
| 关于 | 首卡：Logo 64 + “Hopkey” 字标 30 + 版本（Mono）+ 两个按钮；状态卡；性能卡（`PerfSummary` 用 Font.Mono 11）；许可说明 |

DataGrid 的 CheckBox 列加 `ElementStyle="{StaticResource Hk.GridCheck}" EditingElementStyle="{StaticResource Hk.GridCheck}"`。这类列默认显式设置了样式，不会吃到隐式的开关样式。

`SettingsWindow.PromptText` 新建的对话框：`w.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = … SettingsControls.xaml })`，OK 按钮 `ok.SetResourceReference(StyleProperty, "Hk.Button.Primary")`。

### 8.4 控件模板（SettingsControls.xaml）

**开关（CheckBox，隐式）**

```xml
<Style x:Key="Hk.Toggle" TargetType="CheckBox">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Margin" Value="0,6" />
    <Setter Property="Cursor" Value="Hand" />
    <Setter Property="FocusVisualStyle" Value="{x:Null}" />
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
        <Grid x:Name="Root" Background="Transparent">
            <Grid.ColumnDefinitions><ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
            <Border x:Name="Focus" Margin="-3" CornerRadius="13" BorderThickness="2" BorderBrush="{DynamicResource Brush.Focus}" Opacity="0" />
            <Border x:Name="Track" Width="40" Height="20" CornerRadius="10" BorderThickness="1" VerticalAlignment="Center"
                    Background="{DynamicResource Brush.ToggleTrack}" BorderBrush="{DynamicResource Brush.ToggleEdge}">
                <Ellipse x:Name="Knob" Width="12" Height="12" HorizontalAlignment="Left" Margin="3,0,0,0"
                         Fill="{DynamicResource Brush.ToggleKnob}">
                    <Ellipse.RenderTransform><TranslateTransform /></Ellipse.RenderTransform>
                </Ellipse>
            </Border>
            <ContentPresenter Grid.Column="1" Margin="12,0,0,0" VerticalAlignment="Center" RecognizesAccessKey="True" />
        </Grid>
        <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True">
                <Setter TargetName="Track" Property="BorderBrush" Value="{DynamicResource Brush.Accent}" />
            </Trigger>
            <Trigger Property="IsChecked" Value="True">
                <Setter TargetName="Track" Property="Background" Value="{DynamicResource Brush.AccentFill}" />
                <Setter TargetName="Track" Property="BorderBrush" Value="{DynamicResource Brush.AccentFill}" />
                <Setter TargetName="Knob" Property="Fill" Value="{DynamicResource Brush.AccentText}" />
                <Trigger.EnterActions><BeginStoryboard><Storyboard>
                    <DoubleAnimation Storyboard.TargetName="Knob" Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.X)"
                                     To="20" Duration="0:0:0.12"><DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut" /></DoubleAnimation.EasingFunction></DoubleAnimation>
                </Storyboard></BeginStoryboard></Trigger.EnterActions>
                <Trigger.ExitActions><BeginStoryboard><Storyboard>
                    <DoubleAnimation Storyboard.TargetName="Knob" Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.X)"
                                     To="0" Duration="0:0:0.12"><DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut" /></DoubleAnimation.EasingFunction></DoubleAnimation>
                </Storyboard></BeginStoryboard></Trigger.ExitActions>
            </Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Opacity" Value="1" /></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Root" Property="Opacity" Value="0.45" /></Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
</Style>
<Style TargetType="CheckBox" BasedOn="{StaticResource Hk.Toggle}" />
```
滑块行程：轨道内宽 38 − 左边距 3 − 滑块 12 − 右边距 3 = 20。`Focus` 环放在第 0 列，用负边距包住轨道。

**按钮：次要（隐式）与主要**

颜色写在样式触发器里，结构写在模板里，这样 Primary 用 BasedOn 就能覆盖颜色。

```xml
<Style x:Key="Hk.Button" TargetType="Button">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Background" Value="{DynamicResource Brush.KeyFace}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.KeyEdge}" />
    <Setter Property="BorderThickness" Value="1,1,1,2" />
    <Setter Property="Height" Value="32" /><Setter Property="MinWidth" Value="80" />
    <Setter Property="Padding" Value="14,0" /><Setter Property="Margin" Value="0,0,8,0" />
    <Setter Property="Cursor" Value="Hand" /><Setter Property="FocusVisualStyle" Value="{x:Null}" />
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
        <Grid x:Name="Root">
            <Border CornerRadius="7" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="{TemplateBinding BorderThickness}" SnapsToDevicePixels="True">
                <ContentPresenter Margin="{TemplateBinding Padding}" HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True" />
            </Border>
            <Border x:Name="Focus" Margin="-3" CornerRadius="9" BorderThickness="2" BorderBrush="{DynamicResource Brush.Focus}"
                    Opacity="0" IsHitTestVisible="False" />
        </Grid>
        <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="Opacity" Value="1" /></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Root" Property="Opacity" Value="0.45" /></Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
    <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource Brush.Hover}" /></Trigger>
        <Trigger Property="IsPressed" Value="True">
            <Setter Property="Background" Value="{DynamicResource Brush.Selected}" />
            <Setter Property="BorderThickness" Value="1,2,1,1" />
        </Trigger>
    </Style.Triggers>
</Style>
<Style TargetType="Button" BasedOn="{StaticResource Hk.Button}" />

<Style x:Key="Hk.Button.Primary" TargetType="Button" BasedOn="{StaticResource Hk.Button}">
    <Setter Property="Foreground" Value="{DynamicResource Brush.AccentText}" />
    <Setter Property="Background" Value="{DynamicResource Brush.AccentFill}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.AccentFillEdge}" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Style.Triggers>
        <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource Brush.AccentFillHover}" /></Trigger>
        <Trigger Property="IsPressed" Value="True">
            <Setter Property="Background" Value="{DynamicResource Brush.AccentFill}" />
            <Setter Property="BorderThickness" Value="1,2,1,1" />
        </Trigger>
    </Style.Triggers>
</Style>
```
用 Primary 的按钮：`ElevateButton`、“重建索引”、对话框 OK。其余都用默认的次要按钮。

**文本框（隐式）与 HotkeyBox**

```xml
<Style x:Key="Hk.TextBox" TargetType="TextBox">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Background" Value="{DynamicResource Brush.Input}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CaretBrush" Value="{DynamicResource Brush.Accent}" />
    <Setter Property="SelectionBrush" Value="{DynamicResource Brush.AccentFill}" />
    <Setter Property="Padding" Value="10,6" /><Setter Property="MinHeight" Value="32" />
    <Setter Property="VerticalContentAlignment" Value="Center" />
    <Setter Property="FocusVisualStyle" Value="{x:Null}" />
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
        <Grid x:Name="Root">
            <Border x:Name="Bd" CornerRadius="7" Background="{TemplateBinding Background}"
                    BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}">
                <ScrollViewer x:Name="PART_ContentHost" Margin="{TemplateBinding Padding}" Focusable="False"
                              VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
            </Border>
            <Rectangle x:Name="Underline" Height="2" Margin="6,0" VerticalAlignment="Bottom" RadiusX="1" RadiusY="1"
                       Fill="{DynamicResource Brush.Hop}" Opacity="0" IsHitTestVisible="False" />
        </Grid>
        <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.PlateEdge}" /></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True">
                <Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.PlateEdge}" />
                <Setter TargetName="Underline" Property="Opacity" Value="1" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Root" Property="Opacity" Value="0.45" /></Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
</Style>
<Style TargetType="TextBox" BasedOn="{StaticResource Hk.TextBox}" />

<!-- HotkeyBox = 一枚长键帽 -->
<Style TargetType="local:HotkeyBox" BasedOn="{StaticResource Hk.TextBox}">
    <Setter Property="Width" Value="180" /><Setter Property="HorizontalAlignment" Value="Left" />
    <Setter Property="FontFamily" Value="{DynamicResource Font.Mono}" />
    <Setter Property="Background" Value="{DynamicResource Brush.KeyFace}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.KeyEdge}" />
    <Setter Property="BorderThickness" Value="1,1,1,2" />
    <Setter Property="HorizontalContentAlignment" Value="Center" />
</Style>
```
不要在模板的 `PART_ContentHost` 上写死滚动条可见性。多行框的 `VerticalScrollBarVisibility="Auto"` 由 TextBoxBase 传给它。

**下拉框（隐式）**

```xml
<Style x:Key="Hk.ComboBox" TargetType="ComboBox">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Height" Value="32" />
    <Setter Property="FocusVisualStyle" Value="{x:Null}" />
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
        <Grid x:Name="Root">
            <ToggleButton Focusable="False" ClickMode="Press"
                          IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                    <Border x:Name="Bd" CornerRadius="7" BorderThickness="1"
                            Background="{DynamicResource Brush.Input}" BorderBrush="{DynamicResource Brush.Border}">
                        <TextBlock Text="&#xE70D;" FontFamily="{DynamicResource Font.Icon}" FontSize="10" Margin="0,0,12,0"
                                   HorizontalAlignment="Right" VerticalAlignment="Center" Foreground="{DynamicResource Brush.SubText}" />
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Hover}" /></Trigger>
                        <Trigger Property="IsChecked" Value="True"><Setter TargetName="Bd" Property="BorderBrush" Value="{DynamicResource Brush.AccentFill}" /></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate></ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Margin="12,0,32,0" VerticalAlignment="Center" IsHitTestVisible="False"
                              Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                              ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"
                              ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}" />
            <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" VerticalOffset="4"
                   AllowsTransparency="False" PopupAnimation="None" Focusable="False" hk:PopupChrome.Rounded="True">
                <Border MinWidth="{Binding ActualWidth, ElementName=Root}" MaxHeight="{TemplateBinding MaxDropDownHeight}"
                        Background="{DynamicResource Brush.Card}" Padding="4">
                    <ScrollViewer><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained" /></ScrollViewer>
                </Border>
            </Popup>
        </Grid>
        <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Root" Property="Opacity" Value="0.45" /></Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
</Style>
<Style TargetType="ComboBox" BasedOn="{StaticResource Hk.ComboBox}" />

<Style TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
        <Grid MinHeight="30">
            <Border x:Name="Bd" CornerRadius="6" Background="Transparent" />
            <Rectangle x:Name="Bar" Width="3" Height="14" RadiusX="1.5" RadiusY="1.5" HorizontalAlignment="Left" Margin="2,0,0,0"
                       Fill="{DynamicResource Brush.HopV}" Visibility="Collapsed" />
            <ContentPresenter Margin="12,0,10,0" VerticalAlignment="Center" />
        </Grid>
        <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource Brush.Hover}" /></Trigger>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="Bar" Property="Visibility" Value="Visible" /></Trigger>
        </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
</Style>
```
SettingsControls.xaml 的根节点需要声明 `xmlns:hk="clr-namespace:Hopkey.App.Infrastructure"` 和 `xmlns:local="clr-namespace:Hopkey.App.Settings"`。DataGrid 内的 `DataGridComboBoxColumn` 编辑态也会套用这个隐式样式，Height 32 对 36px 行高合适。

**分段选择（热键模式的两个 RadioButton）**

外面包一层 `Border Background=Brush.KeyFace, BorderBrush=Brush.KeyEdge, BorderThickness=1,1,1,2, CornerRadius=8, Padding=2`。RadioButton 用 `Hk.Segment`：模板是 `Border CornerRadius=6, Padding=12,5`，默认透明；悬停 `Hover`；`IsChecked` 时背景 `AccentFill`、前景 `AccentText`。不加 GroupName，现有注释说明了原因。

**DataGrid（简要）**

DataGrid：`Background=Transparent`，`RowBackground=Transparent`，`AlternatingRowBackground=Transparent`，`GridLinesVisibility=Horizontal`，`HorizontalGridLinesBrush=Brush.Divider`，`RowHeight=36`，`BorderThickness=0`。DataGrid 放在 `Hk.Section` 卡片里。

- `DataGridColumnHeader`：`Background=Transparent`，`Foreground=Brush.SubText`，`FontSize=12`，`FontWeight=SemiBold`，`Padding=10,8`，底边 1px `Divider`。
- `DataGridCell`：模板为 `Border Background={TemplateBinding Background} Padding=10,0` + `ContentPresenter VerticalAlignment=Center`。选中时 `Background=Brush.Selected`、`Foreground=Brush.Text`，取消系统蓝；`FocusVisualStyle=null`。
- `DataGridRow`：悬停 `Brush.Hover`。
- `Hk.GridCheck`：16×16 的 Border，`CornerRadius=4`，`BorderThickness=1`，`BorderBrush=ToggleEdge`。选中时 `Background=AccentFill`，内含 `&#xE73E;` 字形（10px，AccentText）。

`RootsList` 这个 ListBox：放在卡片内，`Background=Transparent`、`BorderThickness=0`；条目复用与 ComboBoxItem 同款模板，即圆角高亮加选中弧光条。

---

## 9. 托盘菜单（WinForms，`TrayIconService`）

- 新增 `HopkeyTrayRenderer : ToolStripProfessionalRenderer`，配一个 `ProfessionalColorTable` 子类。`MenuItemSelected` 和 `MenuItemSelectedGradientBegin/End` 用 `Hover` 色；`ToolStripDropDownBackground`、`ImageMarginGradient*` 用 `Card` 色；`SeparatorDark` 用 `Divider` 色；`MenuBorder` 透明，由 DWM 画边框。文字色在 `OnRenderItemText` 里设为 `Text` 色。
- `menu.Font = new Font("Segoe UI Variable Text", 9f)`，失败时回退 `SystemFonts.MenuFont`。每项 `Padding = new Padding(8, 6, 8, 6)`。
- `menu.Opening` 时，对 `menu.Handle` 调用 `Dwmapi.SetRoundCorners`、`SetDarkMode` 和 `BORDER_COLOR`。
- 订阅 `ThemeManager.ThemeChanged`，主题切换时重建 renderer。颜色取 §3 的十六进制值，用 `System.Drawing.ColorTranslator.FromHtml`。

---

## 10. 动效

| 对象 | 动画 | 时长 | 缓动 | 触发 |
|---|---|---|---|---|
| Launcher 内容 | `Root.Opacity` 0→1 | 110ms | CubicEase Out | `ShowLauncher` 中 `Show()` 之后 |
| 弧光线 `HopLine` | `HopLineScale.ScaleX` 0→1（原点在左） | 150ms | CubicEase Out | 同上 |
| 跳跃弧 `HopGlyph` | `HopGlyphShift.Y` −3→0 | 150ms | BackEase Out（Amplitude 0.6） | 同上（“跳一下”） |
| 结果/动作选中条 | `ScaleY` 0.3→1 | 90ms | CubicEase Out | 模板 `IsSelected` 触发器 |
| 动作面板进入 | `ActionPanel.Opacity` 0→1；`ActionShift.X` 10→0 | 100 / 120ms | CubicEase Out | `OnVmPropertyChanged` 中 `ActionMode` 变为 true |
| 消息胶囊 | `MessagePill.Opacity` 0→1 | 120ms | 线性 | `HasMessage` 变为 true（代码触发） |
| 设置开关滑块 | `TranslateX` 0↔20 | 120ms | CubicEase Out | 模板触发器 |
| 工具条按钮按下 | `TranslateY` 0→1 | 立即 | — | IsPressed |
| 工具条忙碌 | 旋转（已有） | 800ms 循环 | 线性 | 不变 |

**规则：**

- 隐藏一律不做动画，保证 Esc 和点击外部能瞬时响应。
- 背景色切换不做 ColorAnimation：画刷都是冻结的共享画刷，而且没有必要。
- 代码触发的动画在 `SystemParameters.ClientAreaAnimation == false` 时直接设为终值，以尊重“动画效果”系统设置。
- 动画对象（`DoubleAnimation` 和缓动）做成 `static readonly` 并 `Freeze()`，复用，零分配。
- 弹出动画必须使用显式的 `From`，不要设 `BeginTime` 延迟。否则在延迟期间会先显示终值，再跳回起点，出现闪烁。

```csharp
private static readonly IEasingFunction s_out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
private static readonly DoubleAnimation s_fade = Frozen(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(110)) { EasingFunction = s_out });
private static readonly DoubleAnimation s_sweep = Frozen(new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = s_out });
private static readonly DoubleAnimation s_hop = Frozen(new DoubleAnimation(-3, 0, TimeSpan.FromMilliseconds(150))
    { EasingFunction = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }) });
private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

private void PlayOpenAnimation()
{
    if (!SystemParameters.ClientAreaAnimation) return;
    Root.BeginAnimation(OpacityProperty, s_fade);
    HopLineScale.BeginAnimation(ScaleTransform.ScaleXProperty, s_sweep);
    HopGlyphShift.BeginAnimation(TranslateTransform.YProperty, s_hop);
}
```

---

## 11. 风险与需要拍板的问题

1. **最低系统版本。** DWM 边框色、标题栏着色、圆角都要求 Win11。Win10 回退已经写明（WPF 1px 边框，方角）。**需确认：是否仍要正式支持 Win10？**
2. **可变字体字重。** WPF 对 Segoe UI Variable 和 Bahnschrift 的 `SemiLight`/`SemiBold` 命名实例支持因系统而异，不支持时会回落到 Regular，不影响功能。实现后在 100% 和 150% DPI 下目测确认。
3. **DWM 阴影。** 无边框窗口是否出阴影，依赖 `EnableShadow` 技巧，需要在目标机器上验证（§2）。
4. **数字键帽列。** 结果行右侧固定占 30px，标题可用宽度减少约 30px；同时窗口加宽 40px 抵消。**需确认：窗口宽度 720 是否可以接受？**
5. **高对比度模式。** 本方案没有覆盖它。建议后续在 `SystemParameters.HighContrast` 为 true 时让 ThemeManager 改用 `SystemColors`。**需确认是否纳入本期。**
6. **Acrylic/Mica。** 本期不做。如果以后要做，只建议用在设置窗口（Mica，`DWMSBT_MAINWINDOW=2`），Launcher 保持不透明。

---

## 12. 实施清单（按影响排序）

**P0：一眼可见的身份变化**
1. `Shell32.cs` `Dwmapi`：加 `SetColor`、`EnableShadow` 及相关常量（§2）。
2. `ThemeManager.cs`：新调色板、渐变、字体键，`ApplyWindowChrome` 着色（§3）。
3. 新建 `Themes/Controls.xaml`、`Infrastructure/KeyChordConverter.cs`、`Infrastructure/PopupChrome.cs`；在 `App.xaml` 中合并 Controls.xaml（§6）。
4. `LauncherWindow.xaml`：外框、上下文胶囊、查询键帽面板和弧光线、筛选键帽、结果行、动作菜单、空状态、底栏（§5）。
5. `LauncherModels.cs`（`IndexKey`）、`LauncherViewModel.cs`（`KeyHintItems`、`IndexState`、`Selected` 通知）、`HighlightText.cs`（荧光底）。
6. `LauncherWindow.xaml.cs`：`EnableShadow`、`PlayOpenAnimation`、ActionPanel 进入动画、Win10 回退。

**P1：完整的一致性**
7. 新建 `Themes/SettingsControls.xaml`；重构 `SettingsWindow.xaml`，包括导航、卡片分组、保存胶囊；`SettingsWindow.xaml.cs` 的 `PromptText` 加载样式（§8）。
8. `QuickSwitchToolbar.xaml` / `.xaml.cs`：新外观、菜单样式、DWM 外观（§7）。

**P2：收尾**
9. `TrayIconService.cs`：自定义 renderer 和 DWM 圆角（§9）。
10. 两种主题、两种语言、100%/150% DPI 下逐屏截图走查；Win10 回退目测。

**验收标准**
- `dotnet build -c Debug` 无警告新增；`dotnet run --project tests\Hopkey.SelfTest --no-build` 全部通过，`launcher.show` 指标不比改前差超过 10%。
- Launcher 的所有键盘行为不变：↑↓、PgUp/PgDn、Ctrl+Home/End、Esc 三段、Tab 和 →/← 进出动作模式、Ctrl+Tab 切换筛选、Enter / Ctrl+Enter / Shift+Enter、Ctrl+C、Alt+1–9。鼠标点筛选、双击结果、右键结果、点返回、点错误状态打开日志都正常。
- 选中行同时出现：选中底色、弧光条弹出、右侧数字键帽变为实心“按下”态。前 9 行显示 1–9，第 10 行起不显示键帽。
- 匹配字符有荧光底；Light 和 Dark 两种主题下都清晰可读。
- 底栏快捷键提示以键帽形式显示，中英文切换后能实时更新；索引状态点颜色随状态变化，错误时为红色且可点击。
- Light、Dark、System 三种主题实时切换时，所有窗口（包括已打开的工具条和设置窗口）的颜色、DWM 边框和标题栏同步更新。
- 设置页所有开关、按钮、输入框、下拉框、DataGrid 和滚动条都是新样式；Tab 键焦点环可见；禁用态为半透明。
- 结果列表滚动时观感不卡顿；结果模板中不出现任何 `Effect`。
