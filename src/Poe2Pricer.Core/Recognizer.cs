// Recognition core adapted from Dev-Jahn/poe2-ritual-checker (MIT),
// src/Ritual.Core/Vision.cs @ 7c16291: reference building, cell occupancy, template
// scoring and colour penalty. Left out: grid search, tooltip/confirmation detection,
// OCR, defer-mode, controller-focus masking (it fired on every gold-framed deferred
// item of the English client), embeddings and user examples.
// See THIRD_PARTY_NOTICES.md.
using System.Diagnostics;
using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Recognition;

namespace Poe2Pricer.Core;

public sealed class Recognizer
{
    private const int Columns = 12,
        Rows = 10;

    private readonly Catalog catalog;
    private readonly Dictionary<string, CatalogItem> byId;
    private readonly List<Reference> references = [];

    private sealed record Reference(
        CatalogItem Item,
        float[] Gray,
        float[] Edge,
        float[] Mask,
        float[] Color,
        double MinimumScore = 0,
        bool Trained = false
    );

    private sealed record Proposal(int X, int Y, int W, int H, Candidate[] Candidates, double Score);

    private sealed record AppearancePrototype(
        string CatalogId,
        int Columns,
        int Rows,
        float[] Gray,
        float[] Edge,
        float[] Color
    );

    private sealed record AppearanceModel(int SchemaVersion, string CatalogVersion, AppearancePrototype[] Prototypes);

    public Catalog Catalog => catalog;

    // dataDirectory: catalog.json and images/ as tools/catalog_update.py writes them, and
    // optionally recognition-model.json.
    public Recognizer(string dataDirectory, bool useLearnedReferences = true)
    {
        Cv2.SetNumThreads(Math.Min(4, Environment.ProcessorCount));
        catalog = JsonFiles.Read<Catalog>(Path.Combine(dataDirectory, "catalog.json"));
        byId = catalog.Items.ToDictionary(i => i.Id);
        foreach (var item in catalog.Items)
            AddReferences(dataDirectory, item);
        var modelPath = Path.Combine(dataDirectory, "recognition-model.json");
        if (useLearnedReferences && File.Exists(modelPath))
            AddLearnedReferences(JsonFiles.Read<AppearanceModel>(modelPath));
        if (references.Count == 0)
            throw Texts.Failure(m => new InvalidDataException(m), "NoReferenceImages");
    }

    private void AddReferences(string dataDirectory, CatalogItem item)
    {
        using var loaded = Cv2.ImRead(Path.Combine(dataDirectory, item.Image), ImreadModes.Unchanged);
        if (loaded.Empty() || loaded.Channels() != 4 || item.Width > 2 || item.Height > 4)
            return;
        using var rgba = FitToSlot(loaded, item);
        var variants =
            item.Id.StartsWith("Omen_")
                ? Tune.OmenVariants
            : item.Kind == "currency" && item.Width == 1 && item.Height == 1
                ? Tune.CurrencyVariants
            : [(1.0, 0.0, 0.0)];
        foreach (var (scale, shift, shiftY) in variants)
        {
            using var variant = new Mat(rgba.Size(), rgba.Type(), Scalar.All(0));
            using var affine = Mat.FromArray(
                new double[,]
                {
                    { scale, 0, rgba.Width * ((1 - scale) / 2 + shift) },
                    { 0, scale, rgba.Height * ((1 - scale) / 2 + shiftY) },
                }
            );
            Cv2.WarpAffine(rgba, variant, affine, rgba.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            using var bgr = Premultiplied(variant, out var alpha);
            using (alpha)
            {
                var (gray, edge) = Describe(bgr);
                references.Add(new(item, gray, edge, AlphaMask(alpha, item), ColorPixels(bgr)));
            }
        }
    }

    // GGG's image service draws the art at its own size, the catalogue entry names the slot:
    // a focus is 2x2 art in a 2x3 slot, a narrow weapon 1x4 art in a 2x4 slot. The game centres
    // the art in the slot; so does the reference. Art that fits the slot (within a tenth) stays.
    private static Mat FitToSlot(Mat rgba, CatalogItem item)
    {
        double slot = (double)item.Width / item.Height;
        double art = (double)rgba.Width / rgba.Height;
        int width = rgba.Width, height = rgba.Height;
        if (art < slot * 0.9)
            width = (int)Math.Round(rgba.Height * slot);
        else if (art > slot * 1.1)
            height = (int)Math.Round(rgba.Width / slot);
        var canvas = new Mat(new Size(width, height), rgba.Type(), Scalar.All(0));
        using (var target = new Mat(canvas, new Rect((width - rgba.Width) / 2, (height - rgba.Height) / 2, rgba.Width, rgba.Height)))
            rgba.CopyTo(target);
        return canvas;
    }

    private void AddLearnedReferences(AppearanceModel model)
    {
        if (model.SchemaVersion != 1 || model.CatalogVersion != catalog.Version)
            throw Texts.Failure(m => new InvalidDataException(m), "ModelMismatch");
        foreach (var p in model.Prototypes)
        {
            if (!byId.TryGetValue(p.CatalogId, out var item))
                continue;
            item = item with { Width = p.Columns, Height = p.Rows };
            references.Add(new(item, p.Gray, p.Edge, ExampleMask(item), p.Color, Tune.LearnedMinimumScore, true));
        }
    }

    private static Mat Premultiplied(Mat rgba, out Mat alpha)
    {
        var bgr = new Mat();
        Cv2.CvtColor(rgba, bgr, ColorConversionCodes.BGRA2BGR);
        alpha = new Mat();
        Cv2.ExtractChannel(rgba, alpha, 3);
        for (int py = 0; py < bgr.Height; py++)
        for (int px = 0; px < bgr.Width; px++)
        {
            var pixel = bgr.At<Vec3b>(py, px);
            float opacity = alpha.At<byte>(py, px) / 255f;
            bgr.Set(py, px, new Vec3b((byte)(pixel.Item0 * opacity), (byte)(pixel.Item1 * opacity), (byte)(pixel.Item2 * opacity)));
        }
        return bgr;
    }

    // Ignores the stack number (top left) and the deferred medallion (bottom right) of
    // one-cell items, and the outer frame of every item.
    private static bool CornerOfOneCell(CatalogItem item, int x, int y) =>
        item.Width == 1 && item.Height == 1 && ((x < 8 && y < 15) || (x > 15 && y > 31));

    private static float[] AlphaMask(Mat alpha, CatalogItem item)
    {
        using var resized = new Mat();
        Cv2.Resize(alpha, resized, new Size(24, 48));
        var mask = new float[1152];
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 24; x++)
            mask[y * 24 + x] =
                resized.At<byte>(y, x) > Tune.AlphaVisible && x > 1 && x < 22 && y > 1 && y < 46 && !CornerOfOneCell(item, x, y)
                    ? resized.At<byte>(y, x) / 255f
                    : 0;
        return mask;
    }

    private static float[] ExampleMask(CatalogItem item)
    {
        var mask = new float[1152];
        for (int y = 3; y < 45; y++)
        for (int x = 2; x < 22; x++)
            if (!CornerOfOneCell(item, x, y))
                mask[y * 24 + x] = 1;
        return mask;
    }

    public Recognition Recognize(Mat bgr, FavoursGrid grid)
    {
        var watch = Stopwatch.StartNew();
        var c = grid.CellSize;
        var occupied = new bool[Rows, Columns];
        for (int y = 0; y < Rows; y++)
        for (int x = 0; x < Columns; x++)
        {
            var box = FavoursLocator.CellBox(grid, x, y, 1, 1);
            if (!Within(box, bgr))
                continue;
            occupied[y, x] = Occupied(bgr, box, c);
        }

        // Every (cell, size) pair is independent; the OpenCV calls are thread-safe.
        var groups = references.GroupBy(r => (r.Item.Width, r.Item.Height)).ToArray();
        var work = new List<(int X, int Y, IGrouping<(int, int), Reference> Group)>();
        for (int y = 0; y < Rows; y++)
        for (int x = 0; x < Columns; x++)
            if (occupied[y, x])
                foreach (var group in groups)
                {
                    var (w, h) = group.Key;
                    if (x + w <= Columns && y + h <= Rows && AllOccupied(occupied, x, y, w, h))
                        work.Add((x, y, group));
                }
        var found = new Proposal?[work.Count];
        Parallel.For(0, work.Count, i => found[i] = Propose(bgr, grid, work[i].X, work[i].Y, work[i].Group));
        var proposals = found.OfType<Proposal>().ToList();

        var used = new bool[Rows, Columns];
        var items = new List<ItemMatch>();
        var partition = RegionPartitioner.Select(
            proposals.Select((p, i) => new RegionCandidate(p.X, p.Y, p.W, p.H, p.Score, i)),
            Columns,
            Rows
        );
        foreach (var index in partition)
        {
            var p = proposals[index];
            for (int dy = 0; dy < p.H; dy++)
            for (int dx = 0; dx < p.W; dx++)
                used[p.Y + dy, p.X + dx] = true;
            var item = byId[p.Candidates[0].Id];
            // Score is similarity, not a calibrated probability.
            var margin = p.Candidates.Length > 1 ? p.Score - p.Candidates[1].Score : 1;
            items.Add(
                new(
                    p.X,
                    p.Y,
                    p.W,
                    p.H,
                    FavoursLocator.CellBox(grid, p.X, p.Y, p.W, p.H),
                    item.Id,
                    item.NameEn,
                    p.Score,
                    p.Score < Tune.MinimumScore || margin < Tune.MinimumMargin,
                    p.Candidates
                )
            );
        }
        int unassigned = 0;
        for (int y = 0; y < Rows; y++)
        for (int x = 0; x < Columns; x++)
            if (occupied[y, x] && !used[y, x])
                unassigned++;
        return new(grid, items.OrderBy(i => i.Row).ThenBy(i => i.Column).ToArray(), unassigned, watch.Elapsed.TotalMilliseconds);
    }

    private static Proposal? Propose(Mat bgr, FavoursGrid grid, int x, int y, IGrouping<(int, int), Reference> group)
    {
        var (w, h) = group.Key;
        var box = FavoursLocator.CellBox(grid, x, y, w, h);
        using var roi = new Mat(bgr, box.Rect());
        var (g, e) = Describe(roi);
        var color = ColorPixels(roi);
        var scored = group.Select(r => (Reference: r, Score: Score(r, g, e, color))).ToArray();
        var learned = scored
            .Where(r => r.Reference.Trained)
            .GroupBy(r => r.Reference.Item.Id)
            .Select(g => g.Max(r => r.Score))
            .OrderByDescending(s => s)
            .Take(2)
            .ToArray();
        // Similar real prototypes are not independent evidence for a new winner.
        bool learnedReliable =
            learned.Length > 0 && learned[0] >= Tune.LearnedMinimumScore && (learned.Length == 1 || learned[0] - learned[1] >= Tune.LearnedMinimumLead);
        var candidates = scored
            .Where(r => r.Score > 0 && (!r.Reference.Trained || learnedReliable))
            .Select(r => new Candidate(r.Reference.Item.Id, r.Reference.Item.NameEn, r.Score))
            .GroupBy(r => r.Id)
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .Take(3)
            .ToArray();
        if (candidates.Length == 0)
            return null;
        var lead = candidates[0].Score - (candidates.Length > 1 ? candidates[1].Score : 0);
        return w * h == 1 || candidates[0].Score >= Tune.MinimumMultiCellScore && lead >= Tune.MinimumMultiCellMargin
            ? new(x, y, w, h, candidates, candidates[0].Score)
            : null;
    }

    // Whether a cell holds (part of) an item on this frame.
    public static bool CellOccupied(Mat bgr, FavoursGrid grid, int column, int row)
    {
        var box = FavoursLocator.CellBox(grid, column, row, 1, 1);
        return Within(box, bgr) && Occupied(bgr, box, grid.CellSize);
    }

    // The cell has the green ground of the item under the mouse (keyboard UI).
    public static bool CellUnderMouse(Mat bgr, FavoursGrid grid, int column, int row)
    {
        var box = FavoursLocator.CellBox(grid, column, row, 1, 1);
        if (!Within(box, bgr))
            return false;
        double c = grid.CellSize;
        using var crop = new Mat(bgr, Inner(box, c));
        int green = 0;
        for (int cy = 0; cy < crop.Rows; cy += 2)
        for (int cx = 0; cx < crop.Cols; cx += 2)
            if (ItemGround.Green(crop.At<Vec3b>(cy, cx)))
                green++;
        return green / (Math.Ceiling(crop.Rows / 2.0) * Math.Ceiling(crop.Cols / 2.0)) > Tune.GreenShare;
    }

    private static bool AllOccupied(bool[,] occupied, int x, int y, int w, int h)
    {
        for (int dy = 0; dy < h; dy++)
        for (int dx = 0; dx < w; dx++)
            if (!occupied[y + dy, x + dx])
                return false;
        return true;
    }

    // The part of a cell its ground is told by: inside the frame of the cell.
    private static Rect Inner(Box box, double c) =>
        new(box.X + (int)(c * Tune.CellInsetX), box.Y + (int)(c * Tune.CellInsetY), Math.Max(4, (int)(c * Tune.CellInner)), Math.Max(4, (int)(c * Tune.CellInner)));

    // Blue (normal), red (cannot wear), green (under the mouse) or bright content marks a
    // used cell; an empty cell is a dark ornament.
    private static bool Occupied(Mat bgr, Box box, double c)
    {
        using var crop = new Mat(bgr, Inner(box, c));
        int blue = 0,
            bright = 0,
            red = 0,
            green = 0;
        for (int cy = 0; cy < crop.Rows; cy += 2)
        for (int cx = 0; cx < crop.Cols; cx += 2)
        {
            var p = crop.At<Vec3b>(cy, cx);
            if (ItemGround.Blue(p))
                blue++;
            if (ItemGround.Red(p))
                red++;
            if (ItemGround.Green(p))
                green++;
            if (Math.Max(p.Item0, Math.Max(p.Item1, p.Item2)) > Tune.BrightLevel)
                bright++;
        }
        double n = Math.Ceiling(crop.Rows / 2.0) * Math.Ceiling(crop.Cols / 2.0);
        return blue / n > Tune.BlueShare || red / n > Tune.RedShare || green / n > Tune.GreenShare || bright / n > Tune.BrightShare;
    }

    private static double Score(Reference reference, float[] gray, float[] edge, float[] color)
    {
        var score =
            Tune.GrayWeight * Correlation(gray, reference.Gray, reference.Mask)
            + Tune.EdgeWeight * Correlation(edge, reference.Edge, reference.Mask)
            - ColorPenalty(color, reference.Color, reference.Mask);
        return reference.MinimumScore > 0 && score < reference.MinimumScore ? -1 : score;
    }

    private static bool Within(Box box, Mat image) =>
        box.X >= 0 && box.Y >= 0 && box.Width > 0 && box.Height > 0 && box.Right <= image.Width && box.Bottom <= image.Height;

    private static (float[] gray, float[] edge) Describe(Mat bgr)
    {
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(24, 48), 0, 0, InterpolationFlags.Area);
        using var gray = new Mat();
        Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1);
        var g = new float[1152];
        var e = new float[1152];
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 24; x++)
        {
            g[y * 24 + x] = gray.At<byte>(y, x);
            e[y * 24 + x] = MathF.Abs(gx.At<float>(y, x)) + MathF.Abs(gy.At<float>(y, x));
        }
        return (g, e);
    }

    private static double Correlation(float[] a, float[] b, float[] mask)
    {
        double n = 0,
            sa = 0,
            sb = 0,
            aa = 0,
            bb = 0,
            ab = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double m = mask[i];
            if (m == 0)
                continue;
            n += m;
            sa += a[i] * m;
            sb += b[i] * m;
            aa += a[i] * a[i] * m;
            bb += b[i] * b[i] * m;
            ab += a[i] * b[i] * m;
        }
        if (n < 8)
            return -1;
        return (ab - sa * sb / n) / Math.Sqrt(Math.Max(1, (aa - sa * sa / n) * (bb - sb * sb / n)));
    }

    private static float[] ColorPixels(Mat bgr)
    {
        using var resized = new Mat();
        Cv2.Resize(bgr, resized, new Size(24, 48), 0, 0, InterpolationFlags.Area);
        var values = new float[1152 * 3];
        for (int y = 0; y < 48; y++)
        for (int x = 0; x < 24; x++)
        {
            var p = resized.At<Vec3b>(y, x);
            int i = (y * 24 + x) * 3;
            float sum = p.Item0 + p.Item1 + p.Item2;
            if (sum < Tune.DarkSum)
                continue;
            values[i] = p.Item0 / sum;
            values[i + 1] = p.Item1 / sum;
            values[i + 2] = p.Item2 / sum;
        }
        return values;
    }

    private static double ColorPenalty(float[] a, float[] b, float[] mask)
    {
        float distance = 0,
            n = 0,
            saturation = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0)
                continue;
            int j = i * 3;
            if (a[j] + a[j + 1] + a[j + 2] < Tune.NoColour || b[j] + b[j + 1] + b[j + 2] < Tune.NoColour)
                continue;
            saturation += MathF.Max(a[j], MathF.Max(a[j + 1], a[j + 2])) - MathF.Min(a[j], MathF.Min(a[j + 1], a[j + 2]));
            distance += MathF.Abs(a[j] - b[j]) + MathF.Abs(a[j + 1] - b[j + 1]) + MathF.Abs(a[j + 2] - b[j + 2]);
            n++;
        }
        // Grayscale items keep their shape score; coloured variants get a hue tie-break.
        return n > Tune.PenaltyMinimumPixels && saturation / n > Tune.PenaltySaturation ? Tune.PenaltyWeight * distance / n : 0;
    }
}
