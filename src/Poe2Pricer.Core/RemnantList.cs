using Tune = Poe2Pricer.Core.Tuning.Remnants;

namespace Poe2Pricer.Core;

// Picture: how the row looks; with it a row is known again whatever the OCR made of its text.
public sealed record RemnantEntry(int Count, string Text, RowPicture? Picture = null)
{
    public static RemnantEntry Of(RemnantRow row) => new(row.Count, row.Text, row.Picture);

    // The same reward: by the picture where both have one, else by the text, however the OCR
    // spaced or cased it.
    public bool Same(RemnantEntry other) =>
        Picture is not null && other.Picture is not null ? Picture.Same(other.Picture) : Count == other.Count && Key(Text) == Key(other.Text);

    private static string Key(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

// The whole reward list of a remnant as far as it has been seen: a long list shows only some
// rows at a time, and the best reward may lie in a part that is scrolled out of view (the top
// price is to hold for the whole list, and scrolling back must not forget the better one
// below). Two views of one list overlap; where the rows of a view fit the rows
// known so far, it is laid onto them. First, Shown: the rows in view now. Confirmed: the rows
// out of view are known to belong to this list, see Reopened. Immutable.
public sealed record RemnantList(IReadOnlyList<RemnantEntry> Rows, int First, int Shown, bool Confirmed = true)
{
    public static RemnantList Of(IReadOnlyList<RemnantRow> view) => new(view.Select(RemnantEntry.Of).ToArray(), 0, view.Count);

    // The list after scrolling to this view; null when the view does not belong to it. A view
    // that fits after scrolling confirms a list opened again — when a row outside the view
    // before matches a known row, not when the same rows are merely read again (the cursor
    // moving over the list changes its look and has the rows read anew at the same place).
    public RemnantList? Scrolled(IReadOnlyList<RemnantRow> view) => Laid(view, inside: false);

    // The list when it is opened again showing this view: every row of the view must be known;
    // null otherwise. Not confirmed: rows are told apart by their picture, and another remnant
    // may show the same rows ("1x Exalted Orb", "1x Chaos Orb", "1x Regal Orb" look the same
    // in every remnant), so a short remnant of such rows fits onto a long list seen before and
    // would inherit its arrow (OFFEN G12, seen 26.10.2026: six currency rows laid on rows 15–20
    // of the remnant before). The rows out of view count only once a scroll fits as well; a
    // remnant that cannot scroll never confirms. (The game opens a list at its top on the
    // controller UI and at the place it was closed on the keyboard UI, so the place of the
    // view tells nothing.)
    public RemnantList? Reopened(IReadOnlyList<RemnantRow> view) => Laid(view, inside: true);

    // The list as far as the labels may trust it: the whole list once confirmed, else only the
    // rows in view.
    public RemnantList? Trusted => Confirmed ? this : null;

    public bool InView(int row) => row >= First && row < First + Shown;

    private RemnantList? Laid(IReadOnlyList<RemnantRow> view, bool inside)
    {
        var seen = view.Select(RemnantEntry.Of).ToArray();
        // Each row of the view against each known row, compared once.
        var alike = new bool?[seen.Length, Rows.Count];
        bool Alike(int i, int k) => alike[i, k] ??= seen[i].Same(Rows[k]);
        (int At, int Same)? best = null;
        // at: where the view's first row lies in the known rows.
        for (int at = inside ? 0 : 1 - seen.Length; at <= (inside ? Rows.Count - seen.Length : Rows.Count - 1); at++)
        {
            int same = 0,
                other = 0;
            for (int i = 0; i < seen.Length; i++)
                if (at + i >= 0 && at + i < Rows.Count)
                    _ = Alike(i, at + i) ? same++ : other++;
            if (same < Tune.MinimumOverlap || other > Tune.MaximumMisread)
                continue;
            // The most rows in common; among equals the place nearest to the last view.
            if (best is not { } b || same > b.Same || same == b.Same && Math.Abs(at - First) < Math.Abs(b.At - First))
                best = (at, same);
        }
        if (best is not { } fit)
            return null;
        // Rows of the view that lie outside the view before and match: new evidence that the
        // list is the one known (Confirmed).
        int fresh = 0;
        for (int i = 0; i < seen.Length; i++)
            if (fit.At + i >= 0 && fit.At + i < Rows.Count && !InView(fit.At + i) && Alike(i, fit.At + i))
                fresh++;
        int start = Math.Min(0, fit.At),
            end = Math.Max(Rows.Count, fit.At + seen.Length);
        var rows = new RemnantEntry[end - start];
        for (int k = start; k < end; k++)
        {
            int i = k - fit.At;
            bool known = k >= 0 && k < Rows.Count,
                viewed = i >= 0 && i < seen.Length;
            // Read differently now and then: a row at the edge of a view may be cut off, one in
            // its middle is whole.
            bool edge = i == 0 || i == seen.Length - 1;
            rows[k - start] = !known || viewed && !edge ? seen[i] : Rows[k];
        }
        return new(rows, fit.At - start, seen.Length, Confirmed: !inside && (Confirmed || fresh > 0));
    }
}
