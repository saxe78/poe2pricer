using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Poe2Pricer.Core.Market;

// Prices from poe.ninja (PoE 2 economy API, endpoints as used by Dev-Jahn/poe2-ritual-checker).
// Each category is cached on disk with its ETag. A category older than MaxAge is checked
// again; an unchanged one costs a 304 answer. Requests are spaced one second apart.
public sealed class NinjaClient : IDisposable
{
    public static readonly TimeSpan MaxAge = Tuning.Control.PricesMaxAge;
    private readonly TimeSpan spacing;

    // Exchange categories that hold catalog items; Ritual first, it carries the omens.
    // Abyss and Breach: bones and catalysts of the special stash tabs.
    public static readonly string[] ExchangeTypes =
    [
        "Ritual",
        "Currency",
        "Fragments",
        "Essences",
        "SoulCores",
        "Idols",
        "Runes",
        "Delirium",
        "Verisium",
        "Expedition",
        "Abyss",
        "Breach",
        // Uncut and lineage support gems per level ("Uncut Spirit Gem (Level 19)"): remnant
        // rewards.
        "UncutGems",
        "LineageSupportGems",
    ];

    public static readonly string[] UniqueTypes =
    [
        "UniqueArmours",
        "UniqueWeapons",
        "UniqueAccessories",
        "UniqueJewels",
        "UniqueFlasks",
        "UniqueCharms",
        "UniqueSanctumRelics",
        "UniqueTablets",
        "PrecursorTablets",
    ];

    private readonly HttpClient client;
    private readonly string cacheDirectory;
    private DateTimeOffset nextRequest;

    // handler, spacing: for tests, answers instead of poe.ninja and no pause between requests.
    public NinjaClient(string? cacheDirectory = null, HttpMessageHandler? handler = null, TimeSpan? spacing = null)
    {
        this.spacing = spacing ?? Tuning.Control.RequestSpacing;
        this.cacheDirectory =
            cacheDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "poe2pricer", "ninja");
        client = new HttpClient(handler ?? Connecting.Handler()) { BaseAddress = new Uri("https://poe.ninja"), Timeout = Tuning.Control.RequestTimeout, MaxResponseContentBufferSize = Tuning.Control.ResponseLimit };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
    }

    public async Task<string[]> LeaguesAsync(CancellationToken token = default)
    {
        var json = await GetAsync("leagues", "/poe2/api/economy/leagues", n => n is JsonArray, token);
        return json.AsArray().Select(l => l!["name"]!.GetValue<string>()).ToArray();
    }

    // The current softcore league: the first one that is neither hardcore nor permanent.
    // Between two leagues poe.ninja may list the permanent ones only: then "Standard", and
    // neither the load nor the league list of the window may fail on it.
    public static string DefaultLeague(IEnumerable<string> leagues) =>
        leagues.FirstOrDefault(l => !l.StartsWith("HC ") && l is not ("Standard" or "Hardcore")) ?? "Standard";

    // fresh: ask poe.ninja also for categories younger than MaxAge ("Preise neu laden").
    public async Task<PriceBook> PricesAsync(string league, CancellationToken token = default, bool fresh = false)
    {
        var book = new PriceBook(league);
        foreach (var type in ExchangeTypes)
        {
            var json = await GetAsync(
                Path.Combine(league, type),
                $"/poe2/api/economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={type}",
                Prices,
                token,
                fresh
            );
            book.AddExchange(json, type);
        }
        foreach (var type in UniqueTypes)
        {
            var json = await GetAsync(
                Path.Combine(league, type),
                $"/poe2/api/economy/stash/current/item/overview?league={Uri.EscapeDataString(league)}&type={type}",
                Prices,
                token,
                fresh
            );
            book.AddUniques(json, type);
        }
        // Every category empty: poe.ninja lists the league but has no prices for it.
        if (book.Count == 0)
            throw Texts.Failure(m => new InvalidDataException(m), "NinjaNoPrices", league);
        return book;
    }

    private sealed record CacheEntry(DateTimeOffset FetchedAt, string? ETag, string Body);

    // A cache file that cannot be read (cut off by a crash) counts as missing
    // and is fetched again; it no longer blocks its category until deleted by hand.
    private static async Task<CacheEntry?> ReadCacheAsync(string file, CancellationToken token)
    {
        if (!File.Exists(file))
            return null;
        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(file, token));
            if (entry?.Body is null)
                return null;
            JsonNode.Parse(entry.Body);
            return entry;
        }
        catch (JsonException)
        {
            return null;
        }
        // Held by another program just now (a virus scanner, a second run): as good as
        // missing, the answer of poe.ninja is taken instead.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Written next to the file and then moved over it: a crash leaves the old file or the new
    // one, never half of it.
    private static async Task WriteCacheAsync(string file, CacheEntry entry, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var part = $"{file}.{Guid.NewGuid():N}.part"; // a cancelled load may still be writing
        try
        {
            await File.WriteAllTextAsync(part, JsonSerializer.Serialize(entry), token);
            File.Move(part, file, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(part); // a cancelled load leaves nothing behind (code review)
            }
            catch (IOException) { }
            throw;
        }
    }

    // What an answer with prices looks like, also one for a category without any: an object
    // with the list "lines" (so in every answer seen).
    private static bool Prices(JsonNode answer) => answer is JsonObject && answer["lines"] is JsonArray;

    // shaped: what the caller can read. An answer that is JSON but something else (an error
    // object with status 200) is as unreadable as one that is no JSON.
    private async Task<JsonNode> GetAsync(string key, string url, Func<JsonNode, bool> shaped, CancellationToken token, bool again = false)
    {
        var file = Path.Combine(cacheDirectory, key + ".json");
        var cached = await ReadCacheAsync(file, token);
        if (cached is not null && !again && DateTimeOffset.UtcNow - cached.FetchedAt < MaxAge)
            return JsonNode.Parse(cached.Body)!;

        var wait = nextRequest - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait, token);
        nextRequest = DateTimeOffset.UtcNow + spacing;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cached?.ETag is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            // Offline or poe.ninja down: an old price is better than none.
            return cached is not null ? JsonNode.Parse(cached.Body)! : throw Texts.Failure(m => new IOException(m, e), "NinjaNotReachable", url);
        }
        using (response)
        {
            CacheEntry fresh;
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                fresh = cached with { FetchedAt = DateTimeOffset.UtcNow };
            else if (response.IsSuccessStatusCode)
                fresh = new(
                    DateTimeOffset.UtcNow,
                    response.Headers.ETag?.ToString(),
                    await response.Content.ReadAsStringAsync(token)
                );
            else if (cached is not null)
                return JsonNode.Parse(cached.Body)!;
            else
                throw Texts.Failure(m => new IOException(m), "NinjaHttp", (int)response.StatusCode, url);
            // Read before it is kept: an answer that is no JSON (the page of a proxy or of a
            // login portal, with status 200) must not replace the good copy — the old prices
            // are better than none.
            JsonNode? read;
            try
            {
                read = JsonNode.Parse(fresh.Body);
            }
            catch (JsonException)
            {
                read = null;
            }
            if (read is null || !shaped(read))
                return cached is not null ? JsonNode.Parse(cached.Body)! : throw Texts.Failure(m => new IOException(m), "NinjaUnreadable", url);
            // The copy for the next time; the answer counts whether or not it can be written.
            try
            {
                await WriteCacheAsync(file, fresh, token);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return read;
        }
    }

    public void Dispose() => client.Dispose();
}
