using Avalonia.Input;

namespace Comet.App;

/// <summary>Names key presses the way #macro definitions spell them, such as "Ctrl+Shift+F5".</summary>
public static class KeyNames
{
    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>Function keys, keypad keys and Ctrl/Alt chords can be macros. Plain typing keys cannot.</summary>
    public static bool IsMacroCandidate(Key key, KeyModifiers modifiers)
    {
        if (IsModifierKey(key))
        {
            return false;
        }
        if (key is >= Key.F1 and <= Key.F24 or >= Key.NumPad0 and <= Key.Divide)
        {
            return true;
        }
        return (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
    }

    public static string Describe(Key key, KeyModifiers modifiers)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }
        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }
        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Meta");
        }
        parts.Add(key.ToString());
        return string.Join('+', parts);
    }
}
