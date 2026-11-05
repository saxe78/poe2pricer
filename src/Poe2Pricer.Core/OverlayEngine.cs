using System.Collections.Concurrent;
using System.Diagnostics;
using OpenCvSharp;
using Poe2Pricer.Core.Market;
using Poe2Pricer.Core.Runes;

namespace Poe2Pricer.Core;

// What the overlay draws now. Screen: the game's part of the screen. Labels and Rumours are in
// the coordinates of the reference picture (GameView); Scale: screen pixels per pixel of it.
// Marks: drawn as well, no prices (Picture.Marks); Shown leaves them out. Strip: the rune
// chain of this map at the top of the screen, with or without a window (OverlayEngine.Standing).
public sealed record Scene(Box Screen, Label[] Labels, (Box Panel, RumourLine[] Lines, IslandSummary? Island)? Rumours, double Scale = 1, Label[]? Marks = null, RuneStrip? Strip = null)
{
    // Everything to draw: the labels, then the marks over them.
    public Label[] All => Marks is { Length: > 0 } m ? [.. Labels, .. m] : Labels;

    // The same picture: every label, line and island entry alike (the record's own equality
    // compares the arrays by reference). OverlayEngine.Show draws only a scene that differs
    // from the one drawn last: at 3840×2160 the Ritual window was drawn 2.5 times a second
    // while nothing changed (every glance that saw the cursor move brought a follow; log
    // 31.10.2026), each picture 19 ms on the UI thread.
    public bool SameAs(Scene other) =>
        Screen == other.Screen && Scale == other.Scale && Labels.AsSpan().SequenceEqual(other.Labels) && (Marks ?? []).AsSpan().SequenceEqual(other.Marks ?? []) && SameRumours(Rumours, other.Rumours) && (Strip is null ? other.Strip is null : Strip.SameAs(other.Strip));

    // What differs from the other scene, for the log (extended diagnostics): the first part
    // that does, with both values where they are short. Evidence for a picture drawn anew
    // while nothing should have changed (02.11.2026: the caption above the controller's
    // remnant list flickered about once a second, one picture a second in the count).
    public string Differs(Scene other)
    {
        if (Screen != other.Screen)
            return $"screen {Screen} → {other.Screen}";
        if (Scale != other.Scale)
            return $"scale {Scale} → {other.Scale}";
        if (!Labels.AsSpan().SequenceEqual(other.Labels))
            return "labels: " + FirstDifference(Labels, other.Labels);
        if (!(Marks ?? []).AsSpan().SequenceEqual(other.Marks ?? []))
            return "marks: " + FirstDifference(Marks ?? [], other.Marks ?? []);
        if (!SameRumours(Rumours, other.Rumours))
            return "rumours";
        if (!(Strip is null ? other.Strip is null : Strip.SameAs(other.Strip)))
            return $"strip {Strip?.Title}/{Strip?.Hover}/{Strip?.Caption} → {other.Strip?.Title}/{other.Strip?.Hover}/{other.Strip?.Caption}";
        return "nothing";
    }

    private static string FirstDifference(Label[] a, Label[] b)
    {
        if (a.Length != b.Length)
            return $"{a.Length} → {b.Length}";
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return $"#{i} " + string.Join(", ", LabelFields.Where(f => !Equals(f.GetValue(a[i]), f.GetValue(b[i]))).Select(f => $"{f.Name}: {f.GetValue(a[i]) ?? "null"} → {f.GetValue(b[i]) ?? "null"}"));
        return "same";
    }

    // The fields of a label, for the log: only those that differ (a whole label is a line of
    // its own with its caption; 05.11.2026: 43 such lines of a kilobyte each).
    private static readonly System.Reflection.PropertyInfo[] LabelFields = typeof(Label).GetProperties();

    private static bool SameRumours((Box Panel, RumourLine[] Lines, IslandSummary? Island)? a, (Box Panel, RumourLine[] Lines, IslandSummary? Island)? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        var (x, y) = (a.Value, b.Value);
        return x.Panel == y.Panel && x.Lines.AsSpan().SequenceEqual(y.Lines) && SameIsland(x.Island, y.Island);
    }

    private static bool SameIsland(IslandSummary? a, IslandSummary? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.Tier == b.Tier && a.Rolls == b.Rolls && a.Rumours == b.Rumours && a.Complete == b.Complete && a.ProbablyComplete == b.ProbablyComplete
            && a.Rotated.AsSpan().SequenceEqual(b.Rotated) && a.New.AsSpan().SequenceEqual(b.New);
    }
}

// The overlay's control for every window: key, automatic scan, glance (still there?) every
// 50 ms, follow (changed?) every second, foreground changes, diagnosis. Inputs are queued and
// handled one after another on the control's own thread; image work runs on the thread pool,
// and a result from before a hide or a tab switch is dropped (generation). Nothing is captured
// while the game is not in front. Outputs: Draw (a scene, null = hide), Status, Diagnosed; all
// raised on the control's thread, the caller marshals to its UI.
public sealed class OverlayEngine : IDisposable
{
    private readonly GameWindows windows;
    private readonly IGameScreen screen;
    private readonly IPrices prices;
    private readonly TimeProvider time;
    private readonly Func<RumourBook> rumours;
    private readonly Action<string> log;
    private readonly EngineLoop loop = new();
    private readonly ITimer glance;
    private readonly ITimer follow;
    private readonly ITimer auto;
    private Settings settings;

    // Only touched on the control's thread.
    private IOpenWindow? open;
    private Label[] lastLabels = [];
    private Label[] lastMarks = [];
    // Where the game was on the screen when it was last captured: the strip alone stands there.
    private (Box Screen, double Scale)? standingScreen;
    // The mouse as the last picture saw it, and the game's language: for the tile of the rune
    // chain under the mouse and its caption.
    private (int X, int Y)? pointer;
    private string language = GameLanguage.English;
    private (Box Panel, RumourLine[] Lines, IslandSummary? Island)? lastRumours;
    private Picture? lastPicture;
    private Box? lastScreen;
    private double lastScale = 1;
    private GameCapture.Frame? lastFrame;
    private bool visible;
    private bool busy; // a look or follow is running
    private bool glanceBusy;
    private bool readAgain; // the window changed while a follow was running
    private bool exactAsked; // the exact-price key came while a follow was running
    private bool hiddenByKey; // the automatic scan leaves it hidden until no window is open
    private bool keyWaiting; // the key came while a look or follow was running
    private bool rebindWaiting; // own ratings changed while a look or follow was running
    private int generation; // each show, hide and tab switch; older results are dropped
    private Stopwatch? showStarted;
    private int inFlight;

    public event Action<Scene?>? Draw;
    public event Action<StatusMessage>? Status;
    public event Action<string?>? Diagnosed;

    public OverlayEngine(GameWindows windows, IGameScreen screen, IPrices prices, Func<RumourBook> rumours, Settings settings, TimeProvider time, Action<string> log, RuneChain? chain = null)
    {
        this.windows = windows;
        this.chain = chain;
        this.screen = screen;
        this.prices = prices;
        this.rumours = rumours;
        this.settings = settings;
        this.log = log;
        this.time = time;
        glance = time.CreateTimer(_ => Enqueue(GlanceAsync), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        follow = time.CreateTimer(_ => Enqueue(FollowAsync), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        auto = time.CreateTimer(_ => Enqueue(AutoAsync), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        SetAuto(settings);
    }

    // For tests and diagnosis; read on the control's thread or after IdleAsync.
    public IOpenWindow? Open => open;
    public Scene? Shown => visible && lastScreen is { } b ? new Scene(b, lastLabels, lastRumours, lastScale) : null;
    public Picture? LastPicture => lastPicture;

    public void Toggle() => Enqueue(ToggleAsync);

    public void ForegroundChanged() => Enqueue(() => { OnForeground(); return Task.CompletedTask; });

    public void Apply(Settings s) =>
        Enqueue(() =>
        {
            // Another league: the prices on the screen are no longer the ones asked for. They
            // go at once and come back with the next follow, once the new book is there; they
            // must not stand through the load, nor for good when it fails. Rumour ratings do
            // not depend on the league.
            bool otherLeague = s.League != settings.League;
            settings = s;
            SetAuto(s);
            if (otherLeague && open is not null)
            {
                // A follow still running reckons with the old book: its result is dropped,
                // the checks started below read anew.
                generation++;
                lastLabels = [];
                lastMarks = [];
                if (visible && lastScreen is { } b)
                    Show(new Scene(b, [], lastRumours, lastScale));
            }
            if (open is not null)
                StartChecks();
            return Task.CompletedTask;
        });

    public void RumoursChanged() => Enqueue(RebindAsync);

    // The rune classes changed in the settings window: the marks of an open remnant list follow.
    public void RunesChanged() => Enqueue(RebindAsync);

    // The rune chain changed (a bar read, a new map, the reset button): the strip and the
    // badges in the list follow.
    public void ChainChanged() => Enqueue(ChainChangedAsync);

    private readonly RuneChain? chain;

    private async Task ChainChangedAsync()
    {
        if (open is not null)
        {
            await RebindAsync();
            return;
        }
        if (!busy)
            Stand();
    }

    // The strip at the top of the screen: the runes the chain carries, in the order they were
    // chosen, each in its class colour.
    // With the mouse on a tile, that rune's name and effect in the game's words. Null while the
    // chain is empty — then nothing of it is drawn. width: the reference picture's width, for
    // where the tiles are; null: no mouse looked up.
    private RuneStrip? Standing(double? width = null)
    {
        if (chain is null || chain.InHaven || chain.Links is not { Count: > 0 } links)
            return null;
        var runes = links.SelectMany(l => l.Runes.Select(r => (Rune: r, l.Fixed))).ToArray();
        if (runes.Length == 0)
            return null;
        var entries = runes.Select(x => new StripEntry(x.Rune.Id, RuneBook.KeyOf(x.Rune.Class), x.Fixed)).ToArray();
        var strip = new RuneStrip(Texts.Get("OverlayRuneChain", entries.Length), entries);
        int hover = width is { } w ? strip.TileAt(pointer, w) : -1;
        return hover < 0 ? strip : strip with { Hover = hover, Caption = $"{runes[hover].Rune.Name(language)}  ·  {runes[hover].Rune.Effect(language)}" };
    }

    // The mouse moved: the rune chain's caption may come or go. Draws only when the scene
    // differs from the one drawn (Show).
    private void Pointed(GameCapture.Frame frame)
    {
        // Every glance ends here: nothing to do while the mouse rests (review 05.11.2026).
        if (frame.Pointer == pointer && frame.Language == language)
            return;
        pointer = frame.Pointer;
        language = frame.Language;
        if (Standing() is null)
            return;
        if (open is not null && visible && lastScreen is { } b)
            Show(new Scene(b, lastLabels, lastRumours, lastScale, lastMarks));
        else if (open is null)
            Stand();
    }

    // With no window open: the strip alone when the chain has links and the game is in front,
    // else nothing. The strip comes and goes with the automatic scan's turns (ShowAsync).
    private void Stand()
    {
        bool standing = Standing() is not null && !hiddenByKey && screen.InFront;
        if (standing && standingScreen is { } s)
            Show(new Scene(s.Screen, [], null, s.Scale));
        else if (!standing && visible && open is null)
            HideOverlay();
    }

    public void Diagnose(string folder) => Enqueue(() => DiagnoseAsync(folder));

    // The exact-price key: a waystone's or tablet's tooltip shown is asked for exactly as the
    // item is, past the profiles, and labelled anew; any other window, or none: nothing.
    public void Exact() =>
        Enqueue(async () =>
        {
            if (open is null || TooltipGameWindow.Exact(open) is not { } exact)
                return;
            log("exact price asked by key");
            open = exact;
            if (busy)
            {
                // The follow running holds the window from before the key and puts it back when
                // it ends: the key is applied to its result then (review before 1.2.0).
                exactAsked = true;
                readAgain = true;
            }
            else
                await FollowAsync();
        });

    // Every queued input handled and every image job it started finished (tests).
    public async Task IdleAsync()
    {
        while (Volatile.Read(ref inFlight) > 0)
            await Task.Delay(5);
    }

    private readonly Dictionary<string, DateTimeOffset> logged = []; // failures and when they were logged; only on the loop

    // The one place where a failure ends up: logged and shown, the control keeps running.
    private void Enqueue(Func<Task> work)
    {
        Interlocked.Increment(ref inFlight);
        loop.Post(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception e)
            {
                // A failure is logged unless the same one was logged within SameErrorEvery:
                // prices that cannot be loaded fail at every look of the automatic scan.
                // Told by kind and message: thrown again, the same failure has a longer
                // trace. Neither work without a failure in between nor another failure in
                // between makes it new. The
                // price: a failure that is over and comes back within that time is not
                // logged a second time; the status line shows it all the same.
                string kind = e.GetType().FullName + ": " + e.Message;
                var now = time.GetUtcNow();
                foreach (var old in logged.Where(p => now - p.Value >= Tuning.Control.SameErrorEvery).Select(p => p.Key).ToArray())
                    logged.Remove(old);
                if (logged.TryAdd(kind, now))
                    log("Error: " + e);
                Status?.Invoke(StatusMessage.Error(Texts.Shown(e)));
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        });
    }

    // Keep only when pictures are kept: a window with a Keep clones its picture for it
    // (FavoursGameWindow, the first look), which is pure cost otherwise (review 19.10.).
    private LookContext Context() => new(rumours(), settings, log, KeepsEvidence ? Keep : null, Clock: time, Escaped: LastEscape, Confirmed: LastConfirm, EscapeReadable: EscapeReadable, GamepadReadable: GamepadReadable());

    // The last cancel — Esc through the keyboard hook, the gamepad's cancel button through
    // GamepadWatch, Windows.Gaming.Input — and the last confirm (the gamepad's confirm button;
    // a mouse click is not read) seen while the game was in front (UTC ticks; long.MinValue:
    // none yet). The remnant list closes on a cancel without a choice, on a click or the
    // confirm button with one (RemnantGameWindow.Closed). Called from the hook's or the
    // watch's thread; logged only while a remnant list is open — both buttons do other things
    // in the game (05.11.2026: Circle is the dodge roll, up to fifty presses a minute).
    // EscapeReadable: the hook works (false when Windows refused it); GamepadReadable: a pad
    // answered lately.
    public bool EscapeReadable { get; set; } = true;
    public Func<bool> GamepadReadable { get; set; } = () => false;
    private long escaped = long.MinValue;
    private long confirmed = long.MinValue;
    public void Escaped(string what = "Esc") => Pressed(ref escaped, what);
    public void Confirmed(string what = "the confirm button") => Pressed(ref confirmed, what);
    private void Pressed(ref long last, string what)
    {
        Interlocked.Exchange(ref last, time.GetUtcNow().UtcTicks);
        // The reference alone, from another thread: a stale window costs a log line at most.
        // The line is written off this thread: Esc comes through the low-level keyboard hook,
        // whose callback must return at once (Windows drops a slow hook without a word).
        if (Volatile.Read(ref open)?.Window is RemnantGameWindow)
            ThreadPool.QueueUserWorkItem(_ => log($"Remnant list: {what} pressed"));
    }
    private DateTimeOffset? LastEscape() => Last(ref escaped);
    private DateTimeOffset? LastConfirm() => Last(ref confirmed);
    private static DateTimeOffset? Last(ref long field)
    {
        long ticks = Interlocked.Read(ref field);
        return ticks == long.MinValue ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    // Where the pictures go that a window hands over as evidence (LookContext.Keep). Kept
    // only with Settings.ExtendedDiagnostics on — a user's program writes nothing by itself
    // (19.10.2026); the switch is judged here, from the settings the engine holds.
    public string? EvidenceFolder { get; set; }
    private bool KeepsEvidence => EvidenceFolder is not null && settings.ExtendedDiagnostics && screen.PicturesFromWindow;

    // Called from the thread that reads the picture: copied there, written beside it. A
    // picture that cannot be written is logged and nothing else. Of each kind only the newest
    // Tuning.Control.EvidenceKept stay (review 19.10.: every roll in the Ritual window wrote
    // two full pictures, without end — four hundred megabytes for a map of fifty rolls).
    // Of the pictures of one kind ("<time>_<what>.png") only the newest keep stay. A file
    // that cannot be deleted is left; it is tried again with the next picture.
    public static void PruneEvidence(string folder, string what, int keep)
    {
        var old = Directory.GetFiles(folder, $"*_{what}.png").OrderDescending(StringComparer.Ordinal).Skip(keep);
        foreach (var file in old)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // A window's name as part of a file name: letters and digits, the rest a dash.
    private static string Slug(string name) =>
        string.Concat(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');

    // The kinds of evidence being written right now: one picture of a kind at a time, so a
    // burst of glance losses does not pile up clones of the whole frame and encoders beside the
    // game (R3); the newest few are what matter. (Not inFlight: that counts the engine's work.)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> writing = new();

    private void Keep(string what, Mat picture)
    {
        if (!KeepsEvidence || !writing.TryAdd(what, 0))
            return;
        string folder = EvidenceFolder!;
        var copy = picture.Clone();
        var file = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HHmmss_fff}_{what}.png");
        Interlocked.Increment(ref inFlight);
        Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                if (!Cv2.ImWrite(file, copy))
                    throw new IOException("cannot write " + file);
                log("evidence saved: " + Path.GetFileName(file));
                PruneEvidence(folder, what, Tuning.Control.EvidenceKept);
            }
            catch (Exception e)
            {
                log("evidence not saved: " + e.Message);
            }
            finally
            {
                writing.TryRemove(what, out _);
                copy.Dispose();
                Interlocked.Decrement(ref inFlight);
            }
        });
    }

    private void SetAuto(Settings s)
    {
        // NaN (a settings file changed by hand) compares false with everything: the minimum then.
        double seconds = s.AutoIntervalSeconds >= Tuning.Control.AutoMinimumSeconds ? Math.Min(Tuning.Control.AutoMaximumSeconds, s.AutoIntervalSeconds) : Tuning.Control.AutoMinimumSeconds;
        var every = TimeSpan.FromSeconds(seconds);
        autoEvery = every;
        autoPace = every;
        scanSmoothed = 0;
        if (s.AutoAny)
            auto.Change(every, every);
        else
            auto.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // The automatic scan's pace, as the glance's (PaceGlance): its own interval, or slower when
    // the capture of the whole picture takes long — at most GlanceLoad of the time (at
    // 3840×2160 the whole picture takes 93 ms; at the shortest interval of .2 s that was
    // half the time). Smoothed, in steps of ScanPaceStep (Paced).
    private TimeSpan autoEvery = TimeSpan.FromSeconds(Tuning.Control.AutoMinimumSeconds),
        autoPace = TimeSpan.FromSeconds(Tuning.Control.AutoMinimumSeconds);
    private double scanSmoothed;

    private void PaceScan(long captureMs)
    {
        scanSmoothed = scanSmoothed == 0 ? captureMs : scanSmoothed + Tuning.Control.CaptureSmoothing * (captureMs - scanSmoothed);
        var wanted = Paced(autoPace, scanSmoothed / Tuning.Control.GlanceLoad, autoEvery, Tuning.Control.ScanPaceStep, TimeSpan.FromSeconds(Tuning.Control.AutoMaximumSeconds));
        if (wanted == autoPace || !settings.AutoAny)
            return;
        autoPace = wanted;
        auto.Change(wanted, wanted);
        log($"scan every {wanted.TotalMilliseconds:0} ms: its capture takes {scanSmoothed:0} ms");
    }

    // The key again while it is still waiting takes it back. A look still running (automatic
    // scan) is dropped: the key looks for every window.
    private async Task ToggleAsync()
    {
        if (open is not null)
        {
            hiddenByKey = true;
            Hide();
        }
        else if (busy)
        {
            keyWaiting = !keyWaiting;
            hiddenByKey = false;
            generation++;
        }
        else
        {
            hiddenByKey = false;
            await ShowAsync(quiet: false, _ => true);
        }
    }

    private void Release()
    {
        busy = false;
        if (lookAgainWaiting)
        {
            lookAgainWaiting = false;
            if (open is null)
                LookAgainSoon();
        }
        if (rebindWaiting)
        {
            rebindWaiting = false;
            Enqueue(RebindAsync);
        }
        if (!keyWaiting)
            return;
        keyWaiting = false;
        if (open is null)
            Enqueue(() => ShowAsync(quiet: false, _ => true));
    }

    // Hidden by key: the automatic scan leaves it hidden until the window has been closed once
    // (15:17 test: auto scan brought it back 2 s after Alt+F).
    private async Task AutoAsync()
    {
        if (open is not null || busy || !screen.InFront)
            return;
        if (!hiddenByKey)
        {
            await ShowAsync(quiet: true, w => w.Profile.Auto.Get(settings));
            return;
        }
        busy = true;
        try
        {
            bool there = await Task.Run(() =>
            {
                using var frame = screen.Capture();
                return frame is not null && windows.AnyThere(frame);
            });
            if (!there)
                hiddenByKey = false;
        }
        finally
        {
            Release();
        }
    }

    private async Task ShowAsync(bool quiet, Func<IGameWindow, bool> wanted)
    {
        if (busy)
            return;
        busy = true;
        int started = ++generation;
        try
        {
            var total = Stopwatch.StartNew();
            var context = Context();
            var (frame, found) = await Task.Run(async () =>
            {
                var w = Stopwatch.StartNew();
                var captured = screen.Capture();
                long capture = w.ElapsedMilliseconds;
                if (quiet && captured is not null)
                    PaceScan(capture);
                try
                {
                    var seen = captured is null ? null : await windows.LookAsync(captured, context, wanted);
                    // Not the look of the automatic scan that finds nothing: it comes twice a
                    // second while the game is in front and would fill the log.
                    if (!quiet || seen is not null)
                        log($"Time: capture {capture} ms, detection {w.ElapsedMilliseconds - capture} ms");
                    return (captured, seen);
                }
                catch
                {
                    captured?.Dispose();
                    throw;
                }
            });
            showStarted = total;
            if (frame is not null)
            {
                standingScreen = (frame.ScreenBounds, frame.Scale);
                pointer = frame.Pointer;
                language = frame.Language;
            }
            if (frame is null || found is null)
            {
                if (quiet && frame is not null && started == generation)
                    Stand();
                frame?.Dispose();
                if (!quiet)
                    Status?.Invoke(new(Texts.Get(frame is null ? "GameNotFound" : "NoWindowDetected")));
                return;
            }
            if (started != generation)
            {
                frame.Dispose();
                return;
            }
            open = found;
            try
            {
                await LabelAsync(frame, started);
            }
            catch when (started == generation)
            {
                // Nothing shown (prices failed, a window threw): not "open", so the key and the
                // automatic scan look again instead of hiding what is not there (code review).
                open = null;
                throw;
            }
            if (started == generation)
                StartChecks();
        }
        finally
        {
            Release();
        }
    }

    // Labels for the open window on this frame, drawn when the game is in front (else kept:
    // OnForeground shows them on return).
    private async Task LabelAsync(GameCapture.Frame frame, int started)
    {
        var window = open!;
        var book = prices.GetAsync();
        var context = Context();
        Picture picture;
        try
        {
            picture = await Task.Run(() => window.LabelAsync(frame, book, context));
        }
        catch
        {
            frame.Dispose();
            throw;
        }
        if (started != generation || open is null)
        {
            frame.Dispose();
            return;
        }
        open = picture.Next;
        lastPicture = picture;
        lastLabels = picture.Labels;
        lastMarks = picture.Marks ?? [];
        lastRumours = picture.Rumours;
        lastScreen = frame.ScreenBounds;
        lastScale = frame.Scale;
        lastFrame?.Dispose();
        lastFrame = frame;
        if (!screen.InFront)
            return;
        Show(new Scene(frame.ScreenBounds, picture.Labels, picture.Rumours, frame.Scale, lastMarks));
        if (showStarted is { } t)
        {
            log($"Time: hotkey to overlay {t.ElapsedMilliseconds} ms");
            showStarted = null;
        }
        log("shown: " + picture.Shown);
        Status?.Invoke(new(picture.Status));
    }

    // Every GameWindows.FollowEvery: still there, changed (read again) or kept; labels drawn anew.
    private async Task FollowAsync()
    {
        if (busy || open is null)
            return;
        if (!screen.InFront)
        {
            if (visible)
                log("hidden: game not in foreground");
            HideOverlay();
            return;
        }
        busy = true;
        try
        {
            do
            {
                readAgain = false;
                await FollowOnceAsync(generation);
            } while (readAgain && open is not null);
        }
        finally
        {
            Release();
        }
    }

    private async Task FollowOnceAsync(int started)
    {
        var window = open!;
        var context = Context();
        var (frame, next, captureMs) = await Task.Run(async () =>
        {
            var watch = Stopwatch.StartNew();
            var captured = screen.Read(window.FollowArea);
            long took = watch.ElapsedMilliseconds;
            try
            {
                return captured is null ? (captured, (IOpenWindow?)null, took) : (captured, await window.FollowAsync(captured, context), took);
            }
            catch
            {
                captured?.Dispose();
                throw;
            }
        });
        ReportFollow(captureMs, window);
        if (started != generation)
        {
            frame?.Dispose();
            return;
        }
        if (frame is null)
        {
            log("hidden: capture failed");
            Hide();
            return;
        }
        if (next is null)
        {
            frame.Dispose();
            Hide();
            LookAgainSoon();
            return;
        }
        open = next;
        if (exactAsked)
        {
            exactAsked = false;
            open = TooltipGameWindow.Exact(open) ?? open;
        }
        await LabelAsync(frame, started);
    }

    // Every Settings.GlanceIntervalSeconds while shown: closed → hide at once (the once-a-second
    // follow alone would leave prices standing up to 1.5 s); another tab or a moved list →
    // follow now; a tooltip beside the remnant list → labels anew. Runs also while a follow is
    // busy.
    private async Task GlanceAsync()
    {
        if (glanceBusy || open is null || !screen.InFront)
            return;
        glanceBusy = true;
        int started = generation;
        try
        {
            var window = open;
            var ready = prices.Ready;
            var context = Context();
            long capture = 0,
                check = 0;
            var (frame, seen) = await Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var captured = screen.Capture(window.GlanceArea);
                capture = watch.ElapsedMilliseconds;
                try
                {
                    var glanced = captured is null ? Glance.Unchanged : window.Glance(captured, ready, context);
                    check = watch.ElapsedMilliseconds - capture;
                    return (captured, glanced);
                }
                catch
                {
                    captured?.Dispose();
                    throw;
                }
            });
            if (frame is null)
                return; // no picture is no proof; the follow decides
            // What the glance says holds for the window it looked at. A follow may have read
            // the window anew meanwhile; taking the glance's word then put the old reading
            // back, and glance and follow would undo each other for up to a second after every
            // scroll of the remnant list. The next glance looks again. (The pace too: after a
            // Hide the timer stands still and must not be started again; review 19.10.)
            if (started != generation || !ReferenceEquals(open, window))
            {
                frame.Dispose();
                return;
            }
            PaceGlance(capture, window);
            switch (seen)
            {
                case Glance.Gone gone:
                    // The picture the glance lost the window on (with extended diagnostics;
                    // the newest few of each window). Live, the stash tab and the remnant list
                    // are lost within a second of being shown and found again a moment later
                    // (log of 22./23.10.2026: 41 stash losses within 0.2 s of a show), while
                    // every stored picture glances as it looks; what the live picture holds at
                    // that moment is not known. Not for the remnant list: closing it is the
                    // normal way it goes (73 times in one session of 01.11.2026, each picture
                    // showed it closed), and the losses the picture was kept for are explained.
                    if (window.Window is not RemnantGameWindow)
                        Keep("glance-gone-" + Slug(window.Window.Profile.Name), frame.Image);
                    frame.Dispose();
                    log($"Time: quick check capture {capture} ms, check {check} ms");
                    log("hidden (quick check): " + gone.Why);
                    Hide();
                    LookAgainSoon();
                    break;
                case Glance.Reread reread:
                    frame.Dispose();
                    open = reread.Next;
                    if (reread.ClearNow)
                    {
                        generation++; // a follow still running read the old window
                        lastLabels = [];
                        lastMarks = [];
                        lastRumours = null;
                        if (lastScreen is { } b)
                            Show(new Scene(b, [], null, lastScale));
                    }
                    glanceBusy = false;
                    if (busy)
                        readAgain = true;
                    else
                        await FollowAsync();
                    break;
                // (A window that needs prices for its labels asks for this only when they are
                // there; the rumour panel needs none.)
                case Glance.Relabel relabel when !busy:
                    using (frame)
                    {
                        var picture = await relabel.Next.LabelAsync(frame, ready is null ? prices.GetAsync() : Task.FromResult(ready), context);
                        open = picture.Next;
                        lastPicture = picture;
                        lastLabels = picture.Labels;
                        lastMarks = picture.Marks ?? [];
                        lastRumours = picture.Rumours;
                        lastScreen = frame.ScreenBounds;
                        lastScale = frame.Scale;
                        Show(new Scene(frame.ScreenBounds, picture.Labels, picture.Rumours, frame.Scale, lastMarks));
                    }
                    break;
                default:
                    Pointed(frame);
                    frame.Dispose();
                    break;
            }
        }
        finally
        {
            glanceBusy = false;
        }
    }

    // Foreground changed: follow at once with what was shown last; the next follow refreshes.
    private void OnForeground()
    {
        if (open is null)
        {
            // The strip alone: it goes with the game; the automatic scan brings it back.
            if (!screen.InFront && visible)
                HideOverlay();
            return;
        }
        if (!screen.InFront)
        {
            if (visible)
                log("hidden: game not in foreground");
            HideOverlay();
        }
        else if (!visible && lastScreen is { } b)
        {
            Show(new Scene(b, lastLabels, lastRumours, lastScale, lastMarks));
            log("shown again: game in foreground");
        }
    }

    // Own ratings, rune classes or the rune chain changed in the window: the open window is
    // labelled anew from the frame it was last read on, and shown at once.
    private async Task RebindAsync()
    {
        if (busy)
        {
            rebindWaiting = true; // a follow in flight would write back the old ratings (code review)
            return;
        }
        if (open is null || lastFrame is null)
            return;
        var next = open.Rebind(Context());
        if (ReferenceEquals(next, open))
            return;
        open = next;
        var picture = await next.LabelAsync(lastFrame, prices.GetAsync(), Context());
        open = picture.Next;
        lastPicture = picture;
        lastLabels = picture.Labels;
        lastMarks = picture.Marks ?? [];
        lastRumours = picture.Rumours;
        if (visible && lastScreen is { } b)
            Show(new Scene(b, lastLabels, lastRumours, lastScale, lastMarks));
    }

    // Diagnosis key: the screen, what was shown, and what each window reads on it now. Only
    // with the game itself in front: this is the one picture that is saved.
    private async Task DiagnoseAsync(string folder)
    {
        if (!screen.InFront)
        {
            log("diagnostics not saved: game not in foreground");
            Status?.Invoke(new(Texts.Get("DiagnosticsNeedForeground")));
            return;
        }
        var shown = lastPicture;
        var labels = lastLabels;
        var ready = prices.Ready;
        var context = Context() with { Log = _ => { }, Observe = true };
        var current = settings;
        var file = await Task.Run(async () =>
        {
            // Saved: the window as it is on the screen. Read: the same picture as a capture
            // gives it (reference size), so the two cannot differ by a moment.
            using var shot = screen.Snapshot();
            if (shot is null)
                return null;
            using var frame = GameCapture.Stored(shot.Picture, shot.Pointer, null, shot.Popup, shot.Language);
            Directory.CreateDirectory(folder);
            var name = Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
            // From the screen, the picture could hold other windows over the game (a video, a
            // chat): then only the JSON is kept, and the status says so (IGameScreen.PicturesFromWindow).
            bool picture = screen.PicturesFromWindow;
            if (picture)
                Cv2.ImWrite(name + ".png", shot.Picture);
            else
                log("diagnostics: picture not saved (read from the screen, could show other windows)");
            var fresh = new List<object>();
            foreach (var window in windows.All)
            {
                if (await window.LookAsync(frame, context) is not { } found)
                    continue;
                // Without prices yet: an empty book, so the reading is recorded all the same (a
                // diagnosis at 00:04 on 30.09. held nothing but "prices not loaded").
                fresh.Add((await found.LabelAsync(frame, Task.FromResult(ready ?? new PriceBook("")), context)).Description);
            }
            // Pointer: on the saved picture. Scale: of the saved picture against what was
            // read (Shown, Fresh and the labels are in reference coordinates).
            // The settings go in without the two paths: they carry the Windows user name, and
            // a diagnosis is made to be attached to a public issue. What they stand for (popup
            // size, text size) is in the picture's own fields.
            var told = current with { GameConfigFile = "", DataDirectory = "" };
            JsonFiles.Write(name + ".json", new { Settings = told, Pointer = shot.Pointer is { } p ? new[] { p.X, p.Y } : null, frame.Scale, frame.Popup, GameLanguage = shot.Language, shot.TextSize, Shown = shown?.Description, ShownLabels = labels, Fresh = fresh });
            return name;
        });
        if (file is not null && !screen.PicturesFromWindow)
            Status?.Invoke(new(Texts.Get("DiagnosticsNoPictureScreen")));
        Diagnosed?.Invoke(file);
    }

    private void StartChecks()
    {
        follow.Change(GameWindows.FollowEvery, GameWindows.FollowEvery);
        // Not faster than a glance takes, not slower than the follow, which hides as well.
        glanceEvery = TimeSpan.FromSeconds(Math.Clamp(settings.GlanceIntervalSeconds, GameWindows.GlanceEvery.TotalSeconds, GameWindows.FollowEvery.TotalSeconds));
        glancePace = glanceEvery;
        glanceCaptures.Clear();
        glance.Change(glanceEvery, glanceEvery);
    }

    // The glance's pace: its own interval, or slower when its capture takes long — it may
    // keep at most GlanceLoad of the time (measured 19.10.2026 at 3840×2160: the capture of a
    // part takes 47 ms, median, against 20 ms at 2560×1440; at every 50 ms the glance was
    // busy without a pause). The capture time is the median of the last glances (single
    // captures swing between 40 and 120 ms; a mean let the slow ones set the pace, see
    // Tuning.Control.GlanceCaptureWindow) and the pace moves in steps of GlancePaceStep
    // (Paced), so that it settles instead of following every swing (first run 19.10.: 250
    // changes in ten minutes).
    private TimeSpan glanceEvery = GameWindows.GlanceEvery,
        glancePace = GameWindows.GlanceEvery;
    private readonly CaptureTimes glanceCaptures = new(Tuning.Control.GlanceCaptureWindow);
    // The last DiagnosticsEvery captures for the line with extended diagnostics (Report).
    private readonly CaptureTimes glanceReport = new(Tuning.Control.DiagnosticsEvery);

    private void PaceGlance(long captureMs, IOpenWindow window)
    {
        glanceCaptures.Add(captureMs);
        Report(captureMs);
        double usual = glanceCaptures.Median;
        var wanted = Paced(glancePace, usual / Tuning.Control.GlanceLoad, glanceEvery, Tuning.Control.GlancePaceStep, GameWindows.FollowEvery);
        if (wanted == glancePace)
            return;
        glancePace = wanted;
        glance.Change(wanted, wanted);
        log($"glance every {wanted.TotalMilliseconds:0} ms: its capture takes {usual:0} ms (median of {glanceCaptures.Count})");
    }

    // One line per DiagnosticsEvery follows with extended diagnostics on: the median and the
    // slowest capture (IGameScreen.Read of the window's FollowArea) and the last window with
    // its area — against the whole picture's time in the "Time: capture" lines of the key and
    // the automatic scan (OFFEN Z11: 174–280 ms at 3840×2160 on 31.10.2026).
    private readonly CaptureTimes followReport = new(Tuning.Control.DiagnosticsEvery);

    private void ReportFollow(long captureMs, IOpenWindow window) =>
        Sample(followReport, captureMs, () => $"follow: {followReport.Count} captures, median {followReport.Median:0} ms, max {followReport.Max:0} ms, last {window.Window.Profile.Name} {(window.FollowArea is { } a ? $"{a.Width}x{a.Height}" : "whole")}");

    // The capture times of the follows and the glances, counted whatever the switch and written
    // every DiagnosticsEvery only with it (the overlay's line, CompositionOverlay.Measured, counts
    // only with the switch: it has no engine to count for).
    private void Sample(CaptureTimes into, long ms, Func<string> line)
    {
        into.Add(ms);
        if (into.Count < Tuning.Control.DiagnosticsEvery)
            return;
        if (settings.ExtendedDiagnostics)
            log(line());
        into.Clear();
    }

    // One line per DiagnosticsEvery glances with extended diagnostics on: the median and the
    // slowest capture among them and the pace they set. The pace line above comes only when
    // the pace changes, which it does not while the captures stay slow — the time the glance
    // takes beside the overlay's drawing and the readers (OFFEN Z11) is otherwise not in the
    // log. Counted whatever the switch, written only with it.
    private void Report(long captureMs) =>
        Sample(glanceReport, captureMs, () => $"glance: {glanceReport.Count} captures, median {glanceReport.Median:0} ms, max {glanceReport.Max:0} ms, pace {glancePace.TotalMilliseconds:0} ms");

    // The last few capture times and their median: a single slow capture among usual ones
    // does not count, a run of them does. Only touched on the control's thread.
    public sealed class CaptureTimes(int window)
    {
        private readonly double[] times = new double[window];
        private int next;

        public int Count { get; private set; }

        public void Add(double ms)
        {
            times[next] = ms;
            next = (next + 1) % times.Length;
            Count = Math.Min(Count + 1, times.Length);
        }

        public void Clear() => (next, Count) = (0, 0);

        public double Max => Count == 0 ? 0 : times[..Count].Max();

        // Of an even count the lower middle: the pace goes up on a slow capture one short of
        // half, not on half.
        public double Median
        {
            get
            {
                if (Count == 0)
                    return 0;
                var sorted = times[..Count];
                Array.Sort(sorted);
                return sorted[(Count - 1) / 2];
            }
        }
    }

    // The pace a check needs for a capture time: from the pace it has, up to the next step
    // that holds the time, and down a step only when that step still holds it with a fifth
    // of a step to spare — a time right on a boundary does not flip the pace back and forth
    // (seen 19.10. at 2160: the slack alone had only moved the boundary; review 19.10.).
    // Never faster than least, never slower than most.
    public static TimeSpan Paced(TimeSpan pace, double neededMs, TimeSpan least, TimeSpan step, TimeSpan most)
    {
        double ms = Math.Clamp(pace.TotalMilliseconds, least.TotalMilliseconds, most.TotalMilliseconds),
            s = step.TotalMilliseconds;
        if (neededMs > ms)
            ms = Math.Min(most.TotalMilliseconds, Math.Ceiling(neededMs / s) * s);
        else
            while (true)
            {
                double lower = Math.Max(least.TotalMilliseconds, ms - s);
                if (lower >= ms || neededMs > lower - s / 5)
                    break;
                ms = lower;
            }
        return TimeSpan.FromMilliseconds(ms);
    }

    private void Hide()
    {
        log(hiddenByKey ? "hidden: hotkey" : "hidden");
        generation++;
        exactAsked = false;
        follow.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        glance.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        // The strip stays when the chain has links: the window goes, the chain does not.
        if (Standing() is not null && !hiddenByKey && screen.InFront && standingScreen is { } s)
            Show(new Scene(s.Screen, [], null, s.Scale));
        else
            HideOverlay();
        open = null;
        // Nothing of this window may come back with a foreground change while the next one is
        // still being labelled (code review).
        lastScreen = null;
        lastLabels = [];
        lastMarks = [];
        lastRumours = null;
        // A diagnosis after this must not report the window shown last: over a stash tab the
        // program does not know (20.10.), it told "Orbs 1.00" from the tab before.
        lastPicture = null;
    }

    // The scene as drawn last and when, null while nothing is drawn: a follow or glance that
    // ends in the same picture draws nothing (Scene.SameAs) — except once every
    // Tuning.Control.RedrawEvery, in case the window's picture was lost (review 31.10.2026).
    private Scene? drawn;
    private DateTimeOffset drawnAt;

    private void Show(Scene scene)
    {
        scene = scene with { Strip = Standing(scene.Screen.Width / scene.Scale) };
        visible = true;
        var now = time.GetUtcNow();
        if (drawn is not null && drawn.SameAs(scene) && now - drawnAt < Tuning.Control.RedrawEvery)
            return;
        if (drawn is not null && settings.ExtendedDiagnostics)
            log("Overlay: drawn anew, " + (drawn.SameAs(scene) ? "the same picture after RedrawEvery" : drawn.Differs(scene)));
        drawn = scene;
        drawnAt = now;
        Draw?.Invoke(scene);
    }

    // A window the glance or the follow lost: the automatic scan comes Tuning.Control.
    // RescanAfterLoss from now, then at its pace again — not at its next turn, which can be
    // the whole interval away (5 s at the setting of the reporter of issue #6, 26.10.2026):
    // the tooltip of the next item, the tab the stash changed to. Only with the automatic
    // scan on at all; the key alone shows nothing by itself.
    private void LookAgainSoon()
    {
        if (!settings.AutoAny)
            return;
        // A look or follow still running would make the auto turn give up (AutoAsync, busy):
        // the quick turn is set once that is over (Release).
        if (busy)
            lookAgainWaiting = true;
        else
            auto.Change(Tuning.Control.RescanAfterLoss, autoPace);
    }

    private bool lookAgainWaiting;

    private void HideOverlay()
    {
        visible = false;
        drawn = null;
        Draw?.Invoke(null);
    }

    public void Dispose()
    {
        glance.Dispose();
        follow.Dispose();
        auto.Dispose();
        loop.Post(() =>
        {
            lastFrame?.Dispose();
            lastFrame = null;
        });
        loop.Dispose();
    }

    // The control's own thread: runs posted work one after another, and the continuations of
    // awaits started there (it is their SynchronizationContext).
    private sealed class EngineLoop : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Work, object? State)> queue = new();

        public EngineLoop()
        {
            new Thread(Run) { IsBackground = true, Name = "Overlay control" }.Start();
        }

        public void Post(Action work) => Post(_ => work(), null);

        public override void Post(SendOrPostCallback d, object? state)
        {
            try
            {
                queue.Add((d, state));
            }
            catch (InvalidOperationException) { } // disposed: the overlay is closing

        }

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

        private void Run()
        {
            SetSynchronizationContext(this);
            foreach (var (work, state) in queue.GetConsumingEnumerable())
                work(state);
        }

        public void Dispose() => queue.CompleteAdding();
    }
}
