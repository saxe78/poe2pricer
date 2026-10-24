using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Poe2Pricer.Core.Waystones;

namespace Poe2Pricer.Core.Market;

// The trade site of Path of Exile 2 (pathofexile.com/trade2), asked for the cheapest
// waystones like a given one: a search (POST, the query as JSON) returns the ids of the
// matching listings, a fetch (GET, up to ten ids) their prices. Undocumented and merely
// tolerated: the site states its limits in every answer (X-Rate-Limit-Ip "5:10:60" = 5
// requests in 10 s, else blocked 60 s), and this client keeps under them, waiting its turn
// when it has to and giving up after MaxWait. Prices come in the currency the seller named
// (exalted, chaos, divine) and are reckoned in divine with poe.ninja's rates (the book at
// hand). An answer is cached for CacheFor by its query: the same waystone hovered twice asks
// once.
public sealed class TradeClient : ITradePrices, IDisposable
{
    public const string Host = "https://www.pathofexile.com";
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(12);

    // After a failed question (not a 429, which states its own wait): the site is not asked
    // for this long, twice as long after each further failure in a row, at most DownMax; an
    // answer starts again from Down. On 21.10.2026 the site answered every search with 405
    // for an hour, and the tooltip asked every 3 s while it stood (Exiled Exchange asks again
    // only on a click).
    public static readonly TimeSpan Down = TimeSpan.FromSeconds(60), DownMax = TimeSpan.FromMinutes(10);

    // Fetched together: the site takes up to ten ids per fetch.
    public const int Listings = 10;

    // Which listings count, in the site's own status values (checked 21.10.2026, one tablet
    // search: online 2, securable 732, available 734, any 750). Instant Buyout ("securable"):
    // what Ange sells from merchant tabs, bought at once even while the seller is offline —
    // the market, and what the game's own search shows first. With InPerson the listings of
    // premium stash tabs whose seller is online come in too ("available"). Never "any":
    // offline sellers' stash listings are often months old and sold long ago, and they pulled
    // prices far down (the 1 ex bulk that hid an 11 ex waystone, 21.10.).
    public const string InstantBuyout = "securable", InstantAndInPerson = "available";

    // The switch "incl. premium stash (online)" in the settings, DISPLAY, under the live switch.
    public Func<bool> InPerson { get; set; } = () => false;

    private readonly HttpClient client;
    private readonly TimeProvider clock;
    private readonly Func<PriceBook?> rates;
    private readonly Action<string> log;
    private readonly RateLimit search = new(), fetch = new();
    private readonly Dictionary<string, (DateTimeOffset At, TradeAnswer Answer)> cache = [];
    private readonly SemaphoreSlim one = new(1, 1);
    private int failures;
    private DateTimeOffset downUntil;
    private string downWhy = "";

    // The site's currency ids by their names ("vaal" → "Vaal Orb"), from /data/static once:
    // sellers ask for waystones in Vaal Orbs as often as in Exalted, and a listing in a
    // currency not reckoned would be skipped (20.10.: 1 Vaal = 11 ex, the site's cheapest).
    private Dictionary<string, string>? currencies;

    public TradeClient(Func<PriceBook?> rates, Action<string>? log = null, HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        this.rates = rates;
        this.log = log ?? (_ => { });
        this.clock = clock ?? TimeProvider.System;
        client = new HttpClient(handler ?? Connecting.Handler()) { BaseAddress = new Uri(Host), Timeout = Tuning.Control.RequestTimeout, MaxResponseContentBufferSize = Tuning.Control.ResponseLimit };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public Task<TradeAnswer> ForAsync(WaystoneReading reading, WaystoneProfile? profile, int tolerance, string league, CancellationToken token = default) =>
        PriceAsync(TradeQuery.ForReading(reading, profile, tolerance), league, token);

    public Task<TradeAnswer> ForProfileAsync(WaystoneProfile profile, string league, CancellationToken token = default) =>
        PriceAsync(TradeQuery.ForProfile(profile), league, token);

    public Task<TradeAnswer> ForAsync(Tablets.TabletReading reading, Tablets.TabletProfile? profile, int tolerance, string league, CancellationToken token = default) =>
        PriceAsync(Tablets.TabletTradeQuery.ForReading(reading, profile, tolerance), league, token);

    // null: the profile names no kind of tablet.
    public Task<TradeAnswer> ForProfileAsync(Tablets.TabletProfile profile, string league, CancellationToken token = default) =>
        Tablets.TabletTradeQuery.ForProfile(profile) is { } query ? PriceAsync(query, league, token) : Task.FromResult(TradeAnswer.NoListing);

    // A price, no listing, or not asked (no rates yet, the site's limit would take longer than
    // MaxWait to pass, the site refused, or it failed and is left alone for a while: Down). Only
    // a cancellation is thrown. The listings that
    // count: Instant Buyout, with InPerson also sellers online in person.
    public async Task<TradeAnswer> PriceAsync(JsonObject query, string league, CancellationToken token = default)
    {
        query = (JsonObject)query.DeepClone();
        query["query"]!["status"] = new JsonObject { ["option"] = InPerson() ? InstantAndInPerson : InstantBuyout };
        string key = league + "\n" + query.ToJsonString();
        await one.WaitAsync(token);
        try
        {
            var now = clock.GetUtcNow();
            if (cache.TryGetValue(key, out var hit) && now - hit.At < CacheFor)
                return hit.Answer;
            // Answers past their time go; the cache would otherwise keep every waystone ever read.
            foreach (var old in cache.Where(c => now - c.Value.At >= CacheFor).Select(c => c.Key).ToArray())
                cache.Remove(old);
            if (now < downUntil)
                return new(null, NotAsked.Unreachable, downUntil - now, downWhy);
            TradeAnswer answer;
            bool complete;
            try
            {
                (answer, _, complete) = await AskAsync(query, league, token);
            }
            catch (Exception e) when (IsFault(e) && !token.IsCancellationRequested)
            {
                // A refusal of this one question (400 and the like: a stat id the site no longer
                // knows after a patch) leaves the site alone for this question only; the site
                // itself failing (5xx, 405, the network, a time-out, a page that is no answer)
                // pauses every question (review before 1.2.0: one stale id paused all prices).
                if (e is HttpRequestException { StatusCode: { } code } && (int)code is >= 400 and < 500 and not 405)
                {
                    log($"trade: question refused ({(int)code}: {e.Message})");
                    return new(null, NotAsked.Unreachable, Down, ((int)code).ToString());
                }
                failures++;
                var pause = TimeSpan.FromTicks(Math.Min(DownMax.Ticks, Down.Ticks << Math.Min(failures - 1, 10)));
                downWhy = Why(e);
                downUntil = clock.GetUtcNow() + pause;
                log($"trade: not reachable ({downWhy}: {e.Message}), not asked for {pause.TotalSeconds:0} s");
                return new(null, NotAsked.Unreachable, pause, downWhy);
            }
            // Only an answer of the site ends a run of failures, and only an answer priced with
            // every currency is kept: a limit not passed, a refusal, missing rates, or listings
            // in a currency whose rate was not to be had are asked again next time.
            if (answer.Asked)
                failures = 0;
            if (answer.Asked && complete)
                cache[key] = (clock.GetUtcNow(), answer);
            return answer;
        }
        finally
        {
            one.Release();
        }
    }

    // The answer (asked: the site said what there is, listings or none; else why not) and
    // how many listings the search found.
    // Complete: every listing could be reckoned (the site's currency list was there).
    private async Task<(TradeAnswer Answer, int Total, bool Complete)> AskAsync(JsonObject query, string league, CancellationToken token)
    {
        if (rates() is not { } book)
        {
            log("trade: no rates yet (poe.ninja not loaded), not asked");
            return (new(null, NotAsked.NoRates), 0, true);
        }
        if (await search.WaitAsync(clock, MaxWait, token) is { } searchWait)
        {
            log($"trade: search limit, not asked (free in {searchWait.TotalSeconds:0} s)");
            return (new(null, NotAsked.Limit, searchWait), 0, true);
        }
        using var searched = await client.PostAsJsonAsync($"/api/trade2/search/poe2/{Uri.EscapeDataString(league)}", query, token);
        search.Learn(searched.Headers, clock);
        if (searched.StatusCode == HttpStatusCode.TooManyRequests)
        {
            log("trade: too many requests (search)");
            return (new(null, NotAsked.Refused, search.Blocked(clock)), 0, true);
        }
        searched.EnsureSuccessStatusCode();
        var found = await searched.Content.ReadFromJsonAsync<JsonObject>(token) ?? throw new InvalidDataException("trade: empty search answer");
        int total = found["total"]?.GetValue<int>() ?? 0;
        var ids = found["result"]?.AsArray().OfType<JsonNode>().Select(n => n.GetValue<string>()).Take(Listings).ToArray() ?? [];
        string id = found["id"]?.GetValue<string>() ?? "";
        log($"trade: {total} listings for {TradeEnvelope.Describe(query)}");
        if (ids.Length == 0)
            return (TradeAnswer.NoListing, total, true);
        if (await fetch.WaitAsync(clock, MaxWait, token) is { } fetchWait)
        {
            log($"trade: fetch limit, not asked (free in {fetchWait.TotalSeconds:0} s)");
            return (new(null, NotAsked.Limit, fetchWait), total, true);
        }
        using var fetched = await client.GetAsync($"/api/trade2/fetch/{string.Join(",", ids)}?query={Uri.EscapeDataString(id)}&realm=poe2", token);
        fetch.Learn(fetched.Headers, clock);
        if (fetched.StatusCode == HttpStatusCode.TooManyRequests)
        {
            log("trade: too many requests (fetch)");
            return (new(null, NotAsked.Refused, fetch.Blocked(clock)), total, true);
        }
        fetched.EnsureSuccessStatusCode();
        var listings = await fetched.Content.ReadFromJsonAsync<JsonObject>(token) ?? throw new InvalidDataException("trade: empty fetch answer");
        var names = await CurrenciesAsync(token);
        bool complete = names is not null;
        names ??= [];
        var prices = new List<decimal>();
        var asked = new List<string>();
        foreach (var listing in listings["result"]?.AsArray() ?? [])
        {
            var price = listing?["listing"]?["price"];
            if (price?["amount"]?.GetValue<decimal>() is not { } amount || price["currency"]?.GetValue<string>() is not { } currency)
                continue;
            var divine = InDivine(book, names, currency, amount);
            asked.Add($"{amount:0.##} {currency}" + (divine is null ? " (no rate)" : ""));
            if (divine is { } d)
                prices.Add(d);
        }
        log("trade: listings " + string.Join(", ", asked));
        if (prices.Count == 0)
        {
            log("trade: no listing in a known currency");
            return (TradeAnswer.NoListing, total, complete);
        }
        prices.Sort();
        // The cheapest of a few is often a price fixer: with four or more, the second counts.
        decimal taken = prices.Count >= 4 ? prices[1] : prices[0];
        return (new(new TradePrice(taken, total, clock.GetUtcNow(), TradeEnvelope.Describe(query))), total, complete);
    }

    // What asking the site can throw and the caller logs instead of falling over: the
    // network, a refusal, an answer that is not the JSON expected (a maintenance page, a
    // changed API).
    // A short word for the status line: the status code, or what kind of failure.
    private static string Why(Exception e) => e switch
    {
        HttpRequestException { StatusCode: { } code } => ((int)code).ToString(),
        TaskCanceledException => "timeout",
        System.Text.Json.JsonException or InvalidDataException => "no JSON",
        _ => "network",
    };

    public static bool IsFault(Exception e) =>
        e is TradeNotAskedException or HttpRequestException or TaskCanceledException or IOException or InvalidDataException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException or OverflowException;

    private static decimal? InDivine(PriceBook book, Dictionary<string, string> names, string currency, decimal amount) =>
        currency switch
        {
            "divine" => amount,
            "exalted" when book.ExaltedPerDivine > 0 => amount / book.ExaltedPerDivine,
            "chaos" when book.ChaosPerDivine > 0 => amount / book.ChaosPerDivine,
            _ => names.TryGetValue(currency, out var name) && book.Find(name) is { Divine: > 0 } rate ? amount * rate.Divine : null,
        };

    // null: the list could not be had this time (a listing in a currency other than exalted,
    // chaos or divine then has no rate; the answer is not kept). A cancellation is thrown.
    private async Task<Dictionary<string, string>?> CurrenciesAsync(CancellationToken token)
    {
        if (currencies is not null)
            return currencies;
        var found = new Dictionary<string, string>();
        try
        {
            using var answer = await client.GetAsync("/api/trade2/data/static", token);
            answer.EnsureSuccessStatusCode();
            var data = await answer.Content.ReadFromJsonAsync<JsonObject>(token);
            foreach (var group in data?["result"]?.AsArray() ?? [])
                foreach (var entry in group?["entries"]?.AsArray() ?? [])
                    if (entry?["id"]?.GetValue<string>() is { Length: > 0 } id && entry["text"]?.GetValue<string>() is { Length: > 0 } text)
                        found[id] = text;
        }
        catch (Exception e) when (IsFault(e) && !token.IsCancellationRequested)
        {
            log("trade: currency list not read: " + e.Message);
            return null;
        }
        return currencies = found;
    }

    public void Dispose()
    {
        client.Dispose();
        one.Dispose();
    }

    // One of the site's limits per request kind, as its headers state it: rules of
    // "max:period:penalty", and our own count of requests per period. Until the first answer
    // the rules are the ones seen on 20.10.2026 (search 5:10, fetch 12:4), narrowed to the
    // tightest.
    private sealed class RateLimit
    {
        private readonly List<(int Max, TimeSpan Period)> rules = [(3, TimeSpan.FromSeconds(10))];
        private readonly List<DateTimeOffset> sent = [];
        private DateTimeOffset blockedUntil = DateTimeOffset.MinValue;

        // null: waited (if need be) and counted, the request may go. A time: the limit would
        // take longer than maxWait to pass, the request is not to go; that long until it may.
        public async Task<TimeSpan?> WaitAsync(TimeProvider clock, TimeSpan maxWait, CancellationToken token)
        {
            var now = clock.GetUtcNow();
            var until = Due(now);
            if (until - now > maxWait)
                return until - now;
            if (until > now)
                await Task.Delay(until - now, clock, token);
            sent.Add(clock.GetUtcNow());
            return null;
        }

        // How long the site's own block (Retry-After) still lasts; none given: a short pause.
        // Without a Retry-After the short pause is a block of its own: the next question waits
        // for it too, not only the one refused (review before 1.2.0).
        public TimeSpan Blocked(TimeProvider clock)
        {
            var now = clock.GetUtcNow();
            if (blockedUntil <= now)
                blockedUntil = now + TimeSpan.FromSeconds(10);
            return blockedUntil - now;
        }

        // When the next request may go: after the block, and when within every rule's period
        // fewer than its maximum were sent (one in hand for what the site counted differently).
        // What is older than the longest period no rule counts any more and is dropped.
        private DateTimeOffset Due(DateTimeOffset now)
        {
            var longest = rules.Max(r => r.Period);
            sent.RemoveAll(t => now - t >= longest);
            var due = blockedUntil;
            foreach (var (max, period) in rules)
            {
                var within = sent.Where(t => now - t < period).OrderBy(t => t).ToArray();
                if (within.Length >= Math.Max(1, max - 1))
                    due = Max(due, within[^Math.Max(1, max - 1)] + period);
            }
            return Max(due, now);
        }

        public void Learn(System.Net.Http.Headers.HttpResponseHeaders headers, TimeProvider clock)
        {
            if (headers.TryGetValues("X-Rate-Limit-Ip", out var values))
            {
                var parsed = new List<(int, TimeSpan)>();
                foreach (var rule in string.Join(",", values).Split(','))
                {
                    var parts = rule.Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int max) && int.TryParse(parts[1], out int seconds))
                        parsed.Add((max, TimeSpan.FromSeconds(seconds)));
                }
                if (parsed.Count > 0)
                {
                    rules.Clear();
                    rules.AddRange(parsed);
                }
            }
            if (headers.RetryAfter?.Delta is { } delta)
                blockedUntil = clock.GetUtcNow() + delta;
        }

        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    }
}
