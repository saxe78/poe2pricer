using System.Text.Json;
using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Stash;

namespace Poe2Pricer.Core;

public sealed record StashTabWindow(string Kind, UiMode Mode, int OffsetX, int OffsetY, double AnchorScore);

// Present: the slot shows the item on the blue cell ground; an empty slot shows a grey
// silhouette of its item on a dark ground. Name: from the tab's table, else from the symbol
// comparison (StashIdentifier); null = unknown, no label. Free: a slot that takes any item.
// Alternatives: items that share the slot (Ritual: Head of the King or Petition Splinter).
// Covered: a popup (tooltip, "Inspect" box, the help text over the top row) lies on the slot's
// lower edge, where its label goes.
public sealed record StashSlot(int Index, string? Name, Box Bounds, bool Present, double Blue, bool Free = false, string? NamedBy = null, string[]? Alternatives = null, bool Covered = false);

// Special stash tabs (Orbs, Essence, Delirium, Breach, Ritual, Abyss, Expedition): each item
// has a fixed slot, so the position is the identity — tiered currency shares its art ("II",
// "III" only drawn by the game) and can only be told apart that way. Layout in
// data/ui/stash-tabs.json, measured on pictures of the tabs; names from the game data
// (…StashTabLayout tables). A slot without a name (none at present) is named by symbol once
// filled. A tab is recognised by its background art between the slots, not by its name or
// colour (both chosen by the player) nor by the tab symbol (the keyboard UI shows it only in
// the tab list, which can be closed). Only unit prices are wanted: no stack numbers read.
public sealed class StashTabReader
{
    private sealed record Region(int X, int Y, int Width, int Height);

    private sealed record SlotSpec(double X, double Y, int W, int H, string? Name, bool Free, string[]? Alternatives);

    // Other: one inactive button (Breach, Tablets); Others: several (Socketable has five).
    private sealed record SubtabSpec(Region Active, Region? Other, Region[]? Others);

    private sealed record TabSpec(string Kind, string Template, string[] Categories, SlotSpec[] Slots, Region[]? Masks, SubtabSpec? ActiveSubtab);

    private sealed record Layout(Region TemplateArea, Region ControllerOffset, TabSpec[] Tabs);

    // Per tile: the template's masked pixels (quarter size), mean-free and of unit length.
    private sealed record Tile(int[] Pixels, float[] Values);

    // Tiles: background only (slots masked); Full: the whole area incl. slot frames, which tells
    // sub-tabs with the same background apart (Socketable) when their buttons are covered.
    private sealed record Tab(TabSpec Spec, Tile[] Tiles, Tile[] Full);

    private readonly Layout layout;
    private readonly Tab[] tabs;
    // The tab's bare ground of each UI: wooden boards (controller), dark cloth (keyboard).
    private readonly Tile[][] blank;
    private readonly int width, height;

    // Keyboard UI: the tab hangs on the picture's left edge, whatever its width. Controller UI:
    // every tab 298 px right and 55 px lower at 16:9; wider than that it keeps its distance to
    // the middle (measured: at 2.4:1 it stands half the extra width further right), narrower
    // it stands Tune.ControllerNarrower px further right (measured at 16:10 only).
    // Keyboard UI inside a stash folder: Tune.FolderDrop lower, under the folder's second row
    // of tab buttons. The controller UI has no second row: the folder's name is a small line
    // above the tab's title bar and the tab stays where it is (Orbs tab in a folder: found at
    // the usual place, .80).
    private (UiMode Mode, int Dx, int Dy)[] Placements(int width)
    {
        int wider = width - Tune.ReferenceWidth;
        int shift = wider >= 0 ? wider / 2 : Tune.ControllerNarrower;
        return
        [
            (UiMode.KeyboardMouse, 0, 0),
            (UiMode.Controller, layout.ControllerOffset.X + shift, layout.ControllerOffset.Y),
            (UiMode.KeyboardMouse, 0, Tune.FolderDrop),
        ];
    }

    // Compared on fine structure only (high-pass, quarter size), tile by tile, and only the
    // best tiles count (Tune.BestTiles): a tooltip over part of the tab — the controller cursor
    // always rests on a slot — must not hide it (Orbs tab under a tooltip: .22 on the whole
    // area, .99 this way). Thresholds and measures: Tuning.Stash.
    public const double MinimumScore = Tune.MinimumScore;
    private const int Scale = Tune.Shrink,
        TilesAcross = Tune.TilesAcross;

    // Filled slot: blue ground in a ring just inside the frame, not the middle: the grey
    // silhouette of an empty slot is tinted like its item, and blue items (catalysts, essences)
    // would pass for blue ground there, an empty slot for a filled one.
    // And the art must be bright: the violet rune rows have a violet ground behind empty slots
    // too, which passed the blue test (.27; filled .75).
    private const int Ring = Tune.Ring;

    public IReadOnlyList<string> CategoriesOf(string kind) => tabs.First(t => t.Spec.Kind == kind).Spec.Categories;

    public StashTabReader(string uiDirectory)
    {
        layout = JsonSerializer.Deserialize<Layout>(File.ReadAllText(Path.Combine(uiDirectory, "stash-tabs.json")), JsonFiles.Options)
            ?? throw Texts.Failure(m => new InvalidDataException(m), "StashTabsEmpty");
        var a = layout.TemplateArea;
        width = a.Width / Scale;
        height = a.Height / Scale;
        tabs = layout
            .Tabs.Select(spec =>
            {
                using var full = Cv2.ImRead(Path.Combine(uiDirectory, spec.Template), ImreadModes.Grayscale);
                if (full.Empty())
                    throw Texts.Failure(m => new InvalidDataException(m), "MissingFile", spec.Template);
                using var fullMask = new Mat(full.Size(), MatType.CV_8UC1, Scalar.All(255));
                foreach (var s in spec.Slots)
                    fullMask.Rectangle(new Rect((int)(s.X - s.W / 2.0 - Tune.SlotMaskMargin) - a.X, (int)(s.Y - s.H / 2.0 - Tune.SlotMaskMargin) - a.Y, s.W + 2 * Tune.SlotMaskMargin, s.H + 2 * Tune.SlotMaskMargin), Scalar.All(0), -1);
                foreach (var m in spec.Masks ?? [])
                    fullMask.Rectangle(new Rect(m.X - a.X, m.Y - a.Y, m.Width, m.Height), Scalar.All(0), -1);
                var template = HighPass(full);
                using var mask = new Mat();
                Cv2.Resize(fullMask, mask, new Size(width, height), 0, 0, InterpolationFlags.Nearest);
                return new Tab(spec, Tiles(template, (x, y) => mask.At<byte>(y, x) != 0), Tiles(template, (_, _) => true));
            })
            .ToArray();
        // The tab's bare ground (the frames between two tabs, IsBlank), one per UI: the
        // controller UI draws wooden boards (cut from a glance picture at the controller
        // placement, 25.10.2026), the keyboard UI a dark cloth (cut from a glance picture at
        // the keyboard placement, 26.10.2026, 2560×1440).
        blank = new[] { "stash-blank.png", "stash-blank-keyboard.png" }
            .Select(file =>
            {
                using var ground = Cv2.ImRead(Path.Combine(uiDirectory, file), ImreadModes.Grayscale);
                if (ground.Empty())
                    throw Texts.Failure(m => new InvalidDataException(m), "MissingFile", file);
                // Tiles indexes the picture by the template area's size.
                if (ground.Width != a.Width || ground.Height != a.Height)
                    throw new InvalidDataException($"{file}: {ground.Width}×{ground.Height}, the template area is {a.Width}×{a.Height}");
                return Tiles(HighPass(ground), (_, _) => true);
            })
            .ToArray();
    }

    private Tile[] Tiles(float[] template, Func<int, int, bool> use)
    {
        var tiles = new List<Tile>();
        for (int ty = 0; ty < TilesAcross; ty++)
        for (int tx = 0; tx < TilesAcross; tx++)
        {
            var pixels = new List<int>();
            for (int y = ty * height / TilesAcross; y < (ty + 1) * height / TilesAcross; y++)
            for (int x = tx * width / TilesAcross; x < (tx + 1) * width / TilesAcross; x++)
                if (use(x, y))
                    pixels.Add(y * width + x);
            if (pixels.Count < Tune.MinimumTilePixels)
                continue;
            var values = pixels.Select(i => template[i]).ToArray();
            Normalise(values);
            tiles.Add(new(pixels.ToArray(), values));
        }
        return tiles.ToArray();
    }

    // Whether the tab's area shows nothing but the tab's ground, at any placement and in
    // either UI: the frame the game draws while it changes from one tab to another — the
    // content goes before the next tab is drawn (StashGameWindow.Open.Glance). The score:
    // the better ground, against Tuning.Stash.BlankScore.
    public bool IsBlank(Mat bgr) => BlankScore(bgr) >= Tune.BlankScore;

    public double BlankScore(Mat bgr)
    {
        if (Math.Abs(bgr.Height - Tuning.Screen.ReferenceHeight) > Tuning.Screen.HeightTolerance)
            return 0;
        var a = layout.TemplateArea;
        double best = 0;
        foreach (var (_, dx, dy) in Placements(bgr.Width))
        {
            var area = new Rect(a.X + dx, a.Y + dy, a.Width, a.Height);
            if (area.Right > bgr.Width || area.Bottom > bgr.Height)
                continue;
            using var roi = new Mat(bgr, area);
            using var gray = new Mat();
            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
            var image = HighPass(gray);
            foreach (var ground in blank)
                best = Math.Max(best, Score(image, ground, share: 1));
        }
        return best;
    }

    // The part of the picture that tells whether a tab is still open and which one: the
    // tab's area at this window's place and the sub-tab buttons of every tab.
    public Box LookArea(StashTabWindow window)
    {
        var a = layout.TemplateArea;
        var area = new Box(a.X + window.OffsetX, a.Y + window.OffsetY, a.Width, a.Height);
        foreach (var tab in tabs)
            if (tab.Spec.ActiveSubtab is { } sub)
                foreach (var button in (Region[])[sub.Active, .. sub.Others ?? [sub.Other!]])
                    area = area.Union(new Box(button.X + window.OffsetX, button.Y + window.OffsetY, button.Width, button.Height));
        return area;
    }

    // Best tab and UI mode; `kind` limits the search to one tab (the check while shown).
    public StashTabWindow? Locate(Mat bgr, double minimumScore = MinimumScore, string? kind = null)
    {
        if (Math.Abs(bgr.Height - Tuning.Screen.ReferenceHeight) > Tuning.Screen.HeightTolerance)
            return null; // other heights are brought to the reference height first (GameView)
        var a = layout.TemplateArea;
        StashTabWindow? best = null;
        foreach (var (mode, dx, dy) in Placements(bgr.Width))
        {
            var area = new Rect(a.X + dx, a.Y + dy, a.Width, a.Height);
            if (area.Right > bgr.Width || area.Bottom > bgr.Height)
                continue;
            using var roi = new Mat(bgr, area);
            using var gray = new Mat();
            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
            var image = HighPass(gray);
            foreach (var tab in tabs)
            {
                if (kind is not null && tab.Spec.Kind != kind)
                    continue;
                double score = Score(image, tab.Tiles);
                if (score < minimumScore)
                    continue;
                double rank = score;
                if (tab.Spec.ActiveSubtab is { } sub)
                {
                    double active = Glow(bgr, sub.Active, dx, dy),
                        other = (sub.Others ?? [sub.Other!]).Max(o => Glow(bgr, o, dx, dy));
                    if (active - other < Tune.MinimumGlowLead)
                    {
                        // No button lit: a tooltip lies over the buttons (controller cursor in the
                        // top row). Then the slot layout decides.
                        if (Math.Max(active, other) >= Tune.MinimumLitGlow)
                            continue;
                        rank = Score(image, tab.Full);
                        if (rank < Tune.MinimumFullScore)
                            continue;
                    }
                }
                if (rank <= (best?.AnchorScore ?? 0))
                    continue;
                best = new(tab.Spec.Kind, mode, dx, dy, rank);
            }
        }
        return best;
    }

    // Mean of the best share of the tile correlations (Tune.BestTiles; all of them for the
    // bare ground).
    private static double Score(float[] image, Tile[] tiles, double share = Tune.BestTiles)
    {
        var scores = new double[tiles.Length];
        for (int i = 0; i < tiles.Length; i++)
        {
            var tile = tiles[i];
            double mean = 0;
            foreach (var p in tile.Pixels)
                mean += image[p];
            mean /= tile.Pixels.Length;
            double dot = 0, norm = 0;
            for (int k = 0; k < tile.Pixels.Length; k++)
            {
                double x = image[tile.Pixels[k]] - mean;
                dot += x * tile.Values[k];
                norm += x * x;
            }
            scores[i] = norm > 0 ? dot / Math.Sqrt(norm) : 0;
        }
        Array.Sort(scores);
        return scores.Skip(scores.Length - (int)Math.Ceiling(scores.Length * share)).Average();
    }

    private static void Normalise(float[] v)
    {
        float mean = v.Average();
        double norm = 0;
        for (int i = 0; i < v.Length; i++)
        {
            v[i] -= mean;
            norm += v[i] * v[i];
        }
        float n = (float)Math.Sqrt(norm) + 1e-6f;
        for (int i = 0; i < v.Length; i++)
            v[i] /= n;
    }

    // Quarter size (the art is coarse), minus a blurred copy; row by row as one array.
    private float[] HighPass(Mat gray)
    {
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(width, height), 0, 0, InterpolationFlags.Area);
        using var f = new Mat();
        small.ConvertTo(f, MatType.CV_32FC1);
        using var blurred = new Mat();
        Cv2.GaussianBlur(f, blurred, new Size(0, 0), Tune.Blur);
        Cv2.Subtract(f, blurred, f);
        f.GetArray(out float[] values);
        return values;
    }

    // Mean of red minus blue: the orange glow under an active sub-tab button.
    private static double Glow(Mat bgr, Region area, int dx, int dy)
    {
        var r = new Rect(area.X + dx, area.Y + dy, area.Width, area.Height).Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        if (r.Width <= 0 || r.Height <= 0)
            return 0;
        using var roi = new Mat(bgr, r);
        var mean = Cv2.Mean(roi);
        return mean.Val2 - mean.Val0;
    }

    public StashSlot[] Read(Mat bgr, StashTabWindow window)
    {
        var spec = tabs.First(t => t.Spec.Kind == window.Kind).Spec;
        var slots = new StashSlot[spec.Slots.Length];
        var strips = new double[slots.Length];
        var rings = new double[slots.Length];
        var grounds = new List<double>();
        var picture = new Rect(0, 0, bgr.Width, bgr.Height);
        for (int i = 0; i < spec.Slots.Length; i++)
        {
            var s = spec.Slots[i];
            int cx = (int)Math.Round(s.X) + window.OffsetX,
                cy = (int)Math.Round(s.Y) + window.OffsetY;
            var inner = new Rect(cx - s.W / 2 + Tune.SlotInset, cy - s.H / 2 + Tune.SlotInset, s.W - 2 * Tune.SlotInset, s.H - 2 * Tune.SlotInset);
            var (blue, ground) = RingGround(bgr, inner);
            bool present = blue >= Tune.MinimumBlue && Bright(bgr, inner) >= Tune.MinimumBright;
            // The lower side of the ring, where the label goes, and the ring itself, both without
            // the game's lettering (GameText): at a larger UI Text Size the tier mark's backing
            // reaches into the strip and would count as a popup.
            var seen = inner.Intersect(picture);
            if (seen.Width > 2 * Ring && seen.Height > 2 * Ring)
            {
                using var roi = new Mat(bgr, seen);
                using var lettering = GameText.Surround(roi);
                strips[i] = Darkest(roi, new Rect(0, roi.Height - Ring, roi.Width, Ring), lettering);
                rings[i] = RingBrightness(roi, lettering);
            }
            else
                (strips[i], rings[i]) = (double.MaxValue, -1);
            if (present)
                grounds.Add(ground);
            slots[i] = new(i, s.Name, new Box(cx - s.W / 2, cy - s.H / 2, s.W, s.H), present, blue, s.Free, s.Name is null ? null : StashIdentifier.ByTable, s.Alternatives);
        }
        // Covered: where the label goes, a part of it is much darker than the ground of this
        // tab's filled slots. A popup darkens what lies under it; the ground behind every item
        // is the same. Or the whole slot is darker: the ring's ground outside the lettering below
        // Tune.RingCoveredBelow of the usual — a popup over the slot whose bright writing or
        // buttons leave the strip without its say (a controller tooltip's "Use" button, measured
        // on tooltip_currency_ctrl_01: strip bright, ring at .11).
        // Measured against the tab's own slots, so it holds at every size of the picture (the
        // thin frame line below the slot, compared with the template, does not: resizing
        // changes it).
        grounds.Sort();
        double usual = grounds.Count > 0 ? grounds[grounds.Count / 2] : 0;
        return slots.Select((slot, i) => slot with
        {
            Covered = slot.Present && (strips[i] < usual || rings[i] >= 0 && rings[i] < usual * Tune.RingCoveredBelow),
        }).ToArray();
    }

    // 95th percentile of max(R, G, B) inside `area`.
    private static int Bright(Mat bgr, Rect area)
    {
        area = area.Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        if (area.Width <= 0 || area.Height <= 0)
            return 0;
        var histogram = new int[256];
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
        {
            var p = bgr.At<Vec3b>(y, x);
            histogram[Math.Max(p.Item0, Math.Max(p.Item1, p.Item2))]++;
        }
        int above = area.Width * area.Height / Tune.BrightPercentileShare,
            count = 0;
        for (int v = 255; v >= 0; v--)
            if ((count += histogram[v]) > above)
                return v;
        return 0;
    }

    // Cell ground in the ring along the edge of `area` (ItemGround: blue, red, or green under
    // the mouse — without green the price under the mouse would vanish): its share of the
    // ring and how bright it is (median of the brightest channel).
    private static (double Share, double Brightness) RingGround(Mat bgr, Rect area)
    {
        area = area.Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        if (area.Width <= 2 * Ring || area.Height <= 2 * Ring)
            return (0, 0);
        int all = 0;
        var ground = new List<byte>();
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
        {
            if (x >= area.Left + Ring && x < area.Right - Ring && y >= area.Top + Ring && y < area.Bottom - Ring)
                continue;
            all++;
            var p = bgr.At<Vec3b>(y, x);
            if (ItemGround.Any(p))
                ground.Add(Math.Max(p.Item0, Math.Max(p.Item1, p.Item2)));
        }
        ground.Sort();
        return (ground.Count / (double)all, ground.Count > 0 ? ground[ground.Count / 2] : 0);
    }

    // How dark the place of a label is, as the ground brightness below which it counts as
    // covered: the larger of two readings, each brought to its own limit. A part of the strip
    // may lie under a popup (the darkest of LabelParts parts, by its median), or all of it
    // under one with bright text on it (the darkest quarter of all its pixels: the text is
    // bright, what lies between the letters is not).
    // MaxValue: the strip too narrow, or the lettering leaves too little of the strip as a whole
    // (Tune.EvidenceShare) — a part with enough left would decide alone otherwise, and under a
    // tooltip the few pixels between its letters can be bright (measured on
    // socket1_ctrl_tooltip_p125_h2160: four slots under the tooltip looked free that way).
    private static double Darkest(Mat roi, Rect strip, Mat lettering)
    {
        if (strip.Width < Tune.LabelParts || strip.Height <= 0 || strip.Y < 0)
            return double.MaxValue;
        using var stripLettering = new Mat(lettering, strip);
        if (strip.Width * strip.Height - Cv2.CountNonZero(stripLettering) < strip.Width * strip.Height * Tune.EvidenceShare)
            return double.MaxValue;
        int part = strip.Width / Tune.LabelParts;
        double darkestPart = Enumerable.Range(0, Tune.LabelParts).Min(k => Percentile(roi, new Rect(strip.X + k * part, strip.Y, part, strip.Height), 50, lettering));
        double quarter = Percentile(roi, strip, 25, lettering);
        return Math.Min(darkestPart == double.MaxValue ? darkestPart : darkestPart / Tune.CoveredBelow, quarter == double.MaxValue ? quarter : quarter / Tune.CoveredQuarterBelow);
    }

    // The ground in the ring of `roi` outside the lettering: median of the brightest channel;
    // -1 without any.
    private static double RingBrightness(Mat roi, Mat lettering)
    {
        var ground = new List<byte>();
        for (int y = 0; y < roi.Height; y++)
        for (int x = 0; x < roi.Width; x++)
        {
            if (x >= Ring && x < roi.Width - Ring && y >= Ring && y < roi.Height - Ring || lettering.At<byte>(y, x) != 0)
                continue;
            var p = roi.At<Vec3b>(y, x);
            if (ItemGround.Any(p))
                ground.Add(Math.Max(p.Item0, Math.Max(p.Item1, p.Item2)));
        }
        ground.Sort();
        return ground.Count > 0 ? ground[ground.Count / 2] : -1;
    }

    // A percentile of the brightest channel over `area` of `roi`, outside the lettering; MaxValue
    // when less than EvidenceShare of the area remains.
    private static double Percentile(Mat roi, Rect area, int percent, Mat lettering)
    {
        var values = new List<byte>(area.Width * area.Height);
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
        {
            if (lettering.At<byte>(y, x) != 0)
                continue;
            var p = roi.At<Vec3b>(y, x);
            values.Add(Math.Max(p.Item0, Math.Max(p.Item1, p.Item2)));
        }
        if (values.Count < area.Width * area.Height * Tune.EvidenceShare)
            return double.MaxValue;
        values.Sort();
        return values[Math.Min(values.Count - 1, values.Count * percent / 100)];
    }
}
