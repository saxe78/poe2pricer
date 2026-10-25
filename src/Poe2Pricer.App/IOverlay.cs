using Poe2Pricer.Core;
using Poe2Pricer.Core.Market;

namespace Poe2Pricer.App;

// The window the price labels are drawn in, over the game. Program talks to it through this
// interface only: the labels are drawn by WPF (OverlayWindow) or, so that Windows 10 leaves
// them out of the screen captures the game is read from (issue #6), by a window of our own
// outside WPF (CompositionOverlay). All calls come from the UI thread except
// ExcludeFromCapture, which may come from the capture thread.
public interface IOverlay
{
    // Keep the window out of screen captures or let them see it. Takes effect at once; before
    // the window exists, when it is created.
    void ExcludeFromCapture(bool exclude);

    // Draw these labels over the game's part of the screen. scale: screen pixels per pixel of
    // the labels' coordinates (the game runs at another height than the readers are measured at).
    void Show(Box screen, Label[] labels, (Box Panel, RumourLine[] Lines, IslandSummary? Island)? rumours = null, double scale = 1);

    // Nothing to show: the window stays and draws nothing.
    void Clear();
}
