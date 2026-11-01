using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCvSharp;

namespace Poe2Pricer.Core;

public record Box(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public Rect Rect() => new(X, Y, Width, Height);

    // The point lies in the box (its right and lower edge are outside).
    public bool Holds(int x, int y) => X <= x && x < Right && Y <= y && y < Bottom;

    // The smallest box that holds both.
    public Box Union(Box other)
    {
        int x = Math.Min(X, other.X),
            y = Math.Min(Y, other.Y);
        return new(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public bool Intersects(Box other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    // Overlap of at least `margin` pixels in both directions; a popup edge that only grazes a
    // cell does not hide it.
    public bool Overlaps(Box other, int margin) =>
        Math.Min(Right, other.Right) - Math.Max(X, other.X) >= margin
        && Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y) >= margin;
}

// An entry of data/catalog/catalog.json as tools/catalog_update.py writes it (the shape of
// Dev-Jahn/poe2-ritual-checker's catalogue, plus Art). Width and Height: the slot in cells.
// ImageSource: the picture's URL at GGG's image service; Art: the game's art path it encodes
// ("2DItems/Currency/Omens/VoodooOmens1Blue"), by which omens of one shape are grouped.
public record CatalogItem(
    string Id,
    string NameEn,
    string? NameKo,
    string Kind,
    string Image,
    int Width,
    int Height,
    string Source,
    string ImageSource,
    int? MaxStackSize,
    // The game's art path, for the omen groups (OmenGroups); "" in a catalogue written before
    // 30.10.2026, which then has no groups instead of failing to load (review 31.10.2026).
    string Art = ""
);

public record Catalog(string Version, DateTimeOffset RetrievedAt, CatalogItem[] Items);

public record Candidate(string Id, string Name, double Score);

public enum UiMode
{
    Controller,
    KeyboardMouse,
}

public record FavoursGrid(UiMode Mode, Box Bounds, double CellSize, double AnchorScore);

public record ItemMatch(
    int Column,
    int Row,
    int Columns,
    int Rows,
    Box Bounds,
    string? CatalogId,
    string Name,
    double Score,
    bool Uncertain,
    Candidate[] Candidates,
    // Greyed-out member of a shape group: the symbol cannot tell the members apart.
    string[]? Group = null,
    // How an uncertain item was settled, e.g. "tooltip".
    string? ResolvedBy = null
);

public record Occluder(string Kind, Box Bounds, double Score);

// Hidden: items that touch an occluder; their cells cannot be trusted and get no price.
public record Recognition(
    FavoursGrid? Grid,
    ItemMatch[] Items,
    int UnassignedCells,
    double ElapsedMs,
    Occluder[]? Occluders = null,
    ItemMatch[]? Hidden = null
)
{
    // Hidden: items under a popup, and uncertain items right next to one that belong to no
    // shape group: the uncovered rest of a partly covered unique.
    public Recognition WithoutOccluded(Occluder[] occluders)
    {
        bool Hide(ItemMatch i) =>
            occluders.Any(o =>
                o.Bounds.Overlaps(i.Bounds, Tuning.Occluders.HideOverlap)
                || i.Uncertain && i.Group is null && o.Bounds.Overlaps(i.Bounds with { X = i.Bounds.X - Tuning.Occluders.HideNeighbour, Y = i.Bounds.Y - Tuning.Occluders.HideNeighbour, Width = i.Bounds.Width + 2 * Tuning.Occluders.HideNeighbour, Height = i.Bounds.Height + 2 * Tuning.Occluders.HideNeighbour }, 1)
            );
        return this with
        {
            Items = Items.Where(i => !Hide(i)).ToArray(),
            Occluders = occluders,
            Hidden = Items.Where(Hide).ToArray(),
        };
    }
}

public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw Texts.Failure(m => new InvalidDataException(m), "FileEmpty", path);

    public static void Write<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
}
