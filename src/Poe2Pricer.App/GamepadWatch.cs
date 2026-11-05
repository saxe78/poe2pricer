using Windows.Gaming.Input;

namespace Poe2Pricer.App;

// Reads the gamepad's buttons through Windows.Gaming.Input — reading only, nothing is sent,
// nothing stored — for the one thing the picture cannot tell: whether the remnant list closed
// on the cancel button without a choice or on the confirm button with one (OverlayEngine.Escaped
// and Confirmed; Esc is read the same way). Every game controller Windows knows is asked every
// Every ms on a thread of its own, only while Active (the game in front). Which buttons: on an
// Xbox-class pad (Gamepad) B cancels and A confirms; on a Sony pad (DualShock, DualSense;
// vendor SonyVendor) the raw buttons SonyCancel (Circle) and SonyConfirm (Cross) — the author's
// DualSense Edge, 03.11.2026: Circle 2, Cross 1, by USB and by Bluetooth alike. XInput does
// not see a Sony pad the game drives itself (03.11.2026: no slot answered). Another make:
// not read, its layout is not known. Both buttons do other things in the game too (Circle is
// the dodge roll, 05.11.2026: up to fifty presses a minute), so a press is reported, not
// logged; the engine logs one while a remnant list is open. Readable: a pad with known
// buttons answered at the last poll — without one the program cannot tell a cancel from a
// choice, and the remnant list's row stays provisional. Error: the Windows API is not there
// (Windows 10 1703 and later).
public sealed class GamepadWatch : IDisposable
{
    private const int Every = 50;
    private const ushort SonyVendor = 0x054C;
    private const int SonyCancel = 2;
    private const int SonyConfirm = 1;
    private readonly Thread thread;
    private volatile bool stop;
    private int readable;

    public Func<bool> Active { get; set; } = () => true;
    public Action? Cancel { get; set; }
    public Action? Confirm { get; set; }
    // Told what is read (a pad came or went, with its make, buttons and cancel button).
    public Action<string>? Log { get; set; }
    public bool Readable => Volatile.Read(ref readable) != 0;
    public string? Error { get; private set; }

    public GamepadWatch()
    {
        thread = new Thread(Poll) { IsBackground = true, Name = "Gamepad watch" };
        thread.Start();
    }

    private void Poll()
    {
        // Per controller (by its object), which of its buttons were down at the last poll.
        var down = new Dictionary<RawGameController, Buttons>();
        string told = "";
        try
        {
            while (!stop)
            {
                bool any = false;
                var seen = new HashSet<RawGameController>();
                var found = new List<string>();
                foreach (var controller in RawGameController.RawGameControllers)
                {
                    Buttons? pressed = Pressed(controller, out string what);
                    found.Add(what);
                    if (pressed is not { } held)
                        continue;
                    any = true;
                    seen.Add(controller);
                    var was = down.TryGetValue(controller, out var d) ? d : default;
                    if (Active())
                    {
                        if (held.Cancel && !was.Cancel)
                            Cancel?.Invoke();
                        if (held.Confirm && !was.Confirm)
                            Confirm?.Invoke();
                    }
                    down[controller] = held;
                }
                foreach (var gone in down.Keys.Where(c => !seen.Contains(c)).ToArray())
                    down.Remove(gone);
                string now = found.Count == 0 ? "no game controller" : string.Join("; ", found);
                if (now != told)
                {
                    Log?.Invoke("Gamepad: " + now);
                    told = now;
                }
                Interlocked.Exchange(ref readable, any ? 1 : 0);
                Thread.Sleep(Every);
            }
        }
        catch (Exception e)
        {
            // The Windows API is not there (TypeLoad, PlatformNotSupported), or a pad failed in a
            // way Pressed does not catch: the watch ends, said once in the log, and the remnant
            // list's rows stay provisional for the run (an unhandled exception on this thread
            // would end the program; review 05.11.2026).
            Error = e.Message;
            Interlocked.Exchange(ref readable, 0);
            Log?.Invoke($"Gamepad: not read any more ({e.GetType().Name}: {e.Message})");
        }
    }

    private readonly record struct Buttons(bool Cancel, bool Confirm);

    // Whether the controller's cancel and confirm buttons are down; null: the controller is
    // not read (not answering right now, or of a make whose buttons are not known). What: the
    // controller as the log names it.
    private static Buttons? Pressed(RawGameController controller, out string what)
    {
        string link = controller.IsWireless ? "wireless" : "by cable";
        try
        {
            if (Gamepad.FromGameController(controller) is { } gamepad)
            {
                what = $"Xbox-class pad {link}, cancel = B, confirm = A";
                var held = gamepad.GetCurrentReading().Buttons;
                return new((held & GamepadButtons.B) != 0, (held & GamepadButtons.A) != 0);
            }
            what = $"vendor {controller.HardwareVendorId:X4} product {controller.HardwareProductId:X4}, {controller.ButtonCount} buttons, {link}";
            if (controller.HardwareVendorId != SonyVendor || controller.ButtonCount <= Math.Max(SonyCancel, SonyConfirm))
            {
                what += ", not read";
                return null;
            }
            what += $", cancel = button {SonyCancel}, confirm = button {SonyConfirm}";
            var buttons = new bool[controller.ButtonCount];
            controller.GetCurrentReading(buttons, new GameControllerSwitchPosition[controller.SwitchCount], new double[controller.AxisCount]);
            return new(buttons[SonyCancel], buttons[SonyConfirm]);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            what = $"{link}, not answering ({e.GetType().Name})";
            return null;
        }
    }

    public void Dispose() => stop = true;
}
