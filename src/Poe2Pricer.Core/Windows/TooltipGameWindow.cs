using OpenCvSharp;
using Poe2Pricer.Core.Market;
using Poe2Pricer.Core.Tablets;
using Poe2Pricer.Core.Waystones;
using Tune = Poe2Pricer.Core.Tuning.Waystones;

namespace Poe2Pricer.Core;

// The tooltip of a waystone or a tablet, wherever it opens (the map stash tab, the inventory):
// found by the title bar of a magic or rare item, told by its title ("Waystone (Tier 15)",
// "Overseer Tablet"), read by OCR (WaystoneTooltip, TabletTooltip) and priced on the trade
// site (ITradePrices) in the vocabulary of a profile (WaystoneProfile.Choose,
// TabletSettings.For), or as itself without one. The price stands at the right end of the
// title bar.
// The special stash tabs come before this window in GameWindows.Standard: a tooltip over
// one of them is theirs.
public sealed class TooltipGameWindow(Pipeline pipeline, ITradePrices? prices = null) : IGameWindow
{
    internal Pipeline Pipeline { get; } = pipeline;
    internal ITradePrices? Prices { get; } = prices;

    // Tooltips whose title was read and was neither a waystone's nor a tablet's, by the place
    // and the look of their title row: hovering a ring in the inventory would otherwise cost
    // an OCR at every automatic scan (review 20.10.). A tooltip elsewhere or with another
    // title is new.
    private readonly Dictionary<(int X, int Y, int Width), double> rejected = [];
    private readonly Lock rejecting = new();

    public WindowProfile Profile { get; } = new("Waystone", AutoSwitch.Stash);

    // Only whether a tooltip title bar stands on the frame: whether it is a waystone's or a
    // tablet's takes the OCR, which a look does and a check does not.
    public bool IsThere(GameCapture.Frame frame) => Pipeline.WaystoneTooltips(frame.Image, frame.Popup).Length > 0;

    public async Task<IOpenWindow?> LookAsync(GameCapture.Frame frame, LookContext context)
    {
        foreach (var box in Pipeline.WaystoneTooltips(frame.Image, frame.Popup))
        {
            var reading = await ReadAsync(frame.Image, box, frame.Popup);
            if (reading is null)
                continue;
            context.Log($"{reading.What} tooltip at {box.X},{box.Y}: {reading.Title}, {reading.ModCount} mods" + (reading.UnreadCount > 0 ? $", {reading.UnreadCount} lines unread" : ""));
            return new Open(this, box, reading, frame.Popup, null, Look: TitleLook(frame.Image, box, frame.Popup));
        }
        return null;
    }

    // What a tooltip said: one of the two.
    internal sealed record Reading(WaystoneReading? Waystone, TabletReading? Tablet)
    {
        public string What => Waystone is not null ? "Waystone" : "Tablet";
        public string Title => Waystone is not null ? $"{Waystone.Name}, tier {Waystone.Tier}" : $"{Tablet!.Name} ({Tablet.Kind.Name})";
        public int ModCount => Waystone?.Mods.Count ?? Tablet!.Mods.Count;
        public int UnreadCount => Waystone?.Unread.Count ?? Tablet!.Unread.Count;
        public bool Understood => Waystone?.Understood ?? Tablet!.Understood;

        // The same item as before (name and kind): a later reading adds to this one.
        // The name as the OCR reads it may differ by a letter from one picture to the next (a
        // magic item's whole title line): near enough counts, else every such picture dropped
        // what was read and asked the trade site anew (review before 1.2.0).
        public bool SameItem(Reading other) =>
            Waystone is not null ? other.Waystone is { } w && SameName(w.Name, Waystone.Name) && w.Tier == Waystone.Tier
                : other.Tablet is { } t && SameName(t.Name, Tablet!.Name) && t.Kind == Tablet.Kind;

        private static bool SameName(string a, string b)
        {
            string ka = WaystoneMods.KeyOf(a), kb = WaystoneMods.KeyOf(b);
            return ka == kb || TitleReader.Distance(ka, kb) <= WaystoneMods.Tolerance(Math.Max(ka.Length, kb.Length));
        }

        public bool SameValues(Reading other) => Waystone is not null ? Waystone.SameValues(other.Waystone!) : Tablet!.SameValues(other.Tablet!);

        public Reading Merge(Reading later) => Waystone is not null ? new(Waystone.Merge(later.Waystone!), null) : new(null, Tablet!.Merge(later.Tablet!));
    }

    // The title first (two lines of text, cheap): only a waystone's or a tablet's tooltip is
    // read further. The body is read three times: binarised, because the tooltip is half
    // transparent over the stash and without a threshold the OCR reads the tab list and the
    // map behind it into the lines; plain, because the threshold now and then swallows a
    // number or a word the plain reading keeps; and undimmed (TextPrep), because the stash's
    // tier marks and stack numbers show through the tooltip dimmed, pass the threshold beside a
    // line and garble it, and the plain reading reads them too (the author's diagnoses
    // 21.10.2026). The binarised reading leads, each later one fills the gaps of the ones
    // before (Merge); on its own, the undimmed one loses lines in a few pictures (measured
    // 22.10.2026 on 30: 8 better, 4 worse — as the third, 12 better, none worse).
    internal async Task<Reading?> ReadAsync(Mat bgr, Box box, double popup)
    {
        int width = Math.Min(box.Width, Px(Tune.MaxWidth, popup));
        var titleArea = new Rect(box.X, box.Y, width, Px(Tuning.Titles.TwoLines, popup)).Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        if (titleArea.Width <= 0 || titleArea.Height <= 0)
            return null;
        double look;
        using (var row = new Mat(bgr, titleArea))
        {
            var mean = Cv2.Mean(row);
            look = mean.Val0 + mean.Val1 * 1000 + mean.Val2 * 1000000;
        }
        var key = (box.X, box.Y, width);
        lock (rejecting)
            if (rejected.TryGetValue(key, out double seen) && seen == look)
                return null;
        var title = await Pipeline.Titles.ReadLinesAsync(bgr, titleArea);
        // The title's pieces in reading order, also read together: a magic item's one title line
        // comes in two pieces now and then (WaystoneTooltip.Parse).
        string whole = string.Join(" ", Lines(title));
        bool waystone = title.Any(l => WaystoneTooltip.IsTitle(l.Text)) || WaystoneTooltip.IsTitle(whole),
            tablet = !waystone && (title.Any(l => TabletTooltip.IsTitle(l.Text)) || TabletTooltip.IsTitle(whole));
        if (!waystone && !tablet)
        {
            lock (rejecting)
            {
                if (rejected.Count >= Tune.RejectedKept)
                    rejected.Clear();
                rejected[key] = look;
            }
            return null;
        }
        var body = new Rect(box.X, box.Y, width, Px(Tune.BodyHeight, popup));
        var readings = new List<OcrLine[]>();
        foreach (var prep in new[] { TextPrep.Binarised, TextPrep.Plain, TextPrep.Undimmed })
            readings.Add(await Pipeline.Titles.ReadLinesAsync(bgr, body, prep: prep));
        var texts = readings.Select(Lines).ToArray();
        if (waystone)
        {
            WaystoneReading? reading = null;
            foreach (var parsed in texts.Select(WaystoneTooltip.Parse))
                if (parsed is not null)
                    reading = reading is null ? parsed : parsed.Tier == reading.Tier ? reading.Merge(parsed) : reading;
            if (reading is null)
                return null;
            if (reading.Mods.Any(m => m.Mod.HasValue && m.Value is null))
            {
                var mods = reading.Mods.ToList();
                for (int i = 0; i < mods.Count; i++)
                    if (mods[i].Mod.HasValue && mods[i].Value is null && GlyphValue(bgr, popup, readings, mods[i].Mod.Text, mods[i].Mod.De, mods[i].Mod.Max, line => WaystoneMods.Match(line) is { } f && f.Mod.Text == mods[i].Mod.Text) is { } filled)
                        mods[i] = mods[i] with { Value = filled.Value, Line = mods[i].Line + " [" + filled.Glyphs + "]" };
                reading = reading with { Mods = mods };
            }
            return new(reading, null);
        }
        else
        {
            TabletReading? reading = null;
            foreach (var parsed in texts.Select(TabletTooltip.Parse))
                if (parsed is not null)
                    reading = reading is null ? parsed : parsed.Kind == reading.Kind ? reading.Merge(parsed) : reading;
            if (reading is null)
                return null;
            if (reading.Mods.Any(m => m.Mod.HasValue && m.Value is null))
            {
                var mods = reading.Mods.ToList();
                for (int i = 0; i < mods.Count; i++)
                    if (mods[i].Mod.HasValue && mods[i].Value is null && GlyphValue(bgr, popup, readings, mods[i].Mod.Text, mods[i].Mod.De, mods[i].Mod.Max, line => TabletMods.Match(line, reading.Kind) is { } f && f.Mod.Text == mods[i].Mod.Text) is { } filled)
                        mods[i] = mods[i] with { Value = filled.Value, Line = mods[i].Line + " [" + filled.Glyphs + "]" };
                reading = reading with { Mods = mods };
            }
            return new(null, reading);
        }
    }

    // The value of a mod whose line the OCR read without its number, from the game's glyphs
    // in the gap the number must lie in (NumberGlyphs): the first line of any reading that
    // names the mod gives the word boxes; the mod's text in the line's language says where
    // the number stands. A value the glyphs cannot vouch for (shape, roll, size) stays unread.
    private (int Value, string Glyphs)? GlyphValue(Mat bgr, double popup, IEnumerable<OcrLine[]> readings, string text, string? de, int? max, Func<string, bool> names)
    {
        var line = readings.SelectMany(r => r).FirstOrDefault(l => names(l.Text));
        if (line is null)
            return null;
        string key = WaystoneMods.KeyOf(line.Text);
        string template = de is not null && TitleReader.Distance(key, WaystoneMods.KeyOf(de)) < TitleReader.Distance(key, WaystoneMods.KeyOf(text)) ? de : text;
        if (NumberGlyphs.Gap(template, line.Words, popup, bgr.Width, bgr.Height) is not { } gap || Pipeline.Glyphs.Read(bgr, gap, popup) is not { } glyphs)
            return null;
        int? value = WaystoneMods.ValueOf(glyphs);
        if (value is { } v && text.Contains("-#"))
            value = Math.Abs(v);
        return WaystoneMods.Plausible(value, max, glyphs) is { } plausible ? (plausible, glyphs) : null;
    }

    // Top to bottom; pieces of one row (the OCR splits a line now and then) left to right.
    private static string[] Lines(OcrLine[] lines) => lines.OrderBy(l => l.CenterY).ThenBy(l => l.Left).Select(l => l.Text).ToArray();

    private static int Px(double reference, double popup) => (int)Math.Round(reference * popup);

    // The title row of a tooltip (the bar with name and "Waystone (Tier n)") as a small
    // grey picture, zero mean and unit norm: what a glance compares with.
    internal static Rect TitleArea(Box box, double popup, int width, int height) =>
        new Rect(box.X, box.Y, Math.Min(box.Width, Px(Tune.MaxWidth, popup)), Px(Tuning.Titles.TwoLines, popup)).Intersect(new Rect(0, 0, width, height));

    internal static float[] TitleLook(Mat bgr, Box box, double popup)
    {
        var area = TitleArea(box, popup, bgr.Width, bgr.Height);
        if (area.Width < Tune.GlanceShrink || area.Height < Tune.GlanceShrink)
            return [];
        using var crop = new Mat(bgr, area);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(area.Width / Tune.GlanceShrink, area.Height / Tune.GlanceShrink), 0, 0, InterpolationFlags.Area);
        small.GetArray(out byte[] bytes);
        var values = bytes.Select(b => (float)b).ToArray();
        float mean = values.Average();
        double norm = 0;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] -= mean;
            norm += values[i] * values[i];
        }
        float n = (float)Math.Sqrt(norm) + 1e-6f;
        for (int i = 0; i < values.Length; i++)
            values[i] /= n;
        return values;
    }

    internal static double Same(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
            return 0;
        double dot = 0;
        for (int i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    // The price asked for in the background: the trade site may take seconds, and a label
    // must not hold the control that long (hotkey, follow and scan wait for it; review
    // 20.10.). The glance picks the answer up and has the window labelled anew.
    private sealed class PriceFetch
    {
        public readonly CancellationTokenSource Cancel = new();
        public Task<TradeAnswer> Task = System.Threading.Tasks.Task.FromResult(TradeAnswer.NoListing);
        public bool Taken;
    }

    // Glances in a row that did not find the title bar; shared by the window's instances.
    private sealed class Misses
    {
        public int Count;
    }

    // Priced: the trade site was asked for this reading (with or without an answer); asked
    // again when the reading's values change, or AskAgainAfter without an answer.
    // The exact-price key on a shown tooltip: asked anew, as the item is (Exact), until another
    // item's tooltip comes. null: the window is not a tooltip's.
    public static IOpenWindow? Exact(IOpenWindow open)
    {
        if (open is not Open shown)
            return null;
        shown.Fetch?.Cancel.Cancel();
        return shown with { Exact = true, Answer = null, Fetch = null, Priced = false };
    }

    // Answer: what the trade site said for this reading (null: not yet).
    private sealed record Open(TooltipGameWindow Kind, Box Tooltip, Reading Reading, double Popup, TradeAnswer? Answer, PriceFetch? Fetch = null, bool Priced = false, DateTimeOffset AskedAt = default, float[]? Look = null, bool Exact = false) : IOpenWindow
    {
        private readonly Misses misses = new();

        public IGameWindow Window => Kind;

        private TradePrice? Price => Answer?.Price;

        // The title row: the glance asks only whether it still looks as it did (a tooltip of
        // another item, or none, looks different).
        public Box? GlanceArea
        {
            get
            {
                var area = TitleArea(Tooltip, Popup, int.MaxValue, int.MaxValue);
                return new(area.X, area.Y, area.Width, area.Height);
            }
        }

        public Glance Glance(Mat frame, PriceBook? ready, LookContext context)
        {
            double same = Look is null ? 1 : Same(Look, TitleLook(frame, Tooltip, Popup));
            if (same < Tune.GlanceMatch)
            {
                if (++misses.Count < Tune.GoneAfterMisses)
                    return Core.Glance.Unchanged;
                Fetch?.Cancel.Cancel();
                return new Glance.Gone($"{Reading.What.ToLowerInvariant()} tooltip gone ({misses.Count} glances, title row {same:0.00})");
            }
            misses.Count = 0;
            if (Fetch is { Taken: false } fetch && Answered(fetch))
            {
                fetch.Taken = true;
                return new Glance.Relabel(this with { Answer = fetch.Task.Result, Fetch = null, Priced = true, AskedAt = DateTimeOffset.UtcNow });
            }
            return Core.Glance.Unchanged;
        }

        // Read again: a number the OCR lost in one picture it may find in the next; what was
        // read before is kept where the new reading has nothing. A price belongs to the values
        // it was asked with: when they change, it goes and is asked anew.
        public async Task<IOpenWindow?> FollowAsync(GameCapture.Frame frame, LookContext context)
        {
            var boxes = Kind.Pipeline.WaystoneTooltips(frame.Image, frame.Popup);
            var box = boxes.FirstOrDefault(b => Math.Abs(b.X - Tooltip.X) <= Tune.GlanceSlack && Math.Abs(b.Y - Tooltip.Y) <= Tune.GlanceSlack);
            if (box is null)
            {
                Fetch?.Cancel.Cancel();
                context.Log($"hidden: {Reading.What.ToLowerInvariant()} tooltip gone");
                return null;
            }
            var reading = await Kind.ReadAsync(frame.Image, box, frame.Popup);
            if (reading is null)
            {
                Fetch?.Cancel.Cancel();
                context.Log($"hidden: {Reading.What.ToLowerInvariant()} tooltip no longer read");
                return null;
            }
            bool same = Reading.SameItem(reading);
            var merged = same ? Reading.Merge(reading) : reading;
            bool sameValues = same && merged.SameValues(Reading);
            if (!sameValues)
                Fetch?.Cancel.Cancel();
            return this with { Tooltip = box, Reading = merged, Popup = frame.Popup, Answer = sameValues ? Answer : null, Fetch = sameValues ? Fetch : null, Priced = sameValues && Priced, Look = TitleLook(frame.Image, box, frame.Popup), Exact = same && Exact };
        }

        // A question finished and not cancelled: its answer stands. (A cancelled one returns
        // "no listing", which is no answer: the item was left or its values changed.)
        private static bool Answered(PriceFetch fetch) => fetch.Task.IsCompleted && !fetch.Cancel.IsCancellationRequested;

        public async Task<Picture> LabelAsync(GameCapture.Frame frame, Task<PriceBook> prices, LookContext context)
        {
            // An answer that is in is taken here too, not only by the glance: the engine drops a
            // glance whose window a follow replaced meanwhile, and the glance has marked the
            // answer taken by then; "…" stood for good (review before 1.2.0).
            if (Fetch is { } done && Answered(done))
                return await (this with { Answer = done.Task.Result, Fetch = null, Priced = true, AskedAt = DateTimeOffset.UtcNow }).LabelAsync(frame, prices, context);
            var book = await prices;
            // The profile the question is asked in: waystone, of the saved profiles the one the
            // item has most in common with (WaystoneProfile.Choose); tablet, the kind's profile.
            // None: the item itself. Matches: whether the item meets every condition of it.
            string profileName;
            bool matches;
            // Asked by itself with the live switch (Settings.LiveTradePrice); the exact-price key
            // asks even while it is off: the user asked for this one item (21.10.2026).
            // Nothing of the tooltip understood (a client language the program does not read):
            // no question, it would price any item of the tier or kind.
            bool tooltipPrice = (context.Settings.LiveTradePrice || Exact) && Reading.Understood;
            int tolerance;
            Func<ITradePrices, string, CancellationToken, Task<TradeAnswer>> ask;
            if (Reading.Waystone is { } waystone)
            {
                var settings = context.Settings.Waystones;
                var profile = Exact ? null : WaystoneProfile.Choose(settings, waystone);
                (profileName, tolerance, matches) = (profile?.Name ?? "", settings.Tolerance, profile?.Matches(waystone, settings.Tolerance) ?? true);
                ask = (source, league, token) => source.ForAsync(waystone, profile, tolerance, league, token);
            }
            else
            {
                var tablet = Reading.Tablet!;
                var settings = context.Settings.Tablets;
                var profile = Exact ? null : settings.For(tablet.Kind);
                (profileName, tolerance, matches) = (profile?.Kind ?? "", settings.Tolerance, profile?.Matches(tablet, settings.Tolerance) ?? true);
                ask = (source, league, token) => source.ForAsync(tablet, profile, tolerance, league, token);
            }
            string asIn = profileName.Length > 0 ? profileName : Texts.Get("TooltipExact");
            var fetch = Fetch;
            var now = DateTimeOffset.UtcNow;
            // No listing: asked again after AskAgainAfter; not asked (the site's limit, its
            // refusal, no rates yet): once the site is free again, at the soonest TradeRetry.
            bool askAgain = Priced && Price is null && Answer is { } said
                && now - AskedAt > (said.Asked ? Tune.AskAgainAfter : Max(said.RetryIn, Tune.TradeRetry));
            if (Price is null && (!Priced || askAgain) && fetch is null && tooltipPrice && Kind.Prices is { } source && !context.Observe)
            {
                string what = Reading.What;
                context.Log(askAgain ? $"{what} price: asking again" : $"{what} price: asking for {Reading.Title}{(Reading.Waystone is { } w ? ", " + Summary(w) : "")}, as {asIn}");
                var started = new PriceFetch();
                var log = context.Log;
                string league = book.League;
                var question = ask;
                started.Task = Task.Run(async () =>
                {
                    try
                    {
                        var answer = await question(source, league, started.Cancel.Token);
                        log(answer.Price is { } price ? $"{what} price: {price.Divine:0.###} div from {price.Listings} listings ({price.Query})"
                            : answer.Asked ? $"{what} price: none" : $"{what} price: not asked ({answer.Why}, again in {answer.RetryIn.TotalSeconds:0} s)");
                        return answer;
                    }
                    catch (OperationCanceledException)
                    {
                        log($"{what} price: asking cancelled");
                        return TradeAnswer.NoListing;
                    }
                    catch (Exception e) when (TradeClient.IsFault(e))
                    {
                        // The network or the site failed: tried again like a refusal.
                        log($"{what} price: " + e.Message);
                        return new TradeAnswer(null, NotAsked.Refused, Tune.TradeRetry);
                    }
                });
                fetch = started;
            }
            var labels = new List<Label>();
            if (Price is { } priced)
            {
                var quote = new Price(priced.Divine, book.ExaltedPerDivine, priced.Listings, Reading.What) { ChaosPerDivine = book.ChaosPerDivine };
                string unit = context.Settings.PriceUnit;
                labels.Add(new(TitleBar(), quote.TextIn(unit), null, quote.UnitIn(unit), Labels.TierOf(quote.Divine), false, Row: true, AlignRight: true));
            }
            else if (tooltipPrice)
            {
                // No price yet at the title bar, a grey mark where it will stand: asking "…",
                // no listing "–", the site busy "⏳" (21.10.2026: in the game the three
                // looked alike, nothing at all).
                string mark = fetch is not null ? "…" : Answer is { Asked: false } ? "⏳" : Priced ? "–" : "…";
                labels.Add(new(TitleBar(), null, mark, "ex", Tier.Low, false, Row: true, AlignRight: true));
            }
            string priceText = Price is not null ? labels[0].Text!
                : !Reading.Understood ? Texts.Get("TooltipNotRead")
                : !tooltipPrice ? Texts.Get("WaystonePriceOff")
                : fetch is not null ? Texts.Get("WaystonePriceAsking")
                : Answer is { Asked: false } busy ? busy.Reason()
                : Texts.Get("WaystoneNoPrice");
            return new Picture(
                this with { Fetch = fetch, AskedAt = fetch is not null && fetch != Fetch ? now : AskedAt, Priced = Priced && !(fetch is not null && fetch != Fetch) },
                labels.ToArray(),
                null,
                Reading.Waystone is { } ws ? Texts.Get("WaystoneStatus", ws.Tier, priceText) : Texts.Get("TabletStatus", Reading.Tablet!.Kind.Name, priceText),
                $"{Reading.Title}, {Reading.ModCount} mods, as {asIn}{(matches ? "" : " (not every condition)")}, {priceText}",
                Reading.Waystone is { } wr
                    ? new { Window = "waystone", Tooltip, Reading = wr, Profile = profileName, Matches = matches, Price, NotAsked = Answer?.Why, Labels = labels }
                    : new { Window = "tablet", Tooltip, Reading = Reading.Tablet, Profile = profileName, Matches = matches, Price, NotAsked = Answer?.Why, Labels = labels }
            );
        }

        private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

        // The right end of the title bar, where the price stands.
        private Box TitleBar() => new(Tooltip.X, Tooltip.Y, Math.Min(Tooltip.Width, Px(Tune.MaxWidth, Popup)) - Px(Tune.PriceInset, Popup), Px(Tuning.Titles.TwoLines, Popup));

        private static string Summary(WaystoneReading r) =>
            string.Join(", ", new[] { ("IIR", r.ItemRarity), ("pack", r.PackSize), ("rarity", r.MonsterRarity), ("eff", r.Effectiveness), ("drop", r.DropChance) }
                .Where(p => p.Item2 is { } v && v > 0).Select(p => $"{p.Item1} {p.Item2}"));
    }
}
