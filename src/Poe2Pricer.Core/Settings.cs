using System.Text.Json;

namespace Poe2Pricer.Core;

// User settings, stored in %LOCALAPPDATA%\poe2pricer\settings.json.
public sealed record Settings
{
    // null: the current softcore league (first non-HC, non-permanent league on poe.ninja).
    public string? League { get; init; }
    public string ToggleKey { get; init; } = "Alt+F";
    public string DiagnoseKey { get; init; } = "Alt+Shift+F";

    // At a waystone's or tablet's tooltip: its price asked exactly as it is, past the profiles
    // (TooltipGameWindow.Exact).
    public string ExactKey { get; init; } = "Alt+D";

    // The live price at a waystone's or tablet's tooltip, asked on the trade site by itself;
    // off, nothing is asked or drawn at the tooltip. One switch for both kinds (21.10.2026);
    // the exact-price key (ExactKey) asks even while it is off.
    public bool LiveTradePrice { get; init; }

    // Trade prices (live and profile): Instant Buyout only (false), or also sellers online in
    // person (true) — TradeClient.InPerson.
    public bool TradeInPerson { get; init; }

    // Look every AutoIntervalSeconds for the windows ticked below while none is shown
    // (deliberate load; the key always looks for all). As fast as the control allows by
    // default (Tuning.Control.AutoMinimumSeconds): prices as soon as a window opens. The
    // automatic scan runs as soon as one of the three windows below is ticked; three
    // switches side by side need no main switch.
    public double AutoIntervalSeconds { get; init; } = .2;

    // While a window is shown it is glanced at this often (still there? changed?); as fast as
    // the control allows by default (Tuning.Control.GlanceEvery): prices go with their window.
    // With AutoIntervalSeconds the two things to turn up when the game stutters: that one
    // counts while no window is shown, this one while one is.
    public double GlanceIntervalSeconds { get; init; } = .05;

    // Which windows the automatic scan shows by itself; the key always shows all of them.
    // All on by default, the user switches off.
    public bool AutoRitual { get; init; } = true;
    public bool AutoExpedition { get; init; } = true;
    public bool AutoStash { get; init; } = true;

    // The automatic scan runs: at least one window is ticked.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool AutoAny => AutoRitual || AutoExpedition || AutoStash;

    // Items worth less get no label and do not compete for "most valuable" (default: show all).
    public decimal FloorValue { get; init; }
    public string FloorUnit { get; init; } = "ex";

    // The unit prices are written in below one divine: "ex" or "chaos" (from one divine on it
    // is always div). Late in a league exalts are cheap and the numbers grow long; chaos is
    // then the unit traders use (asked for on reddit, 19.10.2026).
    public string PriceUnit { get; init; } = "ex";

    // Start with Windows (Autostart); off by default.
    public bool StartWithWindows { get; init; }

    // Extended diagnostics: the program keeps pictures of what it detected on its own (a
    // reroll in the Ritual window) in the diagnostics folder, at most a few of each kind — for
    // reporting a wrong price. Off by default: a user's program writes nothing by itself, the
    // diagnostics key is the one active step (19.10.2026).
    public bool ExtendedDiagnostics { get; init; }

    // Ask github.com once per start whether a newer release is out (UpdateCheck); on by default,
    // the one request the program makes that is not for prices or rumour ratings.
    public bool CheckForUpdates { get; init; } = true;

    // The game is read from its own window (WindowCapture: Windows.Graphics.Capture, which
    // sends the game nothing), not from the screen: the overlay and every other window over the
    // game are never in the picture, so the overlay need not be kept out of screen captures,
    // and recordings (NVIDIA, OBS, Game Bar) show the prices. Needs Windows 11 (the capture
    // border must come off); elsewhere, and after a missing or black picture while the screen
    // shows the game, the screen is read for the run (WindowsGameScreen). Off: the screen is
    // read and the overlay is hidden from every capture, as up to 1.2.0. Measured 23.10.2026 on
    // an RTX 4070 Ti with HDR: the same colours as the screen, the stash read alike from both
    // pictures, 2 ms for a part of the window where the screen takes 8.
    public bool CaptureGameWindow { get; init; } = true;

    // The notice before the first start (what the program does and does not do, that no tool
    // is approved by GGG) was shown and answered with "Run": never again, not after an update
    // either. "Quit" leaves no mark, so the next start asks again. A file from before the
    // notice existed shows it once at the first start after the update.
    public bool NoticeShown { get; init; }

    // The hints the user closed with their "✕" (MainViewModel.CloseHint), each as
    // "<version> <hint>", e.g. "1.2.0 textsize extra_large", ";" between several: a closed
    // hint stays away until the next version of the program, and another hint (another text
    // size, the capture line) comes all the same. Empty: none closed.
    public string HiddenHint { get; init; } = "";

    // The language of the program window, the status line and the tray (Texts.Languages);
    // log and diagnostics are English whatever stands here.
    public string Language { get; init; } = Texts.Default;

    // The game's own settings file (GameConfig), read for the popup size; empty: where the
    // game keeps it, in the documents folder. Not everyone has it there.
    public string GameConfigFile { get; init; } = "";

    // Recognition data (catalog, images, recognition model): empty means the copy shipped with
    // the program (data/catalog beside it, DataPath). Set to read another folder in Dev-Jahn's
    // layout.
    public string DataDirectory { get; init; } = "";

    // The Waystones tab: profiles for the stash search and the trade query, the live price at
    // the tooltip.
    public Waystones.WaystoneSettings Waystones { get; init; } = new();

    // The Tablets tab: one profile per kind of tablet, the live price at the tooltip.
    public Tablets.TabletSettings Tablets { get; init; } = new();

    public static string ShippedDataPath => Path.Combine(AppContext.BaseDirectory, "data", "catalog");

    // Not written: derived, and the shipped path names the install folder, with the user's name
    // in it for an installed copy (a diagnosis is made to be attached to a public issue).
    [System.Text.Json.Serialization.JsonIgnore]
    public string DataPath => DataDirectory is { Length: > 0 } ? DataDirectory : ShippedDataPath;

    // POE2PRICER_FOLDER replaces it for the golden-master check (frozen icons and rumour table).
    public static string Folder =>
        Environment.GetEnvironmentVariable("POE2PRICER_FOLDER") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "poe2pricer");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    // False until the first save: the first start is the one that shows the window by itself.
    public static bool Stored => File.Exists(FilePath);

    public static Settings Load() => Load(FilePath, out _);

    // problem: something the user should know (the file could not be read), else null.
    public static Settings Load(out Told? problem) => Load(FilePath, out problem);

    // The settings as saved, for a save that writes back one part of them (a tab's ✓). A file
    // that cannot be read right now (locked by a virus scanner, cut off) must not be written
    // over with the standard settings and that part: thrown, and the save fails and says so
    // (review before 1.2.0). No file yet: the standard settings.
    public static Settings LoadForSave() => LoadForSave(FilePath);

    public static Settings LoadForSave(string file)
    {
        var settings = Load(file, out var problem);
        return problem is null ? settings : throw new IOException(problem.Shown);
    }

    // An unreadable file must not keep the program from starting: a file cut off while it was
    // written (crash, power loss) would otherwise throw at every start until it is deleted by
    // hand. The standard settings count; the file is kept as a copy beside it before the next
    // save writes over it.
    public static Settings Load(string file, out Told? problem)
    {
        problem = null;
        if (!File.Exists(file))
            return new Settings();
        try
        {
            var text = File.ReadAllText(file);
            return JsonSerializer.Deserialize<Settings>(text, JsonFiles.Options) ?? new Settings();
        }
        catch (JsonException)
        {
            var copy = Path.Combine(Path.GetDirectoryName(file) ?? "", Path.GetFileNameWithoutExtension(file) + ".broken.json");
            try
            {
                File.Copy(file, copy, overwrite: true);
                problem = new("SettingsUnreadableBackup", copy);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problem = new("SettingsUnreadableNoBackup", file, e.Message);
            }
            return new Settings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = new("SettingsUnreadable", e.Message);
            return new Settings();
        }
    }

    public void Save() => Save(FilePath);

    // Written next to the file and moved over it: a crash while writing leaves the old file.
    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var part = file + ".part";
        File.WriteAllText(part, JsonSerializer.Serialize(this, JsonFiles.Options));
        File.Move(part, file, overwrite: true);
    }
}
