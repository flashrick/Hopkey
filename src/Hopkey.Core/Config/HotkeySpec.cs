using System.Text;
using Hopkey.Core.Native;

namespace Hopkey.Core.Config;

/// <summary>Parsed hotkey: "DoubleCtrl" or "Ctrl+Alt+Space" style. Pure, no UI dependency.</summary>
public readonly record struct HotkeySpec(uint Modifiers, uint VirtualKey, bool IsDoubleCtrl)
{
    public const string DoubleCtrl = "DoubleCtrl";

    public bool IsValid => IsDoubleCtrl || VirtualKey != 0;
    public bool IsEmpty => !IsValid;

    public static HotkeySpec None => default;

    public static HotkeySpec Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        text = text.Trim();
        if (text.Equals(DoubleCtrl, StringComparison.OrdinalIgnoreCase)) return new HotkeySpec(0, 0, true);
        uint mods = 0, vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= User32.MOD_CONTROL; break;
                case "alt": mods |= User32.MOD_ALT; break;
                case "shift": mods |= User32.MOD_SHIFT; break;
                case "win": case "windows": mods |= User32.MOD_WIN; break;
                default:
                    vk = KeyNameToVk(raw);
                    if (vk == 0) return None;
                    break;
            }
        }
        return vk == 0 ? None : new HotkeySpec(mods, vk, false);
    }

    public override string ToString()
    {
        if (IsDoubleCtrl) return DoubleCtrl;
        if (VirtualKey == 0) return string.Empty;
        var sb = new StringBuilder();
        if ((Modifiers & User32.MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((Modifiers & User32.MOD_ALT) != 0) sb.Append("Alt+");
        if ((Modifiers & User32.MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((Modifiers & User32.MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(VkToKeyName(VirtualKey));
        return sb.ToString();
    }

    public static uint KeyNameToVk(string name)
    {
        var n = name.Trim();
        if (n.Length == 1)
        {
            char c = char.ToUpperInvariant(n[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') return c;
        }
        var lower = n.ToLowerInvariant();
        if (lower.Length >= 2 && lower[0] == 'f' && int.TryParse(lower.AsSpan(1), out int fn) && fn is >= 1 and <= 24)
            return (uint)(0x70 + fn - 1);
        return lower switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "backspace" => 0x08,
            "insert" or "ins" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "`" or "oem3" or "backtick" => 0xC0,
            ";" => 0xBA,
            "=" => 0xBB,
            "," => 0xBC,
            "-" => 0xBD,
            "." => 0xBE,
            "/" => 0xBF,
            "[" => 0xDB,
            "\\" => 0xDC,
            "]" => 0xDD,
            "'" => 0xDE,
            _ => 0,
        };
    }

    public static string VkToKeyName(uint vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        return vk switch
        {
            0x20 => "Space",
            0x0D => "Enter",
            0x09 => "Tab",
            0x1B => "Esc",
            0x08 => "Backspace",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0xC0 => "`",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"0x{vk:X2}",
        };
    }
}
