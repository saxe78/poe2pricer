using OpenCvSharp;
using Poe2Pricer.Core.Market;

namespace Poe2Pricer.Core;

// One full look at a frame: window, items, greyed-out omens, popups over the grid, tooltip
// names and cursor, in that order. Shared by the command line and the overlay.
public sealed class Pipeline : IDisposable
{
    public FavoursLocator Locator { get; }
    public Recognizer Recognizer { get; }
    public OmenGroups Groups { get; }
    public RarityCorners Corners { get; }
    public OccluderDetector Occluders { get; }
    public SelectionDetector Selection { get; }
    public TitleReader Titles { get; }
    public NumberGlyphs Glyphs { get; }
    public RemnantReader Remnants { get; }
    public RumourReader Rumours { get; }
    public StashTabReader Stash { get; }

    // The runes of Expedition remnants (data/runes beside data/ui): the crowned tile of a
    // remnant row and its rune.
    public Runes.RuneBook Runes { get; }

    // Item art from poe.ninja for naming stash slots; fetched by the caller (EnsureAsync).
    public IconStore Icons { get; }
    public StashIdentifier Identifier { get; }

    // icons: the item art store; the golden-master check and the tests pass one that never fetches.
    // rumourShapes: the pictures of rumour lines named before (the overlay keeps them in the
    // user's folder); null: none are kept.
    public Pipeline(string dataDirectory, string uiDirectory, IconStore? icons = null, RumourShapes? rumourShapes = null)
    {
        Locator = new FavoursLocator(uiDirectory);
        Recognizer = new Recognizer(dataDirectory, useLearnedReferences: true);
        Groups = new OmenGroups(Recognizer.Catalog);
        Corners = new RarityCorners(uiDirectory, Recognizer.Catalog);
        Occluders = new OccluderDetector(uiDirectory);
        Selection = new SelectionDetector(uiDirectory);
        Titles = new TitleReader();
        Glyphs = new NumberGlyphs(uiDirectory);
        Remnants = new RemnantReader(uiDirectory, Titles);
        Rumours = new RumourReader(uiDirectory, Titles, rumourShapes);
        Stash = new StashTabReader(uiDirectory);
        Runes = new Runes.RuneBook(Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(uiDirectory)) ?? uiDirectory, "runes"));
        Icons = icons ?? new IconStore();
        Identifier = new StashIdentifier(Icons);
    }

    // Keyboard UI: game tooltips in the band beside the remnant list, where its labels lie.
    // The band grows with the popup size: the game opens the tooltip that much further from the
    // window (measured: the left end moves from 766 at popup size 0.80 to 789 at 1.29, the
    // window's edge at 776).
    // Item tooltips beside the list (keyboard and mouse) and the game's rune tooltip inside the
    // list box (both UI modes; RemnantReader.RuneTooltips).
    public Box[] RemnantTooltips(Mat frame, RemnantWindow window, double popup = GameConfig.StandardPopup) =>
        [
            .. window.Mode == UiMode.KeyboardMouse
                ? Occluders.TooltipsAt(frame, window.WindowRight - RemnantReader.Band(Tuning.Remnants.TooltipBandLeft, popup), window.WindowRight + RemnantReader.Band(Tuning.Remnants.TooltipBandRight, popup), popup)
                : [],
            .. RemnantReader.RuneTooltips(frame, window, popup),
        ];

    // Title bars of item tooltips anywhere on the frame but its lowest part (the bottom holds
    // the hotbar and the search box), as the waystone window looks for them.
    public Box[] WaystoneTooltips(Mat frame, double popup = GameConfig.StandardPopup) =>
        Occluders.TooltipsOver(frame, new Box(0, 0, frame.Width, (int)(frame.Height * Tuning.Waystones.SearchHeight)), popup);

    public Recognition Scan(Mat frame)
    {
        var grid = Locator.Locate(frame);
        if (grid is null)
            return new Recognition(null, [], 0, 0);
        return Scan(frame, grid);
    }

    // The grid is known already: the window stays where it was read (FavoursGameWindow).
    public Recognition Scan(Mat frame, FavoursGrid grid) => Corners.Unmask(frame, Groups.MarkGreyed(frame, Recognizer.Recognize(frame, grid)));

    public void Dispose()
    {
        Locator.Dispose();
        Occluders.Dispose();
        Selection.Dispose();
        Glyphs.Dispose();
        Remnants.Dispose();
        Rumours.Dispose();
        Runes.Dispose();
        Icons.Dispose();
    }
}
