using System.Text.RegularExpressions;

namespace Poe2Pricer.Core.Runes;

// One remnant's contribution to the chain: the runes it passes on to the next remnant (its
// crowned slots — one, two with the atlas notable "Double or Nothing", none when a crowned slot
// is empty), and the list it was chosen from. Fixed when the list closed on a choice that could
// be told from a cancel (Esc, the gamepad's cancel button, was readable and not pressed first);
// provisional when only the row under the mouse or the cursor at the moment the list closed
// says so (the list may have been closed without a choice).
public sealed record ChainLink(IReadOnlyList<Rune> Runes, bool Fixed, string List)
{
    public bool Carries(Rune rune) => Runes.Any(r => r.Id == rune.Id);
    public bool SameAs(ChainLink other) => Fixed == other.Fixed && Runes.Select(r => r.Id).SequenceEqual(other.Runes.Select(r => r.Id));
}

// The runes carried through the expedition of this map: what every remnant chosen so far
// passes on. Learned from the world (RemnantGameWindow.Closed: the row under the mouse or the
// controller's cursor once the list is closed), forgotten with the next map (Entered: Client.txt
// names the area and its seed; the hideout and towns keep it) or by hand (Reset). Any thread.
// Tracked: the maps are known (ChainTracker reads the Client.txt). Until then the chain learns
// nothing — a chain without map boundaries would show runes of the last map as carried and mark
// rows that are new, which is worse than no chain (02.11.2026). The program starts it
// untracked; tests, which know their map, tracked.
public sealed class RuneChain(bool tracked = true)
{
    private readonly object gate = new();
    private readonly List<ChainLink> links = [];
    private (string Area, long Seed)? instance;
    private bool inHaven;
    private volatile bool tracked = tracked;

    public event Action? Changed;

    public bool Tracked
    {
        get => tracked;
        set => tracked = value;
    }

    public IReadOnlyList<ChainLink> Links
    {
        get
        {
            lock (gate)
                return links.ToArray();
        }
    }

    // The player is in the hideout or a town: the chain is kept for the way back through the
    // portal, but there is no next pack — nothing is shown of it there.
    public bool InHaven
    {
        get
        {
            lock (gate)
                return inHaven;
        }
    }

    // The link that carries this rune already, the fixed one first; null: not carried.
    public ChainLink? Carrying(Rune rune)
    {
        lock (gate)
            return links.Where(l => l.Carries(rune)).OrderByDescending(l => l.Fixed).FirstOrDefault();
    }

    // The row under the mouse when the list closed and no cancel could be seen: provisional,
    // unless a fixed link stands for this list. Without the event: it comes as the list goes, and the
    // overlay draws the strip anew right then (OverlayEngine.Hide) — the event would have the
    // engine relabel the window that is just closing, and the glance that saw it close would
    // be dropped for the window's new identity (test of 01.11.2026 at 1200 and 1080).
    public void Guess(string list, IReadOnlyList<Rune> runes) => Put(new ChainLink(runes, false, list), replaceFixed: false, tell: false);

    // The list closed on a choice, a cancel being readable: fixed; replaces whatever this list had.
    public void Confirm(string list, IReadOnlyList<Rune> runes) => Put(new ChainLink(runes, true, list), replaceFixed: true, tell: true);

    private void Put(ChainLink link, bool replaceFixed, bool tell)
    {
        if (!tracked)
            return;
        lock (gate)
        {
            int at = links.FindIndex(l => l.List == link.List);
            if (at >= 0 && links[at].Fixed && !replaceFixed)
                return;
            if (at >= 0 && links[at].SameAs(link))
                return;
            if (at >= 0)
                links[at] = link;
            else
                links.Add(link);
        }
        if (tell)
            Changed?.Invoke();
    }

    // The game generated an area (Client.txt: Generating level … area "…" with seed …). A map
    // is one instance: the same area and seed again (back through the portal) keeps the
    // chain, another instance starts a new one. The hideout and the towns keep it too: a trip
    // to sell is no new expedition. True when the chain was emptied.
    public bool Entered(string area, long seed)
    {
        bool emptied = false, changed;
        lock (gate)
        {
            bool haven = IsHaven(area);
            changed = haven != inHaven;
            inHaven = haven;
            if (!haven && instance != (area, seed))
            {
                instance = (area, seed);
                emptied = links.Count > 0;
                links.Clear();
            }
        }
        if (emptied || changed)
            Changed?.Invoke();
        return emptied;
    }

    // Hideouts ("HideoutFelled…"), towns ("G1_town", "C_G1_town"…) and the hideout-like
    // Kingsmarch: the areas between maps.
    internal static bool IsHaven(string area) =>
        area.Contains("Hideout", StringComparison.OrdinalIgnoreCase) || area.EndsWith("_town", StringComparison.OrdinalIgnoreCase) || area.Contains("Kingsmarch", StringComparison.OrdinalIgnoreCase);

    public void Reset()
    {
        lock (gate)
        {
            if (links.Count == 0)
                return;
            links.Clear();
        }
        Changed?.Invoke();
    }

    // The identity of a list: its first rows as read, so that the same list opened again
    // (RemnantList.Reopened) names the same remnant and a second choice replaces the first.
    public static string KeyOf(IReadOnlyList<RemnantEntry> rows) => string.Join("|", rows.Take(3).Select(r => $"{r.Count}x{r.Text}"));

    // What a row of the list passes on: the runes in its crowned tiles (a row without a crown
    // passes nothing).
    public static Rune[] Carried(RuneTile[] row) => row.Where(t => t.Crowned && t.Rune is not null).Select(t => t.Rune!).ToArray();
}

// Reads the game's Client.txt for the areas the player enters: "Generating level 80 area
// "MapAugury" with seed 3141592653". Only the lines written after the reader was made, plus
// the last such line before it (the area the player is in at the program's start). Nothing
// else of the file is read or kept; the file is only read (shared with the game).
public sealed class ClientLog(string path)
{
    public string Path { get; } = path;
    private long position = -1;
    private static readonly Regex Generating = new("Generating level (\\d+) area \"([^\"]+)\" with seed (\\d+)", RegexOptions.Compiled);

    public static string? Find(string? gameFolder) =>
        gameFolder is null ? null : System.IO.Path.Combine(gameFolder, "logs", "Client.txt") is var file && File.Exists(file) ? file : null;

    // The newest area line since the last poll; null when there is none or the file cannot be
    // read right now.
    public (string Area, long Seed)? Poll()
    {
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (position < 0 || position > stream.Length)
                position = Math.Max(0, stream.Length - Tail);
            stream.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
            string? last = null;
            while (reader.ReadLine() is { } line)
                if (Generating.Match(line) is { Success: true } m)
                    last = line;
            position = stream.Length;
            return last is null ? null : Parse(last);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static (string Area, long Seed)? Parse(string line) =>
        Generating.Match(line) is { Success: true } m && long.TryParse(m.Groups[3].Value, out long seed) ? (m.Groups[2].Value, seed) : null;

    // How far back the first poll looks for the area the player is in: the file grows by a
    // few KB per area, this covers the last few.
    private const int Tail = 256 * 1024;
}
