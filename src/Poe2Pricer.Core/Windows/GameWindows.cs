using OpenCvSharp;
using Poe2Pricer.Core.Market;

namespace Poe2Pricer.Core;

// Everything a window needs besides the frame: rumour table, game environment, the user's
// settings (floor, automatic scan) and where to write log lines.
// Keep: a picture a window hands over as evidence, with a word for what it shows; kept where
// the control says (OverlayEngine.EvidenceFolder), or nowhere.
// Observe: the look must leave no trace. The diagnosis reads every window anew from its
// picture; a window that keeps a record of what it saw (the rumour panel: openings per island)
// reports the record as it stands instead of adding to it (two diagnoses on 19.10. counted two
// openings that never were, cleared the "new" marks and moved "probably complete" closer).
// Clock: the time a window's reckoning goes by (when the remnant list was last seen before it
// closed); the tests' fake time, else the system's.
// Escaped: when the keyboard hook or the gamepad last saw a cancel (Esc, the cancel button)
// while the game was in front (null: never, or not known here) — the remnant list closes on
// it without a choice; Confirmed: when the gamepad last saw its confirm button (a mouse click
// is not read) — the list closes on it with one (RemnantGameWindow.Closed). EscapeReadable:
// the keyboard hook works; GamepadReadable: a gamepad answers, its buttons are read. Without,
// a cancel goes unseen.
public sealed record LookContext(RumourBook Rumours, Settings Settings, Action<string> Log, Action<string, Mat>? Keep = null, bool Observe = false, TimeProvider? Clock = null, Func<DateTimeOffset?>? Escaped = null, bool EscapeReadable = true, bool GamepadReadable = false, Func<DateTimeOffset?>? Confirmed = null)
{
    public DateTimeOffset Now => (Clock ?? TimeProvider.System).GetUtcNow();
}

// A window's profile: its name, the checkbox that lets the automatic scan look for it, and how
// often an open window is followed (changed?). How often it is glanced at (still there?) is
// the user's setting, the same for every window (Settings.GlanceIntervalSeconds).
public sealed record WindowProfile(string Name, AutoSwitch Auto);

// A checkbox of the automatic scan in the settings window; windows may share one (remnant list
// and rumour panel: Expedition). A new window that needs its own adds a Settings field and an
// entry to All; the settings window shows All in this order. Label: the name of its text in
// the language file (Texts).
public sealed record AutoSwitch(string Label, Func<Settings, bool> Get, Func<Settings, bool, Settings> Set)
{
    public static readonly AutoSwitch Ritual = new("SwitchRitual", s => s.AutoRitual, (s, on) => s with { AutoRitual = on });
    public static readonly AutoSwitch Rumours = new("SwitchRumours", s => s.AutoRumours, (s, on) => s with { AutoRumours = on });
    public static readonly AutoSwitch Remnant = new("SwitchRemnant", s => s.AutoRemnant, (s, on) => s with { AutoRemnant = on });
    public static readonly AutoSwitch Stash = new("SwitchStash", s => s.AutoStash, (s, on) => s with { AutoStash = on });
    public static readonly AutoSwitch[] All = [Ritual, Rumours, Remnant, Stash];
}

// One kind of game window the overlay knows (Favours, stash tab, remnant list, rumour panel).
public interface IGameWindow
{
    WindowProfile Profile { get; }

    // Found and read, without prices; null when the window is not on the frame.
    Task<IOpenWindow?> LookAsync(GameCapture.Frame frame, LookContext context);

    // Only whether the window is on the frame (a few ms).
    bool IsThere(GameCapture.Frame frame);
}

// A window that is shown, with what was read from it. Immutable: every step returns the
// window as it is now (the same instance when nothing changed).
public interface IOpenWindow
{
    IGameWindow Window { get; }

    // Every Settings.GlanceIntervalSeconds (50 ms): still there, another tab, list moved, tooltip moved. No
    // OCR, no waiting for prices (ready: the prices that are loaded, null while loading).
    // The frame holds only GlanceArea; the rest of it is black.
    Glance Glance(Mat frame, PriceBook? ready, LookContext context);

    // The same with the frame itself: for a window whose labels follow the mouse pointer (the
    // remnant list's caption). Others look at the picture only.
    Glance Glance(GameCapture.Frame frame, PriceBook? ready, LookContext context) => Glance(frame.Image, ready, context);

    // The part of the picture the glance looks at; null: all of it. Only that part is captured,
    // which is what lets the glance run this often (GameCapture.Capture).
    Box? GlanceArea => null;

    // The part of the picture the follow and the labelling read; null: all of it. Everything
    // FollowAsync and LabelAsync look at must lie inside (the frame is black outside): the
    // window, its anchor, and wherever a popup, hint or dialog over it is searched. The rumour
    // panel reads the chart all over the map and keeps the whole; a tooltip's body may reach
    // below any band its title is searched in, so it keeps the whole too.
    Box? FollowArea => null;

    // Every GameWindows.FollowEvery (1 s): null when the window is gone (the reason is logged); read
    // again when it changed, else kept.
    Task<IOpenWindow?> FollowAsync(GameCapture.Frame frame, LookContext context);

    // Labels for this frame. Popups and tooltip names are read while the prices may still load.
    Task<Picture> LabelAsync(GameCapture.Frame frame, Task<PriceBook> prices, LookContext context);

    // The rumour table or own ratings changed.
    IOpenWindow Rebind(LookContext context) => this;
}

public abstract record Glance
{
    public sealed record Same : Glance;

    // Window closed: hide now.
    public sealed record Gone(string Why) : Glance;

    // Follow at once with Next (another stash tab, remnant list scrolled); ClearNow: the old
    // labels go before the new ones are there. Next must be the window as this glance saw it,
    // so that the next glance does not report the same again: glances keep coming while the
    // follow runs, and each ClearNow drops the follow in flight — reported over and over,
    // nothing is ever read (the rumour panel at another island).
    public sealed record Reread(IOpenWindow Next, bool ClearNow) : Glance;

    // Only the labels change (remnant list: a tooltip beside it came or went; rumour panel:
    // it moved a little).
    public sealed record Relabel(IOpenWindow Next) : Glance;

    public static readonly Glance Unchanged = new Same();
}

// What to draw for an open window. Shown: the log line when it is drawn ("gezeigt: …");
// Description: what the window read and named, for diagnosis and the golden-master check.
// Marks: drawn with the labels, but no prices — the frames and the caption of the remnant
// runes; the golden-master check and the tests of the prices look at Labels alone.
public sealed record Picture(
    IOpenWindow Next,
    Label[] Labels,
    (Box Panel, RumourLine[] Lines, IslandSummary? Island)? Rumours,
    string Status,
    string Shown,
    object Description,
    Label[]? Marks = null
);

// The windows the overlay knows, in the order a look tries them: the only place of that order.
public sealed class GameWindows(IReadOnlyList<IGameWindow> all)
{
    public IReadOnlyList<IGameWindow> All { get; } = all;

    public static GameWindows Standard(Pipeline pipeline, Market.ITradePrices? tradePrices = null, Runes.RuneChain? chain = null) =>
        new([new FavoursGameWindow(pipeline), new StashGameWindow(pipeline), new RemnantGameWindow(pipeline, chain ?? new Runes.RuneChain()), new RumourGameWindow(pipeline), new TooltipGameWindow(pipeline, tradePrices)]);

    // The first wanted window on the frame.
    public async Task<IOpenWindow?> LookAsync(GameCapture.Frame frame, LookContext context, Func<IGameWindow, bool> wanted)
    {
        foreach (var window in All.Where(wanted))
            if (await window.LookAsync(frame, context) is { } open)
                return open;
        return null;
    }

    public bool AnyThere(GameCapture.Frame frame) => All.Any(w => w.IsThere(frame));

    public static readonly TimeSpan GlanceEvery = Tuning.Control.GlanceEvery;
    public static readonly TimeSpan FollowEvery = Tuning.Control.FollowEvery;
}
