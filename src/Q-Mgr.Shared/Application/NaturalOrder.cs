using System.Globalization;

namespace QMgr.Application;

/// <summary>
/// Sorting a list of names the way a person reads them, so <c>S2C</c> comes before <c>S10A</c>.
///
/// <para><b>Why this exists rather than <c>OrderBy(x =&gt; x)</c>.</b> Every list this product sorts
/// is full of names with numbers inside them — classes (S1A, S2C, S10B), streams, rooms, counters,
/// service types, dormitories. An ordinal sort orders those by digit, so a school with ten forms
/// gets S1, S10, S2, S3 — which reads as broken, and reads as broken most obviously on exactly the
/// list a school builds first.</para>
///
/// <para>The rule: a run of digits compares as a NUMBER, everything else compares as text, case and
/// accents ignored. <c>StringComparison.OrdinalIgnoreCase</c> is deliberately not used for the text
/// runs — a Ugandan school writing <c>Ssemwogerere</c> beside <c>Ssénkindu</c> wants them adjacent,
/// which needs the culture-aware comparison.</para>
///
/// <para>ONE home. Do not write a second "sort these names properly" helper next to the code that
/// needs one; that is this codebase's most-repeated bug.</para>
/// </summary>
public static class NaturalOrder
{
    /// <summary>A ready-made comparer, for <c>OrderBy(x =&gt; x.Name, NaturalOrder.Comparer)</c>.</summary>
    public static readonly IComparer<string?> Comparer = new NaturalComparer();

    /// <summary>
    /// Compares two names naturally. Null and empty sort first and equal to each other, so a
    /// half-typed row in an editor does not jump around while somebody is still typing it.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a)) return string.IsNullOrWhiteSpace(b) ? 0 : -1;
        if (string.IsNullOrWhiteSpace(b)) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                // Take the whole run on each side and compare as numbers. Leading zeros are skipped
                // so "S04" and "S4" are the same form rather than two, and the runs are compared by
                // LENGTH first so arbitrarily long numbers work without parsing (a roll number can
                // be longer than a long).
                var si = i; var sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                var runA = a.AsSpan(si, i - si).TrimStart('0');
                var runB = b.AsSpan(sj, j - sj).TrimStart('0');

                if (runA.Length != runB.Length) return runA.Length - runB.Length;
                var digits = runA.SequenceCompareTo(runB);
                if (digits != 0) return digits;
                continue;
            }

            var cmp = string.Compare(a[i].ToString(), b[j].ToString(),
                CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
            if (cmp != 0) return cmp;
            i++; j++;
        }

        // One is a prefix of the other: the shorter comes first ("S2" before "S2A").
        return (a.Length - i) - (b.Length - j);
    }

    private sealed class NaturalComparer : IComparer<string?>
    {
        public int Compare(string? x, string? y) => NaturalOrder.Compare(x, y);
    }
}
