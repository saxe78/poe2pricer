using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using Rect = System.Windows.Rect;
using Poe2Pricer.Core;
using Poe2Pricer.Core.Market;
using Poe2Pricer.Ui.ViewModels;
using Poe2Pricer.Ui.Views;

namespace Poe2Pricer.App;

// poe2pricer                       Overlay mit Tastenauslöser, Programmfenster und Infobereich.
// poe2pricer --render-settings <out.png>   Einstellungsfenster als Bild (Prüfung ohne Bildschirm;
//   POE2PRICER_TAB=1: Tab Rumours). Vergleich mit dem Sollbild: tools/fenster_vergleich.py.
// poe2pricer --render <in.png> <out.png> [<vorher.png> ...]
//                                  (POE2PRICER_GAME_LANGUAGE=de, POE2PRICER_GAME_TEXTSIZE=large /
//                                  extra_large, POE2PRICER_GAME_POPUP=1.25: wie der Client stand,
//                                  als das Bild entstand)
//                                  Vorschau ohne Spiel: erkennt ein Testbild, holt Preise und
//                                  zeichnet die Beschriftungen wie das Overlay in eine Datei.
//                                  vorher: Bilder, die das Fenster davor gezeigt hat (in dieser
//                                  Reihenfolge), etwa die Remnant-Liste vor dem Scrollen.
public static class Program
{
    // Only one instance: a second start brings the running one's window to the front and
    // quits; two instances would both take the key and draw two overlays.
    private const string InstanceName = "poe2pricer.single-instance";

    [STAThread]
    public static int Main(string[] args)
    {
        // First of all: on install, update and uninstall the setup starts the program with
        // arguments of its own, which this answers and ends the process. On uninstall the entry
        // that starts the program with Windows goes too. A version downloaded but not installed
        // (the click's restart failed, the process was ended) is not installed at the next start
        // by itself: nothing is installed without a click (review before 1.2.0).
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).OnBeforeUninstallFastCallback(_ => Autostart.Windows().Apply(false, "")).Run();
        Mutex? single = null;
        EventWaitHandle? showRequest = null;
        if (args.Length == 0)
        {
            single = new Mutex(true, InstanceName, out bool first);
            if (!first)
            {
                FileLog.Write("Started again: the running one is asked to show its window");
                if (EventWaitHandle.TryOpenExisting(InstanceName + ".show", out var other))
                    other.Set();
                return 0;
            }
            showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".show");
            // Which copy runs (several may lie around: the unpacked one, a built one), to
            // read the run from the log, and when it ended.
            // The log's lines carry the time only; the date here tells which day they belong to.
            // The Windows build in the first line: what a capture or a key can do depends on it
            // (WDA_EXCLUDEFROMCAPTURE from 10.0.19041, the borderless window capture from 22621),
            // and an issue rarely says it.
            FileLog.Write($"Started: {AppInfo.NameAndVersion}, {DateTime.Now:yyyy-MM-dd}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.ProcessPath}, PID {Environment.ProcessId}");
        }
        try
        {
            int code = Run(args, showRequest);
            if (args.Length == 0)
                FileLog.Write($"Ended with {code}");
            return code;
        }
        // Whatever stops the start before the window and its handlers are there (no English
        // text recognition in Windows, a missing template or font) is said and logged instead
        // of the program just not being there. Not for the calls with arguments: tools run
        // those and must see the failure themselves.
        catch (Exception e) when (args.Length == 0)
        {
            FileLog.Write("Error (start): " + e);
            MessageBox.Show(Texts.Get("StartFailed", Texts.Shown(e), FileLog.Place), "poe2pricer", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            single?.ReleaseMutex();
            single?.Dispose();
            showRequest?.Dispose();
        }
    }

    // The notice's text is long; this wide it reads as a page, not a column.
    private const double NoticeWidth = 500;

    private static int Run(string[] args, EventWaitHandle? showRequest)
    {
        // Before anything saves: no settings file yet means the first start.
        bool firstStart = !Settings.Stored;
        var settings = Poe2Pricer.Ui.HotKey.Usable(Settings.Load(out var settingsProblem), out var replacedKeys);
        Texts.Language = settings.Language;
        // The notice before the first start, before anything else runs or installs: what the
        // program does and does not do, and that GGG approves no tool. "Run" is remembered in
        // the file as it stands (Settings.NoticeShown); "Quit" is not, and the next start asks
        // again.
        // (A file that could not be read gives the standard settings: the notice was shown for
        // that file once, and "Run" could not be stored either way — not asked again, R13.)
        if (args.Length == 0 && !settings.NoticeShown && settingsProblem is null)
        {
            if (Poe2Pricer.Ui.Views.AskWindow.Ask(null, Texts.Get("NoticeTitle"), Texts.Get("NoticeText"), NoticeWidth, Texts.Get("NoticeRun"), Texts.Get("NoticeQuit")) != 0)
                return 0;
            try
            {
                (Settings.LoadForSave() with { NoticeShown = true }).Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                FileLog.Write($"Notice: not remembered ({e.Message})");
            }
        }
        if (args.Length == 0 && !TextRecognition.Ensure())
            return 1;
        var ui =Path.Combine(AppContext.BaseDirectory, "data", "ui");
        var rumourShapes = RumourShapes.Load(Path.Combine(Settings.Folder, "rumour-lines.json"));
        // Every rumour text of the game (built in, RumourTexts) in the game's hand: lines are
        // named by their picture from the start. The English lines come as pictures shipped with
        // the program (data/rumours), the German ones are drawn with the built-in Caveat Bold;
        // the drawing takes a moment and must not hold up the start.
        var drawing = Task.Run(() =>
        {
            try
            {
                FileLog.Write($"Rumour lines loaded: {rumourShapes.Draw(GameLanguage.English)} of {RumourTexts.All.Count} texts");
                foreach (string language in new[] { GameLanguage.German })
                    FileLog.Write($"Rumour lines drawn ({language}): {rumourShapes.Draw(language)} of {RumourTexts.All.Count} texts");
            }
            catch (InvalidDataException e)
            {
                FileLog.Write("Error: " + e.Message);
            }
        });
        using var pipeline = new Pipeline(settings.DataPath, ui, rumourShapes: rumourShapes);
        var catalog = pipeline.Recognizer.Catalog.Items.ToDictionary(i => i.Id);
        ImageSource? Orb(string id) =>
            catalog.TryGetValue(id, out var item) ? LabelLayer.LoadImage(Path.Combine(settings.DataPath, item.Image)) : null;

        if (args.Length == 2 && args[0] == "--render-settings")
        {
            // POE2PRICER_LANGUAGE: the window in that language (the picture of a language pack).
            if (Environment.GetEnvironmentVariable("POE2PRICER_LANGUAGE") is { Length: > 0 } shown)
                Texts.Language = shown;
            var shownModel = new MainViewModel(settings, ["Forbidden Rites", "Runes of Aldur", "Standard"], RumourBook.LoadCached());
            // POE2PRICER_WARNINGS: the window with two lasting warnings (how the lines look).
            if (Environment.GetEnvironmentVariable("POE2PRICER_WARNINGS") is { Length: > 0 })
            {
                shownModel.Warn("textsize", new("GameTextSizeNotDefault", new Poe2Pricer.Core.Told("TextSize_extra_large"), Poe2Pricer.Ui.HotKey.Shown(settings.DiagnoseKey)), hint: "textsize extra_large");
                shownModel.Warn("gameconfig", new("GameSettingsNotFound", @"C:\Users\Name\Documents\My Games\Path of Exile 2\poe2_production_Config.ini"));
                shownModel.Warn("gamelanguage", new("GameLanguageNotRead", "ru"));
            }
            var w = new MainWindow(shownModel, Path.Combine(AppContext.BaseDirectory, "poe2pricer.ico"));
            // POE2PRICER_NOTICE: the notice before the first start (AskWindow).
            if (Environment.GetEnvironmentVariable("POE2PRICER_NOTICE") is { Length: > 0 })
            {
                WindowPicture.Save(Poe2Pricer.Ui.Views.AskWindow.Picture(Texts.Get("NoticeTitle"), Texts.Get("NoticeText"), NoticeWidth, Texts.Get("NoticeRun"), Texts.Get("NoticeQuit")), args[1]);
                return 0;
            }
            // POE2PRICER_ASK: the question for a profile with changes not saved (AskWindow).
            if (Environment.GetEnvironmentVariable("POE2PRICER_ASK") is { Length: > 0 } asked)
            {
                WindowPicture.Save(Poe2Pricer.Ui.Views.AskWindow.Picture(Texts.Get("UnsavedTitle"), Texts.Get("UnsavedProfile", asked), Texts.Get("Save"), Texts.Get("Discard"), Texts.Get("Cancel")), args[1]);
                return 0;
            }
            // POE2PRICER_PRICE: the profile prices of the Waystones and Tablets tabs asked on the
            // trade site before the picture is taken (pictures for the guide).
            if (Environment.GetEnvironmentVariable("POE2PRICER_PRICE") is { Length: > 0 })
                PriceProfiles(shownModel, settings, Orb);
            int tab = Environment.GetEnvironmentVariable("POE2PRICER_TAB") is { } wanted ? int.Parse(wanted) : 0;
            // POE2PRICER_ROOM: the height of the screen's work area (a low screen, where the tab rolls).
            double room = Environment.GetEnvironmentVariable("POE2PRICER_ROOM") is { Length: > 0 } low ? double.Parse(low, System.Globalization.CultureInfo.InvariantCulture) : double.PositiveInfinity;
            WindowPicture.Save(WindowPicture.Of(w, tab, room), args[1]);
            return 0;
        }
        if (args.Length >= 3 && args[0] == "--render")
            return Render(args[1], args[2], args.Skip(3).ToArray(), pipeline, settings, Orb("Divine_Orb"), Orb("Exalted_Orb"), Orb("Chaos_Orb"));

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var iconPath = Path.Combine(AppContext.BaseDirectory, "poe2pricer.ico");
        var overlay = Overlays.Create(Orb("Divine_Orb"), Orb("Exalted_Orb"), Orb("Chaos_Orb"));
        var gameConfig = new GameConfig(settings.GameConfigFile);
        using var screen = new WindowsGameScreen(gameConfig);
        using var prices = new NinjaPrices(settings);
        var rumourBook = RumourBook.LoadCached();
        // The trade site, asked for waystone prices with poe.ninja's rates for the currencies.
        using var trade = new TradeClient(() => prices.Ready, FileLog.Write);
        // Which listings count follows the settings as they were last saved.
        var tradeSettings = settings;
        trade.InPerson = () => tradeSettings.TradeInPerson;
        using var engine = new OverlayEngine(GameWindows.Standard(pipeline, trade), screen, prices, () => rumourBook, settings, TimeProvider.System, FileLog.Write);
        // A rumour panel read before the lines were drawn has lines without a name, and on a
        // client that is not English nothing else names them: they are looked at again once
        // the drawing is done (it takes a few seconds).
        _ = drawing.ContinueWith(_ => engine.RumoursChanged(), TaskScheduler.Default);
        var model = new MainViewModel(settings, [], rumourBook);
        // The game read from its own window or from the screen (Settings.CaptureGameWindow): the
        // overlay keeps out of screen captures only while the screen is read. A black picture of
        // the window falls back to the screen for the run: a line of the window and the log say so.
        void ApplyCapture(Settings s)
        {
            screen.GameWindowOnly = s.CaptureGameWindow;
            overlay.ExcludeFromCapture(!s.CaptureGameWindow || screen.FellBack);
        }
        ApplyCapture(settings);
        Poe2Pricer.Core.WindowCapture.Log = FileLog.Write;
        screen.CaptureFellBack += reason =>
        {
            // Out of captures at once, on the thread that falls back: the next glance reads the
            // screen within milliseconds and must not see the labels (R7). The line and the log
            // can wait for the UI thread.
            overlay.ExcludeFromCapture(true);
            app.Dispatcher.BeginInvoke(() =>
            {
            var told = new Poe2Pricer.Core.Told(reason == Poe2Pricer.Core.FallBackReason.Unavailable ? "CaptureUnavailable" : "CaptureFellBack");
            FileLog.Write(told.English + (reason == Poe2Pricer.Core.FallBackReason.Unavailable ? $" ({Poe2Pricer.Core.WindowCapture.Unavailable})" : ""));
            model.Warn("capture", told, hint: reason == Poe2Pricer.Core.FallBackReason.Unavailable ? "capture unavailable" : "capture black");
            });
        };
        // "Price" on the Waystones tab: the cheapest waystone the profile describes, in the
        // unit of the settings; without poe.ninja's rates there is nothing to reckon it in.
        // "Refresh price" on the Waystones and Tablets tabs: the cheapest item the profile
        // describes, in the unit of the settings; not asked (the trade site's limit, no rates
        // yet): thrown with the reason, which the tab shows instead of a price.
        async Task<(string Price, string Unit, int Listings)?> ProfilePrice(Task<TradeAnswer> asking, string what)
        {
            if (prices.Ready is not { } book)
                throw new TradeNotAskedException(new TradeAnswer(null, NotAsked.NoRates).Reason());
            var answer = await asking;
            if (!answer.Asked)
                throw new TradeNotAskedException(answer.Reason());
            if (answer.Price is not { } price)
                return null;
            var quote = new Price(price.Divine, book.ExaltedPerDivine, price.Listings, what) { ChaosPerDivine = book.ChaosPerDivine };
            string unit = quote.UnitIn(model.Settings.PriceUnit);
            return (quote.TextIn(model.Settings.PriceUnit).Replace(" " + unit, ""), unit, price.Listings);
        }
        model.Waystones.PriceLookup = profile => ProfilePrice(trade.ForProfileAsync(profile, prices.Ready?.League ?? ""), "Waystone");
        model.Waystones.PricesReady = () => prices.Ready is not null;
        model.Waystones.Log = FileLog.Write;
        model.Waystones.Dispatch = run => app.Dispatcher.BeginInvoke(run);
        model.Waystones.IconFor = unit => unit switch { "div" => Orb("Divine_Orb"), "chaos" => Orb("Chaos_Orb"), _ => Orb("Exalted_Orb") };
        // The profiles saved on their own: the overlay judges and prices tooltips by them from now on.
        model.Waystones.Saved += _ => engine.Apply(Core.Settings.Load());
        // The Tablets tab the same way, with the kind's profile.
        model.Tablets.PriceLookup = profile => ProfilePrice(trade.ForProfileAsync(profile, prices.Ready?.League ?? ""), "Tablet");
        model.Tablets.PricesReady = () => prices.Ready is not null;
        model.Tablets.Log = FileLog.Write;
        model.Tablets.Dispatch = run => app.Dispatcher.BeginInvoke(run);
        model.Tablets.IconFor = model.Waystones.IconFor;
        model.Tablets.Saved += _ => engine.Apply(Core.Settings.Load());
        var window = new MainWindow(model, iconPath, Path.Combine(AppContext.BaseDirectory, "data", "guide", "README.html"));
        // A profile with changes not saved, before another one is chosen (UnsavedProfile) or
        // before "Exit" (UnsavedProfileQuit): save (true), throw away (false), or stay (null).
        Func<string, bool?> Asking(string question) => name =>
            Poe2Pricer.Ui.Views.AskWindow.Ask(window, Texts.Get("UnsavedTitle"), Texts.Get(question, name), Texts.Get("Save"), Texts.Get("Discard"), Texts.Get("Cancel")) switch
            {
                0 => true,
                1 => false,
                _ => null,
            };
        model.Waystones.AskSave = model.Tablets.AskSave = Asking("UnsavedProfile");
        model.Waystones.AskQuit = model.Tablets.AskQuit = Asking("UnsavedProfileQuit");
        using var tray = new TrayIcon(iconPath);
        // The status line of the program window: what is going on, what went wrong.
        void Told(StatusMessage message) => model.Report(message);
        void Status(string text) => Told(new(text));
        void Done(string text) => Told(StatusMessage.Ok(text));
        void Failed(string text) => Told(StatusMessage.Error(text));
        engine.Status += Told;
        // The league list comes from the cache or a single request, in the background: the
        // window does not wait for it, and a list that failed is asked for again with the next
        // price load. UI thread.
        bool leaguesKnown = false, leaguesLoading = false;
        void LoadLeagues()
        {
            if (leaguesKnown || leaguesLoading)
                return;
            leaguesLoading = true;
            _ = Task.Run(async () =>
            {
                string[] found = [];
                try
                {
                    using var ninja = new NinjaClient();
                    found = await ninja.LeaguesAsync();
                }
                // Whatever goes wrong — no net, or an answer that is no list of leagues — the
                // list is asked for again with the next load; "loading" must not stay set
                // for the rest of the run.
                catch (Exception e)
                {
                    FileLog.Write("League list not loaded: " + e.Message + Causes.Of(e));
                }
                _ = app.Dispatcher.BeginInvoke(() =>
                {
                    leaguesLoading = false;
                    if (found.Length == 0)
                        return;
                    leaguesKnown = true;
                    model.Settings.SetLeagues(found);
                });
            });
        }
        LoadLeagues();
        // Last resort for anything outside the overlay control (which catches its own): logged
        // and shown instead of ending the overlay in the middle of the game.
        app.DispatcherUnhandledException += (_, e) =>
        {
            FileLog.Write("Error (UI): " + e.Exception);
            Failed(Texts.Shown(e.Exception));
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FileLog.Write("Error (background): " + e.Exception);
            e.SetObserved();
        };
        // The overlay control decides on its own thread; the overlay window lives on the UI thread.
        engine.Draw += scene =>
            app.Dispatcher.BeginInvoke(() =>
            {
                if (scene is null)
                    overlay.Clear();
                else
                    overlay.Show(scene.Screen, scene.Labels, scene.Rumours, scene.Scale);
            });
        // Evidence pictures (a detected reroll in the Ritual window) go beside the diagnoses;
        // the engine writes them only with "Extended diagnostics" on (Settings).
        string diagnosticsFolder = Path.Combine(Settings.Folder, "diagnostics");
        engine.EvidenceFolder = diagnosticsFolder;
        engine.Diagnosed += file =>
        {
            if (file is null)
                Failed(Texts.Get("GameNotFound"));
            else
                Done(Texts.Get("DiagnosticsSaved", Path.GetFileName(file)));
            if (file is not null)
                app.Dispatcher.BeginInvoke(() => tray.Notify(Texts.Get("DiagnosticsSavedTray")));
        };
        // Not while a key field of the program window waits for a key (KeyBox.Listening).
        using var keys = new KeyboardHook(app.Dispatcher) { Active = () => screen.InFront && !KeyBox.Listening };
        using var foreground = new ForegroundWatcher();
        foreground.Changed += engine.ForegroundChanged;
        void Bind(Settings s)
        {
            keys.Clear();
            keys.Bind(s.ToggleKey, engine.Toggle);
            keys.Bind(s.DiagnoseKey, () => engine.Diagnose(diagnosticsFolder));
            if (s.ExactKey.Length > 0)
                keys.Bind(s.ExactKey, engine.Exact);
        }
        Bind(settings);
        // "Mit Windows starten": the entry follows the setting — now, where an entry naming
        // another place is set right, and whenever the settings are saved.
        var autostart = Autostart.Windows();
        void Autostarted(Settings s)
        {
            try
            {
                if (Environment.ProcessPath is { } program && autostart.Apply(s.StartWithWindows, program))
                    FileLog.Write(s.StartWithWindows ? $"Windows startup added: {program}" : "Windows startup removed");
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                FileLog.Write("Windows startup not changed: " + e.Message);
                Failed(Texts.Get("StartupNotChanged", e.Message));
            }
        }
        Autostarted(settings);
        // "Check for updates": once per start and once more when it is switched on, in the
        // background (Updates). The status line and a tray message name a newer version. An
        // installed copy downloads and installs it on a click, which starts the program again;
        // nothing is fetched without that click. An unpacked copy is only told, with the page. A
        // failed request is only logged: the program does not depend on github.com.
        var updates = new Updates();
        bool updatesChecked = false;
        bool installing = false;
        // The click on the version named: downloaded, then installed, which starts the program
        // again. A second click while that runs does nothing. A failed download is said in the
        // status line (Velopack throws its own types, hence any exception); the next start names
        // the version again.
        void Install(string version)
        {
            if (installing)
                return;
            // The restart ends the program: profiles not saved are asked about as on "Exit";
            // Cancel keeps the program, and the update waits for a later click (review before 1.2.0).
            if (!app.Dispatcher.Invoke(() => model.Waystones.Quitting() && model.Tablets.Quitting()))
                return;
            installing = true;
            Told(new StatusMessage(Texts.Get("UpdateDownloading", version)));
            _ = Task.Run(async () =>
            {
                try
                {
                    // The tray icon goes before the process ends, else Windows shows it until the
                    // mouse passes over it.
                    await updates.InstallAsync(FileLog.Write, () => app.Dispatcher.Invoke(tray.Dispose));
                }
                catch (Exception e)
                {
                    installing = false;
                    FileLog.Write("Update failed: " + e.Message);
                    await app.Dispatcher.BeginInvoke(() => Told(StatusMessage.Error(Texts.Get("UpdateFailed", version, e.Message))));
                }
            });
        }
        void CheckUpdates()
        {
            updatesChecked = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var found = await updates.LookAsync();
                    FileLog.Write(found is null ? "Update check: none newer" : $"Update check: {found.Version}{(found.Installable ? ", a click installs it" : "")} ({found.Page})");
                    if (found is null)
                        return;
                    await app.Dispatcher.BeginInvoke(() =>
                    {
                        if (found.Installable)
                        {
                            Told(StatusMessage.Acting(Texts.Get("UpdateOffered", found.Version), () => Install(found.Version)));
                            tray.Notify(Texts.Get("UpdateOfferedTray", found.Version), () => Install(found.Version));
                        }
                        else
                        {
                            Told(StatusMessage.Linked(Texts.Get("UpdateAvailable", found.Version), found.Page));
                            tray.Notify(Texts.Get("UpdateAvailableTray", found.Version), () => OpenPage(found.Page));
                        }
                    });
                }
                catch (Exception e)
                {
                    // Velopack throws types of its own as well; whatever it is goes to the log
                    // instead of vanishing in a task nobody waits for (review before 1.2.0).
                    FileLog.Write("Update check failed: " + e.GetType().Name + ": " + e.Message);
                }
            });
        }
        void OpenPage(string page)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Status(page);
            }
        }
        if (settings.CheckForUpdates)
            CheckUpdates();
        model.Settings.Saved += s =>
        {
            Texts.Language = s.Language;
            gameConfig.Use(s.GameConfigFile);
            engine.Apply(s);
            ApplyCapture(s);
            Bind(s);
            Autostarted(s);
            if (s.CheckForUpdates && !updatesChecked)
                CheckUpdates();
            if (prices.Apply(s))
            {
                Status(Texts.Get("LoadingPrices"));
                Report(prices.GetAsync());
            }
        };
        tray.OpenSettings += () => { window.Show(); window.Activate(); };
        model.Quit += () => app.Shutdown();
        // The outcome of a load the user waits for; a load replaced by another league says nothing.
        void Report(Task<PriceBook> load) =>
            _ = load.ContinueWith(t =>
            {
                if (t.IsCanceled)
                    return;
                if (t.IsFaulted)
                {
                    // In the log too: the status line is gone with the next message.
                    FileLog.Write("Prices not loaded: " + t.Exception!.GetBaseException().Message + Causes.Of(t.Exception!));
                    Failed(Texts.Shown(t.Exception!.GetBaseException()));
                }
                else
                {
                    Done(Texts.Get("Ready", Texts.Count("Prices", t.Result.Count), t.Result.League));
                    _ = app.Dispatcher.BeginInvoke(LoadLeagues);
                }
            });
        prices.Failed += e =>
        {
            FileLog.Write("Prices not refreshed: " + e.Message);
            Failed(Texts.Get("NinjaRefreshFailed", Texts.Reason(e) ?? e.Message));
        };
        // Item art for the stash tabs, fetched once (first start, new items); "?" there meanwhile.
        pipeline.Icons.Loading += (done, total) =>
        {
            if (done == total)
                Done(Texts.Get("ItemImagesLoaded"));
            else if (done == 1 || done % 25 == 0)
                Status(Texts.Get("ItemImagesLoading", done, total));
        };
        model.Rumours.Changed += engine.RumoursChanged;
        // asked: "Refresh" in the window — then the outcome is told, so that "Loading
        // community data…" does not stay for good.
        void LoadRumours(bool asked) =>
            _ = Task.Run(async () =>
            {
                bool loaded = false;
                try
                {
                    using var http = new System.Net.Http.HttpClient(Poe2Pricer.Core.Market.Connecting.Handler()) { MaxResponseContentBufferSize = Tuning.Control.ResponseLimit, Timeout = Tuning.Control.RequestTimeout };
                    http.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
                    loaded = await rumourBook.RefreshAsync(http);
                }
                catch (Exception e)
                {
                    FileLog.Write("Error (community data): " + e);
                }
                if (loaded)
                {
                    // The rows belong to the window's thread; waits until they are built.
                    app.Dispatcher.Invoke(() => model.Rumours.Show(rumourBook));
                    engine.RumoursChanged();
                }
                if (asked && loaded)
                    Done(Texts.Get("CommunityDataLoaded", Texts.Count("Entries", rumourBook.Entries.Count(e => !e.Custom))));
                else if (asked)
                    Failed(Texts.Get("CommunityDataNotLoaded"));
            });
        model.Rumours.Reload += () => { Status(Texts.Get("LoadingCommunityData")); LoadRumours(asked: true); };
        LoadRumours(asked: false);
        // What is saved is applied already (Saved above); the settings file is not read anew
        // here.
        model.Settings.ReloadPrices += () =>
        {
            Status(Texts.Get("LoadingPrices"));
            Report(prices.ReloadAsync());
        };
        Status(Texts.Get("LoadingPrices"));
        Report(prices.GetAsync());
        // Told in the chosen language, logged in English.
        void Warn(Told told)
        {
            FileLog.Write(told.English);
            tray.Notify(told.Shown);
        }
        // What lasts stands in the window too, on a line of its own, until its cause is gone.
        void Lasting(string cause, Told told)
        {
            Warn(told);
            model.Warn(cause, told);
        }
        if (rumourBook.Problem is { } problem)
            Lasting(MainViewModel.RatingsCause, problem);
        if (settingsProblem is not null)
            Lasting(MainViewModel.SettingsCause, settingsProblem);
        if (keys.Error is { } refused)
            Lasting("hotkeys", new("HotkeysUnavailable", refused));
        // One line for the keys: it shares the cause of the settings and goes with the next save.
        // A key that could not be read, or the exact-price key left off ("") because another key
        // of the program holds it.
        Told KeyTold((string Stored, string Used) k) => k.Used.Length == 0 ? new("ExactKeyTaken", k.Stored) : new("KeyUnreadable", k.Stored, k.Used);
        foreach (var replaced in replacedKeys)
            Warn(KeyTold(replaced));
        if (settingsProblem is null && replacedKeys.Count > 0)
            model.Warn(MainViewModel.SettingsCause, KeyTold(replacedKeys[0]));
        // Without the game's settings file the popup size is taken for the standard; with
        // another size set in the game, tooltips and the rumour panel would not be recognised.
        // Looked at again with every save and with the prices drawn, like the text size below.
        // Along with it the client's language: one the program does not read (English and
        // German it does) means nothing is recognised, neither the tooltips nor the stash search
        // text match the game's words, and a window in Russian must not suggest otherwise. A red
        // line in the window as long as it lasts (no "✕": it is a fault, not a hint), once in
        // the log and the tray when the value changes — the game writes its file when the
        // language is switched.
        bool toldNotFound = false;
        string toldLanguage = GameLanguage.English;
        void CheckGameConfig()
        {
            // One reading of the file for all of it: the game may rewrite it between two.
            string client = gameConfig.ClientLanguage;
            string language = GameLanguage.Known(client);
            // The search texts in the words of the client (it may have been switched since).
            model.Waystones.GameLanguage = model.Tablets.GameLanguage = language;
            bool missing = !gameConfig.Found;
            if (missing && !toldNotFound)
                Warn(new("GameSettingsNotFound", gameConfig.File));
            toldNotFound = missing;
            model.Warn("gameconfig", missing ? new("GameSettingsNotFound", gameConfig.File) : null);
            bool supported = language == client;
            if (!supported && client != toldLanguage)
                Warn(new("GameLanguageNotRead", client));
            toldLanguage = client;
            model.Warn("gamelanguage", supported ? null : new("GameLanguageNotRead", client));
        }
        CheckGameConfig();
        model.Settings.Saved += _ => CheckGameConfig();
        model.Settings.Saved += s => tradeSettings = s;
        engine.Draw += _ => app.Dispatcher.BeginInvoke(CheckGameConfig);
        if (gameConfig.Found)
            FileLog.Write($"Game settings: {gameConfig.File}, language {gameConfig.ClientLanguage}{(gameConfig.LanguageSupported ? "" : " (not read, English assumed)")}, UI Text Size {gameConfig.TextSize}, Popup Size {gameConfig.PopupSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
        // A larger UI Text Size is not supported: said at the start, and whenever another
        // value is found later — the game writes its settings file while it runs, so a size
        // changed in the game's options after the start shows up with the next prices drawn.
        string toldTextSize = GameConfig.StandardTextSize;
        // A text size other than the default is a hint on a line of the window, not a tray
        // notice and not red: the windows are found there, but not every one is measured
        // (OFFEN G8: tier marks in the stash, the tribute dialog), so it stays "not supported".
        // One short line with the size in the window's words and the diagnostics key as set.
        string diagnoseKey = settings.DiagnoseKey;
        // The size as the game's options name it (ClientStrings OptionsAccessibilityScale*); a
        // value the game may add later stands as it is in its settings file.
        Poe2Pricer.Core.Told TextSizeTold(string size) =>
            new("GameTextSizeNotDefault", size switch
            {
                "large" => new Poe2Pricer.Core.Told("TextSize_large"),
                "extra_large" => new Poe2Pricer.Core.Told("TextSize_extra_large"),
                _ => size,
            }, Poe2Pricer.Ui.HotKey.Shown(diagnoseKey));
        void CheckTextSize()
        {
            string now = gameConfig.TextSize;
            if (!gameConfig.TextSizeSupported && now != toldTextSize)
                FileLog.Write(TextSizeTold(now).English);
            toldTextSize = now;
            model.Warn("textsize", gameConfig.TextSizeSupported ? null : TextSizeTold(now), hint: "textsize " + now);
        }
        CheckTextSize();
        model.Settings.Saved += s =>
        {
            diagnoseKey = s.DiagnoseKey;
            CheckTextSize();
        };
        // The key's name is in the window's language ("Alt+Umschalt+F"): told again on a switch.
        Texts.Changed += CheckTextSize;
        engine.Draw += _ => app.Dispatcher.BeginInvoke(CheckTextSize);
        if (showRequest is not null)
        {
            var wait = new Thread(() =>
            {
                while (showRequest.WaitOne())
                    app.Dispatcher.Invoke(() =>
                    {
                        window.Show();
                        if (window.WindowState == WindowState.Minimized)
                            window.WindowState = WindowState.Normal;
                        window.Activate();
                    });
            })
            {
                IsBackground = true,
                Name = "Second start",
            };
            wait.Start();
        }
        // The window opens by itself only at the first start; after that the program starts in
        // the tray and the window comes by the tray icon or a second start. A window opened
        // while Windows signs the user in is not let to the front and ends up behind the
        // desktop widgets, where nobody looks for it; the comparable tools show none either.
        if (firstStart)
            window.Show();
        else
            tray.Notify(Texts.Get("RunningInTray", Poe2Pricer.Ui.HotKey.Shown(settings.ToggleKey)));
        return app.Run();
    }

    // The profile prices of both tabs, asked as the program asks them (ProfilePrice in Main), and
    // waited for: the windows' thread runs on until each answer is in.
    private static void PriceProfiles(MainViewModel model, Settings settings, Func<string, ImageSource?> orb)
    {
        using var ninja = new NinjaClient();
        var book = ninja.PricesAsync(settings.League ?? NinjaClient.DefaultLeague(ninja.LeaguesAsync().GetAwaiter().GetResult())).GetAwaiter().GetResult();
        using var trade = new TradeClient(() => book, Console.WriteLine) { InPerson = () => settings.TradeInPerson };
        async Task<(string Price, string Unit, int Listings)?> Price(Task<TradeAnswer> asking, string what)
        {
            var answer = await asking;
            if (answer.Price is not { } price)
                return null;
            var quote = new Price(price.Divine, book.ExaltedPerDivine, price.Listings, what) { ChaosPerDivine = book.ChaosPerDivine };
            string unit = quote.UnitIn(settings.PriceUnit);
            return (quote.TextIn(settings.PriceUnit).Replace(" " + unit, ""), unit, price.Listings);
        }
        Func<string, object?> icon = unit => unit switch { "div" => orb("Divine_Orb"), "chaos" => orb("Chaos_Orb"), _ => orb("Exalted_Orb") };
        model.Waystones.IconFor = model.Tablets.IconFor = icon;
        model.Waystones.PriceLookup = profile => Price(trade.ForProfileAsync(profile, book.League), "Waystone");
        model.Tablets.PriceLookup = profile => Price(trade.ForProfileAsync(profile, book.League), "Tablet");
        // The answers come back on the window's thread, as in the running program.
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
        var frame = new System.Windows.Threading.DispatcherFrame();
        Task.WhenAll(model.Waystones.PriceCommand.ExecuteAsync(null), model.Tablets.PriceCommand.ExecuteAsync(null))
            .ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Console.WriteLine($"profile prices: waystone {model.Waystones.ProfilePrice} {model.Waystones.ProfilePriceNote}, tablet {model.Tablets.ProfilePrice} {model.Tablets.ProfilePriceNote}");
    }

    private static int Render(string input, string output, string[] before, Pipeline pipeline, Settings settings, ImageSource? divine, ImageSource? exalted, ImageSource? chaos)
    {
        using var frame = Cv2.ImRead(input);
        using var ninja = new NinjaClient();
        var book = ninja.PricesAsync(settings.League ?? NinjaClient.DefaultLeague(ninja.LeaguesAsync().GetAwaiter().GetResult())).GetAwaiter().GetResult();
        pipeline.Icons.EnsureAsync(book.Images.Select(p => (p.Key, p.Value.Path))).GetAwaiter().GetResult();
        // The client's language and UI Text Size when the picture was taken, as the test world
        // takes them from testdata/languages.json and textsizes.json.
        string gameLanguage = Environment.GetEnvironmentVariable("POE2PRICER_GAME_LANGUAGE") is { Length: > 0 } lang ? lang : GameLanguage.English;
        string textSize = Environment.GetEnvironmentVariable("POE2PRICER_GAME_TEXTSIZE") is { Length: > 0 } size ? size : GameConfig.StandardTextSize;
        double popup = Environment.GetEnvironmentVariable("POE2PRICER_GAME_POPUP") is { Length: > 0 } scale ? double.Parse(scale, System.Globalization.CultureInfo.InvariantCulture) : GameConfig.StandardPopup;
        using var shot = GameCapture.Stored(frame, popup: popup, language: gameLanguage, textSize: textSize);
        // A waystone's or tablet's tooltip is priced as the overlay prices it: one question to the
        // trade site, with the live price on (pictures for the guide).
        var context = new LookContext(RumourBook.LoadCached(), settings with { LiveTradePrice = true }, Console.WriteLine);
        using var trade = new TradeClient(() => book, Console.WriteLine) { InPerson = () => settings.TradeInPerson };
        var windows = GameWindows.Standard(pipeline, trade);
        IOpenWindow? open = null;
        // As the overlay does: the first picture is looked at, each further one glanced at and
        // followed (the rumour panel shows its tiers only once a glance found it at rest).
        foreach (var file in before.Append(input))
        {
            using var image = Cv2.ImRead(file);
            using var seen = GameCapture.Stored(image, popup: popup, language: gameLanguage, textSize: textSize);
            if (open is not null)
            {
                using var glanced = GameCapture.Stored(image, area: open.GlanceArea, popup: popup, language: gameLanguage, textSize: textSize);
                open = open.Glance(glanced.Image, book, context) switch
                {
                    Glance.Gone => null,
                    Glance.Reread reread => reread.Next,
                    Glance.Relabel relabel => relabel.Next,
                    _ => open,
                };
            }
            open = open is null ? null : open.FollowAsync(seen, context).GetAwaiter().GetResult();
            open ??= windows.LookAsync(seen, context, _ => true).GetAwaiter().GetResult();
            if (open is not null && file != input)
                open = open.LabelAsync(seen, Task.FromResult(book), context).GetAwaiter().GetResult().Next;
        }
        if (open is null)
        {
            Console.Error.WriteLine(Texts.English("NoWindowDetected"));
            return 1;
        }
        var picture = open.LabelAsync(shot, Task.FromResult(book), context).GetAwaiter().GetResult();
        // The price comes in the background ("…" at the title bar); the overlay takes it at a later
        // glance, so does this, for at most half a minute.
        for (int i = 0; i < 60 && picture.Labels.Any(l => l.Mark == "…"); i++)
        {
            Thread.Sleep(500);
            using var glanced = GameCapture.Stored(frame, area: picture.Next.GlanceArea, popup: popup, language: gameLanguage, textSize: textSize);
            if (picture.Next.Glance(glanced.Image, book, context) is Glance.Relabel relabel)
                picture = relabel.Next.LabelAsync(shot, Task.FromResult(book), context).GetAwaiter().GetResult();
        }
        Console.WriteLine(picture.Status);
        var labels = picture.Labels;
        var rumours = picture.Rumours;

        var background = new BitmapImage(new Uri(Path.GetFullPath(input)));
        // Drawn onto the picture as it is: at the game's part of it, at the picture's scale.
        var layer = new LabelLayer { Labels = labels, Divine = divine, Exalted = exalted, Chaos = chaos, Rumours = rumours, PixelScale = shot.Scale, Origin = new System.Windows.Point(shot.ScreenBounds.X, shot.ScreenBounds.Y) };
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(background, new Rect(0, 0, frame.Width, frame.Height));
        }
        layer.Measure(new System.Windows.Size(frame.Width, frame.Height));
        layer.Arrange(new Rect(0, 0, frame.Width, frame.Height));
        var bitmap = new RenderTargetBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Render(layer);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var written = File.Create(output);
        encoder.Save(written);
        Console.WriteLine($"{labels.Length} labels, {labels.Count(l => l.Text is not null)} with a price → {output}");
        // What the window read, as a diagnosis writes it: beside the picture, same name, .json.
        File.WriteAllText(Path.ChangeExtension(output, ".json"), System.Text.Json.JsonSerializer.Serialize(picture.Description, JsonFiles.Options));
        return 0;
    }
}
