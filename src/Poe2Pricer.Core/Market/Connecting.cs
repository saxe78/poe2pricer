using System.Net;
using System.Net.Sockets;

namespace Poe2Pricer.Core.Market;

// How the program's HTTP clients connect: every address of the host is tried, the next one
// Tuning.Control.ConnectStagger after the one before while that has not answered, and the
// first connection wins — the way browsers do it. .NET on its own tries the addresses one
// after the other and waits for Windows to give up on each, about 21 s for an address that
// never answers; with the request time-out of 20 s the second address was never reached.
// That was the "poe.ninja: not reachable … Timeout of 20 seconds" of two players (Reddit,
// 22.10.2026): a host with an IPv6 address first (poe.ninja, Cloudflare) on a PC whose IPv6
// route is dead. Measured 22.10.2026 with a dead first address: 20.0 s to the time-out as the
// handler does it, 0.5 s to the answer with the stagger.
public static class Connecting
{
    public static SocketsHttpHandler Handler() => new() { ConnectCallback = ConnectAsync };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        var socket = await ConnectAsync(Interleaved(addresses), context.DnsEndPoint.Port, Tuning.Control.ConnectStagger, token).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    // The addresses in their order, except that the second one is of the other family when
    // the host has both: a dead route of one family then costs one stagger, not a whole list.
    public static IPAddress[] Interleaved(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count < 3)
            return [.. addresses];
        var first = addresses[0];
        var other = addresses.FirstOrDefault(a => a.AddressFamily != first.AddressFamily);
        if (other is null || addresses[1].AddressFamily != first.AddressFamily)
            return [.. addresses];
        return [first, other, .. addresses.Where(a => !ReferenceEquals(a, first) && !ReferenceEquals(a, other))];
    }

    // A connected socket to the first address that answers; the next address is started
    // `stagger` after the one before while none has answered. All failed: the last failure.
    public static async Task<Socket> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, TimeSpan stagger, CancellationToken token)
    {
        if (addresses.Count == 0)
            throw new SocketException((int)SocketError.HostNotFound);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = new List<Task<Socket>>();
        Exception? failure = null;
        int next = 0;
        while (true)
        {
            // The next address: at the start, when the stagger is over, and right after an
            // attempt failed at once (refused, unreachable) — one per round.
            if (next < addresses.Count)
                running.Add(One(addresses[next++], port, cancel.Token));
            if (running.Count == 0)
                throw failure ?? new SocketException((int)SocketError.HostUnreachable);
            var waiting = next < addresses.Count ? Task.Delay(stagger, token) : Task.Delay(Timeout.Infinite, token);
            var done = await Task.WhenAny(running.Append<Task>(waiting)).ConfigureAwait(false);
            if (done is not Task<Socket> attempt)
            {
                token.ThrowIfCancellationRequested();
                continue; // the stagger is over: the next address joins
            }
            running.Remove(attempt);
            if (attempt.IsCompletedSuccessfully)
            {
                cancel.Cancel(); // the others may stop
                foreach (var other in running)
                    Discard(other);
                return attempt.Result;
            }
            failure = attempt.Exception?.InnerException ?? attempt.Exception;
            if (next >= addresses.Count && running.Count == 0)
                throw failure!;
        }
    }

    // A losing attempt: one that connected in the same instant is closed, a failure is taken
    // note of (no unobserved exception).
    private static void Discard(Task<Socket> attempt) =>
        attempt.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully)
                t.Result.Dispose();
            else
                _ = t.Exception;
        }, TaskContinuationOptions.ExecuteSynchronously);

    private static async Task<Socket> One(IPAddress address, int port, CancellationToken token)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
