namespace Chapter.Core.Indexing;

/// <summary>
/// Subsequence matching with scoring, the behaviour people expect from a Ctrl+T box:
/// "ATR" finds AgentTurnRunner, "turnrun" finds it too, and an exact prefix wins.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Scores <paramref name="candidate"/> against <paramref name="query"/>.
    /// Returns -1 when the query is not a subsequence of the candidate at all.
    /// Higher is better.
    /// </summary>
    public static int Score(string candidate, string query)
    {
        if (query.Length == 0) return 0;
        if (candidate.Length < query.Length) return -1;

        // Exact and prefix matches dominate: if you typed the whole name, you meant it.
        if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 800 - candidate.Length;

        var score = 0;
        var candidateIndex = 0;
        var previousMatchIndex = -1;

        foreach (var queryChar in query)
        {
            var found = -1;

            for (var i = candidateIndex; i < candidate.Length; i++)
            {
                if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(queryChar)) continue;
                found = i;
                break;
            }

            if (found < 0) return -1;

            // Consecutive characters and word boundaries are strong signals; scattered
            // matches across the name are weak ones.
            if (found == previousMatchIndex + 1) score += 12;
            if (IsWordStart(candidate, found)) score += 18;
            if (found == 0) score += 10;

            score -= Math.Min(found - candidateIndex, 8);

            previousMatchIndex = found;
            candidateIndex = found + 1;
        }

        // Shorter names that satisfy the query are more likely to be what was meant.
        return score + Math.Max(0, 40 - candidate.Length);
    }

    private static bool IsWordStart(string text, int index)
    {
        if (index == 0) return true;

        var previous = text[index - 1];
        var current = text[index];

        if (previous is '_' or '.' or '/' or '-') return true;
        return char.IsLower(previous) && char.IsUpper(current);
    }
}
