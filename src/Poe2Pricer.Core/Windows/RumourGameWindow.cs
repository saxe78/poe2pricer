using OpenCvSharp;
using Poe2Pricer.Core.Market;

namespace Poe2Pricer.Core;

// The rumour panel on the atlas: lines read by OCR and matched against the rumour table; the
// overlay draws their ratings beside the panel, no prices.
public sealed class RumourGameWindow(Pipeline pipeline) : IGameWindow
{
    internal Pipeline Pipeline { get; } = pipeline;

    public WindowProfile Profile { get; } = new("Rumours", AutoSwitch.Rumours);

    public bool IsThere(GameCapture.Frame frame) => Pipeline.Rumours.Locate(frame.Image, popup: frame.Popup) is not null;

    // The islands seen since the start, with their rumours (IslandBook).
    public IslandBook Islands { get; } = new();

    public async Task<IOpenWindow?> LookAsync(GameCapture.Frame frame, LookContext context)
    {
        if (Pipeline.Rumours.Locate(frame.Image, popup: frame.Popup) is not { } panel)
            return null;
        var lines = await Pipeline.Rumours.ReadAsync(frame.Image, panel, context.Rumours, frame.Popup, frame.Language);
        var island = Place(frame.Image, panel, lines, opened: true, previous: null, context, out bool tryAgain);
        return new Open(this, panel, lines, RumourReader.Signature(frame.Image, panel), frame.Popup, island, Unplaced: tryAgain);
    }

    // The panel placed at its island: by the chart around the ship marker, which is found
    // next to the panel (IslandMarks). No marker (off the screen, hidden): no island, the
    // rumours are shown without a collection. opened: the panel appeared after being closed
    // (a roll only when its lines changed, IslandBook.Place).
    // Observing (the diagnosis): the island as the book has it, nothing counted or added.
    // tryAgain: no island yet because too little of the chart was read — worth another
    // reading once the panel rests (not so without a ship marker).
    private Island? Place(Mat image, RumourPanel panel, RumourLine[] lines, bool opened, Island? previous, LookContext context, out bool tryAgain)
    {
        tryAgain = false;
        if (IslandMarks.Marker(image, panel.Panel) is not { } marker)
        {
            context.Log("Rumour window: no ship marker beside the panel, island unknown");
            return null;
        }
        var print = IslandMarks.Print(image, panel.Panel, marker);
        if (context.Observe)
        {
            var known = Islands.Find(print);
            var book = Islands.All;
            context.Log(known is null ? "Rumour window at an island not seen yet (observed)" : $"Rumour window at island {IndexOf(book, known)} of {book.Count}, roll {known.Rolls} (observed)");
            return known;
        }
        // Too little of the chart (the map still panning in the controller UI, a tooltip over
        // the water): no judgement possible, the island is placed when the panel rests.
        if (print.Points.Length < Tuning.Islands.MinimumEvidence)
        {
            context.Log($"Rumour window: only {print.Points.Length} chart points, island not placed yet");
            tryAgain = true;
            return null;
        }
        var island = Islands.Place(print, lines.Select(l => l.Entry?.Name).ToArray(), opened, previous);
        var all = Islands.All;
        context.Log($"Rumour window at island {IndexOf(all, island)} of {all.Count}, roll {island.Rolls}, {island.Seen.Count} rumours seen, {print.Points.Length} chart points");
        return island;
    }

    private static int IndexOf(IReadOnlyList<Island> all, Island island)
    {
        for (int i = 0; i < all.Count; i++)
            if (all[i] == island)
                return i + 1;
        return 0;
    }

    // The same opening read again at its island: the rows as read now, and the chart as read
    // now when it shows more than the one stored.
    private void Refresh(Mat image, RumourPanel panel, Island island, RumourLine[] lines)
    {
        var print = IslandMarks.Marker(image, panel.Panel) is { } marker ? IslandMarks.Print(image, panel.Panel, marker) : null;
        Islands.Update(island, lines.Select(l => l.Entry?.Name).ToArray(), print);
    }

    // Stale: the glance saw that what was read no longer holds; the follow reads again, and
    // until it has, the glance has nothing more to say.
    // Resting: the panel was seen twice at the same place. In the controller UI it flies in,
    // up to about 100 px within the first tenth of a second, and ratings drawn where the first
    // picture had it would slip into place a moment later. They are drawn once the panel rests.
    // Resized: the game runs at another height than the readers are measured at (GameView).
    // Popup: the game's popup size when the panel was read (the glance gets a picture without it).
    // Island: where the panel was placed (null: no marker found, nothing is collected).
    // Gone: a glance did not find the panel; what the follow finds is a new opening, whatever
    // the island (review 19.10.: a roll read on the follow after a short absence was counted as
    // the same opening).
    private sealed record Open(RumourGameWindow Kind, RumourPanel Panel, RumourLine[] Lines, double[] Signature, double Popup, Island? Island, bool Stale = false, bool Resting = false, bool Gone = false, bool Unplaced = false) : IOpenWindow
    {
        public IGameWindow Window => Kind;

        // The panel with room to move: in the controller UI it does not stand still at once but
        // drifts a few pixels after it opens; without the room it would be read anew several
        // times in the first half second.
        public Box? GlanceArea
        {
            get
            {
                var seen = Panel.Panel.Union(Panel.Rows);
                int m = Tuning.Rumours.TrackMargin;
                return new Box(seen.X - m, seen.Y - m, seen.Width + 2 * m, seen.Height + 2 * m);
            }
        }

        // Still near its place with the same lines, only moved: the ratings move with it,
        // nothing is cleared or read. No title near its place: the panel is closed or stands at
        // another island — the ratings go at once, the follow looks where it is now. Other
        // lines: read again at once.
        public Glance Glance(Mat frame, PriceBook? ready, LookContext context)
        {
            if (Stale)
                return Core.Glance.Unchanged;
            if (Kind.Pipeline.Rumours.Locate(frame, GlanceArea, Popup) is not { } now)
                return new Glance.Reread(this with { Stale = true, Gone = true }, ClearNow: true);
            if (RumourReader.Changed(RumourReader.Signature(frame, now), Signature))
                return new Glance.Reread(this with { Stale = true }, ClearNow: false);
            int dx = now.Title.X - Panel.Title.X,
                dy = now.Title.Y - Panel.Title.Y;
            // On a resized picture the title is found to a pixel more or less (it is looked
            // for in steps of a hundredth of its size): that is the same place.
            if (Math.Abs(dx) <= Tuning.Rumours.SamePlace && Math.Abs(dy) <= Tuning.Rumours.SamePlace)
            {
                if (Resting)
                    return Core.Glance.Unchanged;
                context.Log("Rumour window at rest, tiers shown");
                return new Glance.Relabel(this with { Resting = true });
            }
            context.Log($"Rumour window moved by {dx}, {dy}");
            return new Glance.Relabel(this with { Panel = now, Lines = Lines.Select(l => l with { CenterY = l.CenterY + dy }).ToArray() });
        }

        // Panel moved (other island) or changed (other rumours) → read again.
        public async Task<IOpenWindow?> FollowAsync(GameCapture.Frame frame, LookContext context)
        {
            var panel = Kind.Pipeline.Rumours.Locate(frame.Image, popup: frame.Popup);
            if (panel is null)
            {
                context.Log("hidden: Rumour window no longer detected");
                return null;
            }
            // No island yet (too little of the chart while the panel flew in) and the panel at
            // rest: placed now from this full picture, with the lines as read — once.
            if (Unplaced && Resting && !Stale)
                return this with { Island = Kind.Place(frame.Image, panel, Lines, opened: true, previous: null, context, out _), Unplaced = false };
            if (!Stale && Math.Abs(panel.Title.X - Panel.Title.X) <= Tuning.Rumours.SamePlace && Math.Abs(panel.Title.Y - Panel.Title.Y) <= Tuning.Rumours.SamePlace && !RumourReader.Changed(RumourReader.Signature(frame.Image, Panel), Signature))
                return this;
            var lines = await Kind.Pipeline.Rumours.ReadAsync(frame.Image, panel, context.Rumours, frame.Popup, frame.Language);
            context.Log("Rumour window rescanned");
            // Read again while open. The same rows: the same opening at the same island, only
            // the rows are brought up to date — placing it anew could land on another entry
            // of the book (19.10.: one island, three entries, the count jumping between them
            // at every rescan). Other rows: the first reading corrected, or another island
            // (the cursor moved on): placed anew, the same opening when it is the same island.
            // After an absence (Gone) it is a new opening, whatever the rows.
            Island? island;
            bool tryAgain = false;
            if (!Gone && Island is { } same && lines.Select(l => l.Entry?.Name).SequenceEqual(Lines.Select(l => l.Entry?.Name)))
            {
                Kind.Refresh(frame.Image, panel, same, lines);
                island = same;
            }
            else
                island = Kind.Place(frame.Image, panel, lines, opened: true, previous: Gone ? null : Island, context, out tryAgain);
            // Other lines on a panel that rests where it stood: shown at once.
            return new Open(Kind, panel, lines, RumourReader.Signature(frame.Image, panel), frame.Popup, island, Resting: Resting && panel.Title == Panel.Title, Unplaced: tryAgain);
        }

        // Own ratings changed in the settings window: shown at once. A line without an entry may
        // have one in the changed table (the community table arrived after the panel was read,
        // at the first start; an own row was added for it): the panel is read again with the
        // next follow, so that such lines do not keep their "?" until the panel is closed.
        public IOpenWindow Rebind(LookContext context) =>
            this with
            {
                Lines = Lines.Select(l => l with { Entry = l.Entry is null ? null : context.Rumours.Entries.FirstOrDefault(e => e.Name == l.Entry.Name) ?? l.Entry }).ToArray(),
                Stale = Stale || Lines.Any(l => l.Entry is null),
            };

        public Task<Picture> LabelAsync(GameCapture.Frame frame, Task<PriceBook> prices, LookContext context) =>
            Task.FromResult(
                new Picture(
                    this,
                    [],
                    Resting ? (Panel.Panel, Lines, Island?.Summary(Lines.Select(l => l.Entry?.Name).OfType<string>(), context.Rumours)) : null,
                    Texts.Get("RumourStatus", string.Join(", ", Lines.Select(l => l.Entry is { } e ? $"{e.Name} {e.Shown}" : "?"))),
                    $"Rumour window, {Lines.Length} rows, {Lines.Count(l => l.Entry is not null)} matched{(Resting ? "" : " (read; shown once the window is at rest)")}",
                    new
                    {
                        Window = "rumour",
                        Panel,
                        // Only name and shown rating of the matched entry: the table itself stays out of the repo.
                        Lines = Lines.Select(l => new { l.Text, l.CenterY, Entry = l.Entry?.Name, Shown = l.Entry?.Shown }),
                        Island = Island is { } i ? new { i.Rolls, i.Seen, i.New, i.Complete, i.SinceNew, i.ProbablyComplete, Marker = new { i.Print.Marker.X, i.Print.Marker.Y }, Points = i.Print.Points.Length } : null,
                    }
                )
            );
    }
}
