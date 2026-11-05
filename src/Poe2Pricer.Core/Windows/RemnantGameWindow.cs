using OpenCvSharp;
using Poe2Pricer.Core.Market;
using Poe2Pricer.Core.Runes;
using RuneTune = Poe2Pricer.Core.Tuning.Runes;

namespace Poe2Pricer.Core;

// The remnant reward list ("Runeshape Combinations"): rows read by OCR, priced by name; game
// tooltips in the band beside the list (keyboard UI) turn the labels light.
public sealed class RemnantGameWindow(Pipeline pipeline, RuneChain chain) : IGameWindow
{
    internal Pipeline Pipeline { get; } = pipeline;
    public RuneChain Chain { get; } = chain;

    private bool toldOff;
    // Pictures kept as evidence this run, at most KeptLooks of a kind (encoding a whole picture
    // costs about half a second of a core; eleven in a quarter of an hour were felt as lag in
    // the game, 02.11.2026): of a list read again because it changed (Open.NeedsReread),
    // for what moves in a list that is read again and again.
    private const int KeptLooks = 3;
    private int keptChanges;
    // Pictures in which the controller's cursor row was lost (Open.Kept), kept this run.
    private int keptLost;
    // The list last shown, as far as it was seen: kept when the window closes, until another
    // list is shown. In memory only.
    private volatile RemnantList? remembered;

    public WindowProfile Profile { get; } = new("Remnant", AutoSwitch.Remnant);

    public bool IsThere(GameCapture.Frame frame) => Pipeline.Remnants.Locate(frame.Image) is not null;

    public async Task<IOpenWindow?> LookAsync(GameCapture.Frame frame, LookContext context)
    {
        if (Pipeline.Remnants.Locate(frame.Image) is not { } list)
            return null;
        // When this showing was first seen: before the rows are read (OCR and tiles take up to
        // a second; a button pressed meanwhile is a word on the list, review 05.11.2026).
        long opened = context.Now.UtcTicks;
        var rows = await Pipeline.Remnants.ReadAsync(frame.Image, list);
        var whole = remembered?.Reopened(rows);
        if (whole is not null && whole.Rows.Count > rows.Length)
            context.Log($"Remnant list recognized: {whole.Rows.Count} rows known (arrow once scrolled)");
        remembered = whole ??= RemnantList.Of(rows);
        var runes = Runes(frame.Image, list, rows, context);
        var tooltips = Pipeline.RemnantTooltips(frame.Image, list, frame.Popup);
        return new Open(this, list, rows, RemnantReader.Signature(frame.Image, list), tooltips, frame.Image.Height, whole, frame.Popup, runes, Hover(frame, list, rows, runes, Pipeline.Remnants, mouseMoved: false), Open.InBox(tooltips, list).Length > 0, new Open.Motion { Seen = context.Now.UtcTicks, Opened = opened, PointerAtOpen = frame.Pointer });
    }

    // The list went. Only a cancel (Esc, the gamepad's cancel button) or the choice of a row
    // (the click on it, the gamepad's confirm button) closes it (03.11.2026: no
    // walking away, no click beside it). So, when a cancel can be seen (LookContext): the
    // first press since this showing of the list was read decides — a cancel: nothing was
    // chosen; a confirm, or no press at all (the click is not read): the row under the mouse
    // or the controller's cursor is the choice, fixed, and its crowned tiles are what the
    // remnant passes on. Inside the list neither button does anything but close it (confirmed in play,
    // 05.11.2026), and the game keeps the list up for up to half a second after either
    // (measured 0.19–0.55 s); a press before the list was read is not a word on it (the
    // confirm button opens it at the remnant). Both buttons do other things in the game
    // (Circle is the dodge roll, Cross a skill), so a press after the one that closed the
    // list is play, not a word on the list. When none can be seen: the row is the choice provisionally, hollow
    // in the chain, and stays so (the bars above the remnants, read for that until
    // 03.11.2026, fixed wrong rows). A list without a crown in any row is a remnant already
    // chosen and opened again (03.11.2026 17:36: its "nothing" replaced a two-rune link):
    // nothing to learn — judged after the cancel, which says more (05.11.2026 18:05: a
    // cancelled list with its crowns on empty slots was put down to that). Not from a
    // diagnosis (Observe).
    private void Closed(Open open, LookContext context)
    {
        if (context.Observe)
            return;
        // Once per showing of the list: the follow and the quick check look at the same
        // moment from two pictures, each sees the list gone, and a quick check that started
        // on the reading before a rescan sees it too (log of 03.11.2026: "provisional" two
        // and three times at one closing, 05:06:10 with two different rows). The first to
        // see it closed has the say.
        if (open.Moving is { } shown && Interlocked.Exchange(ref shown.Closed, 1) != 0)
            return;
        if (!Chain.Tracked)
        {
            // Said once per run, at the moment it matters (ChainTracker says why when it knows).
            if (!toldOff)
                context.Log("Rune chain off: the game's Client.txt is not known (the game's folder was not found)");
            toldOff = true;
            return;
        }
        bool anyCrown = open.Runes.Any(row => row.Any(t => t.Crowned));
        const string noCrown = "Rune chain: the list closed without a crowned rune in any row (a remnant already chosen?), nothing learned";
        string key = RuneChain.KeyOf(open.Whole.Rows);
        bool controller = open.List.Mode == UiMode.Controller;
        string where = controller ? "under the cursor" : "under the mouse";
        // Whether a cancel can be seen at all: Esc through the keyboard hook, the gamepad's
        // cancel button through its state (both OverlayEngine.Escaped). Without, a closed list
        // is a choice or a cancel alike: the row is provisional.
        if (controller ? context.GamepadReadable : context.EscapeReadable)
        {
            var seen = open.Moving is { } m ? new DateTimeOffset(Interlocked.Read(ref m.Seen), TimeSpan.Zero) : context.Now;
            var opened = open.Moving is { } o ? new DateTimeOffset(Interlocked.Read(ref o.Opened), TimeSpan.Zero) : context.Now;
            // Either press since this showing was first read (Motion.Opened) — not before. A
            // window of EscapeSlack before the last picture (until 05.11.2026) missed a cancel
            // the list outlived by 0.39 s and fixed the row instead ("Rage", 19:47).
            DateTimeOffset? cancel = context.Escaped?.Invoke() is { } esc && esc >= opened ? esc : null;
            DateTimeOffset? confirm = context.Confirmed?.Invoke() is { } ok && ok >= opened ? ok : null;
            if (cancel is { } c && (confirm is not { } k || c < k))
            {
                context.Log(controller ? "Rune chain: the list closed with the cancel button, nothing chosen" : "Rune chain: the list closed with Esc, nothing chosen");
                return;
            }
            if (context.Now - seen > RuneTune.CloseStale)
            {
                context.Log($"Rune chain: the list was last seen {(context.Now - seen).TotalSeconds:0.0} s before it was found gone (the overlay stood still meanwhile), nothing learned");
                return;
            }
            if (!anyCrown)
            {
                context.Log(noCrown);
                return;
            }
            if (open.Hover < 0 || open.Hover >= open.Runes.Length)
            {
                // Where the mouse was, and the plates, for the log alone (03.11.2026 20:03: once
                // without a row at 2 rows, the reason not to tell without them).
                context.Log($"Rune chain: the list closed with no row {where}, nothing learned (mouse {(open.Pointer is { } p ? $"{p.X},{p.Y}" : "outside the game")}; list {open.List.List.X}–{open.List.List.Right}, rows {string.Join(", ", open.Rows.Select((r, i) => $"{i + 1}: {(int)r.CenterY - (open.Runes[i].Length > 0 ? (int)r.CenterY - open.Runes[i][0].Y : RuneBook.Rises(open.List, open.Rows)[i]) - RuneTune.RowHalfKeyboard}–{(int)r.CenterY + RuneTune.RowHalfKeyboard}"))})");
                return;
            }
            var chosen = RuneChain.Carried(open.Runes[open.Hover]);
            Chain.Confirm(key, chosen);
            string how = confirm is null ? "" : " on the confirm button";
            // A row without a crowned tile (three tiles where the crown's slot would be the
            // fourth, 05.11.2026 17:49) passes nothing on: an empty link, chosen all the same.
            context.Log(chosen.Length > 0
                ? $"Rune chain: {Names(chosen)} chosen (row {open.Hover + 1} {where} when the list closed{how})"
                : $"Rune chain: row {open.Hover + 1} chosen ({where} when the list closed{how}), no crowned rune in it, an empty link");
            return;
        }
        if (!anyCrown)
        {
            context.Log(noCrown);
            return;
        }
        if (open.Hover >= 0 && open.Hover < open.Runes.Length && RuneChain.Carried(open.Runes[open.Hover]) is { Length: > 0 } runes)
        {
            Chain.Guess(key, runes);
            context.Log($"Rune chain: {Names(runes)} provisional (row {open.Hover + 1} {where} when the list closed; a cancel is not seen)");
        }
    }

    // The rune tiles of every row (RuneBook.Tiles).
    private RuneTile[][] Runes(Mat image, RemnantWindow list, RemnantRow[] rows, LookContext context)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rises = RuneBook.Rises(list, rows);
        var tiles = rows.Select((r, i) => Pipeline.Runes.Tiles(image, list, r, rises[i])).ToList();
        int? rise = RuneBook.Rise(list, rows);
        // One row more than the OCR read: the row whose name is cut off at the list's bottom
        // edge still shows its tiles whole (controller UI, 01.11.). No name, no price; the
        // tiles alone. Only below the last row's tiles, inside the list.
        if (rows.Length > 0 && rise is { } r)
        {
            int pitch = list.Mode == UiMode.Controller ? RuneTune.RowPitchController : RuneTune.RowPitchKeyboard;
            var below = new RemnantRow(0, "", rows[^1].CenterY + pitch);
            var more = Pipeline.Runes.Tiles(image, list, below, r);
            int lastY = tiles[^1].Length > 0 ? tiles[^1][0].Y : (int)Math.Round(rows[^1].CenterY) - r;
            if (more.Length > 0 && more[0].Y >= lastY + pitch / 2 && more.All(t => t.Y + RuneTune.TileSize / 2 <= list.List.Bottom))
                tiles.Add(more);
        }
        context.Log($"Remnant runes: {string.Join(", ", tiles.Select(row => row.Any(x => x.Crowned) ? string.Join("+", row.Where(x => x.Crowned).Select(c => (c.Rune?.Id ?? "?") + $"@{c.Index + 1}")) : "-"))} ({watch.ElapsedMilliseconds} ms)");
        return tiles.ToArray();
    }

    private static string Names(IReadOnlyList<Rune> runes) => runes.Count == 0 ? "nothing" : string.Join("+", runes.Select(r => r.Id));

    // The row the player is on. Keyboard UI: under the mouse pointer (over the list, on the
    // row's plate). Controller UI: the mouse too, once it moved since the list was opened
    // (mouseMoved, Open.Motion; it still moves there, 01.11.2026) — a pointer parked over the
    // list from before the pad was picked up must not decide the row (review 05.11.2026) —
    // else the row the controller's cursor marks (CursorRow). -1: none.
    // The plate: RowHalfController about the name's line in the controller UI; in the keyboard
    // UI the name's line alone (RowHalfKeyboard) while the rows are one line high, with the
    // tiles' line above it (RuneBook.Rise) when they are two — the game's frame goes round
    // both, and a click on the tiles chooses the row (diagnosis of 03.11.2026 18:59:24:
    // the mouse on the tiles, the frame on the row, no row read). A list of one row cannot
    // tell its height (Rise null): the taller plate then, it cannot reach another row.
    private static int Hover(GameCapture.Frame frame, RemnantWindow list, RemnantRow[] rows, RuneTile[][]? runes, RemnantReader reader, bool mouseMoved)
    {
        if (list.Mode != UiMode.Controller)
            return PointerRow(frame.Pointer, list, rows, runes);
        if (mouseMoved && PointerRow(frame.Pointer, list, rows, runes) is var row && row >= 0)
            return row;
        return CursorRow(frame.Image, list, rows, reader);
    }

    // The row whose plate the pointer is on, -1: none. The plate's top in the keyboard UI is
    // the row's tiles' line (read: their Y; not read: RuneBook.Rises) less RowHalfKeyboard,
    // its bottom RowHalfKeyboard below the name.
    public static int PointerRow((int X, int Y)? pointer, RemnantWindow list, RemnantRow[] rows, RuneTile[][]? runes)
    {
        if (pointer is not { } p || p.X < list.List.X || p.X >= list.List.Right)
            return -1;
        if (list.Mode == UiMode.Controller)
        {
            for (int i = 0; i < rows.Length; i++)
                if (Math.Abs(p.Y - rows[i].CenterY) <= RuneTune.RowHalfController)
                    return i;
            return -1;
        }
        var rises = RuneBook.Rises(list, rows);
        for (int i = 0; i < rows.Length; i++)
        {
            int rise = runes is not null && i < runes.Length && runes[i].Length > 0 ? Math.Max(0, (int)Math.Round(rows[i].CenterY) - runes[i][0].Y) : rises[i];
            if (p.Y >= rows[i].CenterY - rise - RuneTune.RowHalfKeyboard && p.Y <= rows[i].CenterY + RuneTune.RowHalfKeyboard)
                return i;
        }
        return -1;
    }

    // The controller's cursor: the gold arrow left of the list box (RemnantReader.CursorArrow;
    // 03.11.2026: it swings sideways, the frame pulses). Where no arrow is read, the
    // row with the gold frame round its whole plate (the others a grey one), measured at the
    // plate's left edge, where the parchment does not confuse it (01.11.2026, four lists at
    // 1440: the cursor's row .23–.39 gold, the others ≤ .04): the row with the most gold, if it
    // has enough and clearly more than the next.
    public static int CursorRow(Mat image, RemnantWindow list, RemnantRow[] rows, RemnantReader reader)
    {
        if (reader.CursorArrow(image, list, rows).Row is var arrow && arrow >= 0)
            return arrow;
        var shares = CursorShares(image, list, rows);
        int best = -1;
        double most = 0, next = 0;
        for (int i = 0; i < shares.Length; i++)
            if (shares[i] > most)
            {
                next = most;
                most = shares[i];
                best = i;
            }
            else if (shares[i] > next)
                next = shares[i];
        return most >= RuneTune.CursorGoldFrom && most >= RuneTune.CursorLead * next ? best : -1;
    }

    // The gold share of the band left of each row's plate.
    public static double[] CursorShares(Mat image, RemnantWindow list, RemnantRow[] rows)
    {
        var shares = new double[rows.Length];
        var whole = new Rect(0, 0, image.Width, image.Height);
        for (int i = 0; i < rows.Length; i++)
        {
            int cy = (int)Math.Round(rows[i].CenterY);
            var band = new Rect(list.List.X - RuneTune.CursorBandLeft, cy - RuneTune.CursorAbove, RuneTune.CursorBandWidth, RuneTune.CursorAbove + RuneTune.CursorBelow).Intersect(whole);
            if (band.Width <= 0 || band.Height <= 0)
                continue;
            using var part = new Mat(image, band);
            using var hsv = new Mat();
            Cv2.CvtColor(part, hsv, ColorConversionCodes.BGR2HSV);
            using var mask = new Mat();
            Cv2.InRange(hsv, new Scalar(RuneTune.CursorHueFrom, RuneTune.CursorSaturationFrom, RuneTune.CursorValueFrom), new Scalar(RuneTune.CursorHueTo, 255, 255), mask);
            shares[i] = Cv2.CountNonZero(mask) / (double)(band.Width * band.Height);
        }
        return shares;
    }

    // Rows read again only when the list looks different (scrolled, new remnant).
    // Tooltips: those the labels were drawn for. Height: of the game's picture. Whole: the
    // list as far as it was seen, Rows being the part in view. Popup: the game's popup size
    // when the list was last read (the glance gets a picture without it).
    // Runes: the rune tiles of each row (RuneBook), Hover: the row under the mouse (-1: none).
    // Occluded: the rows were read while the game's rune tooltip lay inside the list box (the
    // rows and tiles under it read wrong): read again once it goes or moves.
    // Moving: the last picture of the list that differed from the reading, kept until the
    // list rests (NeedsReread). Shared by every copy of this record: a glance may not relabel
    // just to remember a picture, that would log and draw twenty times a second.
    private sealed record Open(RemnantGameWindow Kind, RemnantWindow List, RemnantRow[] Rows, double[] Signature, Box[] Tooltips, int Height, RemnantList Whole, double Popup, RuneTile[][] Runes, int Hover, bool Occluded = false, Open.Motion? Moving = null, (int X, int Y)? Pointer = null) : IOpenWindow
    {
        public IGameWindow Window => Kind;

        // One per showing of the list (LookAsync), shared by all its readings (with).
        public sealed class Motion
        {
            public volatile double[]? Last;
            // 1 once RemnantGameWindow.Closed took this showing's closing.
            public int Closed;
            // When the list was last seen (UTC ticks): the look, every glance and follow that
            // found it (RemnantGameWindow.Closed: a stale one is not to tell). Opened: when this
            // showing was first read (a rescan keeps the record); the cancel and the confirm are
            // dated against it — the game keeps the list up for a moment after either button
            // (05.11.2026: 0.19–0.55 s measured; a window of 0.3 s before the last picture missed
            // 8 of 13 confirms and one cancel).
            public long Seen;
            public long Opened;
            // Where the mouse was when the list was opened, and 1 once a later picture saw it
            // elsewhere: in the controller UI the mouse counts only then (Hover).
            public (int X, int Y)? PointerAtOpen;
            public int MouseMoved;
        }

        // Whether the mouse moved since this showing was opened (noted on the first picture
        // that shows it elsewhere).
        private bool MouseMoved(GameCapture.Frame frame)
        {
            if (Moving is not { } m)
                return true;
            if (Volatile.Read(ref m.MouseMoved) == 0 && frame.Pointer != m.PointerAtOpen)
                Volatile.Write(ref m.MouseMoved, 1);
            return Volatile.Read(ref m.MouseMoved) != 0;
        }

        // The tooltips inside the list box: the game's rune tooltip (the item tooltips beside
        // the list lie outside it).
        public static Box[] InBox(Box[] tooltips, RemnantWindow list) => tooltips.Where(t => t.Intersects(list.List)).ToArray();

        // The list is to be read again: it changed where no rune tooltip lies, in this picture
        // or in the one the reading came from (the tooltip's coming, going and moving is no
        // change of the list: it made the program read the list anew every second and the
        // prices and marks jump, 02.11.2026) — and rests: a second look, the next glance or
        // follow, shows the same changed picture. The page fades in for 0.5–0.8 s after the
        // list opens (log of 02.11.2026 evening: the list box differing from the reading by
        // 79 of 255 at every glance until the reading again, or by 8 in bands of rows; the
        // reading made meanwhile had "?" for runes, no crowns and the wrong number of rows,
        // and every reading again cost 0.8 s of OCR and runes); a scrolled list moves too.
        // While it moves, the reading stands. Or the reading was made under a rune tooltip
        // that has gone or moved since: at once.
        private bool NeedsReread(Mat frame, RemnantWindow list, Box[] tooltips)
        {
            Box[] now = InBox(tooltips, list),
                before = InBox(Tooltips, List);
            Box[] ignored = [.. now, .. before];
            if (Occluded && !now.SequenceEqual(before))
                return true;
            var signature = RemnantReader.Signature(frame, list);
            if (!RemnantReader.Changed(signature, Signature, list, ignored))
            {
                if (Moving is { } back)
                    back.Last = null;
                return false;
            }
            if (Moving is not { } motion)
                return true;
            var last = motion.Last;
            motion.Last = signature;
            return last is not null && !RemnantReader.Changed(signature, last, list, ignored);
        }

        // The rune classes changed in the settings window (RuneBook.SetOwn): the marks are
        // drawn anew (OverlayEngine.RunesChanged → RebindAsync wants another instance).
        public IOpenWindow Rebind(LookContext context) => this with { };

        // The row the player is on, as Hover reads it from this picture — but in the
        // controller UI a row stays the row until another one is read: there one row is
        // always chosen while the list is open, so "none" is a picture in which the cursor's
        // gold frame was not read, not a state. Measured 02.11.2026 (1440): one glance
        // in about 1.4 s read none, the caption above the list went for 90 ms and came back,
        // a flicker. The pictures kept then (cursor-lost) show why: the game pulses the frame —
        // in the band left of the plate hue and saturation are as ever (19, 86) but the
        // brightest tenth of the pixels is at 161 instead of 228–234, under CursorValueFrom;
        // to the eye the frame is gone for that moment. No threshold reads it there. With
        // extended diagnostics the first such pictures of a run are kept.
        private int Kept(GameCapture.Frame frame, LookContext context)
        {
            int hover = RemnantGameWindow.Hover(frame, List, Rows, Runes, Kind.Pipeline.Remnants, MouseMoved(frame));
            if (hover >= 0 || List.Mode != UiMode.Controller || Hover < 0)
                return hover;
            if (context.Keep is { } keep && Kind.keptLost < KeptLooks)
            {
                Kind.keptLost++;
                context.Log($"Remnant list: the controller's cursor row was not read (row {Hover + 1} kept)");
                keep("cursor-lost", frame.Image);
            }
            return Hover;
        }

        // The list as it closed: the row under the mouse from the picture that shows it gone
        // (the click that closed it was a moment before, the mouse still on the row), else the
        // row read last. The engine takes a glance's row only while no follow runs, and the
        // follow reads once a second: the row read last lags the mouse by up to a second, and
        // the player moves onto a row and clicks at once (log 03.11.2026 19:38, "no row under the
        // mouse" twice).
        private Open AtClose(GameCapture.Frame frame) =>
            (List.Mode != UiMode.Controller || MouseMoved(frame)) && RemnantGameWindow.PointerRow(frame.Pointer, List, Rows, Runes) is var row && row >= 0 ? this with { Hover = row, Pointer = frame.Pointer } : this with { Pointer = frame.Pointer };

        // The mouse moved to another row: only the caption changes — on its own, or along
        // with a tooltip that came or went.
        public Glance Glance(GameCapture.Frame frame, PriceBook? ready, LookContext context)
        {
            var seen = Glance(frame.Image, ready, context);
            if (seen is Core.Glance.Gone)
            {
                // The picture shows the list gone: nothing in it to read the cursor from (it was
                // read as "lost" and kept as evidence after every choice, 05.11.2026).
                Kind.Closed(AtClose(frame), context);
                return seen;
            }
            if (Moving is { } shown)
                Interlocked.Exchange(ref shown.Seen, context.Now.UtcTicks);
            int hover = Kept(frame, context);
            return seen switch
            {
                Core.Glance.Same when hover != Hover => new Core.Glance.Relabel(this with { Hover = hover }),
                Core.Glance.Relabel { Next: Open next } => new Core.Glance.Relabel(next with { Hover = hover }),
                _ => seen,
            };
        }

        public Box? GlanceArea => Kind.Pipeline.Remnants.LookArea(List, Height, Popup);

        // The list, its anchor and the tooltip band beside it: all the follow reads.
        public Box? FollowArea => GlanceArea;

        // Title gone → closed; list moved → read again at once; else only the tooltip band
        // beside the window.
        public Glance Glance(Mat frame, PriceBook? ready, LookContext context)
        {
            if (Kind.Pipeline.Remnants.Locate(frame)?.Mode != List.Mode)
                return new Glance.Gone("Remnant window no longer detected");
            if (ready is null)
                return Core.Glance.Unchanged;
            var tooltips = Kind.Pipeline.RemnantTooltips(frame, List, Popup);
            if (NeedsReread(frame, List, tooltips))
                return new Glance.Reread(this, ClearNow: false);
            // What a tooltip beside the list changes is which labels it lies on. Its bottom is
            // measured on the scene behind it (OccluderDetector.DimmingEnd) and may come out a
            // pixel or two apart from frame to frame over a moving scene; that alone draws
            // nothing anew. (Tooltips then stays as it was until the next follow, at most a
            // second: only the diagnosis shows it.) The rune tooltip inside the list box counts
            // as it is: the marks under it go while it stands.
            return Covered(tooltips).SequenceEqual(Covered(Tooltips)) && InBox(tooltips, List).SequenceEqual(InBox(Tooltips, List)) ? Core.Glance.Unchanged : new Glance.Relabel(this with { Tooltips = tooltips });
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
                Kind.Closed(AtClose(frame), context);
                return null;
            }

            if (Moving is { } shown)
                Interlocked.Exchange(ref shown.Seen, context.Now.UtcTicks);
            var next = this with { List = list, Tooltips = Kind.Pipeline.RemnantTooltips(frame.Image, list, frame.Popup), Popup = frame.Popup, Hover = Kept(frame, context) };
            if (!NeedsReread(frame.Image, list, next.Tooltips))
                return next;
            // With extended diagnostics (Keep): where the list changed, and the picture, a few
            // per run — once per reading again, not per glance that asked for it.
            if (context.Keep is { } keep)
            {
                context.Log($"Remnant list changed: {RemnantReader.ChangedWhere(RemnantReader.Signature(frame.Image, list), Signature, list)}");
                if (Kind.keptChanges < KeptLooks)
                {
                    Kind.keptChanges++;
                    keep("list-changed", frame.Image);
                }
            }
            var rows = await Kind.Pipeline.Remnants.ReadAsync(frame.Image, list);
            context.Log($"Time: Remnant window {locate} ms, OCR {watch.ElapsedMilliseconds - locate} ms, {rows.Length} rows");
            context.Log("Remnant list rescanned");
            // Scrolled: the view is laid onto what is known. No fit: another list.
            var whole = Whole.Scrolled(rows) ?? RemnantList.Of(rows);
            if (whole.Rows.Count > rows.Length)
                context.Log($"Remnant list: {whole.Rows.Count} rows known, visible from row {whole.First + 1}");
            Kind.remembered = whole;
            var runes = Kind.Runes(frame.Image, list, rows, context);
            return next with { Rows = rows, Signature = RemnantReader.Signature(frame.Image, list), Whole = whole, Runes = runes, Hover = RemnantGameWindow.Hover(frame, list, rows, runes, Kind.Pipeline.Remnants, MouseMoved(frame)), Occluded = InBox(next.Tooltips, list).Length > 0 };
        }

        public async Task<Picture> LabelAsync(GameCapture.Frame frame, Task<PriceBook> prices, LookContext context)
        {
            var book = await prices;
            var settings = context.Settings;
            // Prices and runes together: the remnant switch of the automatic scan covers both.
            var labels = Labels.ForRows(Rows, List, book, Labels.FloorInDivine(settings.FloorValue, settings.FloorUnit, book), Tooltips, Whole.Trusted, frame.Language, settings.PriceUnit);
            var marks = RuneLabels(labels, frame.Language);
            return new Picture(
                this,
                labels,
                null,
                Texts.Get("RemnantStatus", Texts.Count("Rows", Rows.Length), Texts.Count("Prices", labels.Count(l => l.Text is not null)), book.League),
                $"Remnant list, {Rows.Length} rows, {labels.Length} labels, {Tooltips.Length} tooltips" + (marks.Length > 0 ? $", {marks.Count(l => l.Caption is null)} crowns" : ""),
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
                    Runes = Runes.Select(tiles => tiles.Select(t => new { t.Index, t.Crowned, Rune = t.Rune?.Id, Score = Math.Round(t.Score, 2), Lead = Math.Round(t.Lead, 2) })),
                    Hover,
                    Marks = marks,
                    Chain = Kind.Chain.Links.Select(l => new { Runes = l.Runes.Select(r => r.Id), l.Fixed }),
                },
                marks
            );
        }

        // A mark under every crowned tile in the colour of its rune's class (Label.Frame) — with
        // the ✗ badge when the chain carries that rune already (Mark "x"; "o" hollow when only
        // provisionally) — and in the controller UI, for the row the cursor or the mouse is on, a
        // caption above the list with the rune's name and effect. With keyboard and mouse the
        // game itself shows the rune's tooltip at the row, so no caption there (02.11.2026).
        // The caption stands right of the arrow box when one is there (Labels.ForRows: the best
        // reward out of view above).
        private Label[] RuneLabels(Label[] priced, string language)
        {
            var labels = new List<Label>();
            // Runes may hold one row more than Rows: the row cut off at the bottom edge.
            for (int i = 0; i < Runes.Length; i++)
                foreach (var t in Runes[i].Where(t => t.Crowned))
                {
                    int half = RuneTune.TileSize / 2;
                    var tile = new Box(t.X - half, t.Y - half, RuneTune.TileSize, RuneTune.TileSize);
                    // Under the game's rune tooltip: no mark on top of it.
                    if (Tooltips.Any(tt => tt.Intersects(tile)))
                        continue;
                    var carried = t.Rune is null ? null : Kind.Chain.Carrying(t.Rune);
                    labels.Add(new Label(tile, null, carried is null ? null : carried.Fixed ? "x" : "o", "ex", Tier.Normal, false, Frame: ClassKey(t.Rune)));
                }
            // One caption per crowned rune of the row (two with "Double or Nothing"), stacked
            // above the list in the tiles' order, the first on top, the last right above the
            // list (02.11.2026).
            if (List.Mode == UiMode.Controller && Hover >= 0 && Hover < Runes.Length)
            {
                var crowned = Runes[Hover].Where(t => t.Crowned && t.Rune is not null).Select(t => t.Rune!).ToArray();
                bool arrow = priced.Any(l => l.Arrow == "▲");
                int x = List.List.X + (arrow ? Labels.RowLabelWidth + 8 : 0);
                for (int i = 0; i < crowned.Length; i++)
                {
                    var rune = crowned[i];
                    // As wide as the list at most (LabelLayer.DrawCaption cuts a longer text).
                    int y = List.List.Y - RuneTune.CaptionGap - (crowned.Length - i) * (RuneTune.CaptionHeight + RuneTune.CaptionGap) + RuneTune.CaptionGap;
                    var box = new Box(x, y, List.List.Right - x, RuneTune.CaptionHeight);
                    labels.Add(new Label(box, null, null, "ex", Tier.Normal, false, Frame: ClassKey(rune), Caption: $"{rune.Name(language)}  ·  {rune.Effect(language)}"));
                }
            }
            return labels.ToArray();
        }

        private static string ClassKey(Rune? rune) => (rune?.Class ?? RuneClass.Neutral) switch
        {
            RuneClass.Loot => "loot",
            RuneClass.Weaker => "weaker",
            RuneClass.Danger => "danger",
            _ => "neutral",
        };
    }
}
