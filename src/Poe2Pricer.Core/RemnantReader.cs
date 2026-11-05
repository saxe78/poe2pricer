using System.Text.RegularExpressions;
using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Remnants;

namespace Poe2Pricer.Core;

public sealed record RemnantWindow(UiMode Mode, Box List, int WindowRight, double AnchorScore);

// Picture: the row as it looks (RowPicture); null for rows made up in tests.
public sealed record RemnantRow(int Count, string Text, double CenterY, [property: System.Text.Json.Serialization.JsonIgnore] RowPicture? Picture = null);

// The reward list of a Verisium remnant ("Runeshape Combinations"): a window on the left, one
// reward per row, no item images. Found by a corner of its page (Anchor), read by Windows OCR
// over the full row width (long names run far left).
public sealed partial class RemnantReader : IDisposable
{
    private static readonly (UiMode Mode, int X, int Y, Box List, int Right)[] Layouts = Tune.Layouts;
    private const int SearchRadius = Tune.SearchRadius;

    private readonly Mat anchor;
    private readonly Mat cursorArrow;
    private readonly TitleReader ocr;

    public RemnantReader(string uiDirectory, TitleReader ocr)
    {
        this.ocr = ocr;
        anchor = Anchor.Load(uiDirectory, Tune.AnchorFile);
        cursorArrow = Anchor.Load(uiDirectory, Tuning.Runes.CursorArrowFile);
    }

    public RemnantWindow? Locate(Mat bgr)
    {
        RemnantWindow? best = null;
        foreach (var l in Layouts)
        {
            if (Anchor.InRow(bgr, anchor, l.Y, SearchRadius) is not { } found)
                continue;
            if (found.Score < Tune.MinimumAnchorScore || found.Score <= (best?.AnchorScore ?? 0))
                continue;
            int dx = found.At.X - l.X,
                dy = found.At.Y - l.Y;
            best = new(l.Mode, l.List with { X = l.List.X + dx, Y = l.List.Y + dy }, l.Right + dx, found.Score);
        }
        return best;
    }

    // The part of the picture that tells whether the list is still open and what changed: its
    // title, the list, and the band beside it where the game opens tooltips (to the full
    // height, as far right as a tooltip's inner margin is looked at).
    public Box LookArea(RemnantWindow window, int height, double popup = GameConfig.StandardPopup)
    {
        // With the band left of the list box where the controller's cursor frame is read
        // (RemnantGameWindow.CursorRow): the glance must see it, or the cursor row would come
        // and go between glance and follow.
        var area = window.List.Union(new Box(window.List.X - Tuning.Runes.CursorBandLeft, window.List.Y, Tuning.Runes.CursorBandLeft, window.List.Height));
        // And the arrow's band further left (CursorArrow), which the follow's part of the
        // picture cut off until 03.11.2026 (the evidence pictures "cursor-lost" showed none).
        if (window.Mode == UiMode.Controller)
            area = area.Union(new Box(window.List.X - Tuning.Runes.CursorArrowLeft, window.List.Y - Tuning.Runes.CursorAbove, Tuning.Runes.CursorArrowLeft, window.List.Height + Tuning.Runes.CursorAbove + Tuning.Runes.CursorBelow));
        foreach (var l in Layouts)
            if (l.Mode == window.Mode)
                area = area.Union(new Box(window.List.X - l.List.X + l.X - SearchRadius, window.List.Y - l.List.Y + l.Y - SearchRadius, anchor.Width + 2 * SearchRadius, anchor.Height + 2 * SearchRadius));
        int from = window.WindowRight - Band(Tune.TooltipBandLeft, popup),
            to = window.WindowRight + Band(Tune.TooltipBandRight, popup) + Band(Tuning.Occluders.BesideWidth, popup);
        return area.Union(new Box(from, 0, to - from, height));
    }

    // A measure of the tooltip band at the game's standard popup size, at this size.
    public static int Band(int standard, double popup) => GameConfig.Scaled(standard, popup);

    // The controller's cursor arrow left of the list box (Tuning.Runes.CursorArrow…): the row
    // it points at, -1 when no arrow is found or no row's band holds it; and the arrow's score.
    public (int Row, double Score) CursorArrow(Mat bgr, RemnantWindow window, IReadOnlyList<RemnantRow> rows)
    {
        var band = new Rect(window.List.X - Tuning.Runes.CursorArrowLeft, window.List.Y - Tuning.Runes.CursorAbove, Tuning.Runes.CursorArrowLeft, window.List.Height + Tuning.Runes.CursorAbove + Tuning.Runes.CursorBelow).Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        if (band.Width < cursorArrow.Width || band.Height < cursorArrow.Height)
            return (-1, 0);
        using var part = new Mat(bgr, band);
        using var gray = new Mat();
        Cv2.CvtColor(part, gray, ColorConversionCodes.BGR2GRAY);
        using var scores = new Mat();
        Cv2.MatchTemplate(gray, cursorArrow, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out _, out double max, out _, out var at);
        if (max < Tuning.Runes.CursorArrowFrom)
            return (-1, max);
        double cy = band.Y + at.Y + cursorArrow.Height / 2.0;
        for (int i = 0; i < rows.Count; i++)
            if (cy >= rows[i].CenterY - Tuning.Runes.CursorAbove && cy <= rows[i].CenterY + Tuning.Runes.CursorBelow)
                return (i, max);
        return (-1, max);
    }

    // The count is a number; the OCR reads some of its digits as the letters they resemble
    // ("lx", "IOX" for "10x").
    [GeneratedRegex(@"^\s*([0-9OoQDlIi|!ZzSsB]{1,3})\s*[xX×]\s+(.{3,})$")]
    private static partial Regex RowPattern();

    // A reward is the line that ends at the list's right edge: the game writes the name
    // right-aligned, the rune symbols stand on the left and read as noise ("ezeü"). Most rewards
    // are "Nx Name"; some stand without a count ("Unique Belt").
    public async Task<RemnantRow[]> ReadAsync(Mat bgr, RemnantWindow window)
    {
        var lines = await ocr.ReadRegionAsync(bgr, window.List.Rect());
        var rows = new List<RemnantRow>();
        foreach (var (text, y, _, right) in lines)
        {
            if (right < window.List.Right - Tune.RightAligned)
                continue;
            var m = RowPattern().Match(text);
            var digits = new string(m.Groups[1].Value.Select(Digit).ToArray());
            var picture = RowPicture.Of(bgr, window.List, y);
            if (m.Success && int.TryParse(digits, out int count) && count > 0)
                rows.Add(new(count, m.Groups[2].Value.Trim(), y, picture));
            else if (text.Count(char.IsLetter) >= 3)
                rows.Add(new(1, text.Trim(), y, picture));
        }
        return rows.OrderBy(r => r.CenterY).ToArray();
    }

    private static char Digit(char read) =>
        read switch
        {
            >= '0' and <= '9' => read,
            'O' or 'o' or 'Q' or 'D' => '0',
            'Z' or 'z' => '2',
            'S' or 's' => '5',
            'B' => '8',
            _ => '1',
        };

    // Coarse picture of the list; a scroll or a new remnant changes it.
    public static double[] Signature(Mat bgr, RemnantWindow window)
    {
        using var roi = new Mat(bgr, window.List.Rect().Intersect(new Rect(0, 0, bgr.Width, bgr.Height)));
        using var gray = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(Tune.SignatureWidth, Tune.SignatureHeight), 0, 0, InterpolationFlags.Area);
        var s = new double[Tune.SignatureWidth * Tune.SignatureHeight];
        for (int y = 0; y < Tune.SignatureHeight; y++)
        for (int x = 0; x < Tune.SignatureWidth; x++)
            s[y * Tune.SignatureWidth + x] = small.At<byte>(y, x);
        return s;
    }

    public static bool Changed(double[] a, double[] b) => a.Zip(b).Average(p => Math.Abs(p.First - p.Second)) > Tune.ChangedAbove;

    // As Changed, but the cells of the signature under the given boxes (the game's rune
    // tooltip, in this picture or in the one the other signature came from) do not count: its
    // coming, going and moving is no change of the list. All cells under boxes: not changed.
    public static bool Changed(double[] a, double[] b, RemnantWindow window, IReadOnlyList<Box> ignored)
    {
        if (ignored.Count == 0)
            return Changed(a, b);
        double cw = window.List.Width / (double)Tune.SignatureWidth,
            ch = window.List.Height / (double)Tune.SignatureHeight,
            sum = 0;
        int counted = 0;
        for (int y = 0; y < Tune.SignatureHeight; y++)
        for (int x = 0; x < Tune.SignatureWidth; x++)
        {
            var cell = new Box((int)(window.List.X + x * cw), (int)(window.List.Y + y * ch), (int)Math.Ceiling(cw), (int)Math.Ceiling(ch));
            if (ignored.Any(t => t.Intersects(cell)))
                continue;
            int i = y * Tune.SignatureWidth + x;
            sum += Math.Abs(a[i] - b[i]);
            counted++;
        }
        return counted > 0 && sum / counted > Tune.ChangedAbove;
    }

    // Where two signatures differ, for the log: the rows of the list box (in px from its
    // top) whose signature rows differ by more than ChangedAbove on average, as ranges, and
    // the mean over all. Evidence for what moves in a list that is read again and again
    // (02.11.2026: a list of seven rows in the controller UI, four readings in three seconds).
    public static string ChangedWhere(double[] a, double[] b, RemnantWindow window)
    {
        double ch = window.List.Height / (double)Tune.SignatureHeight;
        var rows = new List<(int From, int To)>();
        double total = 0;
        for (int y = 0; y < Tune.SignatureHeight; y++)
        {
            double sum = 0;
            for (int x = 0; x < Tune.SignatureWidth; x++)
                sum += Math.Abs(a[y * Tune.SignatureWidth + x] - b[y * Tune.SignatureWidth + x]);
            total += sum;
            if (sum / Tune.SignatureWidth <= Tune.ChangedAbove)
                continue;
            int from = (int)(y * ch), to = (int)((y + 1) * ch);
            if (rows.Count > 0 && rows[^1].To >= from)
                rows[^1] = (rows[^1].From, to);
            else
                rows.Add((from, to));
        }
        return $"mean {total / a.Length:0.0}, rows {(rows.Count == 0 ? "none" : string.Join(", ", rows.Select(r => $"{r.From}–{r.To}")))} of {window.List.Height} px";
    }

    // The game's own rune tooltip inside the list box: the controller's "Inspect Runes", or
    // the mouse resting on a row. A dark plate over the parchment (Tuning.Remnants.RuneTip…):
    // the dark pixels of the list, taken to cells, the cells dark enough joined, and a joined
    // patch that is wide and high enough and dark enough over its whole box is one. In the
    // reference picture's pixels, as the list. The scene's dark corner at the list's bottom
    // left in the controller UI is too narrow and too hollow to pass.
    public static Box[] RuneTooltips(Mat bgr, RemnantWindow window, double popup = GameConfig.StandardPopup)
    {
        var rect = window.List.Rect().Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        int cell = Tune.RuneTipCell,
            cw = rect.Width / cell,
            ch = rect.Height / cell;
        if (cw < 2 || ch < 2)
            return [];
        using var roi = new Mat(bgr, rect);
        using var gray = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        using var dark = new Mat();
        Cv2.Threshold(gray, dark, Tune.RuneTipDarkBelow - 1, 255, ThresholdTypes.BinaryInv);
        using var shares = new Mat();
        Cv2.Resize(dark, shares, new Size(cw, ch), 0, 0, InterpolationFlags.Area);
        using var candidates = new Mat();
        Cv2.Threshold(shares, candidates, 255 * Tune.RuneTipCellDark - 1, 255, ThresholdTypes.Binary);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int n = Cv2.ConnectedComponentsWithStats(candidates, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var found = new List<Box>();
        for (int i = 1; i < n; i++)
        {
            int x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left),
                y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width),
                h = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            if (w * cell < Tune.RuneTipWidth * popup || h * cell < Tune.RuneTipHeight * popup)
                continue;
            using var box = new Mat(shares, new Rect(x, y, w, h));
            if (Cv2.Mean(box).Val0 < 255 * Tune.RuneTipFill)
                continue;
            found.Add(new Box(rect.X + x * cell, rect.Y + y * cell, w * cell, h * cell));
        }
        return found.ToArray();
    }

    public void Dispose()
    {
        anchor.Dispose();
        cursorArrow.Dispose();
    }
}
