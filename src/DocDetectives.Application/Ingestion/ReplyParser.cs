using System.Text.RegularExpressions;

namespace DocDetectives.Application.Ingestion;

public enum ReplyKind { Yes, No, All, None, Numbers, ApproveAll, SkipAll, Retry, Undo }

public sealed record Reply(ReplyKind Kind, IReadOnlyList<int> Numbers)
{
    public static Reply Of(ReplyKind kind) => new(kind, []);
}

/// <summary>Understands short typed replies to the review questions. Anything else returns null.</summary>
public static partial class ReplyParser
{
    private static readonly HashSet<string> YesWords = new(StringComparer.Ordinal)
    {
        "yes", "y", "yeah", "yep", "yes please", "ok", "okay", "sure", "approve", "approved", "save", "save it",
        "submit", "submit it", "create", "create it", "confirm", "go ahead", "do it", "extract", "link", "accept",
        "looks good", "lgtm", "correct"
    };

    private static readonly HashSet<string> NoWords = new(StringComparer.Ordinal)
    {
        "no", "n", "nope", "no thanks", "reject", "discard", "skip", "skip it", "cancel", "don't", "do not", "not now", "decline"
    };

    private static readonly HashSet<string> AllWords = new(StringComparer.Ordinal) { "all", "all of them", "everything", "both" };
    private static readonly HashSet<string> NoneWords = new(StringComparer.Ordinal) { "none", "nothing", "neither", "none of them" };
    private static readonly HashSet<string> ApproveAllWords = new(StringComparer.Ordinal)
        { "approve all", "yes to all", "save all", "submit all", "yes all", "accept all", "create all" };
    private static readonly HashSet<string> SkipAllWords = new(StringComparer.Ordinal)
        { "skip all", "reject all", "no to all", "discard all", "no all" };
    private static readonly HashSet<string> RetryWords = new(StringComparer.Ordinal) { "retry", "try again", "again" };
    private static readonly HashSet<string> UndoWords = new(StringComparer.Ordinal)
        { "undo", "undo that", "undo last change", "revert", "go back", "undo it" };

    private static readonly string[] QuestionStarts =
        ["what", "how", "why", "which", "where", "when", "who", "can ", "could ", "does ", "do ", "is ", "are ", "should "];

    public static Reply? Parse(string text)
    {
        var t = Normalize().Replace(text.Trim().ToLowerInvariant(), " ").Trim();
        t = t.TrimEnd('.', '!').Trim();
        if (t.Length == 0) return null;

        if (UndoWords.Contains(t)) return Reply.Of(ReplyKind.Undo);
        if (ApproveAllWords.Contains(t)) return Reply.Of(ReplyKind.ApproveAll);
        if (SkipAllWords.Contains(t)) return Reply.Of(ReplyKind.SkipAll);
        if (RetryWords.Contains(t)) return Reply.Of(ReplyKind.Retry);
        if (AllWords.Contains(t)) return Reply.Of(ReplyKind.All);
        if (NoneWords.Contains(t)) return Reply.Of(ReplyKind.None);
        if (YesWords.Contains(t)) return Reply.Of(ReplyKind.Yes);
        if (NoWords.Contains(t)) return Reply.Of(ReplyKind.No);

        // Number lists: "1", "1, 3", "1 and 3", "1-3", "#2".
        if (NumberList().IsMatch(t))
        {
            var numbers = new SortedSet<int>();
            foreach (Match m in Range().Matches(t))
            {
                var from = int.Parse(m.Groups[1].Value);
                var to = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : from;
                if (to < from || to - from > 100) return null;
                for (var n = from; n <= to; n++) numbers.Add(n);
            }

            return numbers.Count > 0 ? new Reply(ReplyKind.Numbers, numbers.ToList()) : null;
        }

        return null;
    }

    /// <summary>Whether text that is not an answer should be answered as a question about the manual (not applied as a change).</summary>
    public static bool LooksLikeQuestion(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t.EndsWith('?') || QuestionStarts.Any(q => t.StartsWith(q, StringComparison.Ordinal));
    }

    private static readonly string[] EditVerbs =
    [
        "set", "change", "rename", "remove", "delete", "drop", "add", "insert", "replace", "make", "move", "update",
        "use", "put", "swap", "split", "merge", "call", "fix", "correct", "increase", "decrease", "reduce", "lower",
        "raise", "edit", "modify", "turn", "mark", "include", "exclude", "keep only"
    ];

    private static readonly string[] Politeness =
        ["can you ", "could you ", "would you ", "please ", "pls ", "i want to ", "i'd like to ", "let's ", "also ", "and "];

    /// <summary>Whether the text asks to change the draft ("please remove line 3", "can you set the frequency to 6 months?").</summary>
    public static bool LooksLikeEdit(string text)
    {
        var t = Normalize().Replace(text.Trim().ToLowerInvariant(), " ");
        for (var stripped = true; stripped;)
        {
            stripped = false;
            foreach (var p in Politeness.Where(p => t.StartsWith(p, StringComparison.Ordinal)))
            {
                t = t[p.Length..].TrimStart();
                stripped = true;
            }
        }

        return EditVerbs.Any(v => t.StartsWith(v + " ", StringComparison.Ordinal) || t == v);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Normalize();

    [GeneratedRegex(@"^(?:#?\d+\s*(?:(?:-|–|to)\s*\d+)?\s*(?:,|;|&|\band\b|\s)\s*)*#?\d+\s*(?:(?:-|–|to)\s*\d+)?$")]
    private static partial Regex NumberList();

    [GeneratedRegex(@"(\d+)\s*(?:(?:-|–|to)\s*(\d+))?")]
    private static partial Regex Range();
}
