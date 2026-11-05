namespace Poe2Pricer.Core.Runes;

// One tile of the rune chain: the rune's picture, its class colour, hollow while provisional.
public sealed record StripEntry(string Rune, string Class, bool Fixed);

// The rune chain as the overlay draws it (LabelLayer.DrawStrip): a plate at the top of the
// screen with the title ("Rune chain · 4"), one tile per rune in the order chosen. Hover: the
// tile under the mouse (-1: none), Caption: that rune's name and effect,
// drawn under the tile. The tiles' places are laid out here, in the reference picture's pixels,
// so that the engine can tell which tile the mouse is on: the tiles centred on the screen's
// middle, the title above them; the plate wraps whatever that spans.
public sealed record RuneStrip(string Title, StripEntry[] Entries, int Hover = -1, string? Caption = null)
{
    public const int Top = 8;
    public const int Pad = 10;
    public const int Tile = 36;
    public const int Gap = 6;
    public const int TitleHeight = 20;
    public const int TilesTop = Top + Pad + TitleHeight + 2;

    public int TilesWidth => Entries.Length * (Tile + Gap) - Gap;

    // width: the reference picture's width (Scene.Screen.Width / Scene.Scale).
    public int TilesLeft(double width) => (int)Math.Round(width / 2 - TilesWidth / 2.0);

    public Box TileBox(int index, double width) => new(TilesLeft(width) + index * (Tile + Gap), TilesTop, Tile, Tile);

    // The tile under the pointer, -1 for none.
    public int TileAt((int X, int Y)? pointer, double width)
    {
        if (pointer is not { } p)
            return -1;
        for (int i = 0; i < Entries.Length; i++)
            if (TileBox(i, width).Holds(p.X, p.Y))
                return i;
        return -1;
    }

    public bool SameAs(RuneStrip? other) =>
        other is not null && Title == other.Title && Hover == other.Hover && Caption == other.Caption && Entries.AsSpan().SequenceEqual(other.Entries);
}
