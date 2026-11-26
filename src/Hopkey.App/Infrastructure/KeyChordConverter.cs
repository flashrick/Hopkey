using System.Globalization;
using System.Windows.Data;

namespace Hopkey.App.Infrastructure;

/// <summary>"Ctrl+Enter" → ["Ctrl", "Enter"], for rendering a shortcut as a row of keycaps.</summary>
public sealed class KeyChordConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 ? Split(s) : Array.Empty<string>();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Splits on '+', but keeps a literal "+" key (e.g. "Ctrl++").</summary>
    public static string[] Split(string chord)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < chord.Length; i++)
        {
            if (chord[i] != '+' || i == start) continue;
            parts.Add(chord[start..i].Trim());
            start = i + 1;
        }
        if (start < chord.Length) parts.Add(chord[start..].Trim());
        return parts.Where(p => p.Length > 0).ToArray();
    }
}
