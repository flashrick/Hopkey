using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Hopkey.App.Launcher;

/// <summary>Attached property: renders ResultItemViewModel.Title with highlighted ranges as Runs (flat, cheap).</summary>
public static class HighlightText
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(ResultItemViewModel), typeof(HighlightText), new PropertyMetadata(null, OnChanged));

    public static void SetItem(DependencyObject d, ResultItemViewModel? v) => d.SetValue(ItemProperty, v);
    public static ResultItemViewModel? GetItem(DependencyObject d) => (ResultItemViewModel?)d.GetValue(ItemProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not ResultItemViewModel vm) return;
        var text = vm.Title;
        var ranges = vm.Highlights;
        if (ranges.Count == 0) { tb.Inlines.Add(new Run(text)); return; }

        // Merge & clip ranges
        var marks = new bool[text.Length];
        foreach (var (s, l) in ranges)
            for (int i = Math.Max(0, s); i < Math.Min(text.Length, s + l); i++) marks[i] = true;
        int pos = 0;
        while (pos < text.Length)
        {
            bool m = marks[pos];
            int end = pos;
            while (end < text.Length && marks[end] == m) end++;
            var run = new Run(text.Substring(pos, end - pos));
            if (m)
            {
                run.FontWeight = FontWeights.SemiBold;
                run.SetResourceReference(TextElement.ForegroundProperty, "Brush.Highlight");
            }
            tb.Inlines.Add(run);
            pos = end;
        }
    }
}
