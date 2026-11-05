using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Runes;

namespace Poe2Pricer.Core.Runes;

// What a rune does for the loot of the packs it is passed on to, as a class rather than a
// number: the game prices none of it. Loot: more rarity or more rares (Opulent, Bond, Time,
// Power); Weaker: more or stronger monsters (Death, Oath); Neutral: experience only (Wisdom);
// Danger: the rest, harder monsters and nothing for the loot. The defaults come with
// data/runes/runes.json; the player's own classes come later (runes-own.json).
public enum RuneClass { Danger, Weaker, Neutral, Loot }

// One of the 33 runes: the game's id, its name and effect lines in a client language, and its
// picture as the remnant list draws it (Tome, 50 px): the strokes as a mask for the matching.
public sealed class Rune
{
    public required string Id { get; init; }
    public required bool Rare { get; init; }

    // The class in force: the player's own (runes-own.json) or the program's default.
    public RuneClass Class { get; internal set; }
    public required RuneClass Default { get; init; }
    public required IReadOnlyDictionary<string, string> Names { get; init; }
    public required IReadOnlyDictionary<string, string[]> Effects { get; init; }
    internal Mat[] Glyphs { get; init; } = [];

    public string Name(string language) => Names.TryGetValue(language, out var n) ? n : Names[GameLanguage.English];

    // The effect as one line: the headers ("Monsters gain:") joined with their lines.
    public string Effect(string language)
    {
        var lines = Effects.TryGetValue(language, out var e) ? e : Effects[GameLanguage.English];
        return string.Join(" ", lines.Select((l, i) => l.EndsWith(':') && i < lines.Length - 1 ? l : l + (i < lines.Length - 1 ? " ·" : "")));
    }
}

// One tile of a row of the remnant list: where it stands, whether it wears the crown, and the
// rune read in it (null: none of the 33 was near enough). Score, Lead: of the reading.
public sealed record RuneTile(int Index, int X, int Y, bool Crowned, Rune? Rune, double Score, double Lead);

// The runes of Expedition remnants: read from data/runes (built by tools/rune_data.py from the
// game's data), and found again in the tiles of the remnant list. A row's crowned tile is the
// rune the remnant passes on to every later pack of the explosive chain (patch 0.5.4: the
// highlighted slot); the other tiles are the recipe's runes, which name the row for the chain.
public sealed class RuneBook : IDisposable
{
    public IReadOnlyList<Rune> Runes { get; }

    public RuneBook(string runesDirectory)
    {
        var file = Path.Combine(runesDirectory, Tune.File);
        if (!File.Exists(file))
            throw Texts.Failure(m => new FileNotFoundException(m), "MissingFile", file);
        var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(file), Json) ?? throw new InvalidDataException(file);
        var runes = new List<Rune>();
        foreach (var r in doc.Runes)
        {
            using var picture = Cv2.ImRead(Path.Combine(runesDirectory, Tune.TomeFolder, r.Id + ".png"), ImreadModes.Unchanged);
            if (picture.Empty() || picture.Channels() != 4)
                throw Texts.Failure(m => new InvalidDataException(m), "MissingFile", Path.Combine(Tune.TomeFolder, r.Id + ".png"));
            runes.Add(new Rune
            {
                Id = r.Id,
                Rare = r.Rare,
                Class = ClassOf(r.Class),
                Default = ClassOf(r.Class),
                Names = r.Name,
                Effects = r.Effect,
                Glyphs = Tune.GlyphSizes.Select(size => Glyph(picture, size)).ToArray(),
            });
        }
        Runes = runes;
    }

    public Rune? this[string id] => Runes.FirstOrDefault(r => r.Id == id);

    public static RuneClass ClassOf(string s) => s switch
    {
        "loot" => RuneClass.Loot,
        "weaker" => RuneClass.Weaker,
        "neutral" => RuneClass.Neutral,
        _ => RuneClass.Danger,
    };

    public static string KeyOf(RuneClass c) => c switch
    {
        RuneClass.Loot => "loot",
        RuneClass.Weaker => "weaker",
        RuneClass.Neutral => "neutral",
        _ => "danger",
    };

    // The player's own classes live in runes-own.json in the user's folder ({"classes":
    // {"Opulent": "loot", …}}); a rune not in it keeps the default. folder null: defaults
    // only (the tests, the command line). An unreadable file counts as none.
    private string? ownFile;

    // A file that cannot be read is put aside as runes-own.broken.json (as Settings.Load does
    // with the settings) and said so: the next save would otherwise write over it, or delete it
    // when every class is the default (review 05.11.2026).
    public void UseOwn(string folder, Action<string>? log = null)
    {
        ownFile = Path.Combine(folder, OwnFile);
        if (!File.Exists(ownFile))
            return;
        try
        {
            var own = JsonSerializer.Deserialize<Own>(File.ReadAllText(ownFile), Json);
            Apply(own?.Classes ?? []);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            var copy = Path.Combine(folder, Path.GetFileNameWithoutExtension(OwnFile) + ".broken.json");
            try { File.Copy(ownFile, copy, overwrite: true); } catch (Exception f) when (f is IOException or UnauthorizedAccessException) { }
            log?.Invoke($"Runes: {OwnFile} not read ({e.Message}); a copy lies at {Path.GetFileName(copy)}, the default classes apply");
        }
    }

    public const string OwnFile = "runes-own.json";

    // The classes as edited in the settings window: applied at once, written when a folder is
    // set; why not written, if not. Only what differs from the default goes into the file.
    public Told? SetOwn(IReadOnlyDictionary<string, RuneClass> classes)
    {
        Apply(classes.ToDictionary(c => c.Key, c => KeyOf(c.Value)));
        if (ownFile is null)
            return null;
        try
        {
            var kept = Runes.Where(r => r.Class != r.Default).ToDictionary(r => r.Id, r => KeyOf(r.Class));
            if (kept.Count == 0)
            {
                if (File.Exists(ownFile))
                    File.Delete(ownFile);
                return null;
            }
            JsonFiles.Write(ownFile, new Own(kept));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Told("RunesNotSaved", e.Message);
        }
    }

    private void Apply(IReadOnlyDictionary<string, string> classes)
    {
        foreach (var r in Runes)
            r.Class = classes.TryGetValue(r.Id, out var c) ? ClassOf(c) : r.Default;
    }

    private sealed record Own(Dictionary<string, string> Classes);

    // The rune's strokes at one size: its alpha (the glyph on nothing), scaled, as float 0–1.
    private static Mat Glyph(Mat bgra, int size)
    {
        var channels = bgra.Split();
        try
        {
            using var alpha = channels[3];
            using var scaled = new Mat();
            Cv2.Resize(alpha, scaled, new Size(size, size), 0, 0, InterpolationFlags.Area);
            var f = new Mat();
            scaled.ConvertTo(f, MatType.CV_32FC1, 1 / 255.0);
            return f;
        }
        finally
        {
            foreach (var c in channels.Take(3))
                c.Dispose();
        }
    }

    // The tiles of a row, from left to right as far as they go: frame at reference size
    // (1440), the list as located, the row as read. A tile is there while it has a frame and
    // holds one of the runes or wears the crown.
    // Where a list's tiles stand against the names: on the name's line (0) or RiseController
    // px above it (the controller UI always; the keyboard UI when its rows are two lines high,
    // TwoLineFrom px apart — remnant_tast_04). null: not to tell from one row alone, both are
    // tried. Guessing by trying on a one-line list lands on the neighbouring row's tiles.
    public static int? Rise(RemnantWindow window, IReadOnlyList<RemnantRow> rows)
    {
        if (window.Mode == UiMode.Controller)
            return Tune.RiseController;
        if (rows.Count < 2)
            return null;
        double pitch = rows.Zip(rows.Skip(1), (a, b) => b.CenterY - a.CenterY).Min();
        return pitch >= Tune.TwoLineFrom ? Tune.RiseController : 0;
    }

    // The same row by row. A keyboard row is two lines high, its tiles above its name, when
    // its name stands TwoLineFrom or more below the name before it (the taller plate grows
    // upwards; the row after it follows at the usual pitch), or, for the first row,
    // TwoLineFirstFrom below the list box's top; else one line, its tiles beside the name. A
    // list mixes both (03.11.2026 19:41: six tiles above "Chilling Flux", two beside each of
    // the three names below): the tiles stand beside the name while they fit.
    public static int[] Rises(RemnantWindow window, IReadOnlyList<RemnantRow> rows)
    {
        var rises = new int[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            rises[i] = window.Mode == UiMode.Controller ? Tune.RiseController
                : i > 0 ? (rows[i].CenterY - rows[i - 1].CenterY >= Tune.TwoLineFrom ? Tune.RiseController : 0)
                : (rows[0].CenterY - window.List.Y >= Tune.TwoLineFirstFrom ? Tune.RiseController : 0);
        return rises;
    }

    public RuneTile[] Tiles(Mat bgr, RemnantWindow window, RemnantRow row, int? rise)
    {
        int centre = (int)Math.Round(row.CenterY);
        int first = window.List.X + Tune.ListFirstX;
        var (nominal, _) = rise is { } r
            ? Align(bgr, first, centre - r, centre - r)
            : Align(bgr, first, centre, centre - Tune.RiseController);
        // The centre of a name as the OCR gives it lies up to RowSearchY px off the tiles'
        // centre (7 px on one and the same picture, read live and read again, 01.11.): the
        // row's tiles stand where the first tile's rune fits best (Align; the frame's depth
        // is no guide: off the tile it reads the strokes and the neighbours as a frame).
        int cy = nominal;
        var tiles = new List<RuneTile>();
        // A row scrolled half out at the top: its name is in the list, its tiles are not.
        if (cy - Tune.TileSize / 2 < window.List.Y)
            return [];
        for (int k = 0; k < Tune.MostTiles; k++)
        {
            int cx = window.List.X + Tune.ListFirstX + k * Tune.ListPitch;
            if (!Inside(bgr, cx, cy, Tune.TileSize / 2 + Tune.SearchX, Tune.TileSize / 2 + Tune.SearchY))
                break;
            if (FrameDepth(bgr, cx, cy) < Tune.FrameDepthFrom)
                break;
            using var ink = Ink(bgr, cx, cy);
            var (rune, score, lead) = Nearest(ink);
            tiles.Add(new RuneTile(k, cx, cy, Crowned(bgr, cx, cy), rune, score, lead));
        }
        // A frame alone is not a tile: the row's name, right-aligned, can reach into the tile
        // columns and draws edges too, and its letters can pass for a rune a few columns on.
        // The row ends before two unread tiles in a row, and with its last tile that holds
        // one of the runes or wears the crown; a single unread tile in between (a greyed one,
        // 15:51 on 01.11.) still counts.
        // An unread tile that counts has a tile's frame (UnreadFrameFrom): the parchment's
        // sketches beyond the last tile pass the weaker frame test and can even pass for a
        // rune a column on (.49 on the page's drawings, 01.11.).
        int end = tiles.Count;
        for (int i = 0; i < tiles.Count; i++)
            if (!Known(tiles[i]) && (i > 0 && !Known(tiles[i - 1]) || FrameDepth(bgr, tiles[i].X, tiles[i].Y) < Tune.UnreadFrameFrom))
            {
                end = i;
                break;
            }
        int last = end == 0 ? -1 : tiles.FindLastIndex(end - 1, end, Known);
        return tiles.Take(last + 1).ToArray();

        static bool Known(RuneTile t) => t.Rune is not null || t.Crowned;
    }

    // Where the row's tiles stand: the first tile's rune, read at the nominal height, slid up
    // and down RowSearchY px for its best fit (its glyphs alone, not all of them, which took
    // a second per list of six rows, 01.11.); when nothing is read there, all the runes at a
    // few heights, then the best one slid. Falls back to the nominal height.
    private (int Y, double Fit) Align(Mat bgr, int cx, int nominal, int other)
    {
        var a = AlignNear(bgr, cx, nominal);
        if (other == nominal)
            return a;
        var b = AlignNear(bgr, cx, other);
        return b.Fit > a.Fit ? b : a;
    }

    private (int Y, double Fit) AlignNear(Mat bgr, int cx, int nominal)
    {
        bool Fits(int x, int y) => Inside(bgr, x, y, Tune.TileSize / 2 + Tune.SearchX, Tune.TileSize / 2 + Tune.SearchY);
        // The runes of the first two tiles, read at the nominal height; a first tile that reads
        // nothing there is tried at a few other heights. The second tile keeps a gold-framed
        // first one from leaning the row (1080, 01.11.).
        var runes = new List<(int X, Rune Rune)>();
        for (int k = 0; k < 2; k++)
        {
            int x = cx + k * Tune.ListPitch;
            Rune? rune = null;
            foreach (int dy in k == 0 ? [0, -8, -4, 4, 8, -12, 12] : new[] { 0 })
            {
                if (!Fits(x, nominal + dy))
                    continue;
                using var ink = Ink(bgr, x, nominal + dy);
                rune = Nearest(ink).Rune;
                if (rune is not null)
                    break;
            }
            if (rune is not null)
                runes.Add((x, rune));
        }
        if (runes.Count == 0)
            return (nominal, -1);
        int cy = nominal;
        double fit = -1;
        for (int dy = -Tune.RowSearchY; dy <= Tune.RowSearchY; dy++)
        {
            int y = nominal + dy;
            if (runes.Any(r => !Fits(r.X, y)))
                continue;
            double f = 0;
            foreach (var (x, rune) in runes)
            {
                using var ink = Ink(bgr, x, y);
                f += Score(ink, rune);
            }
            if (f > fit)
                (fit, cy) = (f, y);
        }
        return (cy, fit);
    }

    // How well one rune's strokes fit the ink, at any of its sizes.
    private static double Score(Mat ink, Rune rune)
    {
        using var scaled = new Mat();
        ink.ConvertTo(scaled, MatType.CV_32FC1, 1 / 765.0);
        using var result = new Mat();
        double score = -1;
        foreach (var glyph in rune.Glyphs)
        {
            if (glyph.Width > scaled.Width || glyph.Height > scaled.Height)
                continue;
            Cv2.MatchTemplate(scaled, glyph, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double max);
            score = Math.Max(score, max);
        }
        return score;
    }

    private static bool Inside(Mat m, int cx, int cy, int rx, int ry) => cx - rx >= 0 && cy - ry >= 0 && cx + rx < m.Width && cy + ry < m.Height;

    // How much the tile's frame differs from the parchment inside: on each of its four sides
    // the mean colour distance (sum over the channels) of the TileFrame strip from the inside's
    // median colour, the least of the four. Brown, gold and the pale cyan frame all count;
    // measured at 1440 (01.11.): tiles 13–120, empty places 0–8, a name's letters in the
    // column up to 33 (hence a tile must hold a rune or the crown as well).
    public static double FrameDepth(Mat bgr, int cx, int cy)
    {
        int half = Tune.TileSize / 2,
            f = Tune.TileFrame,
            t = Tune.TileSize;
        using var part = new Mat(bgr, new Rect(cx - half, cy - half, t, t));
        using var middle = new Mat(part, new Rect(f, f, t - 2 * f, t - 2 * f));
        var ground = Parchment(middle);
        using var fl = new Mat();
        part.ConvertTo(fl, MatType.CV_32FC3);
        using var diff = new Mat();
        Cv2.Absdiff(fl, new Scalar(ground.Item0, ground.Item1, ground.Item2), diff);
        using var dist = Distance(diff);
        double Side(Rect r)
        {
            using var strip = new Mat(dist, r);
            return Cv2.Mean(strip).Val0;
        }
        return Math.Min(Math.Min(Side(new Rect(0, 0, t, f)), Side(new Rect(0, t - f, t, f))), Math.Min(Side(new Rect(0, 0, f, t)), Side(new Rect(t - f, 0, f, t))));
    }

    // The channels of a 3-channel float picture added up.
    private static Mat Distance(Mat diff)
    {
        var channels = diff.Split();
        var sum = new Mat();
        try
        {
            Cv2.Add(channels[0], channels[1], sum);
            Cv2.Add(sum, channels[2], sum);
            return sum;
        }
        finally
        {
            foreach (var c in channels)
                c.Dispose();
        }
    }

    // The strokes inside a tile: each pixel's distance (sum over the channels) from the
    // parchment, the median colour of the inside, or SaturationWeight times its saturation
    // above the median saturation, whichever is more (a gold rune's strokes lie close to the
    // parchment in colour but far in saturation; measured Opulent .42 → .57); as float. The
    // inside with GlyphSlack px to spare each side (the rows' centres come from the OCR).
    private static Mat Ink(Mat bgr, int cx, int cy)
    {
        int half = Tune.TileSize / 2 - Tune.TileFrame + Tune.GlyphSlack;
        using var part = new Mat(bgr, new Rect(cx - half, cy - half, 2 * half, 2 * half));
        var ground = Parchment(part);
        using var f = new Mat();
        part.ConvertTo(f, MatType.CV_32FC3);
        using var diff = new Mat();
        Cv2.Absdiff(f, new Scalar(ground.Item0, ground.Item1, ground.Item2), diff);
        var ink = Distance(diff);
        using var hsv = new Mat();
        Cv2.CvtColor(part, hsv, ColorConversionCodes.BGR2HSV);
        var planes = hsv.Split();
        try
        {
            using var saturation = new Mat();
            planes[1].ConvertTo(saturation, MatType.CV_32FC1);
            planes[1].GetArray(out byte[] sv);
            Array.Sort(sv);
            using var excess = new Mat();
            Cv2.Subtract(saturation, new Scalar(sv[sv.Length / 2]), excess);
            Cv2.Threshold(excess, excess, 0, 0, ThresholdTypes.Tozero);
            Cv2.Max(ink, excess * Tune.SaturationWeight, ink);
        }
        finally
        {
            foreach (var pl in planes)
                pl.Dispose();
        }
        return ink;
    }

    // The parchment behind the strokes: the median of all pixels, channel by channel (the
    // strokes cover a fifth of the inside at most).
    private static Vec3f Parchment(Mat part)
    {
        var channels = part.Split();
        try
        {
            float Median(Mat c)
            {
                c.GetArray(out byte[] v);
                Array.Sort(v);
                return v[v.Length / 2];
            }
            return new Vec3f(Median(channels[0]), Median(channels[1]), Median(channels[2]));
        }
        finally
        {
            foreach (var c in channels)
                c.Dispose();
        }
    }

    // The rune whose strokes fit the ink best, at any of the glyph sizes, anywhere within the
    // slack: normalised correlation. null below SameRuneFrom or less than LeadFrom ahead.
    private (Rune? Rune, double Score, double Lead) Nearest(Mat ink)
    {
        using var scaled = new Mat();
        ink.ConvertTo(scaled, MatType.CV_32FC1, 1 / 765.0);
        Rune? best = null;
        double bestScore = -1, second = -1;
        using var result = new Mat();
        foreach (var rune in Runes)
        {
            double score = -1;
            foreach (var glyph in rune.Glyphs)
            {
                if (glyph.Width > scaled.Width || glyph.Height > scaled.Height)
                    continue;
                Cv2.MatchTemplate(scaled, glyph, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double max);
                score = Math.Max(score, max);
            }
            if (score > bestScore)
            {
                second = bestScore;
                bestScore = score;
                best = rune;
            }
            else if (score > second)
                second = score;
        }
        double lead = bestScore - second;
        return bestScore >= Tune.SameRuneFrom && lead >= Tune.LeadFrom ? (best, bestScore, lead) : (null, bestScore, lead);
    }

    // The crown: gold in the tile's frame ring, searched pixel by pixel a few px around the
    // nominal place. (The three gold spikes above the tile are no help on their own: measured
    // .07–.36 on crowned tiles and up to .15 on plain ones, 01.11.) The ring alone: a rule
    // that asked for gold on both the ring's left and right side as well (02.11.2026, against
    // the inscription tile left of a crowned one, OFFEN F12) lost real crowns on the first
    // tile of a controller row the same evening (ring .24, left .04).
    public static bool Crowned(Mat bgr, int cx, int cy) => CrownRing(bgr, cx, cy) >= Tune.CrownRingFrom;

    public static double CrownRing(Mat bgr, int cx, int cy) => CrownMeasure(bgr, cx, cy).Ring;

    // The gold share of the ring at the best of the places searched, and of its left and right
    // side there (each TileFrame px wide, the tile's full height).
    public static (double Ring, double Left, double Right) CrownMeasure(Mat bgr, int cx, int cy)
    {
        int half = Tune.TileSize / 2;
        int inner = Tune.TileSize - 2 * Tune.TileFrame;
        double ringArea = Tune.TileSize * Tune.TileSize - inner * inner;
        var area = new Rect(cx - half - Tune.SearchX, cy - half - Tune.SearchY, Tune.TileSize + 2 * Tune.SearchX, Tune.TileSize + 2 * Tune.SearchY);
        if (area.X < 0 || area.Y < 0 || area.Right > bgr.Width || area.Bottom > bgr.Height)
            return (0, 0, 0);
        using var mask = GoldMask(bgr, area);
        Cv2.Threshold(mask, mask, 0, 1, ThresholdTypes.Binary);
        using var sums = new Mat();
        Cv2.Integral(mask, sums);
        sums.GetArray(out int[] s);
        int stride = sums.Cols;
        int Sum(int x0, int y0, int x1, int y1) => s[y1 * stride + x1] - s[y0 * stride + x1] - s[y1 * stride + x0] + s[y0 * stride + x0];
        double best = -1, left = 0, right = 0;
        double sideArea = Tune.TileFrame * Tune.TileSize;
        for (int dy = 0; dy <= 2 * Tune.SearchY; dy++)
        for (int dx = 0; dx <= 2 * Tune.SearchX; dx++)
        {
            int outer = Sum(dx, dy, dx + Tune.TileSize, dy + Tune.TileSize);
            int middle = Sum(dx + Tune.TileFrame, dy + Tune.TileFrame, dx + Tune.TileFrame + inner, dy + Tune.TileFrame + inner);
            double ring = (outer - middle) / ringArea;
            if (ring <= best)
                continue;
            best = ring;
            left = Sum(dx, dy, dx + Tune.TileFrame, dy + Tune.TileSize) / sideArea;
            right = Sum(dx + Tune.TileSize - Tune.TileFrame, dy, dx + Tune.TileSize, dy + Tune.TileSize) / sideArea;
        }
        return (Math.Max(best, 0), left, right);
    }

    // 255 where the picture is gold (the crown's colour), else 0.
    private static Mat GoldMask(Mat bgr, Rect r)
    {
        using var part = new Mat(bgr, r);
        using var hsv = new Mat();
        Cv2.CvtColor(part, hsv, ColorConversionCodes.BGR2HSV);
        var mask = new Mat();
        Cv2.InRange(hsv, new Scalar(Tune.GoldHueFrom, 111, 151), new Scalar(28, 255, 255), mask);
        return mask;
    }

    public void Dispose()
    {
        foreach (var r in Runes)
            foreach (var g in r.Glyphs)
                g.Dispose();
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record Document(Entry[] Runes);

    private sealed record Entry(string Id, bool Rare, string Class, Dictionary<string, string> Name, Dictionary<string, string[]> Effect);
}
