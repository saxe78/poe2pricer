using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Poe2Pricer.Core;

namespace Poe2Pricer.Ui.ViewModels;

// The program window: both tabs, the status line and "Exit". "Save" takes settings and rumour
// ratings together: the settings are written, then the ratings, then the program is told.
// A language of the window in the list at the right of the tabs: its code as the settings
// hold it ("en", "de") and what stands beside the flag ("EN", "DE").
public sealed record LanguageChoice(string Code)
{
    public string Label => Code.ToUpperInvariant();
}

public sealed class MainViewModel : ObservableObject
{
    private StatusMessage status = new("");
    private readonly List<(string Cause, Told Told, string? Hint)> warnings = [];
    private readonly Action<string> storeHiddenHint;
    private string hiddenHint;
    private readonly Action<string> storeLanguage;
    private LanguageChoice language;

    // store: writes the settings; the tests pass their own, the program writes settings.json.
    // storeLanguage: writes the chosen language at once; the program writes it into
    // settings.json without touching what else stands there.
    // storeHiddenHint: writes a hint closed with its "✕" at once (Settings.HiddenHint), the
    // same way.
    // storeWaystones: writes the Waystones tab's own save; the program writes it into
    // settings.json without touching what else stands there.
    // runes: the runes of the remnant list for the Runes tab; null: the tab stays empty.
    public MainViewModel(Settings settings, IEnumerable<string> leagues, RumourBook rumours, Action<Settings>? store = null, Action<string>? storeLanguage = null, TimeProvider? clock = null, Action<Core.Waystones.WaystoneSettings>? storeWaystones = null, Action<Core.Tablets.TabletSettings>? storeTablets = null, Action<string>? storeHiddenHint = null, Core.Runes.RuneBook? runes = null)
    {
        this.storeLanguage = storeLanguage ?? (code => (Core.Settings.Load() with { Language = code }).Save());
        this.storeHiddenHint = storeHiddenHint ?? (hint => (Core.Settings.LoadForSave() with { HiddenHint = hint }).Save());
        hiddenHint = settings.HiddenHint;
        CloseHintCommand = new RelayCommand<string>(CloseHint);
        language = Languages.FirstOrDefault(l => l.Code == Texts.Language) ?? Languages[0];
        Rumours = new RumoursViewModel(rumours);
        Runes = new RunesViewModel(runes);
        Settings = new SettingsViewModel(
            settings,
            leagues,
            next =>
            {
                (store ?? (s => s.Save()))(next);
                // The tabs' profiles are saved with it: changes are measured from here. (Called
                // on a save, long after the constructor has set both tabs.)
                Waystones?.Accept(next.Waystones);
                Tablets?.Accept(next.Tablets);
                var notSaved = Rumours.Save();
                var runesNotSaved = Runes.Save();
                // Written anew: what could not be read at the start is no longer the matter.
                Warn(SettingsCause, null);
                if (notSaved is null)
                    Warn(RatingsCause, null);
                return notSaved ?? runesNotSaved;
            },
            Report
        );
        Waystones = new WaystonesViewModel(settings.Waystones, Report, clock, storeWaystones ?? (w => (Core.Settings.LoadForSave() with { Waystones = w }).Save()));
        Settings.WaystonesSource = Waystones.Current;
        Tablets = new TabletsViewModel(settings.Tablets, Report, clock, storeTablets ?? (t => (Core.Settings.LoadForSave() with { Tablets = t }).Save()));
        Settings.TabletsSource = Tablets.Current;
        // "Exit" with a profile not saved in the Waystones or Tablets tab asks first; "Cancel"
        // keeps the program running.
        QuitCommand = new RelayCommand(() =>
        {
            if (Waystones.Quitting() && Tablets.Quitting())
                Quit?.Invoke();
        });
        Texts.Changed += () => OnPropertyChanged(nameof(Warnings));
    }

    // What stays to be said as long as its cause lasts, each on a line of its own above the
    // status line: the settings or the own ratings could not be read, the game's settings file
    // is not found, the keys are not available. A line of its own for each, not a passing
    // notice in the tray. Window thread.
    // cause: one line per cause, a new text replaces the old one; told null: the cause is gone.
    // hint: no fault, only something to know (the game's text size is not supported), named
    // ("textsize extra_large"): drawn in brass with an "i" and a "✕", not red with a warning
    // sign (issue #1). Closed with the "✕", it stays away until the next version.
    // Warnings that go with the next save carry SettingsCause or RatingsCause.
    public const string SettingsCause = "settings", RatingsCause = "ratings";

    public void Warn(string cause, Told? told, string? hint = null)
    {
        if (hint is not null && Hidden(hint))
            told = null;
        int at = warnings.FindIndex(w => w.Cause == cause);
        if (told is null && at < 0)
            return;
        if (told is null)
            warnings.RemoveAt(at);
        else if (at < 0)
            warnings.Add((cause, told, hint));
        else
            warnings[at] = (cause, told, hint);
        OnPropertyChanged(nameof(Warnings));
    }

    public IReadOnlyList<Lasting> Warnings => warnings.Select(w => new Lasting(w.Cause, w.Told.Shown, w.Hint is not null)).ToArray();

    // The "✕" of a hint (parameter: its cause): the line goes, and it is saved at once with the
    // program's version, so it comes back with the next one. Warnings have no "✕".
    public IRelayCommand<string> CloseHintCommand { get; }

    // Settings.HiddenHint holds every hint closed in this version, ";" between them (R4: two
    // closable lines, text size and capture, one must not bring the other back).
    private bool Hidden(string hint) => HiddenHints().Contains(AppInfo.Version + " " + hint);

    private IEnumerable<string> HiddenHints() => hiddenHint.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void CloseHint(string? cause)
    {
        int at = warnings.FindIndex(w => w.Cause == cause);
        if (at < 0 || warnings[at].Hint is not { } hint)
            return;
        // This version's entries plus the new one; entries of other versions are spent.
        hiddenHint = string.Join(';', HiddenHints().Where(h => h.StartsWith(AppInfo.Version + " ", StringComparison.Ordinal)).Append(AppInfo.Version + " " + hint).Distinct());
        warnings.RemoveAt(at);
        OnPropertyChanged(nameof(Warnings));
        Settings.SetHiddenHint(hiddenHint);
        try
        {
            storeHiddenHint(hiddenHint);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            // Away for this run all the same; the status line says why it comes back.
            Report(StatusMessage.Error(Texts.Shown(e)));
        }
    }

    // The languages of the window, each with its flag and country code (the list at the right
    // of the tabs). Choosing one counts at once and is saved at once, without "Save": every
    // text of the window follows, the status line with its next message.
    public IReadOnlyList<LanguageChoice> Languages { get; } = Texts.Languages.Select(code => new LanguageChoice(code)).ToArray();

    public LanguageChoice Language
    {
        get => language;
        set
        {
            if (value is null || !SetProperty(ref language, value))
                return;
            Texts.Language = value.Code;
            Settings.SetLanguage(value.Code);
            storeLanguage(value.Code);
        }
    }

    public SettingsViewModel Settings { get; }
    public RumoursViewModel Rumours { get; }
    public RunesViewModel Runes { get; }
    public WaystonesViewModel Waystones { get; }
    public TabletsViewModel Tablets { get; }
    public IRelayCommand QuitCommand { get; }

    // Behind the name in the title bar.
    public string Version { get; } = "v" + AppInfo.Version;

    public event Action? Quit;

    // The status line: its text and what it says about the program (the dot before it, red
    // text for an error). May be set from any thread: the overlay control reports from its own.
    public void Report(StatusMessage message)
    {
        status = message;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusKind));
        OnPropertyChanged(nameof(StatusLink));
        OnPropertyChanged(nameof(StatusClickable));
    }

    // Whether the line is a link: it leads to a page or does something.
    public bool StatusClickable => status.Link is not null || status.Act is not null;

    // A click on the line: does what it does, else opens its page (open: how).
    public void StatusClicked(Action<Uri> open)
    {
        if (status.Act is { } act)
            act();
        else if (StatusLink is { } link)
            open(link);
    }

    public string Status => status.Text;
    public StatusKind StatusKind => status.Kind;

    // The page the line leads to; null: plain text. A Uri, as the link in the window wants
    // it: a null string could not be converted to one.
    public Uri? StatusLink => status.Link is { } link ? new Uri(link) : null;
}

// A line of the window that stays while its cause lasts (MainViewModel.Warn).
public sealed record Lasting(string Cause, string Text, bool Hint);
