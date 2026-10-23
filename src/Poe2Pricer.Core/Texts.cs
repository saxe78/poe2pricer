using System.Globalization;
using System.Resources;

namespace Poe2Pricer.Core;

// The texts of the program window, the status line and the tray in the chosen language:
// one file per language (Languages/Strings.resx is
// English, the main language; what another language lacks is taken from there). Log and
// diagnostics do not come from here — they stay English whatever the language.
// Numbers put into a text follow the language: English has the decimal point and the comma
// between thousands ("{0:N0}").
public static class Texts
{
    public const string Default = "en";

    private static readonly ResourceManager Resources = new("Poe2Pricer.Core.Languages.Strings", typeof(Texts).Assembly);
    private static CultureInfo culture = CultureInfo.GetCultureInfo(Default);

    // The decimal sign of the chosen language: number fields show their values with it.
    public static string DecimalSeparator => culture.NumberFormat.NumberDecimalSeparator;

    // The languages there is a file for; the first is the main language.
    public static IReadOnlyList<string> Languages { get; } = [Default, "de", "ru"];

    // The window shows its texts anew; a status line already written stays as it is.
    public static event Action? Changed;

    // A language there is no file for counts as the main language.
    public static string Language
    {
        get => culture.Name;
        set
        {
            var next = CultureInfo.GetCultureInfo(Languages.Contains(value) ? value : Default);
            if (next.Equals(culture))
                return;
            culture = next;
            Changed?.Invoke();
        }
    }

    public static bool Has(string key) => Resources.GetString(key, culture) is not null;

    // A name without a text is shown as it is, in brackets: seen at once, nothing breaks.
    public static string Get(string key, params object?[] args) =>
        Resources.GetString(key, culture) is { } text ? (args.Length == 0 ? text : string.Format(culture, text, args)) : $"[{key}]";

    // A counted text: "1 price", "2 prices". The count is the first value put in, more follow.
    public static string Count(string key, int count, params object?[] args) =>
        Get(key + CountForm(culture, count), [count, .. args]);

    // A text in a given language, whatever language is chosen for the window: the overlay
    // follows the language of the game (GameLanguage). A language without the text: English.
    public static string In(string language, string key, params object?[] args)
    {
        var of = Culture(language);
        return Resources.GetString(key, of) is { } text ? (args.Length == 0 ? text : string.Format(of, text, args)) : $"[{key}]";
    }

    // A counted text in a given language (Count).
    public static string CountIn(string language, string key, int count, params object?[] args) =>
        In(language, key + CountForm(Culture(language), count), [count, .. args]);

    // Russian: 1/21 предмет, 2–4/22–24 предмета, 0/5–20 предметов.
    // Keep _other for the many form so the existing English/German keys stay unchanged.
    private static string CountForm(CultureInfo language, int count)
    {
        if (language.TwoLetterISOLanguageName != "ru")
            return count == 1 ? "_one" : "_other";
        long n = Math.Abs((long)count);
        if (n % 10 == 1 && n % 100 != 11)
            return "_one";
        return n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14) ? "_few" : "_other";
    }

    private static CultureInfo Culture(string language)
    {
        try
        {
            return CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.GetCultureInfo(Default);
        }
    }

    // The same text in the main language, whatever language is chosen: for the log.
    public static string English(string key, params object?[] args)
    {
        var english = CultureInfo.GetCultureInfo(Default);
        return Resources.GetString(key, english) is { } text ? (args.Length == 0 ? text : string.Format(english, text, args)) : $"[{key}]";
    }

    private const string KeyEntry = "poe2pricer.text";
    private const string ArgsEntry = "poe2pricer.values";

    // A failure the user is told about in his language: the exception carries the name of its
    // text, its message is the text in English (that is what the log shows). make keeps the
    // kind of exception the callers catch. Reasons why poe.ninja failed (names beginning with
    // "Ninja") are half sentences; the message and Shown put "poe.ninja: " before them.
    public static T Failure<T>(Func<string, T> make, string key, params object?[] args)
        where T : Exception
    {
        string reason = English(key, args);
        var failure = make(FromNinja(key) ? English("NinjaFailed", reason) : reason);
        failure.Data[KeyEntry] = key;
        failure.Data[ArgsEntry] = args;
        return failure;
    }

    // The text of a failure made by Failure, in the chosen language; null for any other.
    public static string? Reason(Exception e) => e.Data[KeyEntry] is string key ? Get(key, e.Data[ArgsEntry] as object?[] ?? []) : null;

    // What the status line says about a failure: its own text, or for one nobody foresaw the
    // message as it came (from .NET or Windows, in their language).
    public static string Shown(Exception e) =>
        Reason(e) is { } reason ? (FromNinja((string)e.Data[KeyEntry]!) ? Get("NinjaFailed", reason) : reason) : Get("UnexpectedError", e.Message);

    private static bool FromNinja(string key) => key.StartsWith("Ninja", StringComparison.Ordinal) && key != "NinjaFailed";
}

// Something the user is told and the log notes: shown in the chosen language, logged in English.
// An argument that is itself a Told (a word of the program, such as the name of a text size)
// follows the language too.
public sealed record Told(string Key, params object?[] Args)
{
    public string Shown => Texts.Get(Key, Args.Select(a => a is Told t ? t.Shown : a).ToArray());
    public string English => Texts.English(Key, Args.Select(a => a is Told t ? t.English : a).ToArray());
}
