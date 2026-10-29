using System.Globalization;

namespace Poe2Pricer.Core;

// What the tool has to know of the game's own settings: the file the game writes when its
// options are saved (poe2_production_Config.ini). Only read, never written. Exiled Exchange 2
// reads the same file from the same place (main/src/host-files/GameConfig.ts).
//
// Popup Size (Options → UI): the game draws every popup at this size — item tooltips and the
// rumour panel. The slider runs from 0.8 to 1.29, the game writes the value as a number, and
// tooltips and panel grow exactly with it (measured). A value outside the range (entered by
// hand) is drawn by the game at 0.80.
public sealed class GameConfig
{
    public const double StandardPopup = 1;
    public const double SmallestPopup = .8;
    public const double LargestPopup = 1.29;

    private const string PopupEntry = "popup_size_scale";
    private const string LanguageSection = "[LANGUAGE]";
    private const string LanguageEntry = "language";
    private const string TextSizeEntry = "descriptive_text_scale";

    // UI Text Size as the game writes it when nothing was changed.
    public const string StandardTextSize = "medium";

    private readonly Lock gate = new();
    private string file;
    private DateTime? stamp;
    private double popup = StandardPopup;
    private string language = GameLanguage.English;
    private string clientLanguage = GameLanguage.English;
    private string textSize = StandardTextSize;
    private bool looked;

    // Where the game keeps the file: the user's documents folder, wherever Windows has it (it
    // may be moved to OneDrive or another drive).
    public static string StandardFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Path of Exile 2", "poe2_production_Config.ini");

    // file: where the user says the file lies (Settings.GameConfigFile); empty: the standard place.
    public GameConfig(string? file = null) => this.file = Resolve(file);

    private static string Resolve(string? file) => string.IsNullOrWhiteSpace(file) ? StandardFile : file;

    public string File
    {
        get
        {
            lock (gate)
                return file;
        }
    }

    // A measure taken at the game's standard popup size, at this popup size: tooltips, the
    // rumour panel and the remnant list grow and shrink with it. The one place for it; the
    // tribute dialog uses it with the text scale (TextScale) the same way.
    public static int Scaled(int standard, double popup) => popup == StandardPopup ? standard : (int)Math.Round(standard * popup);

    public void Use(string? other)
    {
        lock (gate)
        {
            var next = Resolve(other);
            if (next == file)
                return;
            file = next;
            looked = false;
        }
    }

    // Found at the last look. Without the file every value is the game's standard.
    public bool Found
    {
        get
        {
            lock (gate)
            {
                Look();
                return stamp is not null;
            }
        }
    }

    // Cheap enough for every capture: the file is read again only when its time has changed.
    public double PopupSize
    {
        get
        {
            lock (gate)
            {
                Look();
                return popup;
            }
        }
    }

    // The language the client runs in ("language" below [LANGUAGE]); what the program cannot
    // read the game in, and a missing file or entry, counts as English.
    public string Language
    {
        get
        {
            lock (gate)
            {
                Look();
                return language;
            }
        }
    }

    // The language as the file names it ("ru"), whether the program reads it or not; a missing
    // file or entry: English. For telling the user (Program.CheckGameLanguage); the recognition
    // goes by Language.
    public string ClientLanguage
    {
        get
        {
            lock (gate)
            {
                Look();
                return clientLanguage;
            }
        }
    }

    // Found and the client's language from one look (Program.CheckGameConfig: the game may
    // rewrite the file between two).
    public (bool Found, string ClientLanguage) FoundAndClientLanguage
    {
        get
        {
            lock (gate)
            {
                Look();
                return (stamp is not null, clientLanguage);
            }
        }
    }

    // The program reads the game in this language (GameLanguage: English and German). In any
    // other, nothing is recognised: the tooltips are not read and the stash search text is in
    // the wrong words — said in the window, not swallowed (23.10.2026, a Russian client).
    public bool LanguageSupported => GameLanguage.Known(ClientLanguage) == ClientLanguage;

    // UI Text Size (Options → UI; "medium", "large", "extra_large"): the size of the stack
    // numbers and tier marks on items and of the lettering in dialogs. The program is measured
    // at "medium"; the value is read to be written into every diagnosis, where pictures of
    // another size would otherwise not be told apart afterwards.
    public string TextSize
    {
        get
        {
            lock (gate)
            {
                Look();
                return textSize;
            }
        }
    }

    // The three sizes the game offers are measured (testdata/textsize: Large and Extra Large
    // on two players' pictures at 1080, 1440, 2160 and 3440, every window; 23.10.2026). A value
    // the program does not know (a game update) is told at the start (Program.CheckTextSize)
    // and read as the standard.
    public bool TextSizeSupported => TextSize is StandardTextSize or "large" or "extra_large";

    // How much larger the game draws its lettering at this UI Text Size (Tuning.TextSizes); an
    // unknown value counts as the standard.
    public static double TextScale(string textSize) => textSize switch
    {
        "large" => Tuning.TextSizes.Large,
        "extra_large" => Tuning.TextSizes.ExtraLarge,
        _ => 1,
    };

    private void Look()
    {
        DateTime? now;
        try
        {
            var info = new FileInfo(file);
            now = info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            now = null;
        }
        if (looked && now == stamp)
            return;
        looked = true;
        if (now is null)
        {
            (stamp, popup, language, clientLanguage, textSize) = (null, StandardPopup, GameLanguage.English, GameLanguage.English, StandardTextSize);
            return;
        }
        try
        {
            var lines = System.IO.File.ReadAllLines(file);
            popup = PopupOf(lines);
            clientLanguage = ClientLanguageOf(lines);
            language = GameLanguage.Known(clientLanguage);
            textSize = lines.Select(l => l.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim() == TextSizeEntry)?[1].Trim() ?? StandardTextSize;
            stamp = now;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The game is writing it right now: the old value stands, the next look reads again.
            looked = false;
        }
    }

    // The entry's value within what the slider allows; missing or unreadable: the standard.
    public static double PopupOf(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var parts = line.Split('=', 2);
            if (parts.Length == 2 && parts[0].Trim() == PopupEntry)
                return double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
                    ? Math.Clamp(value, SmallestPopup, LargestPopup)
                    : StandardPopup;
        }
        return StandardPopup;
    }

    // The client's language as the program reads it: the entry below its section (the file
    // also holds "chat_language"), any language the program does not read counts as English.
    public static string LanguageOf(IEnumerable<string> lines) => GameLanguage.Known(ClientLanguageOf(lines));

    // The client's language as the file names it; no entry: English.
    public static string ClientLanguageOf(IEnumerable<string> lines)
    {
        bool inSection = false;
        foreach (var line in lines)
        {
            string text = line.Trim();
            if (text.StartsWith('['))
                inSection = text.Equals(LanguageSection, StringComparison.OrdinalIgnoreCase);
            else if (inSection && text.Split('=', 2) is [var name, var value] && name.Trim() == LanguageEntry)
                return value.Trim() is { Length: > 0 } named ? named : GameLanguage.English;
        }
        return GameLanguage.English;
    }
}
