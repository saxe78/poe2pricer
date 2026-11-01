using OpenCvSharp;
using Poe2Pricer.Core.Market;
using Tune = Poe2Pricer.Core.Tuning.Favours;

namespace Poe2Pricer.Core;

// The Ritual "Favours" window: grid, items, greyed-out omens, tooltip names.
// What was read once stays. Every cell is remembered as it looked when
// it was read (CellMemory), and each later frame is compared with that: a covered cell loses its
// label and keeps its name, a greyed-out or deferred one keeps both, a bought item leaves the
// empty cell, a cell that was covered when it was read is read once it is free. Only another
// roll reads everything again, and a roll is what the game says it is: another number on the
// reroll button (RerollMark). The look of the cells is no measure of a new roll: frames coming
// and going around deferred items, greying and the cursor together pass for one, everything
// would be read again, and a greyed-out omen named by its tooltip would be "!" once more.
// Where a tooltip lies is guessed from its outline (OccluderDetector) only at the first look,
// for cells that have no picture yet: the guess breaks over a bright scene, under the
// performance overlay and on the title bar of Vaal uniques.
public sealed class FavoursGameWindow(Pipeline pipeline) : IGameWindow
{
    private const int Columns = CellMemory.Columns,
        Rows = CellMemory.Rows;

    public WindowProfile Profile { get; } = new("Favours", AutoSwitch.Ritual);

    public bool IsThere(GameCapture.Frame frame) => pipeline.Locator.Locate(frame.Image) is not null;

    // The window as it was labelled last, per UI mode. One ritual has one reward window for all
    // its altars: closed and opened again it shows the same roll unless
    // the number on the reroll button changed — or it is another map's ritual, which may show
    // the same number. So an opened window is always read, and what it holds decides: the same
    // items at the same places are the same roll, and names known from before — read from a
    // tooltip, or before an item was greyed out — are taken over for the greyed-out omens the
    // picture cannot tell apart. The look of the cells would not do: deferring an item, closing
    // and opening could pass for another roll. Measured: in the same roll every item read for
    // certain lies as remembered (greyed out, deferred, in defer mode and after a purchase
    // too); in another roll only a few do.
    private readonly Open?[] remembered = new Open?[2];

    public async Task<IOpenWindow?> LookAsync(GameCapture.Frame frame, LookContext context)
    {
        if (pipeline.Locator.Locate(frame.Image) is not { } grid)
            return null;
        var read = await ReadAsync(frame, grid);
        if (context.Keep is not null && read.Doubtful.Any(d => d))
            read = read with { FirstLook = frame.Image.Clone() };
        if (remembered[(int)grid.Mode] is not { } known)
            return read;
        if (known.Number?.OtherNumber(frame.Image, grid) == true)
        {
            context.Log("Favours window back, different roll: reroll count changed");
            return read;
        }
        // The same roll: nothing lies there that cannot be of the remembered roll, and at least
        // SameRollItems items are there for certain as remembered. One alone is no proof — items
        // deferred in earlier rituals are carried into the next one and lie at the left again.
        var (same, other) = Agreement(known, read.Scan.Items);
        if (same < Tune.SameRollItems || other.Length > 0)
        {
            context.Log($"Favours window back, different roll: {same} items as remembered, {other.Length} different{(other.Length > 0 ? " (" + string.Join("; ", other.Take(5)) + ")" : "")}");
            return read;
        }
        context.Log("Favours window back, same roll");
        var carried = known.Carried.Where(c => !known.Scan.Items.Any(i => Place(i) == Place(c))).Concat(known.Scan.Items).ToArray();
        return read with { Scan = read.Scan with { Items = read.Scan.Items.Select(i => Inherit(i, carried)).ToArray() }, Carried = carried };
    }

    private static (int Column, int Row, int Columns, int Rows) Place(ItemMatch item) => (item.Column, item.Row, item.Columns, item.Rows);

    // A greyed-out omen of a shape group whose name is not known: shown as "!".
    private static bool Unnamed(ItemMatch item) => item.Group is not null && item.ResolvedBy is null;

    // An item the picture names for certain: not a guess, and not a greyed-out omen of a group
    // that looks alike.
    private static bool Certain(ItemMatch item) => item.CatalogId is not null && !item.Uncertain && item.Group is null;

    // The items read now against the roll that was remembered.
    // Same: named for certain now, and the same item was remembered at the same place.
    // Other: what lies there now cannot be what was remembered there — another item, another
    // size, a greyed-out omen of a group the remembered item does not belong to, or an item on
    // cells that were read as empty. Within one roll nothing of that can happen: items only go
    // (bought) or change their look.
    // Neither: a guess (now or then), a place that was never read (it lay under a tooltip), a
    // greyed-out omen whose group fits the remembered name.
    private static (int Same, string[] Other) Agreement(Open remembered, ItemMatch[] now)
    {
        static bool Guess(ItemMatch i) => i.CatalogId is null || i.Uncertain && i.Group is null;
        // For the log: what lies where now, and what was remembered there.
        static string Told(ItemMatch i) => $"R{i.Row + 1}C{i.Column + 1} {i.Columns}×{i.Rows} \"{(Unnamed(i) ? "greyed group omen" : i.Name)}\"";
        int same = 0;
        var other = new List<string>();
        foreach (var item in now.Where(i => !Guess(i)))
        {
            var cells = CellsOf(item).ToHashSet();
            var there = remembered.Scan.Items.Where(r => CellsOf(r).Any(cells.Contains)).ToArray();
            if (there.Length == 0)
            {
                if (cells.All(c => remembered.Memory.Stored(c) && !remembered.Memory.Occupied(c)))
                    other.Add($"{Told(item)}, remembered: empty");
                continue;
            }
            if (there.Any(Guess))
                continue;
            if (there.Length > 1 || Place(there[0]) != Place(item))
            {
                other.Add($"{Told(item)}, remembered: {string.Join(" + ", there.Select(Told))}");
                continue;
            }
            var was = there[0];
            if (Certain(item))
            {
                if (Unnamed(was) ? !was.Group!.Contains(item.CatalogId!) : was.CatalogId != item.CatalogId)
                    other.Add($"{Told(item)}, remembered: {Told(was)}");
                else if (!Unnamed(was))
                    same++;
            }
            else if (Unnamed(was) ? !was.Group!.Intersect(item.Group!).Any() : !item.Group!.Contains(was.CatalogId!))
                other.Add($"{Told(item)}, remembered: {Told(was)}");
        }
        return (same, other.ToArray());
    }

    // A greyed-out omen of a shape group takes the name remembered at its place, when that name
    // belongs to its group: it was read before the omen was greyed out, or from its tooltip.
    private static ItemMatch Inherit(ItemMatch item, ItemMatch[] remembered)
    {
        if (!Unnamed(item))
            return item;
        var known = remembered.FirstOrDefault(r => Place(r) == Place(item) && r.CatalogId is not null && !Unnamed(r) && item.Group!.Contains(r.CatalogId));
        return known is null ? item : item with { CatalogId = known.CatalogId, Name = known.Name, Uncertain = false, ResolvedBy = known.ResolvedBy ?? "remembered" };
    }

    // What runs beside a read of the picture. If it throws, the read is waited for before the
    // failure goes on: the caller disposes the picture then, and the read must not still be
    // looking at it.
    private static async Task<T> Beside<T>(Task reading, Func<T> work)
    {
        try
        {
            return work();
        }
        catch
        {
            await reading.ContinueWith(_ => { }, TaskScheduler.Default);
            throw;
        }
    }

    // A full read: every cell; the cells under a popup are not trusted and get no picture.
    // Recognition and the search for popups run side by side (0.45 s and 0.25 s).
    private async Task<Open> ReadAsync(GameCapture.Frame frame, FavoursGrid grid)
    {
        var reading = Task.Run(() => pipeline.Scan(frame.Image, grid));
        var found = await Beside(reading, () => pipeline.Occluders.Detect(frame.Image, grid, frame.Popup, frame.TextSize));
        var scan = await reading;
        var popups = Placed(found, frame, scan);
        var blocked = Blocked(scan, popups);
        var picture = GridPicture.Of(frame.Image, grid);
        var memory = CellMemory.Empty(grid).With(picture, Enumerable.Range(0, blocked.Length).Where(c => !blocked[c]), Occupancy(frame.Image, grid));
        bool Trusted(ItemMatch item) => !CellsOf(item).Any(c => blocked[c]);
        return new Open(
            this,
            pipeline,
            scan with { Items = scan.Items.Where(Trusted).ToArray() },
            scan.Items.Where(i => !Trusted(i)).ToArray(),
            memory,
            Unsure(scan.Items.Where(Trusted), new bool[Rows * Columns]),
            Boxes(popups),
            CellMeans(frame.Image, grid),
            (frame, popups),
            Mark(frame.Image, grid, popups),
            []
        );
    }

    // The number on the reroll button, taken where no popup lies on the button: a tooltip's
    // letters would pass for digits.
    private static RerollMark? Mark(Mat frame, FavoursGrid grid, Occluder[] popups)
    {
        var button = RerollCount.Button(grid);
        return popups.Any(p => p.Bounds.Overlaps(button, 1)) ? null : RerollMark.Of(frame, grid);
    }

    // The game opens a tooltip right above the item under the mouse: its bottom edge lies on
    // the item's top edge (measured: within 1 px).
    // The outline search finds that edge by a bright line, which fails over a bright scene (it
    // stops at a text line) and over some pictures (it runs on to the screen edge). With the
    // mouse on an item the edge is known instead. The item's top: where its green ground ends
    // (the game colours every cell of the item under the mouse, however the recognition cut it
    // up — a staff with sockets); items with a gold frame show no green, there the
    // recognised item counts.
    private static Occluder[] Placed(Occluder[] popups, GameCapture.Frame frame, Recognition scan)
    {
        var grid = scan.Grid!;
        if (grid.Mode != UiMode.KeyboardMouse || frame.Pointer is not { } pointer || popups.Length == 0)
            return popups;
        int column = (int)Math.Floor((pointer.X - grid.Bounds.X) / grid.CellSize),
            row = (int)Math.Floor((pointer.Y - grid.Bounds.Y) / grid.CellSize);
        if (column < 0 || column >= Columns || row < 0 || row >= Rows)
            return popups;
        int top;
        if (Recognizer.CellUnderMouse(frame.Image, grid, column, row))
        {
            while (row > 0 && Recognizer.CellUnderMouse(frame.Image, grid, column, row - 1))
                row--;
            top = FavoursLocator.CellBox(grid, column, row, 1, 1).Y;
        }
        else if (scan.Items.FirstOrDefault(i => CellsOf(i).Contains(row * Columns + column)) is { } item)
            top = item.Bounds.Y;
        else
            return popups;
        return popups
            .Select(o =>
                o.Kind.StartsWith("tooltip-") && o.Bounds.X <= pointer.X && pointer.X <= o.Bounds.Right && o.Bounds.Y < top
                    ? o with { Bounds = o.Bounds with { Height = top - o.Bounds.Y } }
                    : o
            )
            .ToArray();
    }

    private static Func<int, bool> Occupancy(Mat frame, FavoursGrid grid) => cell => Recognizer.CellOccupied(frame, grid, cell % Columns, cell / Columns);

    // Mean brightness of each cell. The grid stands still: between two frames with nothing
    // moved the means differ by at most 0.2; the mouse on another item, the controller cursor, a
    // tooltip, a purchase change at least one cell by 3.4 or more (Tuning.Favours.CellMoved).
    private static double[] CellMeans(Mat frame, FavoursGrid grid)
    {
        var means = new double[Rows * Columns];
        var area = grid.Bounds.Rect().Intersect(new Rect(0, 0, frame.Width, frame.Height));
        if (area.Width <= 0 || area.Height <= 0)
            return means;
        using var crop = new Mat(frame, area);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        for (int y = 0; y < Rows; y++)
        for (int x = 0; x < Columns; x++)
        {
            var cell = FavoursLocator.CellBox(grid, x, y, 1, 1);
            var box = new Rect(cell.X - area.X, cell.Y - area.Y, cell.Width, cell.Height).Intersect(new Rect(0, 0, gray.Width, gray.Height));
            if (box.Width <= 0 || box.Height <= 0)
                continue;
            using var seen = new Mat(gray, box);
            means[y * Columns + x] = Cv2.Mean(seen).Val0;
        }
        return means;
    }

    private static bool Moved(double[] before, double[] now)
    {
        for (int i = 0; i < before.Length; i++)
            if (Math.Abs(now[i] - before[i]) > Tune.CellMoved)
                return true;
        return false;
    }

    // Cells whose reading found no usable candidate: nothing the catalog knows, or the cell was
    // covered without the popup's outline being found (the title bar of some tooltips).
    // They are read again when they get clearly brighter: under another tooltip they would only
    // be misread again.
    private static bool[] Unsure(IEnumerable<ItemMatch> read, bool[] before)
    {
        var unsure = (bool[])before.Clone();
        foreach (var item in read)
        foreach (int cell in CellsOf(item))
            unsure[cell] = item.Uncertain && item.Group is null;
        return unsure;
    }

    private static Box[] Boxes(Occluder[] popups) => popups.Select(p => p.Bounds).ToArray();

    private static IEnumerable<int> CellsOf(ItemMatch item)
    {
        for (int y = item.Row; y < item.Row + item.Rows; y++)
        for (int x = item.Column; x < item.Column + item.Columns; x++)
            yield return y * Columns + x;
    }

    // Cells that cannot be trusted on this frame by the popups' outlines: under a popup, or part
    // of an item the popup hides (Recognition.WithoutOccluded); the cells of an item that only
    // grazes a popup are trusted with it.
    private static bool[] Blocked(Recognition scan, Occluder[] popups)
    {
        var blocked = new bool[Rows * Columns];
        if (popups.Length == 0)
            return blocked;
        for (int y = 0; y < Rows; y++)
        for (int x = 0; x < Columns; x++)
        {
            var box = FavoursLocator.CellBox(scan.Grid!, x, y, 1, 1);
            blocked[y * Columns + x] = popups.Any(p => p.Bounds.Overlaps(box, 1));
        }
        var seen = scan.WithoutOccluded(popups);
        foreach (var item in seen.Items)
        foreach (int cell in CellsOf(item))
            blocked[cell] = false;
        foreach (var item in seen.Hidden!)
        foreach (int cell in CellsOf(item))
            blocked[cell] = true;
        return blocked;
    }

    // Scan: the items that were read from trusted cells, with names read from tooltips kept once
    // the tooltip is gone. Unseen: what the last read made of the cells it could not trust (for
    // the diagnosis only). Memory: every cell as it was read. Doubtful: cells read without a
    // usable result. Popups: where the popups' outlines
    // lay at the last try to read cells without a picture. Cells: the cell means of the frame
    // last followed. Seen: the popups found on a frame, so that labelling that frame does not
    // search them again. Number: the reroll button as it was read; null as long as it could not
    // be seen. Rolled: the glance saw another number; the follow reads the new roll, and until
    // it has, the glance has nothing more to say about it (told once: every further report
    // would throw away the read that is running). Carried: the items of this roll as they were
    // known before the window was closed, for cells that are read only later.
    private sealed record Open(
        FavoursGameWindow Kind,
        Pipeline Pipeline,
        Recognition Scan,
        ItemMatch[] Unseen,
        CellMemory Memory,
        bool[] Doubtful,
        Box[] Popups,
        double[] Cells,
        (GameCapture.Frame Frame, Occluder[] Popups)? Seen,
        RerollMark? Number,
        ItemMatch[] Carried,
        bool Rolled = false,
        Mat? FirstLook = null
    ) : IOpenWindow
    {
        // FirstLook: the picture of the first reading while cells were doubtful — kept only
        // with extended diagnostics on (LookContext.Keep), handed over with the picture of the
        // reading that resolves them (evidence for OFFEN U12: the first look after the window
        // opens read worse than a picture seconds later), then dropped. Freed when the follow
        // reads anew; a window closed before that leaves it to the finalizer (review 19.10.).
        public IGameWindow Window => Kind;

        // The grid, the anchor and the reroll button: the glance looks at the number too.
        public Box? GlanceArea => Pipeline.Locator.LookArea(Scan.Grid!).Union(RerollCount.Button(Scan.Grid!));

        // The whole width down to the anchor under the grid (GlanceArea reaches it: the anchor
        // sits a few pixels below the grid's bottom edge, Locate needs it): the popups are
        // searched across the width above the grid and beside it to the right edge
        // (OccluderDetector.Detect), the tooltip that names an omen stands beside the grid, the
        // dialog and the hints lie over it. Only the band below is left out — a fifth of the picture.
        public Box? FollowArea => new(0, 0, (int)Math.Ceiling(Tuning.Screen.ReferenceHeight * Tuning.Screen.WidestAspect), GlanceArea!.Bottom);

        // The window is open as long as its button or the grid that was read is on the frame:
        // in defer mode the button reads "Defer Item", and whatever else the game may write
        // there, the grid stays. Something moved in the grid — mouse, cursor, tooltip, a
        // purchase: follow now instead of at the next full second. Another roll
        // (another number on the reroll button): what is shown belongs to other items and goes
        // at once, before the new roll is read.
        public Glance Glance(Mat frame, PriceBook? ready, LookContext context)
        {
            var picture = GridPicture.Of(frame, Scan.Grid!);
            if (Pipeline.Locator.Locate(frame) is null && !Memory.StillThere(picture))
                return new Glance.Gone("Favours window no longer detected");
            var cells = CellMeans(frame, Scan.Grid!);
            bool rolled = !Rolled && Number?.OtherNumber(frame, Scan.Grid!) == true;
            if (rolled)
                Evidence(context, "roll-glance", frame);
            if (!rolled && !Moved(Cells, cells))
                return Core.Glance.Unchanged;
            return new Glance.Reread(this with { Cells = cells, Rolled = Rolled || rolled }, ClearNow: rolled);
        }

        public async Task<IOpenWindow?> FollowAsync(GameCapture.Frame frame, LookContext context)
        {
            var known = Scan.Grid!;
            var grid = Pipeline.Locator.Locate(frame.Image);
            var picture = GridPicture.Of(frame.Image, known);
            if (grid is null ? !Memory.StillThere(picture) : grid.Mode != known.Mode)
            {
                context.Log(grid is null ? "hidden: Favours window no longer detected" : "hidden: UI mode changed");
                return null;
            }
            // The button is found to the pixel; a window that really lies elsewhere is read anew.
            if (grid is not null && (Math.Abs(grid.Bounds.X - known.Bounds.X) > 2 || Math.Abs(grid.Bounds.Y - known.Bounds.Y) > 2))
            {
                context.Log("Favours window moved, rescanning");
                FirstLook?.Dispose();
                return await Kind.ReadAsync(frame, grid);
            }
            var looks = Memory.Look(picture);
            var now = this with { Cells = CellMeans(frame.Image, known), Seen = null };
            // A dialog dims the whole window: nothing can be told from this frame.
            if (CellMemory.WindowDimmed(looks))
                return now;
            if (Rolled || Number?.OtherNumber(frame.Image, known) == true)
            {
                context.Log("new roll detected, rescanning (reroll count changed)");
                Evidence(context, "roll-follow", frame.Image);
                FirstLook?.Dispose();
                return await Kind.ReadAsync(frame, grid ?? known);
            }
            // The button was covered when the window was read: its number is taken as soon as
            // no popup lies on it. A roll before that cannot be told.
            if (Number is null)
                now = now with { Number = Mark(frame.Image, known, Pipeline.Occluders.Detect(frame.Image, known, frame.Popup, frame.TextSize)) };
            return now.ReadFreed(frame, picture, looks, context);
        }

        // A roll was once told before the player rolled, and the real roll after it was not
        // told any more. Why is not known, no picture of that moment exists. Until the cause is
        // known, every roll that is told keeps its pictures: the frame the other number was
        // seen on, and the button as it was remembered.
        private void Evidence(LookContext context, string what, Mat frame)
        {
            if (context.Keep is not { } keep)
                return;
            keep(what, frame);
            if (Number is null)
                return;
            using var button = Number.Picture();
            keep(what + "-button", button);
        }

        // Bought items go; cells that have no picture yet, whose picture was a covered one, or
        // that were read without a usable result and are clearly brighter now, are read when
        // they are free. Everything else stays as it was read.
        private Open ReadFreed(GameCapture.Frame frame, GridPicture picture, CellLook?[] looks, LookContext context)
        {
            var cells = Enumerable.Range(0, looks.Length).ToArray();
            var gone = cells.Where(c => Memory.Occupied(c) && looks[c] is { Covered: true } && Memory.LooksEmpty(picture, c)).ToHashSet();
            var wanted = cells.Where(c => looks[c] is null or { Uncovered: true } || Doubtful[c] && looks[c]?.Brightness > Tune.DoubtfulBrighter).ToHashSet();
            if (gone.Count == 0 && wanted.Count == 0)
                return this;

            var memory = Memory.With(picture, gone, _ => false);
            var items = Scan.Items.Where(i => !CellsOf(i).All(gone.Contains)).ToArray();
            var doubtful = Doubtful.Select((d, c) => d && !gone.Contains(c)).ToArray();
            if (wanted.Count == 0)
                return this with { Scan = Scan with { Items = items }, Memory = memory, Doubtful = doubtful };

            // Cells without a picture cannot be compared: for them the popups' outlines are all
            // there is. Tried once per position of the popups.
            Occluder[] popups = [];
            if (wanted.Any(c => looks[c] is null))
            {
                popups = Placed(Pipeline.Occluders.Detect(frame.Image, Scan.Grid!, frame.Popup, frame.TextSize), frame, Scan);
                if (gone.Count == 0 && wanted.All(c => looks[c] is null) && Boxes(popups).SequenceEqual(Popups))
                    return this with { Seen = (frame, popups) };
            }
            var scan = Pipeline.Scan(frame.Image, Scan.Grid!);
            var blocked = Blocked(scan, popups);
            foreach (int c in cells)
                if (looks[c] is { Covered: true } && !gone.Contains(c))
                    blocked[c] = true;

            bool Free(ItemMatch item) => !CellsOf(item).Any(c => blocked[c]);
            var read = scan.Items.Where(i => Free(i) && CellsOf(i).Any(wanted.Contains)).Select(i => Inherit(i, Carried)).ToArray();
            var partly = scan.Items.Where(i => !Free(i)).SelectMany(CellsOf).ToHashSet();
            var taken = read.SelectMany(CellsOf).Concat(wanted.Where(c => !blocked[c] && !partly.Contains(c))).ToHashSet();
            if (taken.Count > 0)
                context.Log($"rescanned: {taken.Count} cells uncovered, {read.Length} items");
            // Doubtful cells of the first look now read for certain: the first look was the
            // worse one — both pictures kept (U12), the first one is not needed any more.
            var firstLook = FirstLook;
            if (firstLook is not null && context.Keep is { } keep && read.Any(i => Certain(i) && CellsOf(i).Any(c => Doubtful[c])))
            {
                int resolved = read.Where(Certain).SelectMany(CellsOf).Count(c => Doubtful[c]);
                context.Log($"first look read worse: {resolved} doubtful cells certain now, pictures kept");
                keep("first-look", firstLook);
                keep("first-look-later", frame.Image);
                firstLook.Dispose();
                firstLook = null;
            }
            return this with
            {
                FirstLook = firstLook,
                Scan = Scan with { Items = items.Where(old => !CellsOf(old).Any(taken.Contains)).Concat(read).OrderBy(i => i.Row).ThenBy(i => i.Column).ToArray() },
                Unseen = scan.Items.Where(i => !Free(i) && CellsOf(i).Any(c => !memory.Stored(c))).ToArray(),
                Memory = memory.With(picture, taken, Occupancy(frame.Image, Scan.Grid!)),
                Doubtful = Unsure(read, doubtful.Select((d, c) => d && !taken.Contains(c)).ToArray()),
                Popups = Boxes(popups),
                Seen = (frame, popups),
            };
        }

        // Where the price of an item is drawn: centred on its bottom edge.
        private static Box Spot(ItemMatch item) =>
            new(item.Bounds.X + (item.Bounds.Width - Tune.LabelSpotWidth) / 2, item.Bounds.Bottom - Tune.LabelSpotHeight, Tune.LabelSpotWidth, Tune.LabelSpotHeight);

        public async Task<Picture> LabelAsync(GameCapture.Frame frame, Task<PriceBook> prices, LookContext context)
        {
            var grid = Scan.Grid!;
            var picture = GridPicture.Of(frame.Image, grid);
            bool dimmed = CellMemory.WindowDimmed(Memory.Look(picture));
            // The item with the tooltip: under the controller cursor, or under the mouse pointer.
            var selected = grid.Mode == UiMode.Controller ? Pipeline.Selection.Selected(frame.Image, grid, Scan.Items) : null;
            var pointed =
                grid.Mode == UiMode.KeyboardMouse && frame.Pointer is { } at
                    ? Scan.Items.FirstOrDefault(i => i.Bounds.Holds(at.X, at.Y))
                    : null;
            // The popups' outlines are searched again (~0.25 s) only for what they are still
            // needed: the tooltip that names a greyed-out omen under the mouse or cursor, and
            // everything while the window is dimmed. Otherwise the hints beside the mouse and
            // the dialog are enough: the dialog is cheap (two small templates), and it does not
            // always dim the window — on the author's client it did not at all (23.10.2026, Large
            // at 1080 and 2160: the cells kept their brightness with the dialog open), so the
            // dimming is no gate for it (OFFEN F45: a dialog found by the first look only).
            var popups = Seen is { } seen && ReferenceEquals(seen.Frame, frame) ? seen.Popups : null;
            if (popups is null)
            {
                bool naming = (selected ?? pointed) is { } under && Unnamed(under);
                popups = dimmed || naming
                    ? Pipeline.Occluders.Detect(frame.Image, grid, frame.Popup, frame.TextSize)
                    : Pipeline.Occluders.Dialog(frame.Image, frame.TextSize) is { } dialog
                        ? [dialog, .. Pipeline.Occluders.Hints(frame.Image, grid)]
                        : Pipeline.Occluders.Hints(frame.Image, grid);
            }

            // A label goes when its place is covered: darkened like under a tooltip, replaced
            // like under its title bar, under the hint beside the mouse, or under the dialog. A
            // dimmed window (dialog) darkens every place; then only the dialog itself counts.
            // The item under the controller cursor does not count as replaced: the cursor's
            // frame and its tip lie in the label's spot, and what the stored picture has left
            // there is the item's own art — at 3840×2160 the structure read .23 on an Omen of
            // Bartering, .72–.76 on two others, and below ReplacedBelow on an Omen of Chaotic
            // Effectiveness, whose price went while the cursor stood on it (OFFEN F10,
            // 31.10.2026). Darkened it still goes: a tooltip over it hides the label as before.
            bool Covered(ItemMatch item) =>
                popups.Any(p => (p.Kind == "hint" || dimmed && p.Kind == "dialog") && p.Bounds.Overlaps(item.Bounds, Tuning.Occluders.HideOverlap))
                || !dimmed && Memory.Look(picture, Spot(item)) is { } spot && (spot.Covered || spot.Replaced && item != selected);
            var shown = new Recognition(grid, Scan.Items.Where(i => !Covered(i)).ToArray(), Scan.UnassignedCells, 0, popups, Scan.Items.Where(Covered).Concat(Unseen).ToArray());
            if (shown.Items.Any(Unnamed))
            {
                var read = new List<(Occluder, string[])>();
                foreach (var tooltip in popups.Where(o => o.Kind.StartsWith("tooltip-")))
                    read.Add((tooltip, await Pipeline.Titles.ReadAsync(frame.Image, tooltip, frame.Popup)));
                // The mouse pointer counts with keyboard and mouse only: with the controller it rests
                // somewhere, and when the cursor was not found, the name from the tooltip went to the
                // omen under it.
                shown = TooltipResolver.Resolve(shown, read, Pipeline.Recognizer.Catalog, selected is null ? null : shown.Items.FirstOrDefault(i => i == selected), grid.Mode == UiMode.KeyboardMouse ? frame.Pointer : null, frame.Language);
            }

            var book = await prices;
            var settings = context.Settings;
            var labels = Labels.For(shown.Items, book, Labels.FloorInDivine(settings.FloorValue, settings.FloorUnit, book), settings.PriceUnit);
            var named = shown.Items.Where(i => i.ResolvedBy is not null).ToDictionary(i => (i.Row, i.Column));
            var next = this with { Scan = Scan with { Items = Scan.Items.Select(i => named.GetValueOrDefault((i.Row, i.Column)) ?? i).ToArray() } };
            // Without the pictures: the remembered roll lives on until the next, a first look
            // not resolved by then would be pinned with it (review 19.10.).
            Kind.remembered[(int)grid.Mode] = next with { Seen = null, FirstLook = null };
            int priced = labels.Count(l => l.Text is not null);
            return new Picture(
                next,
                labels,
                null,
                priced == 0 && shown.Items.Length > 0 && settings.FloorValue > 0
                    ? Texts.Count("AllBelowThreshold", shown.Items.Length, settings.FloorValue, settings.FloorUnit)
                    : Texts.Get("FavoursStatus", Texts.Count("Items", shown.Items.Length), Texts.Count("Prices", priced), book.League),
                $"{shown.Items.Length} items, {labels.Length} labels, {shown.Occluders?.Length ?? 0} occluders, {shown.Hidden?.Length ?? 0} covered, window {frame.ScreenBounds}",
                new { Window = "favours", Favours = shown, Labels = labels }
            );
        }
    }
}
