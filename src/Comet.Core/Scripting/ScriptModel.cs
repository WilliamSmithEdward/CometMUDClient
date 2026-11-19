using Comet.Core.Text;

namespace Comet.Core.Scripting;

public enum ScriptItemKind
{
    Alias,
    Action,
    Substitute,
    Gag,
    AntiSubstitute,
    Highlight,
    Variable,
    Ticker,
    Macro,
    TabWord,
}

/// <summary>
/// One user-defined script object. What the fields mean depends on <see cref="Kind"/>:
/// Alias (name, commands); Action (pattern, commands, priority); Substitute (pattern,
/// replacement); Gag and AntiSubstitute (pattern); Highlight (pattern, colors); Variable
/// (name, value); Ticker (name, commands, seconds); Macro (key, commands); TabWord (word).
/// </summary>
public sealed record ScriptItem(ScriptItemKind Kind, string Key, string Value = "", string Extra = "");

/// <summary>What a <see cref="ScriptEngine"/> needs from the session that owns it.</summary>
public interface IScriptHost
{
    /// <summary>Sends one line to the MUD.</summary>
    void Send(string text);

    /// <summary>Shows a line in the output without sending anything.</summary>
    void Print(TextLine line);

    void Bell();

    void ClearOutput();

    void Connect(string name, string host, int port, bool useTls);

    void Disconnect();

    bool IsLogging { get; }

    /// <summary>
    /// Starts a session log, appending if the file exists. Returns an error message, or null
    /// on success. <paramref name="fullPath"/> is where the log is (or would have been) written.
    /// </summary>
    string? StartLog(string path, out string fullPath);

    void StopLog();

    /// <summary>Sends a GMCP message. Returns false when GMCP is not active.</summary>
    bool SendGmcp(string package, string json);

    /// <summary>Sets the text of a status bar panel, numbered from 1.</summary>
    void SetPanel(int index, string text);

    /// <summary>Resolves a script path typed by the user to a full path.</summary>
    string ResolvePath(string path);
}
