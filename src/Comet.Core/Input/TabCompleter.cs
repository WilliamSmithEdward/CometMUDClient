namespace Comet.Core.Input;

/// <summary>
/// Completes the word before the caret from the user's own word list first, then from words
/// recently seen in the output. Pressing Tab again cycles to the next candidate.
/// </summary>
public sealed class TabCompleter(int recentCapacity = 3000)
{
    private const int MinWordLength = 3;

    private readonly LinkedList<string> _recent = new();
    private readonly Dictionary<string, LinkedListNode<string>> _recentIndex = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _candidates = [];
    private int _candidateIndex;
    private string? _lastResult;
    private int _wordStart;
    private int _lastLength;

    /// <summary>Remembers the words of an output line as completion candidates.</summary>
    public void Observe(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        int start = -1;
        for (int i = 0; i <= line.Length; i++)
        {
            bool inWord = i < line.Length && (char.IsLetter(line[i]) || (start >= 0 && line[i] is '\'' or '-'));
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                if (i - start >= MinWordLength)
                {
                    Remember(line[start..i].TrimEnd('\'', '-'));
                }
                start = -1;
            }
        }
    }

    private void Remember(string word)
    {
        if (word.Length < MinWordLength)
        {
            return;
        }
        if (_recentIndex.TryGetValue(word, out LinkedListNode<string>? node))
        {
            _recent.Remove(node);
        }
        else if (_recent.Count >= recentCapacity && _recent.Last is { } oldest)
        {
            _recentIndex.Remove(oldest.Value);
            _recent.RemoveLast();
        }
        _recentIndex[word] = _recent.AddFirst(word);
    }

    /// <summary>
    /// Completes the word ending at <paramref name="caret"/>. Returns false when nothing matches.
    /// Call again with the returned text and caret to cycle through further matches.
    /// </summary>
    public bool TryComplete(string text, int caret, IEnumerable<string> userWords, out string completedText, out int completedCaret)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(userWords);
        caret = Math.Clamp(caret, 0, text.Length);
        completedText = text;
        completedCaret = caret;

        bool cycling = _lastResult is not null && text == _lastResult && _candidates.Count > 0;
        if (!cycling)
        {
            int start = caret;
            while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
            {
                start--;
            }
            string prefix = text[start..caret];
            if (prefix.Length == 0)
            {
                Reset();
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _candidates = [];
            foreach (string word in userWords.Concat(_recent))
            {
                if (word.Length > prefix.Length && word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && seen.Add(word))
                {
                    _candidates.Add(word);
                }
            }
            if (_candidates.Count == 0)
            {
                Reset();
                return false;
            }
            _candidates.Add(prefix);
            _candidateIndex = 0;
            _wordStart = start;
        }
        else
        {
            _candidateIndex = (_candidateIndex + 1) % _candidates.Count;
        }

        string candidate = _candidates[_candidateIndex];
        int wordEnd = cycling ? Math.Min(text.Length, _wordStart + _lastLength) : caret;
        completedText = string.Concat(text.AsSpan(0, _wordStart), candidate, text.AsSpan(wordEnd));
        completedCaret = _wordStart + candidate.Length;
        _lastLength = candidate.Length;
        _lastResult = completedText;
        return true;
    }

    /// <summary>Forgets the cycling state. Call when the user types anything other than Tab.</summary>
    public void Reset()
    {
        _candidates = [];
        _lastResult = null;
    }
}
