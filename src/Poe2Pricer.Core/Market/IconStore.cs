using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace Poe2Pricer.Core.Market;

// Item art from poe.ninja (GGG material: fetched at run time and kept on disk, not shipped).
// Stored under %LOCALAPPDATA%\poe2pricer\icons\<name>.png, loaded once per run.
public sealed partial class IconStore : IDisposable
{
    private readonly string folder;
    private readonly HttpClient client;
    private readonly ConcurrentDictionary<string, Mat?> loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim fetching = new(1, 1);

    // fetch: false for the golden-master check and the tests — only what is on disk counts,
    // nothing is downloaded into the frozen data.
    private readonly bool fetch;

    // handler: for tests, answers instead of the image server.
    public IconStore(string? folder = null, bool fetch = true, HttpMessageHandler? handler = null)
    {
        this.folder = folder ?? Path.Combine(Settings.Folder, "icons");
        this.fetch = fetch;
        client = new HttpClient(handler ?? Connecting.Handler()) { BaseAddress = new Uri("https://web.poecdn.com"), Timeout = Tuning.Control.RequestTimeout, MaxResponseContentBufferSize = Tuning.Control.ResponseLimit };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex Unsafe();

    private string FileOf(string name) => Path.Combine(folder, Unsafe().Replace(name, "_") + ".png");

    public bool Has(string name) => loaded.TryGetValue(name, out var m) ? m is not null : File.Exists(FileOf(name));

    // BGRA as poe.ninja delivers it; null when not on disk.
    public Mat? Get(string name)
    {
        if (loaded.TryGetValue(name, out var there))
            return there;
        // One at a time: two callers at once would each read the file, and one picture would
        // be left over.
        lock (reading)
            return loaded.GetOrAdd(name, Read);
    }

    private readonly Lock reading = new();

    private Mat? Read(string n)
    {
        var file = FileOf(n);
        if (!File.Exists(file))
            return null;
        var m = Decode(file);
        if (m is null)
        {
            // A file that is there but no picture of the kind (cut off, no alpha): not
            // waited for in this run, and removed so that the next run fetches it anew —
            // otherwise every free stash slot would stay "icons loading" in every run.
            tried[n] = true;
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return null;
        }
        return m;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> PngEnd => [0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82]; // "IEND" and its CRC, the last bytes of every whole PNG

    // The art comes from the network, and this is the one place where such data meets a
    // parser. It is decoded by the Windows Imaging Component (the decoder of the Explorer and
    // the browsers, patched with Windows), not by the one OpenCV ships, and only after the
    // file's own header has passed: a PNG with an alpha channel, no larger than IconMaxEdge a
    // side — a header naming a huge picture is refused before any decoder sets memory aside
    // for it. A cut-off file is refused by its end (the decoder would read what is there and
    // fill the rest, and the half picture would stay for good). null: not such a picture.
    // BGRA with straight alpha, as OpenCV read it before (StashIdentifier takes the alpha as
    // the mask).
    private static Mat? Decode(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            Span<byte> header = stackalloc byte[26];
            if (stream.Read(header) != header.Length || !header[..8].SequenceEqual(PngSignature) || !header[12..16].SequenceEqual("IHDR"u8))
                return null;
            int width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            int height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            bool alpha = (header[25] & 4) != 0; // colour type 4 (grey + alpha) or 6 (RGBA)
            if (width <= 0 || height <= 0 || width > Tuning.Control.IconMaxEdge || height > Tuning.Control.IconMaxEdge || !alpha)
                return null;
            Span<byte> end = stackalloc byte[8];
            if (stream.Length < header.Length + end.Length)
                return null;
            stream.Seek(-end.Length, SeekOrigin.End);
            if (stream.Read(end) != end.Length || !end.SequenceEqual(PngEnd))
                return null;
            stream.Position = 0;
            var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.PixelWidth != width || frame.PixelHeight != height)
                return null;
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);
            var m = new Mat(height, width, MatType.CV_8UC4);
            Marshal.Copy(pixels, 0, m.Data, pixels.Length);
            return m;
        }
        catch (Exception e) when (e is FileFormatException or NotSupportedException or InvalidOperationException or COMException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Tried in this run: an image poe.ninja cannot deliver is not waited for again.
    private readonly ConcurrentDictionary<string, bool> tried = new(StringComparer.OrdinalIgnoreCase);

    // Without fetching nothing is waited for: what is not on disk will not come.
    public bool Tried(string name) => !fetch || tried.ContainsKey(name);

    // While fetching: images done and to do (for the status line).
    public event Action<int, int>? Loading;

    // A fetch brought images that were not there before: what was told without them (a stash
    // slot named by comparing with the art there was) is to be told anew.
    public event Action? Arrived;

    // Fetches what is missing, four at a time; one run at a time. Failures are skipped (the
    // slot then stays without a label) and tried again with the next call.
    public async Task EnsureAsync(IEnumerable<(string Name, string Path)> items, CancellationToken token = default)
    {
        var missing = items.Where(i => !File.Exists(FileOf(i.Name))).DistinctBy(i => i.Name).ToArray();
        if (missing.Length == 0)
            return;
        if (!fetch)
            return;
        await fetching.WaitAsync(token);
        int done = 0,
            arrived = 0;
        try
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // No folder, no art: nothing is waited for (else stash slots stay "?").
                foreach (var item in missing)
                    tried[item.Name] = true;
                return;
            }
            await Parallel.ForEachAsync(
                missing,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (item, t) =>
                {
                    // Every item counts as tried and done, whatever happens to it (code review:
                    // one unexpected error left the rest waiting, and the count never finished).
                    try
                    {
                        var file = FileOf(item.Name);
                        if (File.Exists(file))
                            return; // fetched meanwhile by an overlapping run
                        // The path is poe.ninja's word; only art below the image server is
                        // fetched. An absolute address in the answer would take the request
                        // anywhere (HttpClient prefers it to the base), so it counts as none.
                        if (!Uri.TryCreate(client.BaseAddress, item.Path, out var address) || address.Scheme != Uri.UriSchemeHttps || address.Host != client.BaseAddress!.Host)
                            return;
                        var bytes = await client.GetByteArrayAsync(address, t);
                        await File.WriteAllBytesAsync(file + ".part", bytes, t);
                        File.Move(file + ".part", file, overwrite: true);
                        // Under the lock of Get: a reader that has just seen "no file"
                        // stores that first and it is removed here, not the other way round —
                        // else the image would count as missing for the rest of the run.
                        lock (reading)
                            loaded.TryRemove(item.Name, out _);
                        Interlocked.Increment(ref arrived);
                    }
                    catch (Exception) when (!t.IsCancellationRequested) { }
                    finally
                    {
                        tried[item.Name] = true;
                        Loading?.Invoke(Interlocked.Increment(ref done), missing.Length);
                    }
                }
            );
        }
        finally
        {
            fetching.Release();
        }
        if (arrived > 0)
            Arrived?.Invoke();
    }

    public void Dispose()
    {
        client.Dispose();
        foreach (var m in loaded.Values)
            m?.Dispose();
    }
}
