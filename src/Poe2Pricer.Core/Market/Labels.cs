namespace Poe2Pricer.Core.Market;

public enum Tier { Low, Normal, High, Divine }

// What the overlay draws on one item. Mark "!" = greyed-out group omen not yet named,
// "?" = recognised but poe.ninja has no price.
// Row = a line label beside a list (orb left, number right), otherwise centred on an item.
// AlignRight: Bounds.Right is the right edge of the plate. Light: bright plate with dark text,
// for a label that lies on a (black) game tooltip. Note: a fixed text instead of a price, drawn
// without an orb (Labels.Fixed). Arrow: "▲" or "▼" before the price — the best reward of a
// list lies out of view in that direction.
public sealed record Label(
    Box Bounds,
    string? Text,
    string? Mark,
    string Unit,
    Tier Tier,
    bool MostValuable,
    bool Row = false,
    bool AlignRight = false,
    bool Light = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Note = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Arrow = null,
    // Frame: a class mark at Bounds and nothing else (the crowned rune of a remnant row: a dot
    // on the tile's bottom edge), named by the rune's class: "loot", "weaker", "danger",
    // "neutral" (RuneClass).
    // Caption: a plate with this text at Bounds (its height; the width follows the text, up to
    // Bounds' width), framed in the Frame colour — the effect of the row the controller's cursor
    // is on.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Frame = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Caption = null
);

public static class Labels
{
    // Colour tiers in divine, so they follow the exchange rate (as Dev-Jahn's colour scale
    // does): below 0.02 div grey, below 0.2 white, below 1 yellow, from 1 div orange,
    // which is also where the unit switches to div.
    public static Tier TierOf(decimal divine) =>
        divine < Tuning.Labels.Low ? Tier.Low
        : divine < Tuning.Labels.Normal ? Tier.Normal
        : divine < Tuning.Labels.DivineFrom ? Tier.High
        : Tier.Divine;

    // Remnant rewards: the value of the whole row (count × unit); "?" when poe.ninja does not
    // know the name. Controller: inside the row, top right beside the runes (the name sits in
    // the lower half, so there is always room). Keyboard: one line per row, no room for long
    // names → beside the window at a fixed place; where a game tooltip lies there, the plate
    // turns light instead of moving.
    public const int RowLabelWidth = Tuning.Labels.RowLabelWidth;

    // Remnant rewards that are no single item and have no market price get a fixed text, as
    // PoeAncientsPriceHelper shows them (its behaviour, not its code): the best that can come
    // out of them. They take no part in "most valuable" and the floor.
    // The German client: the note names the item as that client does ("Kopfjäger"). Its rows
    // read "Einzigartiger Gürtel" and "5x zufällige Währung" (the game's table of remnant
    // recipes, Expedition2Recipes).
    private static readonly (string Language, string[] Words, string Note)[] FixedNotes =
    [
        (GameLanguage.English, ["unique", "belt"], "Headhunter!"),
        (GameLanguage.English, ["random", "currency"], "5 Mirrors"),
        (GameLanguage.German, ["einzigartig", "gürtel"], "Kopfjäger!"),
        (GameLanguage.German, ["zufällig", "währung"], "5 Spiegel"),
    ];

    public static string? Fixed(string rowText, string language = GameLanguage.English)
    {
        var text = rowText.ToLowerInvariant();
        return FixedNotes.FirstOrDefault(f => f.Language == language && f.Words.All(text.Contains)).Note;
    }

    // Where the label of a row of the remnant list stands: in the list (controller) or beside
    // the window (keyboard and mouse), where a tooltip can lie on it.
    public static Box RowBox(RemnantRow row, RemnantWindow window)
    {
        bool controller = window.Mode == UiMode.Controller;
        int y = (int)Math.Round(controller ? row.CenterY - Tuning.Labels.ControllerRuneOffset : row.CenterY);
        int height = Tuning.Labels.RowLabelHeight;
        return controller
            ? new Box(window.List.Right - Tuning.Labels.RowInset - RowLabelWidth, y - height / 2, RowLabelWidth, height)
            : new Box(window.WindowRight + Tuning.Labels.RowGap, y - height / 2, RowLabelWidth, height);
    }

    // whole: the list as far as it has been seen (RemnantList). The top price holds for all of
    // it; when the best reward is scrolled out of view, nothing in view is green and an arrow
    // with its price stands at the list's upper or lower edge.
    public static Label[] ForRows(IEnumerable<RemnantRow> rows, RemnantWindow window, PriceBook prices, decimal floorDivine, IEnumerable<Box>? tooltips = null, RemnantList? whole = null, string language = GameLanguage.English, string unit = "ex")
    {
        var labels = new List<(Label Label, decimal Divine)>();
        var covers = (tooltips ?? []).ToArray();
        foreach (var row in rows)
        {
            bool controller = window.Mode == UiMode.Controller;
            var box = RowBox(row, window);
            bool light = covers.Any(t => t.Intersects(box));
            if (Fixed(row.Text, language) is { } note)
            {
                labels.Add((new(box, null, null, "ex", Tier.Normal, false, true, controller, light, note), -1));
                continue;
            }
            if (prices.Match(row.Text, language) is not { } m)
            {
                labels.Add((new(box, null, "?", "ex", Tier.Normal, false, true, controller, light), -1));
                continue;
            }
            var total = m.Price with { Divine = m.Price.Divine * row.Count };
            if (BelowFloor(total, floorDivine, unit))
                continue;
            labels.Add((new(box, total.TextIn(unit), null, total.UnitIn(unit), TierOf(total.Divine), false, true, controller, light), total.Divine));
        }
        // All items at the top price are green (several copies of one omen are common), also a
        // single one.
        decimal top = labels.Any(l => l.Divine >= 0) ? labels.Max(l => l.Divine) : -1;
        var unseen = Unseen(whole, prices, floorDivine, unit, language);
        var shown = labels.Select(l => l.Divine >= 0 && l.Divine == top && unseen.All(u => u.Price.Divine <= top) ? l.Label with { MostValuable = true } : l.Label).ToList();
        foreach (bool above in new[] { true, false })
        {
            var beyond = unseen.Where(u => u.Above == above && u.Price.Divine > top).ToArray();
            if (beyond.Length == 0 || unseen.Any(u => u.Above != above && u.Price.Divine > beyond.Max(b => b.Price.Divine)))
                continue;
            var best = beyond.MaxBy(b => b.Price.Divine).Price;
            int height = Tuning.Labels.RowLabelHeight;
            var box = new Box(window.List.X, above ? window.List.Y - height - Tuning.Labels.ArrowGap : window.List.Bottom + Tuning.Labels.ArrowGap, RowLabelWidth, height);
            shown.Add(new(box, best.TextIn(unit), null, best.UnitIn(unit), TierOf(best.Divine), true, true, Arrow: above ? "▲" : "▼"));
        }
        return shown.ToArray();
    }

    // The rewards of the list that are not in view now, each with the value of its row; those
    // without a market price or below the floor do not count, as in view.
    private static (bool Above, Price Price)[] Unseen(RemnantList? whole, PriceBook prices, decimal floorDivine, string unit, string language)
    {
        var unseen = new List<(bool, Price)>();
        for (int i = 0; whole is not null && i < whole.Rows.Count; i++)
        {
            var row = whole.Rows[i];
            if (whole.InView(i) || Fixed(row.Text, language) is not null || prices.Match(row.Text, language) is not { } m)
                continue;
            var total = m.Price with { Divine = m.Price.Divine * row.Count };
            if (!BelowFloor(total, floorDivine, unit))
                unseen.Add((i < whole.First, total));
        }
        return unseen.ToArray();
    }


    // Compared as shown: "not below 1 ex" must keep an Exalted Orb that poe.ninja prices at
    // 0.999 ex and the label shows as 1 — rounded in the unit the label is written in.
    public static bool BelowFloor(Price price, decimal floorDivine, string unit = "ex")
    {
        if (floorDivine <= 0)
            return false;
        return Shown(price.Divine) < Shown(floorDivine);
        decimal Shown(decimal divine) =>
            divine >= Tuning.Labels.DivineFrom ? Math.Round(divine, 2)
            : price.UnitIn(unit) == "chaos" ? Math.Round(divine * price.ChaosPerDivine, 2) / price.ChaosPerDivine
            : Math.Round(divine * price.ExaltedPerDivine, 1) / price.ExaltedPerDivine;
    }

    public static decimal FloorInDivine(decimal value, string unit, PriceBook prices) =>
        unit switch
        {
            "div" => value,
            "chaos" when prices.ChaosPerDivine > 0 => value / prices.ChaosPerDivine,
            _ => prices.ExaltedPerDivine > 0 ? value / prices.ExaltedPerDivine : 0,
        };

    // Stash tab: only what is there, unit price (no stack sums), "?" when poe.ninja does not
    // know the item, "<" below the floor (Favours alike; not in the remnant list, whose rows
    // have no item art). No green top price: nothing is chosen here, the labels only say what
    // the stash holds of value.
    public static Label[] ForStash(IEnumerable<StashSlot> slots, PriceBook prices, decimal floorDivine, string unit = "ex")
    {
        var labels = new List<Label>();
        // Unnamed: not in the table and not clearly recognised by symbol → nothing; "?" while
        // the item art it needs is still being fetched.
        foreach (var slot in slots.Where(s => s.Present && s.Name is null && s.NamedBy == StashIdentifier.Pending))
            labels.Add(new(slot.Bounds, null, "?", "ex", Tier.Normal, false));
        foreach (var slot in slots.Where(s => s.Present && s.Name is not null))
        {
            if (prices.Find(slot.Name!) is not { } price)
                labels.Add(new(slot.Bounds, null, "?", "ex", Tier.Normal, false));
            else if (BelowFloor(price, floorDivine, unit))
                labels.Add(new(slot.Bounds, null, "<", "ex", Tier.Low, false));
            else
                labels.Add(new(slot.Bounds, price.TextIn(unit), null, price.UnitIn(unit), TierOf(price.Divine), false));
        }
        return labels.ToArray();
    }

    public static Label[] For(IEnumerable<ItemMatch> items, PriceBook prices, decimal floorDivine, string unit = "ex")
    {
        var labels = new List<(Label Label, decimal Divine)>();
        foreach (var item in items)
        {
            if (item.Uncertain)
            {
                // No usable candidate (rares, magic items) gets nothing.
                if (item.Group is not null)
                    labels.Add((new(item.Bounds, null, "!", "ex", Tier.Normal, false), -1));
                continue;
            }
            var price = prices.Find(item.Name);
            if (price is null)
            {
                labels.Add((new(item.Bounds, null, "?", "ex", Tier.Normal, false), -1));
                continue;
            }
            // Below the floor: "<" (recognised, cheap); not competing for the top price.
            if (BelowFloor(price, floorDivine, unit))
            {
                labels.Add((new(item.Bounds, null, "<", "ex", Tier.Low, false), -1));
                continue;
            }
            labels.Add((new(item.Bounds, price.TextIn(unit), null, price.UnitIn(unit), TierOf(price.Divine), false), price.Divine));
        }
        // All items at the top price are green (several copies of one omen are common), also a
        // single one.
        decimal top = labels.Any(l => l.Divine >= 0) ? labels.Max(l => l.Divine) : -1;
        return labels.Select(l => l.Divine >= 0 && l.Divine == top ? l.Label with { MostValuable = true } : l.Label).ToArray();
    }
}
