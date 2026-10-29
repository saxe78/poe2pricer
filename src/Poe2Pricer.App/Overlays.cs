using System.Windows.Media;
using Poe2Pricer.Core;
using Poe2Pricer.Core.Market;

namespace Poe2Pricer.App;

// Which window draws the labels: the composition window (CompositionOverlay) for everyone,
// the WPF window (OverlayWindow) where that cannot be built or loses its device for good.
public static class Overlays
{
    public static IOverlay Create(ImageSource? divine, ImageSource? exalted, ImageSource? chaos)
    {
        try
        {
            var composition = new CompositionOverlay(divine, exalted, chaos);
            FileLog.Write("Overlay: composition window");
            return new Guarded(composition, () => new OverlayWindow(divine, exalted, chaos));
        }
        catch (Exception e)
        {
            FileLog.Write($"Overlay: composition window not available ({e.Message}), WPF window");
            return new OverlayWindow(divine, exalted, chaos);
        }
    }

    // The composition window until it fails (its device lost and not to be rebuilt); from then
    // on the WPF window, with the exclusion as it stands and the picture that failed.
    private sealed class Guarded(CompositionOverlay composition, Func<IOverlay> fallback) : IOverlay
    {
        private IOverlay inner = composition;
        private bool excluded = true;

        public void ExcludeFromCapture(bool exclude)
        {
            excluded = exclude;
            inner.ExcludeFromCapture(exclude);
        }

        public void Show(Box screen, Label[] labels, (Box Panel, RumourLine[] Lines, IslandSummary? Island)? rumours = null, double scale = 1)
        {
            try
            {
                inner.Show(screen, labels, rumours, scale);
            }
            catch (Exception e) when (inner is CompositionOverlay)
            {
                FallBack(e);
                inner.Show(screen, labels, rumours, scale);
            }
        }

        public void Clear()
        {
            try
            {
                inner.Clear();
            }
            catch (Exception e) when (inner is CompositionOverlay)
            {
                FallBack(e);
                inner.Clear();
            }
        }

        private void FallBack(Exception e)
        {
            FileLog.Write($"Overlay: composition window failed ({e.Message}), WPF window from here on");
            composition.Dispose();
            inner = fallback();
            inner.ExcludeFromCapture(excluded);
        }
    }
}
