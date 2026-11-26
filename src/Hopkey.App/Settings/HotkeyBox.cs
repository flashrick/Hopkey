using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hopkey.App.Infrastructure;

namespace Hopkey.App.Settings;

/// <summary>TextBox that captures a key combination ("Ctrl+Alt+Space"). Backspace/Delete clears.</summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyBox)d).Text = (string?)e.NewValue ?? ""));

    public string Hotkey
    {
        get => (string)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = LocalizationManager.T("Settings.HotkeyHint");
        InputMethod.SetIsInputMethodEnabled(this, false);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            Hotkey = "";
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.ImeProcessed or Key.DeadCharProcessed)
            return;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = false;
            return;
        }
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None && !(key >= Key.F1 && key <= Key.F24)) return; // require a modifier except for F-keys
        var sb = new StringBuilder();
        if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (mods.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (mods.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        int vk = KeyInterop.VirtualKeyFromKey(key);
        sb.Append(Core.Config.HotkeySpec.VkToKeyName((uint)vk));
        Hotkey = sb.ToString();
        // Re-sync text with the bound value (the VM may reject it).
        Text = Hotkey;
    }
}
