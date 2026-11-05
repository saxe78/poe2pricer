using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Poe2Pricer.Core;
using Poe2Pricer.Core.Market;
using Poe2Pricer.Core.Runes;

namespace Poe2Pricer.App;

// Draws the price labels: number on a dark plate centred on the item's bottom edge, the
// currency orb small in the top right corner of the item on a plate of its own; "!" large, "?"
// and "<" small, also at the bottom. Coordinates are physical pixels relative to the game
// client area.
public sealed class LabelLayer : FrameworkElement
{
    private static readonly Brush Plate = Frozen(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)));
    private static readonly Brush LightPlate = Frozen(new SolidColorBrush(Color.FromArgb(235, 240, 234, 220)));
    private static readonly Brush Unique = Frozen(new SolidColorBrush(Color.FromRgb(223, 142, 60)));
    private static readonly Pen Outline = new(Brushes.Black, 2) { LineJoin = PenLineJoin.Round };
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public Label[] Labels { get; set; } = [];

    // Rumour table beside the "Uncharted Waters" panel, like PoeAncientsPriceHelper shows it:
    // Rumour, Map, Mods, Rating; one row per rumour, level with its line in the panel.
    public (Box Panel, RumourLine[] Lines, IslandSummary? Island)? Rumours { get; set; }

    // The rune chain at the top of the screen (Scene.Strip); null: none.
    public RuneStrip? Strip { get; set; }
    public ImageSource? Divine { get; set; }
    public ImageSource? Exalted { get; set; }
    public ImageSource? Chaos { get; set; }

    // The orb drawn for a label's unit (Label.Unit).
    private ImageSource? OrbOf(Label l) => l.Unit switch { "div" => Divine, "chaos" => Chaos, _ => Exalted };
    public double PixelScale { get; set; } = 1;

    // Where the labels' origin lies on this element (the preview draws onto a whole picture;
    // the overlay window itself lies on the game's part of the screen).
    public Point Origin { get; set; }

    private static Brush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    // On a light plate the same meaning in dark shades.
    public static Brush DarkColourOf(Label l) =>
        l.MostValuable ? new SolidColorBrush(Color.FromRgb(0, 120, 40))
        : l.Tier switch
        {
            Tier.Low => new SolidColorBrush(Color.FromRgb(95, 95, 95)),
            Tier.High => new SolidColorBrush(Color.FromRgb(150, 105, 0)),
            Tier.Divine => new SolidColorBrush(Color.FromRgb(185, 80, 0)),
            _ => Brushes.Black,
        };

    public static Brush ColourOf(Label l) =>
        l.MostValuable ? new SolidColorBrush(Color.FromRgb(80, 255, 120))
        : l.Tier switch
        {
            Tier.Low => new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            Tier.High => new SolidColorBrush(Color.FromRgb(255, 201, 64)),
            Tier.Divine => new SolidColorBrush(Color.FromRgb(255, 140, 0)),
            _ => Brushes.White,
        };

    protected override void OnRender(DrawingContext dc)
    {
        dc.PushTransform(new TranslateTransform(Origin.X, Origin.Y));
        dc.PushTransform(new ScaleTransform(PixelScale, PixelScale));
        if (Rumours is { } r)
            DrawRumours(dc, r.Panel, r.Lines, r.Island);
        if (Strip is { } strip)
            DrawStrip(dc, strip, RenderSize.Width / PixelScale);
        foreach (var l in Labels)
        {
            var b = l.Bounds;
            double cx = b.X + b.Width / 2.0;
            if (l.Caption is { } caption)
            {
                DrawCaption(dc, l, caption);
                continue;
            }
            if (l.Frame is { } frame)
            {
                DrawFrame(dc, l, frame);
                continue;
            }
            if (l.Row)
            {
                DrawRow(dc, l);
                continue;
            }
            if (l.Mark is "<" or "?")
            {
                // "<" below the floor, "?" no market price: both mean "recognised, no number".
                // Small grey mark on a small plate, so they do not compete with the prices:
                // an item without any label looks like a recognition failure, a "?" as large
                // as a price out of proportion. "!" stays large: it asks for the mouse or
                // cursor on a greyed-out omen.
                dc.DrawRoundedRectangle(Plate, null, new Rect(cx - 11, b.Bottom - 26, 22, 22), 4, 4);
                Text(dc, l.Mark, 18, Brushes.Silver, cx, b.Bottom - 15);
                continue;
            }
            if (l.Mark is { } mark)
            {
                double my = b.Bottom - 3 - 19;
                dc.DrawRoundedRectangle(Plate, null, new Rect(cx - 14, my - 19, 28, 38), 6, 6);
                Text(dc, mark, mark == "!" ? 34 : 26, mark == "!" ? Brushes.Red : Brushes.LightGray, cx, my);
                continue;
            }
            if (l.Text is null)
                continue;
            string number = l.Text.Replace(" div", "").Replace(" ex", "").Replace(" chaos", "");
            // One size for every price, whatever the size of the item (PriceSize). Only a number
            // too long for its field is set smaller, as far as it takes to fit.
            double size = PriceSize;
            var text = Format(number, size, ColourOf(l));
            while (text.Width + 12 > b.Width && size > SmallestPriceSize)
                text = Format(number, --size, ColourOf(l));
            // Centred on the bottom edge: the top left holds the stack size in the stash, the
            // top right the orb (for every window alike).
            double pw = text.Width + 12,
                ph = size + 8,
                py = b.Bottom - 3 - ph / 2;
            dc.DrawRoundedRectangle(Plate, null, new Rect(cx - pw / 2, py - ph / 2, pw, ph), 5, 5);
            Draw(dc, text, cx - text.Width / 2, py - text.Height / 2);
            var orb = OrbOf(l);
            if (orb is not null)
            {
                double s = OrbSize;
                dc.DrawRoundedRectangle(Plate, null, new Rect(b.Right - s - 5, b.Y + 3, s + 2, s + 2), 4, 4);
                dc.DrawImage(orb, new Rect(b.Right - s - 4, b.Y + 4, s, s));
            }
        }
        dc.Pop();
        dc.Pop();
    }

    // One size for every price of the overlay — on a small item, on a large one and beside a
    // list row — and one for the orb beside it. 18 is the largest size at which five
    // characters ("116.7") fit the narrowest field (64 px); no price is longer.
    private const double PriceSize = 18;
    private const double SmallestPriceSize = 12;
    private const double OrbSize = 20;

    // Beside a list row: plate with the orb left and the number right of it; anchored at the
    // left edge, or at the right edge when AlignRight.
    private void DrawRow(DrawingContext dc, Label l)
    {
        var b = l.Bounds;
        double cy = b.Y + b.Height / 2.0;
        var plate = l.Light ? LightPlate : Plate;
        if (l.Note is { } note)
        {
            // A fixed text instead of a price: no orb, the colour of unique items.
            var words = Format(note, 18, l.Light ? Brushes.SaddleBrown : Unique);
            double nw = words.Width + 16,
                nx = l.AlignRight ? b.Right - nw : b.X;
            dc.DrawRoundedRectangle(plate, null, new Rect(nx, cy - 16, nw, 32), 5, 5);
            Draw(dc, words, nx + 8, cy - words.Height / 2, outline: !l.Light);
            return;
        }
        if (l.Mark is { } mark)
        {
            double mx = l.AlignRight ? b.Right - 30 : b.X;
            dc.DrawRoundedRectangle(plate, null, new Rect(mx, cy - 15, 30, 30), 5, 5);
            var m = Format(mark, 22, l.Light ? Brushes.DimGray : Brushes.LightGray);
            Draw(dc, m, mx + 15 - m.Width / 2, cy - m.Height / 2, outline: !l.Light);
            return;
        }
        if (l.Text is null)
            return;
        string number = l.Text.Replace(" div", "").Replace(" ex", "").Replace(" chaos", "");
        var text = Format(number, PriceSize, l.Light ? DarkColourOf(l) : ColourOf(l));
        // The best reward lies out of view: an arrow that way before the orb.
        var arrow = l.Arrow is null ? null : Format(l.Arrow, 18, l.Light ? DarkColourOf(l) : ColourOf(l));
        double s = OrbSize,
            lead = arrow is null ? 0 : arrow.Width + 6,
            w = 6 + lead + s + 6 + text.Width + 8,
            x = l.AlignRight ? b.Right - w : b.X;
        dc.DrawRoundedRectangle(plate, null, new Rect(x, cy - 16, w, 32), 5, 5);
        if (arrow is not null)
            Draw(dc, arrow, x + 6, cy - arrow.Height / 2, outline: !l.Light);
        if (OrbOf(l) is { } orb)
            dc.DrawImage(orb, new Rect(x + 6 + lead, cy - s / 2, s, s));
        Draw(dc, text, x + 6 + lead + s + 6, cy - text.Height / 2, outline: !l.Light);
    }

    private static readonly Typeface Regular = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Typeface Light = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);
    private static readonly Typeface SemiBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush Card = Frozen(new SolidColorBrush(Color.FromArgb(225, 18, 18, 22)));
    private static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(235, 235, 240)));
    private static readonly Brush Faint = Frozen(new SolidColorBrush(Color.FromRgb(150, 150, 160)));
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromRgb(140, 140, 150)));
    private static readonly Brush Warn = Frozen(new SolidColorBrush(Color.FromRgb(255, 120, 120)));
    // The hint in the program's brass (Theme.Accent): the one action to take, as in the window.
    private static readonly Brush HintInk = Frozen(new SolidColorBrush(Poe2Pricer.Ui.Theme.Current.Accent));
    private static readonly Brush Fresh = Frozen(new SolidColorBrush(Color.FromRgb(60, 150, 90)));
    private static readonly Brush FreshInk = Frozen(new SolidColorBrush(Color.FromRgb(240, 255, 240)));
    private static readonly Brush PillInk = Frozen(new SolidColorBrush(Color.FromRgb(20, 20, 24)));
    private static readonly Brush DimPill = Frozen(new SolidColorBrush(Color.FromRgb(70, 70, 78)));
    private static readonly Brush DimPillInk = Frozen(new SolidColorBrush(Color.FromRgb(190, 190, 200)));
    private static readonly Pen Rule = new(Frozen(new SolidColorBrush(Color.FromRgb(70, 70, 80))), 1);

    // The card beside the panel. On top the island (IslandSummary): "Expedition" with its tier,
    // the counts at the right. Then one row per line of the panel: the rumour with its tier in
    // a pill, "new" when the island shows it for the first time, below it island, mods and note
    // in one small line. A rule, and under it the rumours seen at the island that the panel
    // does not show now, dimmed. Three lines on the panel and the island not (probably)
    // complete: a hint under the head, in the program's brass, to move an item for more. The first row
    // stands at the height of the panel's first line, the head (and the hint) above it.
    // Without an island (no marker found): the rows alone, with the hint.
    private void DrawRumours(DrawingContext dc, Box panel, RumourLine[] lines, IslandSummary? island)
    {
        if (lines.Length == 0)
            return;
        const double width = 520,
            pad = 22,
            rowHeight = 54,
            headHeight = 46,
            hintHeight = 24,
            pillWidth = 36,
            pillHeight = 22;
        FormattedText T(string text, Typeface face, double size, Brush brush, double? maxWidth = null)
        {
            var f = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, 1);
            if (maxWidth is { } w)
            {
                f.MaxTextWidth = w;
                f.MaxLineCount = 1;
                f.Trimming = TextTrimming.CharacterEllipsis;
            }
            return f;
        }
        // The card follows the language of the game, not that of the program window: rumour
        // and island as the game names them; mods and notes are the community's (English).
        string language = lines[0].Language;
        string In(string key, params object?[] args) => Poe2Pricer.Core.Texts.In(language, key, args);
        var isNew = island?.New.ToHashSet() ?? [];
        double rowsTop = lines.Min(l => l.CenterY) - rowHeight / 2;
        bool hint = lines.Length == 3 && !(island?.ProbablyComplete ?? false);
        double above = (island is null ? 0 : headHeight) + (hint ? hintHeight : 0);
        double top = rowsTop - pad - above;
        int rotated = island?.Rotated.Length ?? 0;
        double height = pad + above + lines.Length * rowHeight + (rotated > 0 ? 18 + rotated * rowHeight : 0) + pad - 12;
        // Opposite the side the panel leans to; left when there is room.
        double x = panel.X - width - 12 >= 0 ? panel.X - width - 12 : panel.Right + 12;
        dc.DrawRoundedRectangle(Card, null, new Rect(x, top, width, height), 14, 14);

        if (island is { } i)
        {
            var head = T(In("OverlayExpeditionUnrated"), Light, 26, Ink);
            dc.DrawText(head, new Point(x + pad, top + pad - 6));
            var tierText = i.Tier is { } tier ? T(tier, Light, 26, Poe2Pricer.Ui.RatingColours.Of(tier)) : null;
            if (tierText is not null)
                dc.DrawText(tierText, new Point(x + pad + head.Width + 9, top + pad - 6));
            string state = i.Complete ? " · " + In("ExpeditionComplete") : i.ProbablyComplete ? " · " + In("ExpeditionProbablyComplete") : "";
            // The counts end at the right edge and must not run under the head and the tier.
            double headWidth = head.Width + (tierText is null ? 0 : tierText.Width + 9);
            var counts = T(Poe2Pricer.Core.Texts.CountIn(language, "ExpeditionRumours", i.Rumours) + " · " + Poe2Pricer.Core.Texts.CountIn(language, "ExpeditionRolls", i.Rolls) + state, Regular, 13, Faint, width - 2 * pad - headWidth - 20);
            dc.DrawText(counts, new Point(x + width - pad - counts.Width, top + pad + 6));
        }
        if (hint)
            dc.DrawText(T(In("OverlayRollHint"), SemiBold, 13, HintInk, width - 2 * pad), new Point(x + pad, rowsTop - hintHeight - 2));

        void Row(Rumour? e, string name, string? map, double y, bool dimmed, bool marked)
        {
            bool warning = e is { Warning.Length: > 0 };
            var ink = dimmed ? Faint : Ink;
            var title = T(name, Face, 17, warning && !dimmed ? Warn : ink, width - 2 * pad - pillWidth - 70);
            dc.DrawText(title, new Point(x + pad, y));
            if (marked)
            {
                double px = x + pad + title.Width + 10;
                dc.DrawRoundedRectangle(Fresh, null, new Rect(px, y + 3, 40, 16), 8, 8);
                var tag = T(In("OverlayNew"), SemiBold, 12, FreshInk);
                dc.DrawText(tag, new Point(px + 20 - tag.Width / 2, y + 11 - tag.Height / 2));
            }
            string shown = e is null ? "?" : e.Shown + (e.Own.Length > 0 ? "*" : "");
            // The pill keeps the rating's colour in a dimmed row too: it is the one place the
            // rating sits, and grey hid an S+ among the rotated rumours (reported 31.10.2026);
            // the faint title alone tells the row apart from the panel's lines.
            var pillBrush = e is null ? DimPill : Poe2Pricer.Ui.RatingColours.Of(e.Shown);
            dc.DrawRoundedRectangle(pillBrush, null, new Rect(x + width - pad - pillWidth, y + 1, pillWidth, pillHeight), 11, 11);
            var pill = T(shown, SemiBold, 13, e is null ? DimPillInk : PillInk);
            dc.DrawText(pill, new Point(x + width - pad - pillWidth / 2 - pill.Width / 2, y + 1 + pillHeight / 2 - pill.Height / 2));
            if (e is null)
                return;
            string second = string.Join(" · ", new[] { map ?? e.Map, e.Mods }.Where(t => t.Length > 0)) + (e.Hint.Length > 0 ? " — " + e.Hint : "");
            dc.DrawText(T(second, Regular, 13, warning ? Warn : Dim, width - 2 * pad - 10), new Point(x + pad, y + 24));
        }

        double rowY = rowsTop + rowHeight / 2 - 22;
        for (int n = 0; n < lines.Length; n++)
        {
            var l = lines[n];
            Row(l.Entry, l.Entry is { } e ? l.Name ?? e.Name : l.Text, l.Island, rowY, dimmed: false, marked: l.Entry is { } f && isNew.Contains(f.Name));
            rowY += rowHeight;
        }
        if (island is not null && rotated > 0)
        {
            dc.DrawLine(Rule, new Point(x + pad, rowY + 2), new Point(x + width - pad, rowY + 2));
            rowY += 18;
            foreach (var e in island.Rotated)
            {
                Row(e, e.Name, null, rowY, dimmed: true, marked: false);
                rowY += rowHeight;
            }
        }
    }

    // The crowned rune of a remnant row: a small black square on the tile's bottom edge with a
    // dot in the colour of the rune's class (Label.Frame) — loot in the program's gold, danger
    // red, weaker green, neutral white. The square is the size of the orb beside a price
    // (OrbSize; half of it was too small in the game, 02.11.2026 evening). 01.11.2026: a
    // bar, not a frame; 02.11.: a dot, not a bar — a bar under
    // every crowned rune was too much on a list where the price decides. The class colours also
    // frame the tiles of the rune chain (FramePens, CaptionPens).
    private static readonly Brush DotPlate = Frozen(new SolidColorBrush(Color.FromArgb(235, 0, 0, 0)));
    private static readonly Dictionary<string, Brush> DotBrushes = new[] { "loot", "danger", "weaker", "neutral" }.ToDictionary(k => k, k => Frozen(new SolidColorBrush(Poe2Pricer.Ui.RuneColours.Of(k))));
    private static readonly Dictionary<string, Pen> FramePens = new[] { "loot", "danger", "weaker", "neutral" }.ToDictionary(k => k, k => FrozenPen(Poe2Pricer.Ui.RuneColours.Of(k), 5));
    private static readonly Dictionary<string, Pen> CaptionPens = new[] { "loot", "danger", "weaker", "neutral" }.ToDictionary(k => k, k => FrozenPen(Poe2Pricer.Ui.RuneColours.Of(k), 2));

    private static Pen FrozenPen(Color colour, double width)
    {
        var pen = new Pen(new SolidColorBrush(colour), width);
        pen.Freeze();
        return pen;
    }

    // The class mark: the square centred on the tile's bottom edge, the dot in its middle; with
    // Mark "x" or "o" the badge at the tile's bottom right corner: the chain carries this rune
    // already (a red disc with a white cross; "o": hollow, red ring and red cross, while that is
    // only provisional). Square and badge do not touch (the tile is 50 wide, the badge 20).
    private static void DrawFrame(DrawingContext dc, Label l, string frame)
    {
        var b = l.Bounds;
        double s = OrbSize,
            cx = b.X + b.Width / 2.0;
        dc.DrawRoundedRectangle(DotPlate, null, new Rect(cx - s / 2, b.Bottom - s / 2, s, s), 3, 3);
        dc.DrawEllipse(DotBrushes.GetValueOrDefault(frame, DotBrushes["neutral"]), null, new Point(cx, b.Bottom), s / 4, s / 4);
        if (l.Mark is not ("x" or "o"))
            return;
        bool hollow = l.Mark == "o";
        var centre = new Point(b.Right - 2, b.Bottom - 2);
        dc.DrawEllipse(hollow ? Card : Badge, hollow ? BadgeRing : BadgeSeam, centre, 10, 10);
        var cross = hollow ? BadgeCrossRed : BadgeCrossWhite;
        dc.DrawLine(cross, new Point(centre.X - 4.5, centre.Y - 4.5), new Point(centre.X + 4.5, centre.Y + 4.5));
        dc.DrawLine(cross, new Point(centre.X + 4.5, centre.Y - 4.5), new Point(centre.X - 4.5, centre.Y + 4.5));
    }

    private static readonly Brush Badge = Frozen(new SolidColorBrush(Color.FromRgb(205, 50, 50)));
    private static readonly Pen BadgeSeam = FrozenPen(Color.FromArgb(230, 0, 0, 0), 2);
    private static readonly Pen BadgeRing = FrozenPen(Color.FromRgb(225, 70, 70), 2.5);
    private static readonly Pen BadgeCrossWhite = FrozenPen(Colors.White, 3);
    private static readonly Pen BadgeCrossRed = FrozenPen(Color.FromRgb(225, 70, 70), 3);

    // The rune chain: a plate at the top of the screen, the tiles centred (RuneStrip lays them
    // out, in the reference picture's pixels; width: its width). The title above the tiles, one
    // tile per rune — its picture from the bar on a dark ground, framed in its class colour,
    // thin while provisional — and under the tile the mouse is on that rune's name and effect.
    private static void DrawStrip(DrawingContext dc, RuneStrip strip, double width)
    {
        var title = Format(strip.Title, 14, Faint);
        double left = strip.TilesLeft(width),
            right = left + strip.TilesWidth;
        right = Math.Max(right, left + title.Width);
        var plate = new Rect(left - RuneStrip.Pad, RuneStrip.Top, right - left + 2 * RuneStrip.Pad, RuneStrip.Pad + RuneStrip.TitleHeight + 2 + RuneStrip.Tile + RuneStrip.Pad);
        dc.DrawRoundedRectangle(Card, StripEdge, plate, 8, 8);
        dc.DrawText(title, new Point(left, RuneStrip.Top + RuneStrip.Pad - 2));
        for (int i = 0; i < strip.Entries.Length; i++)
        {
            var e = strip.Entries[i];
            var t = strip.TileBox(i, width);
            var box = new Rect(t.X, t.Y, t.Width, t.Height);
            dc.DrawRoundedRectangle(StripGround, null, box, 4, 4);
            if (RuneIcon(e.Rune) is { } icon)
                dc.DrawImage(icon, new Rect(t.X + 2, t.Y + 2, t.Width - 4, t.Height - 4));
            var pen = e.Fixed ? FramePens.GetValueOrDefault(e.Class, FramePens["neutral"]) : CaptionPens.GetValueOrDefault(e.Class, CaptionPens["neutral"]);
            dc.DrawRoundedRectangle(null, pen, box, 4, 4);
        }
        if (strip.Hover >= 0 && strip.Caption is { } caption)
        {
            var t = strip.TileBox(strip.Hover, width);
            var text = Format(caption, 17, Ink);
            double pad = 10,
                w = text.Width + 2 * pad,
                x = Math.Max(0, Math.Min(width - w, t.X + t.Width / 2.0 - w / 2));
            var rect = new Rect(x, plate.Bottom + 4, w, 34);
            dc.DrawRoundedRectangle(Card, CaptionPens.GetValueOrDefault(strip.Entries[strip.Hover].Class, CaptionPens["neutral"]), rect, 6, 6);
            dc.DrawText(text, new Point(x + pad, rect.Y + (rect.Height - text.Height) / 2));
        }
    }

    private static readonly Pen StripEdge = FrozenPen(Color.FromArgb(200, 90, 90, 100), 1);
    private static readonly Brush StripGround = Frozen(new SolidColorBrush(Color.FromRgb(34, 34, 40)));
    private static readonly Dictionary<string, ImageSource?> RuneIcons = [];

    // The rune's picture as the bar in the world shows it (data/runes/remnant), loaded once.
    private static ImageSource? RuneIcon(string rune)
    {
        lock (RuneIcons)
        {
            if (!RuneIcons.TryGetValue(rune, out var icon))
                RuneIcons[rune] = icon = LoadImage(Path.Combine(AppContext.BaseDirectory, "data", "runes", "remnant", rune + ".png"));
            return icon;
        }
    }

    // The caption above the list: the rune's name and effect on a dark plate, framed in the
    // class colour. The plate's left edge and height are the label's; the width follows the text,
    // at most the label's width when it has one (the remnant list's: a longer text ends in "…",
    // the game's own tooltip has it whole; 02.11.2026).
    private static void DrawCaption(DrawingContext dc, Label l, string caption)
    {
        var b = l.Bounds;
        double pad = 10;
        var text = new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 17, Ink, 1) { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        if (b.Width > 2 * pad)
            text.MaxTextWidth = b.Width - 2 * pad;
        double width = text.Width + 2 * pad;
        var rect = new Rect(b.X, b.Y, width, b.Height);
        dc.DrawRoundedRectangle(Card, CaptionPens.GetValueOrDefault(l.Frame ?? "neutral", CaptionPens["neutral"]), rect, 6, 6);
        dc.DrawText(text, new Point(b.X + pad, b.Y + (b.Height - text.Height) / 2));
    }

    private static FormattedText Format(string s, double size, Brush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush, 1);

    private static void Text(DrawingContext dc, string s, double size, Brush brush, double cx, double cy)
    {
        var t = Format(s, size, brush);
        Draw(dc, t, cx - t.Width / 2, cy - t.Height / 2);
    }

    private static void Draw(DrawingContext dc, FormattedText t, double x, double y, bool outline = true)
    {
        if (outline)
            dc.DrawGeometry(null, Outline, t.BuildGeometry(new Point(x, y)));
        dc.DrawText(t, new Point(x, y));
    }

    public static ImageSource? LoadImage(string path)
    {
        if (!File.Exists(path))
            return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
