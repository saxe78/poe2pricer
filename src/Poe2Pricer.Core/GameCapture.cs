using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Poe2Pricer.Core;

// Captures the client area of the Path of Exile 2 window (borderless window mode; an
// exclusive fullscreen game cannot be overlaid anyway): from the window itself through the
// desktop compositor (Settings.CaptureGameWindow, WindowCapture — the overlay and other windows
// over the game are never in the picture, and the game gets no message for it), or from the
// screen with BitBlt (then the overlay must keep out of captures, IOverlay.ExcludeFromCapture). The game is recognised by its process
// name, as in Dev-Jahn/poe2-ritual-checker (MIT). Coordinates are physical pixels, so the
// process must be per-monitor DPI aware.
public static class GameCapture
{
    // Image: the game's picture at reference size (GameView), whatever size the game runs at.
    // ScreenBounds: where the game's part of its window lies on the screen; Scale: screen
    // pixels per pixel of Image — labels are drawn at ScreenBounds in Image coordinates times
    // Scale. Pointer: where the mouse pointer is on Image (Windows knows; nothing of the game
    // is touched), null when it lies outside the game or the picture comes from a file without
    // one. With the mouse on an item in the keyboard UI, that item has the tooltip.
    // Language: the language the client runs in (GameConfig.Language, GameLanguage).
    // Popup: the size the game draws its popups at (GameConfig.PopupSize) — tooltips and the
    // rumour panel on Image are this much larger than at the game's standard.
    // TextSize: the UI Text Size the client runs at (GameConfig.TextSize): what the game letters
    // (the tribute dialog, stack numbers) is larger than at the standard (GameConfig.TextScale).
    public sealed record Frame(Mat Image, Box ScreenBounds, (int X, int Y)? Pointer = null, double Scale = 1, double Popup = GameConfig.StandardPopup, string Language = GameLanguage.English, string TextSize = GameConfig.StandardTextSize) : IDisposable
    {
        public void Dispose() => Image.Dispose();
    }

    // The client area as it is on the screen, with the pointer on it: what the diagnosis saves.
    public sealed record Shot(Mat Picture, (int X, int Y)? Pointer, double Popup = GameConfig.StandardPopup, string Language = GameLanguage.English, string TextSize = GameConfig.StandardTextSize) : IDisposable
    {
        public void Dispose() => Picture.Dispose();
    }

    public static nint FindGame()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.StartsWith("PathOfExile", StringComparison.OrdinalIgnoreCase) && p.MainWindowHandle != 0)
                    return p.MainWindowHandle;
            }
            catch (InvalidOperationException) { }
            finally
            {
                p.Dispose();
            }
        }
        return 0;
    }

    // area: only this part of the reference picture is wanted; the rest of the frame stays
    // black. Taking a part is what makes the glance cheap enough to run many times a second
    // (measured at 2560×1440: the whole picture 22 ms, a part 8 ms).
    public static Frame? Capture(nint window, Box? area = null, double popup = GameConfig.StandardPopup, string language = GameLanguage.English, string textSize = GameConfig.StandardTextSize) =>
        Capture(window, area, popup, language, textSize, gameWindow: false, out _);

    // gameWindow: the picture from the window itself (WindowCapture) instead of the screen;
    // null then also when window capture is not available here (WindowCapture.Unavailable) or
    // gives nothing — the sign to read the screen instead (WindowsGameScreen), as is
    // black: the window delivered nothing (every pixel below Tuning.Control.BlackCaptureBelow).
    public static Frame? Capture(nint window, Box? area, double popup, string language, string textSize, bool gameWindow, out bool black)
    {
        black = false;
        if (!Client(window, out var origin, out int w, out int h))
            return null;
        var view = GameView.Of(w, h);
        var (from, to) = view.Part(area);
        using var part = gameWindow
            ? GrabWindow(window, origin, w, h, new Rect(view.Game.X + from.X, view.Game.Y + from.Y, from.Width, from.Height))
            : Grab(origin.X + view.Game.X + from.X, origin.Y + view.Game.Y + from.Y, from.Width, from.Height);
        if (part is null)
            return null;
        black = gameWindow && IsBlack(part);
        return new Frame(
            view.Placed(part, to),
            new Box(origin.X + view.Game.X, origin.Y + view.Game.Y, view.Game.Width, view.Game.Height),
            view.Point(Pointer(origin, w, h)),
            view.Scale,
            popup,
            language,
            textSize
        );
    }

    public static Shot? Snapshot(nint window, double popup = GameConfig.StandardPopup, bool gameWindow = false)
    {
        if (!Client(window, out var origin, out int w, out int h))
            return null;
        var picture = gameWindow ? GrabWindow(window, origin, w, h, new Rect(0, 0, w, h)) : Grab(origin.X, origin.Y, w, h);
        return picture is null ? null : new Shot(picture, Pointer(origin, w, h), popup);
    }

    // No pixel brighter than Tuning.Control.BlackCaptureBelow.
    public static bool IsBlack(Mat bgr)
    {
        Cv2.MinMaxLoc(bgr.Reshape(1), out _, out double brightest);
        return brightest < Tuning.Control.BlackCaptureBelow;
    }

    private static bool Client(nint window, out POINT origin, out int width, out int height)
    {
        origin = new POINT();
        (width, height) = (0, 0);
        if (window == 0 || !GetClientRect(window, out var rect) || !ClientToScreen(window, ref origin))
            return false;
        (width, height) = (rect.Right - rect.Left, rect.Bottom - rect.Top);
        return width > 0 && height > 0;
    }

    private static (int X, int Y)? Pointer(POINT origin, int width, int height) =>
        GetCursorPos(out var at) && at.X >= origin.X && at.Y >= origin.Y && at.X < origin.X + width && at.Y < origin.Y + height ? (at.X - origin.X, at.Y - origin.Y) : null;

    // A part of the window's client area as a BGR picture from the window's own content
    // (WindowCapture). The content is the window as the compositor shows it: for the borderless
    // game that is the client area; a window with a frame has the client area further in, by
    // the distance from the frame's edge (DWMWA_EXTENDED_FRAME_BOUNDS) to the client origin. One
    // capture is kept for the window at hand and opened again for another window, or after it
    // closed.
    private static WindowCapture? windowCapture;
    private static readonly object windowGate = new();

    private static Mat? GrabWindow(nint window, POINT origin, int width, int height, Rect part)
    {
        lock (windowGate)
        {
            if (windowCapture is not null && (windowCapture.Window != window || windowCapture.Closed))
            {
                windowCapture.Dispose();
                windowCapture = null;
            }
            windowCapture ??= WindowCapture.Open(window);
            try
            {
                var cut = part.Intersect(new Rect(0, 0, width, height));
                if (DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out RECT frame, 16) == 0)
                    cut = new Rect(cut.X + origin.X - frame.Left, cut.Y + origin.Y - frame.Top, cut.Width, cut.Height);
                return windowCapture?.Grab(cut);
            }
            catch (Exception e) when (e is COMException or InvalidOperationException)
            {
                // The device or the window went away under the copy: the next look opens anew.
                WindowCapture.Log?.Invoke($"Window capture: {e.Message}");
                windowCapture?.Dispose();
                windowCapture = null;
                return null;
            }
        }
    }

    // A part of the screen as a BGR picture.
    private static Mat? Grab(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return null;
        nint screen = GetDC(0),
            memory = CreateCompatibleDC(screen),
            bitmap = CreateCompatibleBitmap(screen, width, height),
            old = SelectObject(memory, bitmap);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020))
                return null;
            var info = new BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            };
            using var bgra = new Mat(height, width, MatType.CV_8UC4);
            SelectObject(memory, old);
            if (GetDIBits(memory, bitmap, 0, (uint)height, bgra.Data, ref info, 0) == 0)
                return null;
            var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    // A stored picture of the client area as a capture would give it (tests, golden-master
    // check, preview): the same way through GameView as the live capture takes.
    public static Frame Stored(Mat client, (int X, int Y)? pointer = null, Box? area = null, double popup = GameConfig.StandardPopup, string language = GameLanguage.English, string textSize = GameConfig.StandardTextSize)
    {
        var view = GameView.Of(client.Width, client.Height);
        return new Frame(view.Read(client, area), new Box(view.Game.X, view.Game.Y, view.Game.Width, view.Game.Height), view.Point(pointer), view.Scale, popup, language, textSize);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, nint bits, ref BITMAPINFOHEADER info, uint usage);
}
