using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Poe2Pricer.Ui.ViewModels;

namespace Poe2Pricer.Ui.Views;

// The program window. It opens at start unless "im Infobereich starten" is set; closing only
// hides it, "Beenden" quits. Here stands only what cannot be bound: the size, the list rolling
// to a new row, the links.
public partial class MainWindow : Window
{
    // The symbol in the title bar is 36 high; twice that covers scalings up to 200 %.
    private const int TitleIconPixels = 64;

    // The guide (data/guide/README.html beside the program); without one, or when it is
    // missing, "Help" opens the README of the repository.
    private readonly string? guide;

    public MainWindow(MainViewModel model, string iconPath, string? guide = null)
    {
        this.guide = guide;
        // The view model first: bindings built with it take their values at once. Set after
        // InitializeComponent, the values came two turns of the dispatcher later — after the
        // window had taken its size from the content, too low by a box, and the START box was
        // cut off.
        DataContext = model;
        InitializeComponent();
        // Every size of the file goes to the window (Windows takes the small one for the title
        // of the taskbar entry, a large one for the switcher). The title bar shows the smallest
        // one that has the wreath of rays and is no smaller than the symbol at twice the scaling.
        if (File.Exists(iconPath))
        {
            var sizes = BitmapDecoder.Create(new Uri(iconPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
            Icon = sizes.MaxBy(f => f.PixelWidth);
            titleIcon.Source = sizes.Where(f => f.PixelWidth >= TitleIconPixels).MinBy(f => f.PixelWidth) ?? Icon;
        }
        PricerChrome.Attach(this, frame);
        model.Rumours.RowAdded += rumourScroll.ScrollToEnd;
        // "Browse…": the file dialog opens where the path in the field points.
        model.Settings.PickGameConfig = current =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Poe2Pricer.Core.Texts.Get("PickGameSettingsFile"),
                Filter = Poe2Pricer.Core.Texts.Get("GameSettingsFileFilter") + "|*.ini",
                FileName = Path.GetFileName(current),
                CheckFileExists = true,
            };
            if (Path.GetDirectoryName(current) is { Length: > 0 } folder && Directory.Exists(folder))
                dialog.InitialDirectory = folder;
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
        // Opens sized to the Settings tab, then keeps its size across tabs and can be resized;
        // the rumour list grows with it.
        Loaded += (_, _) =>
        {
            FixSize();
            // Sized to its content by Windows' count, the window adds the title bar and frame
            // it no longer has (an empty strip below the last box). With its own frame the
            // window is as large as what it shows: the size is taken from the content itself.
            frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            natural = frame.DesiredSize;
            double widthBefore = ActualWidth,
                heightBefore = ActualHeight;
            SizeToContent = SizeToContent.Manual;
            var area = SystemParameters.WorkArea;
            Fit(Room ?? area.Height);
            Left += (widthBefore - Width) / 2;
            Top = Math.Max(area.Top, Top + (heightBefore - Height) / 2);
            // A warning that comes or goes later takes or frees its lines: the window grows
            // and shrinks by them, the tabs keep their height.
            warnings.SizeChanged += (_, e) =>
            {
                // Not at every step of dragging the window wider.
                if (!e.HeightChanged)
                    return;
                natural = new Size(natural.Width, natural.Height + e.NewSize.Height - e.PreviousSize.Height);
                Fit(Room ?? SystemParameters.WorkArea.Height);
            };
        };
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }

    // The height the window may take; none: the work area of the screen. For tests, which
    // cannot bring a lower screen along.
    public double? Room { get; init; }

    // What the content asks for without a limit: the size the window opens at and cannot be
    // made smaller than — unless the screen is lower (Fitted).
    private Size natural;

    // The least size Fit gave the window last; none before the first time.
    private Size? fitted;

    // The Settings tab rolls (the screen is lower than the content), and: the window is being
    // laid out anew because Fit itself has just changed its width.
    private bool rolls,
        ownWidth;

    // The window at the size of its content, keeping what the user has added by dragging.
    // What was added is counted against the least size set here before, not against MinWidth
    // and MinHeight: at the first call those are 0 and Width is already the width the window
    // opened at, so the whole window counted as added and it opened twice as wide and as high
    // as the screen.
    private void Fit(double room)
    {
        var (wider, higher) = fitted is { } before ? (Math.Max(0, Width - before.Width), Math.Max(0, Height - before.Height)) : (0, 0);
        // A warning that breaks just at the width of the window fits one line once the window
        // is wider by the bar. Rolling is not left on that account: the window would get
        // narrower, the warning break again, and so on for ever (the window flickers until
        // Windows ends the process).
        rolls = natural.Height > room || (ownWidth && rolls);
        var least = Fitted(natural, room, rolls);
        double width = least.Width + wider;
        if (width != Width)
        {
            ownWidth = true;
            Dispatcher.BeginInvoke(() => ownWidth = false, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        (MinWidth, MinHeight) = (0, 0);
        Width = width;
        Height = Math.Max(least.Height, Math.Min(room, least.Height + higher));
        (MinWidth, MinHeight) = (least.Width, least.Height);
        fitted = least;
        // Grown downwards by a warning, the foot with "Exit" and "Save" must not leave the
        // screen: the window moves up as far as needed (S3). Only on the screen this is
        // counted for; on another one the window stays where it is.
        var area = SystemParameters.WorkArea;
        if (area.Contains(new Point(Left, Top)) && Top + Height > area.Bottom)
            Top = Math.Max(area.Top, area.Bottom - Height);
    }

    // The smallest size of the window on a screen with this much room in height. With room
    // enough it is the size of the content. On a lower screen (1920×1080 at 150 %: 672 against
    // 706, more with warnings) it is as high as the screen: the Settings tab rolls, the
    // rumour list gives way, and the window is wider by the bar the Settings tab then shows.
    // Otherwise "Exit" and "Save" would lie below the screen there, and the window could not
    // be made smaller.
    // Not only a question: it also sets the least heights of the two tabs to match.
    public Size Fitted(Size content, double room) => Fitted(content, room, content.Height > room);

    private Size Fitted(Size content, double room, bool rolling)
    {
        FixSize();
        if (!rolling)
            return content;
        expedition.MinHeight = Math.Max(0, expedition.MinHeight - Math.Max(0, content.Height - room));
        // The bar is as wide as Windows makes its bars: the theme asks for 10, but a bar has a
        // least width of its own (17 here) that the theme does not touch.
        return new Size(content.Width + SystemParameters.VerticalScrollBarWidth, room);
    }

    // The Settings tab sets the smallest size; switching tabs keeps the window's size.
    // The pages are at least this high: the height the Settings tab had with its League box
    // (1.2.0), kept when the box went (23.10.2026) — the room below the last box takes the
    // status line and a warning, and the Waystones tab with a profile fits without rolling.
    private const double LeastPageHeight = 609;

    public void FixSize()
    {
        general.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        foreach (var page in new FrameworkElement[] { general, expedition })
        {
            // DesiredSize includes the page's own margin; MinWidth/MinHeight must not, or the
            // margin counts twice (an extra gap below the last box).
            page.MinWidth = Math.Max(general.DesiredSize.Width - general.Margin.Left - general.Margin.Right, 640);
            page.MinHeight = Math.Max(general.DesiredSize.Height - general.Margin.Top - general.Margin.Bottom, LeastPageHeight);
        }
    }

    // "Copy" in the menu of the status line or of a warning: the text of the line it was
    // opened on. A clipboard another program holds just now is no reason to end.
    private void CopyText(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem { Parent: System.Windows.Controls.ContextMenu { PlacementTarget: System.Windows.Controls.TextBlock line } } || line.Text.Length == 0)
            return;
        ToClipboard(line.Text);
    }

    // Where "Copy" puts the text; tests take it instead of the clipboard of the user.
    public Action<string> ToClipboard { get; set; } = text =>
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    };

    private void OpenLink(object sender, RequestNavigateEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });

    private void StatusClicked(object sender, RoutedEventArgs e) =>
        ((MainViewModel)DataContext).StatusClicked(link => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }));

    public const string OnlineGuide = "https://github.com/saxe78/poe2pricer#readme";

    private void OpenGuide(object sender, RoutedEventArgs e)
    {
        // The guide opens in the program's language: the language goes as
        // "?lang=", which the browser gets only from a file URL, not from a bare path.
        string target = guide is not null && System.IO.File.Exists(guide) ? new Uri(guide).AbsoluteUri + "?lang=" + Poe2Pricer.Core.Texts.Language : OnlineGuide;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        // No browser, or the association refused: the place of the guide goes to the status
        // line, to be opened by hand.
        catch (System.ComponentModel.Win32Exception)
        {
            ((MainViewModel)DataContext).Report(new(target));
        }
    }
}
