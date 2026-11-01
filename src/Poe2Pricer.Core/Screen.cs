using System.Runtime.InteropServices;

namespace Poe2Pricer.Core;

// The game screen as the overlay control sees it: is the game in front, and a picture of it.
// Adapters: WindowsGameScreen (the real game) and, in tests, a sequence of PNG files.
public interface IGameScreen
{
    // The game owns the foreground window. Cheap (two system calls), any thread. Our own windows
    // do not count: with the settings window in front the prices go, and come back with the next
    // click into the game. (Until 31.10.2026 our windows counted as the game, and with a browser's
    // full-screen video over the game, Alt+Tab to the settings drew the covered game's prices over
    // the video — OFFEN F11. The overlay windows themselves are never activated, so showing them
    // cannot take the foreground.)
    bool InFront { get; }

    // The pictures come from the game's window itself (window capture), not from the screen.
    // From the screen, whatever lies over the game is in the picture too — another window, a
    // video — so then no picture is written to disk: the diagnosis keeps its JSON only, and
    // the evidence pictures of the extended diagnostics stay off (31.10.2026).
    bool PicturesFromWindow => true;

    // Off the UI thread. null: game not found or capture failed. The caller disposes the frame.
    // The picture comes at reference size (GameView). area: only this part of it is needed;
    // the frame keeps its size and is black outside of it (the glance: a part is taken in a
    // third of the time).
    GameCapture.Frame? Capture(Box? area = null);

    // The follow's capture (OverlayEngine.FollowOnceAsync): the same picture, taken for a
    // reading of the window rather than a glance at it. Its area is the window's FollowArea —
    // the whole picture until 31.10.2026; at 3840×2160 that was 33 MB read back from the GPU
    // and resized every second and after every glance that saw the cursor move, with every
    // glance waiting behind it (OFFEN Z11). Its own method, so that a test double can tell a
    // reading from a glance by more than the area.
    GameCapture.Frame? Read(Box? area = null) => Capture(area);

    // The game's window as it is on the screen, for the diagnosis. The caller disposes it.
    GameCapture.Shot? Snapshot();
}

// The Path of Exile 2 window, looked for every 5 s (the game may start or restart). Every
// picture carries the popup size from the game's settings file, as it stands at that moment.
// Why the game is read from the screen although Settings.CaptureGameWindow is on
// (WindowsGameScreen.CaptureFellBack): Unavailable — this Windows cannot capture the window
// without a border (WindowCapture.Unavailable); Stopped — the capture gave nothing; Black — a
// black picture while the screen showed the game.
public enum FallBackReason { Unavailable, Stopped, Black }

public sealed class WindowsGameScreen : IGameScreen, IDisposable
{
    private readonly Timer search;
    private readonly GameConfig config;
    private volatile int gameProcess;
    private nint gameWindow;
    private bool gameWindowOnly;
    private volatile bool fellBack;

    // Read the game from its own window (Settings.CaptureGameWindow) rather than from the
    // screen. Every assignment (the start, every save of the settings) gives the window another
    // try after a fall-back: the user who saw the line and presses Save expects one (R9).
    public bool GameWindowOnly
    {
        get => gameWindowOnly;
        set
        {
            fellBack = false;
            troubleSince = -1;
            gameWindowOnly = value;
        }
    }

    // The window gave nothing (window capture is not available on this Windows, see
    // WindowCapture.Unavailable, or it stopped) or a black picture while the screen showed the
    // game (a window in exclusive fullscreen, a driver): the screen is read for the rest of the
    // run, and the overlay must keep out of captures again (Program). Told once, off the UI
    // thread, with the reason. A black screen as well is the game itself (its loading screen,
    // 22.10.2026: the client restarted in another language fell back for nothing) and no fault
    // of the capture.
    public bool FellBack => fellBack;
    public event Action<FallBackReason>? CaptureFellBack;

    private bool FromWindow => gameWindowOnly && !fellBack;

    private void FallBack(FallBackReason reason)
    {
        if (fellBack)
            return;
        fellBack = true;
        CaptureFellBack?.Invoke(reason);
    }

    public WindowsGameScreen(GameConfig config)
    {
        this.config = config;
        search = new Timer(_ => Search(), null, TimeSpan.Zero, Tuning.Control.GameSearchEvery);
    }

    private void Search()
    {
        var window = GameCapture.FindGame();
        gameWindow = window;
        GetWindowThreadProcessId(window, out var id);
        gameProcess = window == 0 ? 0 : (int)id;
    }

    public bool InFront => Front();

    public bool PicturesFromWindow => FromWindow;

    private bool Front()
    {
        int game = gameProcess;
        if (game == 0)
            return false;
        GetWindowThreadProcessId(GetForegroundWindow(), out var id);
        return id == game;
    }

    // The window gave nothing or a black picture while the screen shows the game: for the first
    // Tuning.Control.WindowCaptureGrace that is no verdict (the client has just started and
    // draws nothing yet; a loading screen; the first comparison with the screen may still hold
    // the overlay's own labels, which are not kept out of screen captures while the window is
    // read — the glance hides them on the black picture within a moment). Meanwhile the black
    // or missing picture is what the caller gets, not the screen: read from the screen with the
    // overlay in it, the program would see its own labels. Trouble past the grace, with the
    // screen not black: fall back for the run. A screen as black as the window ends the trouble
    // (the game is black). Review R1/R12 (23.10.2026).
    private long troubleSince = -1;

    public GameCapture.Frame? Capture(Box? area = null)
    {
        var frame = GameCapture.Capture(gameWindow, area, config.PopupSize, config.Language, config.TextSize, FromWindow, out bool black);
        if (!FromWindow || gameWindow == 0)
            return frame;
        if (frame is null && WindowCapture.Unavailable is not null)
        {
            // Known for good on this Windows: no grace.
            FallBack(FallBackReason.Unavailable);
            return GameCapture.Capture(gameWindow, area, config.PopupSize, config.Language, config.TextSize, gameWindow: false, out _);
        }
        if (frame is not null && !black)
        {
            troubleSince = -1;
            return frame;
        }
        if (!InFront)
            return frame;
        var screen = GameCapture.Capture(gameWindow, area, config.PopupSize, config.Language, config.TextSize, gameWindow: false, out _);
        if (screen is null || GameCapture.IsBlack(screen.Image))
        {
            screen?.Dispose();
            troubleSince = -1;
            return frame;
        }
        long now = Environment.TickCount64;
        if (troubleSince < 0)
            troubleSince = now;
        if (now - troubleSince < Tuning.Control.WindowCaptureGrace.TotalMilliseconds)
        {
            screen.Dispose();
            return frame;
        }
        frame?.Dispose();
        FallBack(frame is null ? FallBackReason.Stopped : FallBackReason.Black);
        return screen;
    }

    // One picture (the diagnostics key): the window's, or the screen's when the window gives
    // nothing or black; a single picture is no verdict on the window capture (Capture decides).
    public GameCapture.Shot? Snapshot()
    {
        var shot = GameCapture.Snapshot(gameWindow, config.PopupSize, FromWindow);
        if (FromWindow && gameWindow != 0 && (shot is null || GameCapture.IsBlack(shot.Picture)))
        {
            var screen = GameCapture.Snapshot(gameWindow, config.PopupSize);
            if (screen is not null && !GameCapture.IsBlack(screen.Picture))
            {
                shot?.Dispose();
                shot = screen;
            }
            else
                screen?.Dispose();
        }
        return shot is null ? null : shot with { Language = config.Language, TextSize = config.TextSize };
    }

    public void Dispose() => search.Dispose();

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
}

// A fixed picture of the game, always in front: the golden-master check and --render.
public sealed class StillScreen(OpenCvSharp.Mat image, (int X, int Y)? pointer = null, double popup = GameConfig.StandardPopup, string language = GameLanguage.English, string textSize = GameConfig.StandardTextSize) : IGameScreen
{
    public bool InFront => true;

    public GameCapture.Frame? Capture(Box? area = null) => GameCapture.Stored(image, pointer, area, popup, language, textSize);

    public GameCapture.Shot? Snapshot() => new(image.Clone(), pointer, popup, language, textSize);
}

// Where the mouse pointer was on stored pictures (testdata/pointers.json: picture → [x, y]).
// The pictures themselves do not hold it; those taken before the pointer was recorded carry
// the place worked out from the "ALT Inspect" hint beside it.
public static class StoredPointers
{
    public static IReadOnlyDictionary<string, (int X, int Y)> Load(string testdata)
    {
        var file = Path.Combine(testdata, "pointers.json");
        return File.Exists(file)
            ? JsonFiles.Read<Dictionary<string, int[]>>(file).ToDictionary(p => p.Key, p => (p.Value[0], p.Value[1]))
            : new Dictionary<string, (int, int)>();
    }
}

// The language the game was set to when a stored picture was taken (testdata/languages.json:
// picture → language); pictures not named there were taken on the English client.
public static class StoredLanguages
{
    // The language the client ran in when a stored picture was taken (testdata/languages.json:
    // name below testdata without extension → language); a picture not named there: English.
    public static IReadOnlyDictionary<string, string> Load(string testdata)
    {
        var file = Path.Combine(testdata, "languages.json");
        return File.Exists(file) ? JsonFiles.Read<Dictionary<string, string>>(file) : new Dictionary<string, string>();
    }
}

// The UI Text Size the game was set to when a stored picture was taken (testdata/textsizes.json:
// name below testdata without extension → "large" or "extra_large"); a picture not named there
// was taken at the standard.
public static class StoredTextSizes
{
    public static IReadOnlyDictionary<string, string> Load(string testdata)
    {
        var file = Path.Combine(testdata, "textsizes.json");
        return File.Exists(file) ? JsonFiles.Read<Dictionary<string, string>>(file) : new Dictionary<string, string>();
    }
}

// The popup size the game was set to when a stored picture was taken (testdata/popups.json:
// picture → value); pictures not named there were taken at the game's standard.
public static class StoredPopups
{
    public static IReadOnlyDictionary<string, double> Load(string testdata)
    {
        var file = Path.Combine(testdata, "popups.json");
        return File.Exists(file) ? JsonFiles.Read<Dictionary<string, double>>(file) : new Dictionary<string, double>();
    }
}
