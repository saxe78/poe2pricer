using System.Windows.Media;
using Poe2Pricer.Core.Runes;

namespace Poe2Pricer.Ui;

// The colours of the rune classes: loot in the program's gold (the rating S), danger red,
// weaker green, neutral white (02.11.2026: grey was never one of the program's colours).
// The same in the overlay (the dot under the rune, the tiles of the rune chain) and in the
// program window's Runes tab (the row's ground, faint; none for neutral).
public static class RuneColours
{
    public static Color Of(RuneClass c) => c switch
    {
        RuneClass.Loot => Color.FromRgb(255, 201, 64),
        RuneClass.Danger => Color.FromRgb(255, 90, 90),
        RuneClass.Weaker => Color.FromRgb(80, 200, 120),
        _ => Color.FromRgb(235, 235, 240),
    };

    public static Color Of(string key) => Of(RuneBook.ClassOf(key));

    // The row's ground in the tab: the colour at a fifth.
    public static Brush Ground(RuneClass c)
    {
        var colour = Of(c);
        var brush = new SolidColorBrush(Color.FromArgb(c == RuneClass.Neutral ? (byte)0 : (byte)48, colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }
}
