using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using Tune = Poe2Pricer.Core.Tuning.Rumours;

namespace Poe2Pricer.Core;

// Rating: the community value; Own: the player's rating ("" = community counts); Custom:
// added by the player, not in the sheet.
// Remark: text after the tier ("S+(is a gamble)" → tier "S+", remark "is a gamble") or the
// source's note on the rating; Warning: shown red at the source (e.g. "Horrible layout …").
public sealed record Rumour(
    string Name,
    string Map,
    string Mods,
    string Rating,
    string Own = "",
    string Note = "",
    bool Custom = false,
    string Remark = "",
    string Warning = ""
)
{
    public string Shown => Own.Length > 0 ? Own : Rating;

    // One line for the overlay: own note, else warning, else the source's note.
    public string Hint => Note.Length > 0 ? Note : Warning.Length > 0 ? Warning : Remark;
}

// Island rumour ratings, loaded at run time and cached, never shipped with the tool:
// 1. Gnejs PoE Tools, rumours page: names as in the game ("Warm but risky"), notes and red
//    warnings, "ratings & notes from sheet by Jestra1220" (no licence stated);
// 2. fallback: the public "Expedition Cheatsheet" Google sheet PoeAncientsPriceHelper uses,
//    an older copy with shortened names ("It's Warm") and no notes.
// The player's own ratings, notes and rows live in rumours-own.json.
public sealed class RumourBook
{
    public const string SheetCsv = "https://docs.google.com/spreadsheets/d/16YU8mSS7TdLPdmOunVjiPn_NrKVGfcnMkuMQDy8jgZA/export?format=csv&gid=0";
    public const string SheetLink = "https://docs.google.com/spreadsheets/d/16YU8mSS7TdLPdmOunVjiPn_NrKVGfcnMkuMQDy8jgZA/htmlview";
    public static readonly string[] Ratings = ["S+", "S", "A+", "A", "B+", "B", "C", "D", "F"];

    public const string GnejsPage = "https://poe.gnejs.app/poe2/expedition/rumours";
    private readonly string folder;
    private string CacheFile => Path.Combine(folder, "rumours-sheet.csv");
    private string GnejsCache => Path.Combine(folder, "rumours-gnejs.html");
    private string OwnFile => Path.Combine(folder, "rumours-own.json");
    private string WeightsFile => Path.Combine(folder, "tier-weights.json");

    // The weights of the tiers for the expedition tier (TierWeights), the player's own from
    // tier-weights.json, else the standard ones. An unreadable file counts as the standard.
    public TierWeights Weights
    {
        get => Volatile.Read(ref weights);
        private set => Volatile.Write(ref weights, value);
    }

    private TierWeights weights = TierWeights.Standard;

    // The weights as edited in the settings window, kept and written; why not written, if not.
    public Told? SetWeights(TierWeights next)
    {
        lock (gate)
        {
            Weights = next;
            try
            {
                Directory.CreateDirectory(folder);
                var part = WeightsFile + ".part";
                File.WriteAllText(part, JsonSerializer.Serialize(next.Weights, JsonFiles.Options));
                File.Move(part, WeightsFile, overwrite: true);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new("CustomTiersNotSaved", e.Message);
            }
        }
    }

    private void ReadWeights()
    {
        if (!File.Exists(WeightsFile))
            return;
        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(WeightsFile), JsonFiles.Options) is { } read)
                Weights = new TierWeights(read.Where(w => RumourBook.Ratings.Contains(w.Key) && double.IsFinite(w.Value)).ToDictionary());
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // The standard weights count; the expedition tier is a hint, not something to stop the start for.
        }
    }

    // Where the table comes from: the name of its text in the language file (Texts), "" before
    // anything was loaded.
    public const string FromGnejs = "SourceGnejs";
    public const string FromSheet = "SourceSheet";

    public string Source { get; private set; } = "";
    public DateTime? SheetDate { get; private set; }

    // Something the user should know after loading (own ratings unreadable), else null.
    public Told? Problem { get; private set; }

    // Sheet rows from the source and the player's own rows; Entries is both merged, replaced
    // whole on every change and never changed in place: the settings window, the rumour reading
    // and a refresh run on different threads.
    private readonly Lock gate = new();
    private List<Rumour> sheet = [];
    private List<Rumour> own = [];
    private Rumour[] entries = [];

    public IReadOnlyList<Rumour> Entries => Volatile.Read(ref entries);

    // folder: for tests; the app uses Settings.Folder.
    private RumourBook(string folder) => this.folder = folder;

    public static RumourBook LoadCached(string? folder = null)
    {
        var book = new RumourBook(folder ?? Settings.Folder);
        book.ReadWeights();
        List<Rumour> list = [];
        // A copy that cannot be read just now (held by a virus scanner or a sync tool) counts
        // as missing: the table comes with the refresh at the start. It must not end the
        // program before its window is there.
        if (Cached(book.GnejsCache) is { } page && ParseGnejs(page) is { Count: > 0 } g)
        {
            list = g;
            book.SheetDate = File.GetLastWriteTime(book.GnejsCache);
            book.Source = FromGnejs;
        }
        else if (Cached(book.CacheFile) is { } csv)
        {
            list = Parse(csv);
            book.SheetDate = File.GetLastWriteTime(book.CacheFile);
            book.Source = FromSheet;
        }
        lock (book.gate)
        {
            book.sheet = Corrected(list);
            book.own = book.LoadOwn();
            book.Rebuild();
        }
        return book;
    }

    private static string? Cached(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Fetches Gnejs, else the sheet; on any failure the cached copy stays.
    public async Task<bool> RefreshAsync(HttpClient http, CancellationToken token = default)
    {
        try
        {
            var page = await http.GetStringAsync(GnejsPage, token);
            var list = ParseGnejs(page);
            if (list.Count > 0)
            {
                SetSheet(list, FromGnejs);
                await KeepAsync(GnejsCache, page, token);
                return true;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        if (Source == FromGnejs)
            return false; // keep the Gnejs copy rather than fall back to the older sheet
        try
        {
            var csv = await http.GetStringAsync(SheetCsv, token);
            var list = Parse(csv);
            if (list.Count == 0)
                return false;
            SetSheet(list, FromSheet);
            await KeepAsync(CacheFile, csv, token);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    // The copy for the next start, written beside the file and moved over it. The table counts
    // whether or not the copy can be written: a file held by another program must not throw
    // a good download away.
    private async Task KeepAsync(string file, string text, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(file + ".part", text, token);
            File.Move(file + ".part", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Where the community table names an island that is not the one the game's own text
    // points at (RumourTexts, from the game files): the rumour's name, as the game gives it to
    // that island. The sheet has the two names swapped, not the islands: mods, rating and note
    // of a row belong to the island it names. Seen in the game on 19.10.2026 (the chart column)
    // and on 02.11.2026 at Barren Atoll (seen in play: strongboxes all over the ground, the map mods
    // 616 % strongboxes, no gold line — the sheet's "Bleak and Awful" row, Strongbox, rating D;
    // until then the program moved the island to the name and showed "Something Fishy: Gold B+"
    // there). Not reported to the sheet's author: the site that carries the sheet has no
    // contact, and the ratings come from a streamer's sheet (02.11.).
    // Looked up by Key, as the book keys names everywhere else: a spelling variant in the
    // sheet ("Something Fishy...") must not leave the row uncorrected (review 19.10.).
    public static readonly IReadOnlyDictionary<string, string> IslandCorrections = new Dictionary<string, string>
    {
        ["Something Fishy"] = "Barren Atoll",
        ["Bleak and Awful"] = "Bleached Shoals",
    };

    // A row whose name the game gives to another island takes the game's name for its own
    // island; where no name is known for that island, the island moves to the name as before.
    public static List<Rumour> Corrected(IEnumerable<Rumour> list) =>
        list.Select(r =>
        {
            if (IslandCorrections.FirstOrDefault(c => Key(c.Key) == Key(r.Name)) is not { Key.Length: > 0 } correction || r.Map == correction.Value)
                return r;
            return IslandCorrections.FirstOrDefault(c => c.Value == r.Map) is { Key.Length: > 0 } named ? r with { Name = named.Key } : r with { Map = correction.Value };
        }).ToList();

    private void SetSheet(List<Rumour> list, string source)
    {
        lock (gate)
        {
            sheet = Corrected(list);
            SheetDate = DateTime.Now;
            Source = source;
            Rebuild();
        }
    }

    // The rows as edited in the settings window: only own ratings, notes and own rows are
    // taken over, laid onto the current sheet (a refresh meanwhile is not overwritten) and saved.
    // Returns why the file was not written, if it was not: the ratings count for this run all
    // the same, and the window says so instead of "Saved".
    public Told? SetOwn(IEnumerable<Rumour> rows)
    {
        lock (gate)
        {
            own = rows.Where(IsOwn).ToList();
            Rebuild();
            return WriteOwn();
        }
    }

    private static bool IsOwn(Rumour e) => e.Own.Length > 0 || e.Note.Length > 0 || e.Custom;

    // Own ratings survive when a rumour leaves the sheet: they are kept as custom rows. Two own
    // rows for one rumour ("It's Warm" rated and an own row "Its warm") must not make the
    // start fail: the one saved last wins. Under the gate.
    private void Rebuild()
    {
        var byName = new Dictionary<string, Rumour>();
        var blank = new List<Rumour>();
        foreach (var o in own)
        {
            var key = Key(o.Name);
            if (key.Length == 0)
            {
                blank.Add(o);
                continue;
            }
            byName.Remove(key);
            byName[key] = o;
        }
        var merged = sheet
            .Select(s => byName.Remove(Key(s.Name), out var o) ? s with { Own = o.Own, Note = o.Note } : s)
            .ToList();
        merged.AddRange(byName.Values.Select(o => o with { Custom = true }));
        merged.AddRange(blank.Select(o => o with { Custom = true }));
        Volatile.Write(ref entries, merged.ToArray());
    }

    // Written next to the file and moved over it; from the merged rows, so no rumour twice.
    public Told? SaveOwn()
    {
        lock (gate)
            return WriteOwn();
    }

    private bool keepOwnFile;

    private Told? WriteOwn()
    {
        if (keepOwnFile)
            return new("CustomTiersNotSavedKept", OwnFile);
        try
        {
            Directory.CreateDirectory(folder);
            var part = OwnFile + ".part";
            File.WriteAllText(part, JsonSerializer.Serialize(Entries.Where(IsOwn).ToList(), JsonFiles.Options));
            File.Move(part, OwnFile, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new("CustomTiersNotSaved", e.Message);
        }
    }

    // An unreadable file is kept as a copy and reported, instead of being taken for "no own
    // ratings" and overwritten with the next save.
    private List<Rumour> LoadOwn()
    {
        if (!File.Exists(OwnFile))
            return [];
        try
        {
            // Entries that are no rumours (null, no name: a file changed by hand) are left out,
            // and a missing rating or note is none; they ended the program at every start.
            return (JsonSerializer.Deserialize<List<Rumour>>(File.ReadAllText(OwnFile), JsonFiles.Options) ?? [])
                .Where(e => e?.Name is not null)
                .Select(e => e with { Map = e.Map ?? "", Mods = e.Mods ?? "", Rating = e.Rating ?? "", Own = e.Own ?? "", Note = e.Note ?? "" })
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Held by another program: not read, and not overwritten with what this run knows.
            keepOwnFile = true;
            Problem = new("CustomTiersLocked", e.Message, OwnFile);
            return [];
        }
        catch (JsonException)
        {
            var copy = Path.Combine(folder, "rumours-own.broken.json");
            try
            {
                File.Copy(OwnFile, copy, overwrite: true);
                Problem = new("CustomTiersUnreadableBackup", copy);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // No copy: the unreadable file stays and is not overwritten (code review).
                keepOwnFile = true;
                Problem = new("CustomTiersUnreadableNoBackup", e.Message, OwnFile);
            }
            return [];
        }
    }

    // Gnejs rumours page: server-rendered tables (Grand Expeditions, Unique Maps, Bosses) with
    // cells Rumour, Map, Mod, Rating. A red, titled name carries a warning, a titled name a
    // tip, a titled rating a note ("D (note)").
    public static List<Rumour> ParseGnejs(string page)
    {
        var list = new List<Rumour>();
        string Clean(string s) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "")).Trim();
        string Title(string s) => System.Text.RegularExpressions.Regex.Match(s, "title=\"([^\"]*)\"") is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        foreach (System.Text.RegularExpressions.Match row in System.Text.RegularExpressions.Regex.Matches(page, "<tr>(.*?)</tr>", System.Text.RegularExpressions.RegexOptions.Singleline))
        {
            var cells = System.Text.RegularExpressions.Regex.Matches(row.Groups[1].Value, "<td[^>]*>(.*?)</td>", System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(c => c.Groups[1].Value)
                .ToArray();
            if (cells.Length < 4 || Clean(cells[0]).Length == 0)
                continue;
            string warning = cells[0].Contains("text-red") ? Title(cells[0]) : "";
            string tip = warning.Length == 0 ? Title(cells[0]) : "";
            var (tier, remark) = SplitRating(Clean(cells[3]));
            string note = Title(cells[3]);
            if (remark.Equals("note", StringComparison.OrdinalIgnoreCase))
                remark = "";
            string all = string.Join(" — ", new[] { note, remark, tip }.Where(x => x.Length > 0));
            list.Add(new(Clean(cells[0]), Clean(cells[1]), Clean(cells[2]), tier, Remark: all, Warning: warning));
        }
        return list;
    }

    // Columns Rumor, Map Type, Mods, Rating; section headings ("Unique Maps", "Bosses",
    // "Sagas") have no rating and are skipped, as are empty lines and the header row.
    public static List<Rumour> Parse(string csv)
    {
        var list = new List<Rumour>();
        foreach (var line in csv.Split('\n').Skip(1))
        {
            var f = SplitCsv(line.TrimEnd('\r'));
            if (f.Count < 4 || f[0].Trim().Length == 0 || f[3].Trim().Length == 0)
                continue;
            var (tier, remark) = SplitRating(f[3].Trim());
            list.Add(new(f[0].Trim(), f[1].Trim(), f[2].Trim(), tier, Remark: remark));
        }
        return list;
    }

    private static (string Tier, string Remark) SplitRating(string cell)
    {
        var m = System.Text.RegularExpressions.Regex.Match(cell, @"^([SABCDF][+-]?)\s*\(?\s*(.*?)\s*\)?$");
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (cell, "");
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    cur.Append(c);
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                fields.Add(cur.ToString());
                cur.Clear();
            }
            else
                cur.Append(c);
        }
        fields.Add(cur.ToString());
        return fields;
    }

    public static string Key(string s) => TitleReader.Key(s);

    // The panel writes rumours in a handwriting font, abbreviated and worded differently from
    // the sheet ("Somethin' fishy..." vs "Something Fishy"); OCR garbles it ("$omüiw' fishå...").
    // With ~23 entries a loose match is still clear: a reading that names the right entry
    // reaches a similarity of at least .62 with a lead of at least .2 over the runner-up.
    public Rumour? Match(string text) => Match(text, out _);

    // sure: the match is clear enough to remember the line's picture by it (RumourShapes).
    public Rumour? Match(string text, out bool sure)
    {
        sure = false;
        var key = Key(text);
        if (key.Length < Tune.MinimumKeyLength)
            return null;
        var ranked = Entries
            .Select(e => (Entry: e, Score: Similarity(key, Key(e.Name))))
            .OrderByDescending(r => r.Score)
            .Take(2)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Score < Tune.MinimumSimilarity)
            return null;
        double lead = ranked.Length > 1 ? ranked[0].Score - ranked[1].Score : 1;
        if (lead < Tune.MinimumLead)
            return null;
        sure = ranked[0].Score >= Tune.LearnSimilarity && lead >= Tune.LearnLead;
        return ranked[0].Entry;
    }

    private static double Similarity(string a, string b)
    {
        // Longest common subsequence ratio: tolerant to dropped and garbled letters.
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
            d[i, j] = a[i - 1] == b[j - 1] ? d[i - 1, j - 1] + 1 : Math.Max(d[i - 1, j], d[i, j - 1]);
        return 2.0 * d[a.Length, b.Length] / (a.Length + b.Length);
    }
}

public sealed record RumourPanel(Box Title, Box Rows, Box Panel);

// Language: the game's language when the line was read. Name, Island: rumour and island as the
// game names them in that language, for the overlay's table; null: as the rumour table has them.
public sealed record RumourLine(string Text, double CenterY, Rumour? Entry, string Language = GameLanguage.English, string? Name = null, string? Island = null);

// The "Uncharted Waters / Island Rumours" panel on the atlas. It opens above the chosen island
// and moves left or right near the screen edge, so it is searched over the whole screen by its
// title; same size in both UI modes.
public sealed class RumourReader : IDisposable
{
    private readonly Mat left;
    private readonly Mat right;
    private readonly TitleReader ocr;
    private readonly RumourShapes? shapes;

    public double Threshold { get; init; } = Tune.Threshold;

    // shapes: the pictures of lines named before; null: every line is read by OCR each time
    // (the golden-master check and the shared test world: one picture must not teach another).
    public RumourReader(string uiDirectory, TitleReader ocr, RumourShapes? shapes = null)
    {
        this.ocr = ocr;
        this.shapes = shapes;
        left = Piece(uiDirectory, "rumour-frame-left.png");
        right = Piece(uiDirectory, "rumour-frame-right.png");
    }

    private static Mat Piece(string uiDirectory, string file)
    {
        var piece = Cv2.ImRead(Path.Combine(uiDirectory, file), ImreadModes.Grayscale);
        return piece.Empty() ? throw Texts.Failure(m => new InvalidDataException(m), "MissingFile", file) : piece;
    }

    // The panel is found by the two ends of its title bar: artwork without lettering, the same
    // in every language of the game. The title itself ("Uncharted Waters") is no anchor: the
    // German client writes it differently ("Unerforschte Gewässer", at small sizes on two
    // lines). Measured, both pieces as a pair (the right one where it belongs to the left):
    // with the panel .96–1.00 at every window height, popup size and in both UI modes, without
    // it at most .49. One piece alone does not separate (the left one reaches .69 without a
    // panel). The pieces are artwork, not lettering: on a picture brought to reference size
    // they fit as they are, the size steps lettering would need (it is set anew at every size)
    // are gone. The panel's place is taken from the title's corner, at most 2 px off.
    //
    // within: only this part of the picture is searched (the glance: around the place where
    // the panel stood); null: the whole screen.
    // popup: the size the game draws its popups at (GameConfig.PopupSize). The panel is a
    // popup and grows with it; the pieces are looked for at that size, and the panel's
    // measures grow with the value.
    public RumourPanel? Locate(Mat bgr, Box? within = null, double popup = GameConfig.StandardPopup)
    {
        if (Math.Abs(bgr.Height - Tuning.Screen.ReferenceHeight) > Tuning.Screen.HeightTolerance)
            return null;
        var all = new Rect(0, 0, bgr.Width, bgr.Height);
        return Find(bgr, within is null ? all : within.Rect().Intersect(all), popup);
    }

    private RumourPanel? Find(Mat bgr, Rect area, double popup)
    {
        using var leftSized = Sized(left, popup);
        using var rightSized = Sized(right, popup);
        int between = (int)Math.Round(Tune.FrameBetween * popup);
        if (area.Width < between + rightSized.Width || area.Height < leftSized.Height)
            return null;
        using var part = new Mat(bgr, area);
        using var gray = new Mat();
        Cv2.CvtColor(part, gray, ColorConversionCodes.BGR2GRAY);
        // Half resolution for the search: the places where the left piece fits best. Then each
        // of them exactly, with the right piece where it belongs.
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(), Tune.CoarseScale, Tune.CoarseScale, InterpolationFlags.Area);
        using var leftSmall = Sized(left, popup * Tune.CoarseScale);
        if (leftSmall.Width > small.Width || leftSmall.Height > small.Height)
            return null;
        using var coarse = new Mat();
        Cv2.MatchTemplate(small, leftSmall, coarse, TemplateMatchModes.CCoeffNormed);
        (double Score, double X, double Y) best = default;
        for (int candidate = 0; candidate < Tune.FrameCandidates; candidate++)
        {
            Cv2.MinMaxLoc(coarse, out _, out double c, out _, out Point at);
            if (c < Threshold - Tune.CoarseSlack)
                break;
            // Not this place again.
            Cv2.Rectangle(coarse, new Rect(at.X - leftSmall.Width / 2, at.Y - leftSmall.Height / 2, leftSmall.Width, leftSmall.Height), Scalar.All(-1), -1);
            var near = new Rect((int)Math.Round(at.X / Tune.CoarseScale) - Tune.FineMargin, (int)Math.Round(at.Y / Tune.CoarseScale) - Tune.FineMargin, leftSized.Width + 2 * Tune.FineMargin, leftSized.Height + 2 * Tune.FineMargin)
                .Intersect(new Rect(0, 0, gray.Width, gray.Height));
            if (near.Width < leftSized.Width || near.Height < leftSized.Height)
                continue;
            using var roi = new Mat(gray, near);
            using var scores = new Mat();
            Cv2.MatchTemplate(roi, leftSized, scores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(scores, out _, out double leftScore, out _, out Point p);
            int x = near.X + p.X,
                y = near.Y + p.Y;
            var there = new Rect(x + between - Tune.FrameSlack, y - Tune.FrameSlack, rightSized.Width + 2 * Tune.FrameSlack, rightSized.Height + 2 * Tune.FrameSlack).Intersect(new Rect(0, 0, gray.Width, gray.Height));
            if (there.Width < rightSized.Width || there.Height < rightSized.Height)
                continue;
            using var rightRoi = new Mat(gray, there);
            using var rightScores = new Mat();
            Cv2.MatchTemplate(rightRoi, rightSized, rightScores, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(rightScores, out _, out double rightScore, out _, out _);
            double pair = Math.Min(leftScore, rightScore);
            if (pair > best.Score)
                best = (pair, x + Tune.FrameToTitleX * popup, y + Tune.FrameToTitleY * popup);
        }
        if (best.Score < Threshold)
            return null;
        return Panel(area.X + best.X, area.Y + best.Y, popup);
    }

    // The panel around its title's centre. From the title (360 x 39): rows between "Island
    // Rumours" and "Requires:", the frame about 70 px left and right of the title — every
    // measure at the popup size.
    private static RumourPanel Panel(double centreX, double centreY, double popup)
    {
        int Px(int standard) => GameConfig.Scaled(standard, popup);
        int width = Px(Tune.TitleWidth),
            height = Px(Tune.TitleHeight);
        int x = (int)Math.Round(centreX - width / 2.0),
            y = (int)Math.Round(centreY - height / 2.0);
        return new(
            new Box(x, y, width, height),
            new Box(x - Px(Tune.RowsLeft), y + Px(Tune.RowsDown), width + 2 * Px(Tune.RowsLeft), Px(Tune.RowsHeight)),
            new Box(x - Px(Tune.PanelLeft), y - Px(Tune.PanelUp), width + 2 * Px(Tune.PanelLeft), Px(Tune.PanelHeight))
        );
    }

    // A frame piece at this size.
    private static Mat Sized(Mat piece, double size)
    {
        var sized = new Mat();
        if (size == 1)
            piece.CopyTo(sized);
        else
            Cv2.Resize(piece, sized, new Size(Math.Round(piece.Width * size), Math.Round(piece.Height * size)), 0, 0, size < 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
        return sized;
    }

    // The panel's handwriting is beyond the OCR: one and the same line comes out differently at
    // every size ("Somedoiw' fishy..", "$owvetWiw' flhå...", "$nüiw'"), and no single way of
    // reading names every line. The matching never named a wrong rumour, it only
    // found none. So the panel is read in several ways (Tune.Readings), and each line takes the
    // first reading that names a rumour; as long as a line has no name, further ways follow
    // (Tune.MoreReadings).
    // Before any reading, the lines are looked at as pictures (RumourShapes): against every
    // rumour text drawn with the game's font, and against the lines the game showed before.
    // When all are named that way, no OCR runs at all; the OCR remains for a line no picture
    // names (a rumour the game added since the texts were built in).
    //
    // popup: at another popup size the panel is first brought to the game's standard size (the
    // picture of it resized by the reciprocal), and everything above reads that — by the value
    // from the game's settings, not by the size the title fitted best at (measured: resized by
    // the setting the lines overlap .70–.79, by the best title fit only .59–.75). The lines
    // come back at their place on the picture.
    // language: the language the client runs in — the lines are compared with that language's
    // texts in that language's hand (RumourShapes.SetDrawn).
    public async Task<RumourLine[]> ReadAsync(Mat bgr, RumourPanel panel, RumourBook book, double popup = GameConfig.StandardPopup, string language = GameLanguage.English)
    {
        if (popup == GameConfig.StandardPopup)
            return await ReadStandardAsync(bgr, panel, book, language);
        var seen = panel.Panel.Union(panel.Rows).Rect().Intersect(new Rect(0, 0, bgr.Width, bgr.Height));
        if (seen.Width <= 0 || seen.Height <= 0)
            return [];
        using var part = new Mat(bgr, seen);
        using var standard = new Mat();
        Cv2.Resize(part, standard, new Size(), 1 / popup, 1 / popup, popup > 1 ? InterpolationFlags.Area : InterpolationFlags.Cubic);
        // The frame again, at its standard size: its place there is not the scaled one to the
        // pixel. Not found (cut off at the picture's edge): where the scaled one says.
        var local =
            Find(standard, new Rect(0, 0, standard.Width, standard.Height), GameConfig.StandardPopup)
            ?? Panel((panel.Title.X + panel.Title.Width / 2.0 - seen.X) / popup, (panel.Title.Y + panel.Title.Height / 2.0 - seen.Y) / popup, GameConfig.StandardPopup);
        var lines = await ReadStandardAsync(standard, local, book, language);
        return lines.Select(l => l with { CenterY = seen.Y + l.CenterY * popup }).ToArray();
    }

    private async Task<RumourLine[]> ReadStandardAsync(Mat bgr, RumourPanel panel, RumourBook book, string language)
    {
        // A drawn line is stored under the game's text, which the table writes a little
        // differently ("Somethin' fishy..." / "Something Fishy").
        var names = new Dictionary<string, string?>();
        string? NameOf(string label) =>
            names.TryGetValue(label, out var name) ? name : names[label] = (book.Entries.FirstOrDefault(e => e.Name == label) ?? book.Match(label))?.Name;
        // Another language than English: pictures only. The OCR reads English, and what it
        // makes of a German line the matching takes for some English rumour („Alles, was
        // glänzt ...“ came out as "Fallen Stars", „Wenigstens ist es trocken ...“ as "End of
        // the Circle"). A line no picture names stays without a rumour ("?") rather than
        // getting a wrong rating.
        if (language != GameLanguage.English)
        {
            var named = new List<RumourLine>();
            foreach (var (centerY, shape) in RumourShapes.Cut(bgr, panel))
            {
                var entry = shapes?.Match(shape, NameOf, language) is { } name ? book.Entries.FirstOrDefault(e => e.Name == name) : null;
                if (entry is not null)
                    shapes!.Learn(entry.Name, shape);
                // What the game calls the rumour and its island in this language.
                var shown = entry is null ? null : RumourTexts.All.Where(t => NameOf(t.Text) == entry.Name).Select(t => RumourTexts.Shown(language, t.Text)).FirstOrDefault();
                named.Add(new(entry?.Name ?? "", centerY, entry, language, shown?.Rumour, shown?.Island));
            }
            return named.ToArray();
        }
        var seen = (shapes is null ? [] : RumourShapes.Cut(bgr, panel))
            .Select(c => (c.CenterY, c.Shape, Entry: shapes!.Match(c.Shape, NameOf, language) is { } name ? book.Entries.FirstOrDefault(e => e.Name == name) : null))
            .ToArray();
        // Named by a picture: the line as the game shows it is remembered, a closer fit than
        // the drawing.
        foreach (var (_, shape, entry) in seen)
            if (entry is not null)
                shapes!.Learn(entry.Name, shape);
        if (seen.Length > 0 && seen.All(s => s.Entry is not null))
            return seen.Select(s => new RumourLine(s.Entry!.Name, s.CenterY, s.Entry)).ToArray();

        var lines = new List<RumourLine>();
        var sure = new HashSet<double>();
        foreach (var (scale, binarize) in Tune.Readings)
            await ReadOnceAsync(bgr, panel, book, lines, sure, scale, binarize);
        foreach (var (scale, binarize) in Tune.MoreReadings)
        {
            if (lines.Count > 0 && lines.All(l => l.Entry is not null))
                break;
            await ReadOnceAsync(bgr, panel, book, lines, sure, scale, binarize);
        }
        foreach (var (centerY, shape, entry) in seen)
        {
            int at = lines.FindIndex(l => Math.Abs(l.CenterY - centerY) <= Tune.SameLine);
            if (entry is not null)
            {
                // Known by its picture: that stands, whatever the OCR made of the line this time.
                if (at < 0)
                    lines.Add(new(entry.Name, centerY, entry));
                else
                    lines[at] = lines[at] with { Entry = entry };
            }
            else if (at >= 0 && lines[at].Entry is { } read && sure.Contains(lines[at].CenterY))
                shapes!.Learn(read.Name, shape);
        }
        return lines.OrderBy(l => l.CenterY).ToArray();
    }

    // sure: the lines (by their centre) that a reading named clearly.
    private async Task ReadOnceAsync(Mat bgr, RumourPanel panel, RumourBook book, List<RumourLine> lines, HashSet<double> sure, double scale, bool binarize)
    {
        foreach (var (text, centerY, _, _) in await ocr.ReadRegionAsync(bgr, panel.Rows.Rect(), scale, binarize ? TextPrep.Binarised : TextPrep.Plain))
        {
            var key = RumourBook.Key(text);
            if (key.Length < Tune.MinimumKeyLength || key.Contains("rumour"))
                continue;
            var entry = book.Match(text, out bool clear);
            int at = lines.FindIndex(l => Math.Abs(l.CenterY - centerY) <= Tune.SameLine);
            if (at < 0)
            {
                lines.Add(new(text, centerY, entry));
                at = lines.Count - 1;
            }
            else if (lines[at].Entry is null && entry is not null)
                lines[at] = lines[at] with { Text = text, Entry = entry };
            if (clear && entry is not null && lines[at].Entry == entry)
                sure.Add(lines[at].CenterY);
        }
    }

    // Coarse picture of the rumour lines: in the controller UI the panel stays in the middle
    // for every island, so only the lines tell that another island was chosen.
    public static double[] Signature(Mat bgr, RumourPanel panel)
    {
        using var roi = new Mat(bgr, panel.Rows.Rect().Intersect(new Rect(0, 0, bgr.Width, bgr.Height)));
        using var gray = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(Tune.SignatureWidth, Tune.SignatureHeight), 0, 0, InterpolationFlags.Area);
        var s = new double[Tune.SignatureWidth * Tune.SignatureHeight];
        for (int y = 0; y < Tune.SignatureHeight; y++)
        for (int x = 0; x < Tune.SignatureWidth; x++)
            s[y * Tune.SignatureWidth + x] = small.At<byte>(y, x);
        return s;
    }

    public static bool Changed(double[] a, double[] b) => a.Zip(b).Average(p => Math.Abs(p.First - p.Second)) > Tune.ChangedAbove;

    public void Dispose()
    {
        left.Dispose();
        right.Dispose();
    }
}
