using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Poe2Pricer.Core;
using Poe2Pricer.Core.Market;

namespace Poe2Pricer.App;

// The labels drawn by a window outside WPF: a Win32 window without a redirection surface
// (WS_EX_NOREDIRECTIONBITMAP) whose only content is a Direct3D swap chain that
// DirectComposition composes onto the screen. WPF's transparent windows (AllowsTransparency,
// drawn with UpdateLayeredWindow) stay in screen captures on Windows 10 although
// SetWindowDisplayAffinity accepted the exclusion (Microsoft: by design, changed in Windows 11
// 24H2); the tool then read its own labels from the screen and hid them every second (issue
// #6). This window is left out of captures on Windows 10 22H2 and Windows 11 alike, transparent
// where nothing is drawn, and lets clicks through (measured 24./25.10.2026 with
// capture-check-3, variant J: with the layered style as well, as the WPF window had it). The
// labels are still drawn by LabelLayer, into a bitmap on the CPU, which is copied into the swap
// chain's back buffer for every picture. Direct3D, DXGI and DirectComposition are reached
// through their vtables, as in WindowCapture (no interop package). Show and Clear come from the
// UI thread, whose message loop (WPF's) serves this window too; ExcludeFromCapture from any.
public sealed unsafe class CompositionOverlay : IOverlay, IDisposable
{
    private readonly LabelLayer layer = new();
    private readonly nint hwnd;
    private bool visible;
    private bool excluded = true;

    // Direct3D device and context, its DXGI face, the composition device with the window's
    // target and the one visual; all of them rebuilt when the device is lost.
    private nint d3d, context, dxgi, dcomp, target, visual;
    // The swap chain in the size of the game's part of the screen, and the picture drawn for it.
    private nint swapChain;
    private int width, height;
    private RenderTargetBitmap? bitmap;
    private byte[] pixels = [];

    public CompositionOverlay(ImageSource? divine, ImageSource? exalted, ImageSource? chaos)
    {
        layer.Divine = divine;
        layer.Exalted = exalted;
        layer.Chaos = chaos;
        hwnd = CreateWindow();
        try
        {
            Affinity(excluded);
            Build();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static bool registered;

    // WS_POPUP, never activated, no taskbar entry, on top, clicks pass (WS_EX_TRANSPARENT and
    // HTTRANSPARENT), no redirection surface, layered at full opacity.
    private static nint CreateWindow()
    {
        if (!registered)
        {
            fixed (char* name = ClassName)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProc,
                    hInstance = GetModuleHandleW(0),
                    lpszClassName = name,
                };
                if (RegisterClassExW(&wc) == 0)
                    throw new InvalidOperationException($"RegisterClassEx failed, error {Marshal.GetLastWin32Error()}");
            }
            registered = true;
        }
        const uint WS_POPUP = 0x80000000;
        const uint ex = 0x8 /* TOPMOST */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */ | 0x20 /* TRANSPARENT */ | 0x200000 /* NOREDIRECTIONBITMAP */ | 0x80000 /* LAYERED */;
        nint hwnd = CreateWindowExW(ex, ClassName, "poe2pricer overlay", WS_POPUP, 0, 0, 1, 1, 0, 0, GetModuleHandleW(0), 0);
        if (hwnd == 0)
            throw new InvalidOperationException($"CreateWindowEx failed, error {Marshal.GetLastWin32Error()}");
        if (!SetLayeredWindowAttributes(hwnd, 0, 255, 2 /* LWA_ALPHA */))
            FileLog.Write($"Overlay: SetLayeredWindowAttributes refused, error {Marshal.GetLastWin32Error()}");
        return hwnd;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == 0x84 /* WM_NCHITTEST */)
            return -1; // HTTRANSPARENT: the click goes to the window below
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // Device (hardware, else WARP), composition device, the window's target and its root visual.
    private void Build()
    {
        int hr = D3D11CreateDevice(0, 1 /* D3D_DRIVER_TYPE_HARDWARE */, 0, 0x20 /* BGRA_SUPPORT */, 0, 0, 7 /* D3D11_SDK_VERSION */, out d3d, out _, out context);
        if (hr < 0)
        {
            FileLog.Write($"Overlay: no Direct3D hardware device (HRESULT 0x{hr:X8}), software (WARP)");
            Check(D3D11CreateDevice(0, 5 /* D3D_DRIVER_TYPE_WARP */, 0, 0x20, 0, 0, 7, out d3d, out _, out context), "D3D11CreateDevice");
        }
        var iidDxgi = IID_IDXGIDevice;
        Check(Marshal.QueryInterface(d3d, in iidDxgi, out dxgi), "IDXGIDevice");
        var iidDcomp = IID_IDCompositionDevice;
        Check(DCompositionCreateDevice(dxgi, &iidDcomp, out dcomp), "DCompositionCreateDevice");
        nint created;
        var createTarget = (delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Slot(dcomp, 6);
        Check(createTarget(dcomp, hwnd, 1 /* topmost */, &created), "CreateTargetForHwnd");
        target = created;
        var createVisual = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dcomp, 7);
        Check(createVisual(dcomp, &created), "CreateVisual");
        visual = created;
        var setRoot = (delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(target, 3);
        Check(setRoot(target, visual), "SetRoot");
    }

    private void Teardown()
    {
        Release(ref visual);
        Release(ref target);
        Release(ref swapChain);
        Release(ref dcomp);
        Release(ref dxgi);
        Release(ref context);
        Release(ref d3d);
        width = height = 0;
    }

    private static void Release(ref nint com)
    {
        if (com != 0)
            Marshal.Release(com);
        com = 0;
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} failed, HRESULT 0x{hr:X8}");
    }

    private void Affinity(bool exclude)
    {
        if (SetWindowDisplayAffinity(hwnd, exclude ? 0x11u : 0u))
            FileLog.Write(exclude ? "Overlay: excluded from screen captures" : "Overlay: visible to screen captures (game window read)");
        else
            FileLog.Write($"Overlay: SetWindowDisplayAffinity({(exclude ? "exclude" : "none")}) refused, error {Marshal.GetLastWin32Error()}");
    }

    // 0x11 is WDA_EXCLUDEFROMCAPTURE. Any thread: the flag is a plain field and
    // SetWindowDisplayAffinity takes the handle.
    public void ExcludeFromCapture(bool exclude)
    {
        if (excluded == exclude)
            return;
        excluded = exclude;
        Affinity(exclude);
    }

    public void Show(Box screen, Label[] labels, (Box Panel, RumourLine[] Lines, IslandSummary? Island)? rumours = null, double scale = 1)
    {
        layer.Rumours = rumours;
        layer.Labels = labels;
        layer.PixelScale = scale;
        if (!visible)
        {
            ShowWindow(hwnd, 8 /* SW_SHOWNA */);
            visible = true;
        }
        SetWindowPos(hwnd, -1 /* HWND_TOPMOST */, screen.X, screen.Y, screen.Width, screen.Height, 0x10 /* SWP_NOACTIVATE */);
        Present(screen.Width, screen.Height, draw: true);
    }

    // The window stays and shows a fully transparent picture (as the WPF window keeps drawing
    // nothing): a hidden window would show its last picture the moment it is shown again.
    public void Clear()
    {
        layer.Rumours = null;
        layer.Labels = [];
        if (swapChain != 0)
            Present(width, height, draw: false);
    }

    // A lost device (driver update, GPU reset: DXGI_ERROR_DEVICE_REMOVED and the like) is built
    // anew once for this picture; when that fails as well the exception reaches the caller,
    // which falls back to the WPF window (Overlays). Any other failure (the swap chain cannot
    // be made on this machine, say) gets the same one rebuild, named as what it is: the log is
    // what a report brings, and "lost its device" would point at a GPU reset that never was.
    private void Present(int w, int h, bool draw)
    {
        if (w <= 0 || h <= 0)
            return;
        int hr = TryPresent(w, h, draw, out string step);
        if (hr >= 0)
            return;
        string what = DeviceLost(hr) ? "lost its device" : $"failed at {step}";
        FileLog.Write($"Overlay: composition window {what} (HRESULT 0x{hr:X8}), built anew");
        Teardown();
        Build();
        hr = TryPresent(w, h, draw, out step);
        if (hr < 0)
            throw new InvalidOperationException($"composition window {(DeviceLost(hr) ? "lost its device" : "failed at " + step)} again, HRESULT 0x{hr:X8}");
    }

    // DXGI_ERROR_DEVICE_REMOVED, DEVICE_HUNG, DEVICE_RESET, DRIVER_INTERNAL_ERROR.
    private static bool DeviceLost(int hr) => hr is unchecked((int)0x887A0005) or unchecked((int)0x887A0006) or unchecked((int)0x887A0007) or unchecked((int)0x887A0020);

    private int TryPresent(int w, int h, bool draw, out string step)
    {
        step = "swap chain";
        int hr = Fit(w, h);
        if (hr < 0)
            return hr;
        step = "back buffer";
        Render(w, h, draw);
        var iidTexture = IID_ID3D11Texture2D;
        var getBuffer = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(swapChain, 9);
        nint back;
        hr = getBuffer(swapChain, 0, &iidTexture, &back);
        if (hr < 0)
            return hr;
        try
        {
            var updateSubresource = (delegate* unmanaged[Stdcall]<nint, nint, uint, nint, byte*, uint, uint, void>)Slot(context, 48);
            fixed (byte* p = pixels)
                updateSubresource(context, back, 0, 0, p, (uint)(w * 4), 0);
        }
        finally
        {
            Marshal.Release(back);
        }
        step = "present";
        var present = (delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(swapChain, 8);
        // Sync interval 1: the call waits for the next vertical blank on the UI thread, up to
        // 17 ms at 60 Hz, most of the 16–22 ms measured per picture at 3840×2160 (25.10.2026).
        // Interval 0 would return at once (the compositor shows the picture with its own
        // vertical sync); kept for after 1.4.0, the path as it is was confirmed on the
        // reporter's Windows 10 (OFFEN Z11).
        return present(swapChain, 1, 0);
    }

    // The swap chain in this size: created with the first picture (and hung into the visual
    // tree, committed once), resized after.
    private int Fit(int w, int h)
    {
        if (swapChain != 0 && w == width && h == height)
            return 0;
        int hr;
        if (swapChain == 0)
        {
            nint adapter, factory, created;
            var getAdapter = (delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7);
            if ((hr = getAdapter(dxgi, &adapter)) < 0)
                return hr;
            var iidFactory = IID_IDXGIFactory2;
            var getParent = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(adapter, 6);
            hr = getParent(adapter, &iidFactory, &factory);
            Marshal.Release(adapter);
            if (hr < 0)
                return hr;
            var desc = new SWAP_CHAIN_DESC1
            {
                Width = (uint)w, Height = (uint)h, Format = 87 /* DXGI_FORMAT_B8G8R8A8_UNORM */, SampleCount = 1,
                BufferUsage = 0x20 /* DXGI_USAGE_RENDER_TARGET_OUTPUT */, BufferCount = 2, Scaling = 0 /* STRETCH */,
                SwapEffect = 3 /* FLIP_SEQUENTIAL */, AlphaMode = 1 /* PREMULTIPLIED */,
            };
            var createSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, SWAP_CHAIN_DESC1*, nint, nint*, int>)Slot(factory, 24);
            hr = createSwapChain(factory, d3d, &desc, 0, &created);
            Marshal.Release(factory);
            if (hr < 0)
                return hr;
            swapChain = created;
            var setContent = (delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(visual, 15);
            if ((hr = setContent(visual, swapChain)) < 0)
                return hr;
            var commit = (delegate* unmanaged[Stdcall]<nint, int>)Slot(dcomp, 3);
            if ((hr = commit(dcomp)) < 0)
                return hr;
        }
        else
        {
            var resizeBuffers = (delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, int>)Slot(swapChain, 13);
            if ((hr = resizeBuffers(swapChain, 2, (uint)w, (uint)h, 87, 0)) < 0)
                return hr;
        }
        width = w;
        height = h;
        bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        pixels = new byte[w * h * 4];
        return 0;
    }

    // The labels into `pixels`, premultiplied BGRA as the swap chain wants it. At 96 dpi one
    // unit of the layer is one pixel, so PixelScale is the labels' scale alone (the WPF window
    // divides by the monitor's scale). InvalidateVisual makes Arrange draw again.
    private void Render(int w, int h, bool draw)
    {
        if (!draw)
        {
            Array.Clear(pixels);
            return;
        }
        layer.InvalidateVisual();
        layer.Measure(new Size(w, h));
        layer.Arrange(new Rect(0, 0, w, h));
        bitmap!.Clear();
        bitmap.Render(layer);
        bitmap.CopyPixels(pixels, w * 4, 0);
    }

    public void Dispose()
    {
        Teardown();
        if (hwnd != 0)
            DestroyWindow(hwnd);
    }

    private static nint Slot(nint com, int index) => (*(nint**)com)[index];

    private const string ClassName = "Poe2PricerOverlay";
    private static readonly Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IID_IDXGIFactory2 = new("50C83A1C-E072-4C48-87B0-3630FA36A6D0");
    private static readonly Guid IID_IDCompositionDevice = new("C37EA93A-E7AA-450D-B16F-9746CB0407F3");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW { public uint cbSize, style; public nint lpfnWndProc; public int cbClsExtra, cbWndExtra; public nint hInstance, hIcon, hCursor, hbrBackground; public char* lpszMenuName; public char* lpszClassName; public nint hIconSm; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SWAP_CHAIN_DESC1 { public uint Width, Height, Format; public int Stereo; public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags; }

    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(nint name);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint featureLevels, uint levels, uint sdkVersion, out nint device, out int featureLevel, out nint context);
    [DllImport("dcomp.dll")] private static extern int DCompositionCreateDevice(nint dxgiDevice, Guid* iid, out nint device);
}
