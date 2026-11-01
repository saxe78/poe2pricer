using System.Text.RegularExpressions;
using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.OmenGroups;

namespace Poe2Pricer.Core;

// Omens that share one artwork and differ only in the colour of their sparks, e.g.
// VoodooOmens2Yellow (Sinistral Exaltation) and VoodooOmens2Purple (Sinistral Annulment).
// A greyed-out one (not affordable with the current tribute) loses that colour, so the
// symbol alone cannot name it: it is marked uncertain ("!") until its tooltip is read.
public sealed partial class OmenGroups
{

    private readonly Dictionary<string, string[]> groupOf;

    public OmenGroups(Catalog catalog)
    {
        groupOf = catalog
            .Items.Select(i => (i.Id, Art: ArtName().Match(i.Art)))
            .Where(i => i.Art.Success)
            .GroupBy(i => i.Art.Groups[1].Value)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(i => (i.Id, Members: g.Select(m => m.Id).ToArray())))
            .ToDictionary(i => i.Id, i => i.Members);
    }

    [GeneratedRegex(@"/(VoodooOmens\d+)[A-Za-z]*$")]
    private static partial Regex ArtName();

    public string[]? GroupOf(string? id) => id is not null && groupOf.TryGetValue(id, out var g) ? g : null;

    public Recognition MarkGreyed(Mat bgr, Recognition recognition, bool treatAllAsGrey = false)
    {
        var items = recognition
            .Items.Select(item =>
                item.Columns * item.Rows == 1
                && GroupOf(item.CatalogId) is { } group
                && (treatAllAsGrey || IsGrey(bgr, item.Bounds))
                    ? item with { Uncertain = true, Group = group }
                    : item
            )
            .ToArray();
        return recognition with { Items = items };
    }

    private static bool IsGrey(Mat bgr, Box cell)
    {
        double s = cell.Width / Tune.CellWidth;
        var inner = new Rect(
            cell.X + (int)(Tune.InnerX * s),
            cell.Y + (int)(Tune.InnerY * s),
            (int)(Tune.InnerWidth * s),
            (int)(Tune.InnerHeight * s)
        ).Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        using var roi = new Mat(bgr, inner);
        using var hsv = new Mat();
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
        var saturation = new List<byte>();
        for (int y = 0; y < hsv.Rows; y++)
        for (int x = 0; x < hsv.Cols; x++)
        {
            var p = hsv.At<Vec3b>(y, x);
            if (p.Item2 >= Tune.MinimumValue)
                saturation.Add(p.Item1);
        }
        if (saturation.Count <= Tune.MinimumPixels)
            return false;
        saturation.Sort();
        return saturation[(int)(saturation.Count * Tune.Percentile)] < Tune.GreySaturation;
    }
}
