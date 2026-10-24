using System.Reflection;

namespace Poe2Pricer.Core;

public static class AppInfo
{
    // What changed in each version, on github.com; the version number in the window header
    // links to it (opens in the browser on a click, the program itself requests nothing).
    public const string ChangelogPage = "https://github.com/saxe78/poe2pricer/blob/main/CHANGELOG.md";

    // The repository: where a site operator who sees the program in a log finds its author.
    public const string Repository = "https://github.com/saxe78/poe2pricer";

    // How every request of the program introduces itself (poe.ninja, poecdn, the trade site,
    // GitHub, the rumour sources): the program, its version and the repository, the form
    // operators know ("+URL" for the contact). It proves nothing — anyone can send the same
    // line — it only says who is asking and where to turn.
    public static string UserAgent => $"poe2pricer/{Version} (+{Repository})";

    // The program's version: <Version> in Directory.Build.props. The build may append the
    // commit after a "+"; that part is not shown.
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    // Name and version as shown to the user: "poe2pricer v0.1.0".
    public static string NameAndVersion => $"poe2pricer v{Version}";
}
