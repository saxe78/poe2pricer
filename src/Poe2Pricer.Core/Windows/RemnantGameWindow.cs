using OpenCvSharp;
using Poe2Pricer.Core.Market;

namespace Poe2Pricer.Core;

// The remnant reward list ("Runeshape Combinations"): rows read by OCR, priced by name; game
// tooltips in the band beside the list (keyboard UI) turn the labels light.
public sealed class RemnantGameWindow(Pipeline pipeline) : IGameWindow
{
    internal Pipeline Pipeline { get; } = pipeline;

    // The list last shown, as far as it was seen: kept when the window closes, until another
    // list is shown. In memory only.
    private volatile RemnantList? remembered;

    public WindowProfile Profile { get; } = new("Remnant", AutoSwitch.Expedition, GameWindows.FollowEvery);

    public bool IsThere(GameCapture.Frame frame) => Pipeline.Remnants.Locate(frame.Image) is not null;

    public async Task<IOpenWindow?> LookAsync(GameCapture.Frame frame, LookContext context)
    {
        if (Pipeline.Remnants.Locate(frame.Image) is not { } list)
            return null;
        var rows = await Pipeline.Remnants.ReadAsync(frame.Image, list);
        var whole = remembered?.Reopened(rows);
        if (whole is not null && whole.Rows.Count > rows.Length)
            context.Log($"Remnant list recognized: {whole.Rows.Count} rows known (arrow once scrolled)");
        remembered = whole ??= RemnantList.Of(rows);
        return new Open(this, list, rows, RemnantReader.Signature(frame.Image, list), Pipeline.RemnantTooltips(frame.Image, list, frame.Popup), frame.Image.Height, whole, frame.Popup);
    }

    // Rows read again only when the list looks different (scrolled, new remnant).
    // Tooltips: those the labels were drawn for. Height: of the game's picture. Whole: the
    // list as far as it was seen, Rows being the part in view. Popup: the game's popup size
    // when the list was last read (the glance gets a picture without it).
    private sealed record Open(RemnantGameWindow Kind, RemnantWindow List, RemnantRow[] Rows, double[] Signature, Box[] Tooltips, int Height, RemnantList Whole, double Popup) : IOpenWindow
    {
        public IGameWindow Window => Kind;

        public Box? GlanceArea => Kind.Pipeline.Remnants.LookArea(List, Height, Popup);

        // Title gone → closed; list moved → read again at once; else only the tooltip band
        // beside the window.
        public Glance Glance(Mat frame, PriceBook? ready, LookContext context)
        {
            if (Kind.Pipeline.Remnants.Locate(frame)?.Mode != List.Mode)
                return new Glance.Gone("Remnant window no longer detected");
            if (ready is null)
                return Core.Glance.Unchanged;
            if (RemnantReader.Changed(RemnantReader.Signature(frame, List), Signature))
                return new Glance.Reread(this, ClearNow: false);
            // What a tooltip changes is which labels it lies on. Its bottom is measured on the
            // scene behind it (OccluderDetector.DimmingEnd) and may come out a pixel or two
            // apart from frame to frame over a moving scene; that alone draws nothing anew.
            // (Tooltips then stays as it was until the next follow, at most a second: only the
            // diagnosis shows it.)
            var tooltips = Kind.Pipeline.RemnantTooltips(frame, List, Popup);
            return Covered(tooltips).SequenceEqual(Covered(Tooltips)) ? Core.Glance.Unchanged : new Glance.Relabel(this with { Tooltips = tooltips });
        }

        private bool[] Covered(Box[] tooltips) => Rows.Select(r => tooltips.Any(t => t.Intersects(Labels.RowBox(r, List)))).ToArray();

        public async Task<IOpenWindow?> FollowAsync(GameCapture.Frame frame, LookContext context)
        {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
            var list = Kind.Pipeline.Remnants.Locate(frame.Image);
            long locate = watch.ElapsedMilliseconds;
            if (list is null)
            {
                context.Log("hidden: Remnant window no longer detected");
                return null;
            }
            var next = this with { List = list, Tooltips = Kind.Pipeline.RemnantTooltips(frame.Image, list, frame.Popup), Popup = frame.Popup };
            if (!RemnantReader.Changed(RemnantReader.Signature(frame.Image, list), Signature))
                return next;
            var rows = await Kind.Pipeline.Remnants.ReadAsync(frame.Image, list);
            context.Log($"Time: Remnant window {locate} ms, OCR {watch.ElapsedMilliseconds - locate} ms, {rows.Length} rows");
            context.Log("Remnant list rescanned");
            // Scrolled: the view is laid onto what is known. No fit: another list.
            var whole = Whole.Scrolled(rows) ?? RemnantList.Of(rows);
            if (whole.Rows.Count > rows.Length)
                context.Log($"Remnant list: {whole.Rows.Count} rows known, visible from row {whole.First + 1}");
            Kind.remembered = whole;
            return next with { Rows = rows, Signature = RemnantReader.Signature(frame.Image, list), Whole = whole };
        }

        public async Task<Picture> LabelAsync(GameCapture.Frame frame, Task<PriceBook> prices, LookContext context)
        {
            var book = await prices;
            var settings = context.Settings;
            var labels = Labels.ForRows(Rows, List, book, Labels.FloorInDivine(settings.FloorValue, settings.FloorUnit, book), Tooltips, Whole.Trusted, frame.Language, settings.PriceUnit);
            return new Picture(
                this,
                labels,
                null,
                Texts.Get("RemnantStatus", Texts.Count("Rows", Rows.Length), Texts.Count("Prices", labels.Count(l => l.Text is not null)), book.League),
                $"Remnant list, {Rows.Length} rows, {labels.Length} labels, {Tooltips.Length} tooltips",
                new
                {
                    Window = "remnant",
                    Remnant = List,
                    Rows = Rows.Select(r => new { r.Count, r.Text, r.CenterY, Match = book.Match(r.Text, frame.Language)?.Name }),
                    Known = Whole.Rows.Count,
                    Whole.First,
                    Whole.Confirmed,
                    Tooltips,
                    Labels = labels,
                }
            );
        }
    }
}
