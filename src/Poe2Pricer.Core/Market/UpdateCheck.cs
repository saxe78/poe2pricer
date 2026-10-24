using System.Net.Http;
using System.Text.Json;

namespace Poe2Pricer.Core.Market;

// "Check for updates": the newest release of the repository, asked for once per start while
// the setting is on. The program has no installer and updates nothing by itself: a newer
// version is only told, with the page to get it from. The request carries the program's name
// and version as its user agent and nothing else.
public sealed class UpdateCheck : IDisposable
{
    public const string Repository = "saxe78/poe2pricer";
    public const string RepositoryPage = "https://github.com/" + Repository;
    public const string ReleasesPage = RepositoryPage + "/releases";

    private readonly HttpClient client;

    // handler: the tests answer the request themselves.
    public UpdateCheck(HttpMessageHandler? handler = null)
    {
        client = new HttpClient(handler ?? Connecting.Handler()) { BaseAddress = new Uri("https://api.github.com"), Timeout = Tuning.Control.RequestTimeout, MaxResponseContentBufferSize = Tuning.Control.ResponseLimit };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public sealed record Release(string Version, string Page);

    // The newest release when it is newer than the running version; null when it is not. What
    // the request may throw (no network, GitHub refusing) is the caller's to log.
    public async Task<Release?> NewerAsync(CancellationToken cancel = default)
    {
        using var response = await client.GetAsync($"/repos/{Repository}/releases/latest", cancel);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        string tag = document.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        // The page is opened in the browser by a click: only one below the repository is
        // taken from the answer, anything else (another site, another scheme) is the releases page.
        string page = document.RootElement.TryGetProperty("html_url", out var u) && u.GetString() is { } url && url.StartsWith(RepositoryPage + "/", StringComparison.Ordinal) ? url : ReleasesPage;
        return Newer(AppInfo.Version, tag) ? new Release(VersionOf(tag), page) : null;
    }

    // Whether the tag ("v1.2.0", "1.2") names a version newer than the running one ("1.0.0"):
    // by the numbers, part by part, missing parts as 0. Anything that is not numbers is not newer.
    public static bool Newer(string current, string tag)
    {
        if (Parts(current) is not { } now || Parts(tag) is not { } other)
            return false;
        for (int i = 0; i < now.Length; i++)
            if (other[i] != now[i])
                return other[i] > now[i];
        return false;
    }

    // "v1.2.0" → "1.2.0"; a suffix after "-" or "+" (pre-release, build) is not part of it.
    public static string VersionOf(string tag) => tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];

    private static int[]? Parts(string version)
    {
        var parts = VersionOf(version).Split('.');
        if (parts.Length is 0 or > 4)
            return null;
        var numbers = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out numbers[i]) || numbers[i] < 0)
                return null;
        return numbers;
    }

    public void Dispose() => client.Dispose();
}
