using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Poe2Pricer.Core;
using Poe2Pricer.Core.Market;

namespace Poe2Pricer.App;

// Transparent, click-through, never focused, always on top. Left out of screen captures
// (SetWindowDisplayAffinity 0x11) while the game is read from the screen, so the tool never
// sees its own labels — which also keeps the labels out of every recording, and makes the
// NVIDIA app refuse to record the desktop at all; with the game read from its own window
// (Settings.CaptureGameWindow) the flag is off. Window styles as in
// Dev-Jahn/poe2-ritual-checker Overlay.cs (MIT).
public sealed class OverlayWindow : Window
{
    private readonly LabelLayer layer = new();
    private bool excluded = true;
    private nint hwnd;

    public OverlayWindow(ImageSource? divine, ImageSource? exalted, ImageSource? chaos)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        Content = layer;
        layer.Divine = divine;
        layer.Exalted = exalted;
        layer.Chaos = chaos;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
            SetWindowLongPtrW(hwnd, -20, GetWindowLongPtrW(hwnd, -20) | 0x08000000 | 0x20 | 0x80);
            this.hwnd = hwnd;
            Affinity(excluded);
        };
    }

    // Windows accepts the exclusion or not: Windows 10 before 2004 does not know 0x11, and a
    // refusal leaves the labels in every screen capture, our own included (issue #6: prices
    // that flicker every second). Either way in the log, once per change.
    private void Affinity(bool exclude)
    {
        if (SetWindowDisplayAffinity(hwnd, exclude ? 0x11u : 0u))
            FileLog.Write(exclude ? "Overlay: excluded from screen captures" : "Overlay: visible to screen captures (game window read)");
        else
            FileLog.Write($"Overlay: SetWindowDisplayAffinity({(exclude ? "exclude" : "none")}) refused, error {Marshal.GetLastWin32Error()}");
    }

    // Keep the window out of screen captures (0x11, WDA_EXCLUDEFROMCAPTURE) or let them see
    // it. Takes effect at once; before the window exists, when it is created. Any thread: the
    // flag is a plain field and SetWindowDisplayAffinity takes the handle.
    public void ExcludeFromCapture(bool exclude)
    {
        if (excluded == exclude)
            return;
        excluded = exclude;
        if (hwnd != 0)
            Affinity(exclude);
    }

    // scale: screen pixels per pixel of the labels' coordinates (the game runs at another
    // height than the readers are measured at).
    public void Show(Box screen, Label[] labels, (Box Panel, RumourLine[] Lines, IslandSummary? Island)? rumours = null, double scale = 1)
    {
        layer.Rumours = rumours;
        if (!IsVisible)
            Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, -1, screen.X, screen.Y, screen.Width, screen.Height, 0x10);
        layer.PixelScale = scale / VisualTreeHelper.GetDpi(this).DpiScaleX;
        layer.Labels = labels;
        layer.InvalidateVisual();
    }

    // Nothing to show: the window stays and draws nothing. A hidden window keeps its last
    // picture, and Windows shows that picture the moment the window is shown again, before the
    // new labels are drawn — the old prices of the last window would flash up on the next one.
    public void Clear()
    {
        layer.Rumours = null;
        layer.Labels = [];
        layer.InvalidateVisual();
    }

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
}
