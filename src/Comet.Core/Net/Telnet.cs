namespace Comet.Core.Net;

/// <summary>Telnet command bytes (RFC 854).</summary>
public static class TelnetCommand
{
    public const byte EndOfRecord = 239;
    public const byte SubnegotiationEnd = 240;
    public const byte Nop = 241;
    public const byte GoAhead = 249;
    public const byte SubnegotiationBegin = 250;
    public const byte Will = 251;
    public const byte Wont = 252;
    public const byte Do = 253;
    public const byte Dont = 254;
    public const byte Iac = 255;
}

/// <summary>Telnet option codes this client knows about.</summary>
public static class TelnetOption
{
    public const byte Echo = 1;
    public const byte SuppressGoAhead = 3;
    public const byte TerminalType = 24;
    public const byte EndOfRecord = 25;
    public const byte WindowSize = 31;
    public const byte Charset = 42;
    public const byte Mssp = 70;
    public const byte Compress2 = 86;
    public const byte Gmcp = 201;
}

/// <summary>Receives the events a <see cref="TelnetParser"/> extracts from a byte stream.</summary>
public interface ITelnetHandler
{
    /// <summary>Plain application data with telnet sequences removed.</summary>
    void OnData(ReadOnlySpan<byte> data);

    /// <summary>WILL, WONT, DO or DONT for an option.</summary>
    void OnNegotiation(byte command, byte option);

    /// <summary>A complete subnegotiation payload with IAC escapes already removed.</summary>
    /// <returns>
    /// True when the parser must stop consuming input immediately, because the bytes that
    /// follow are in a different transport encoding (MCCP2 compression starts).
    /// </returns>
    bool OnSubnegotiation(byte option, ReadOnlySpan<byte> payload);

    /// <summary>A bare command such as GA or EOR.</summary>
    void OnCommand(byte command);
}

/// <summary>
/// Incremental telnet stream parser. Holds state between calls, so a sequence split across
/// two network reads is handled correctly.
/// </summary>
public sealed class TelnetParser
{
    /// <summary>Upper bound for one subnegotiation. A longer one is dropped, not buffered.</summary>
    public const int MaxSubnegotiationLength = 64 * 1024;

    private enum State { Data, Iac, Negotiation, SubOption, SubData, SubIac }

    private readonly List<byte> _sub = [];
    private State _state = State.Data;
    private byte _negotiationCommand;
    private byte _subOption;
    private bool _subOverflow;

    /// <summary>Parses <paramref name="input"/> and returns the number of bytes consumed.</summary>
    /// <remarks>
    /// The count is less than the input length only when the handler asked the parser to stop
    /// from <see cref="ITelnetHandler.OnSubnegotiation"/>.
    /// </remarks>
    public int Process(ReadOnlySpan<byte> input, ITelnetHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        int dataStart = -1;

        for (int i = 0; i < input.Length; i++)
        {
            byte b = input[i];
            switch (_state)
            {
                case State.Data:
                    if (b == TelnetCommand.Iac)
                    {
                        if (dataStart >= 0)
                        {
                            handler.OnData(input[dataStart..i]);
                            dataStart = -1;
                        }
                        _state = State.Iac;
                    }
                    else if (dataStart < 0)
                    {
                        dataStart = i;
                    }
                    break;

                case State.Iac:
                    switch (b)
                    {
                        case TelnetCommand.Iac:
                            handler.OnData([TelnetCommand.Iac]);
                            _state = State.Data;
                            break;
                        case TelnetCommand.Will or TelnetCommand.Wont or TelnetCommand.Do or TelnetCommand.Dont:
                            _negotiationCommand = b;
                            _state = State.Negotiation;
                            break;
                        case TelnetCommand.SubnegotiationBegin:
                            _state = State.SubOption;
                            break;
                        default:
                            handler.OnCommand(b);
                            _state = State.Data;
                            break;
                    }
                    break;

                case State.Negotiation:
                    handler.OnNegotiation(_negotiationCommand, b);
                    _state = State.Data;
                    break;

                case State.SubOption:
                    _subOption = b;
                    _sub.Clear();
                    _subOverflow = false;
                    _state = State.SubData;
                    break;

                case State.SubData:
                    if (b == TelnetCommand.Iac)
                    {
                        _state = State.SubIac;
                    }
                    else
                    {
                        AppendSub(b);
                    }
                    break;

                case State.SubIac:
                    if (b == TelnetCommand.SubnegotiationEnd)
                    {
                        _state = State.Data;
                        bool stop = !_subOverflow
                            && handler.OnSubnegotiation(_subOption, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_sub));
                        _sub.Clear();
                        if (stop)
                        {
                            return i + 1;
                        }
                    }
                    else
                    {
                        // IAC IAC is an escaped 255. Anything else is malformed; keep the byte.
                        AppendSub(b);
                        _state = State.SubData;
                    }
                    break;
            }
        }

        if (dataStart >= 0)
        {
            handler.OnData(input[dataStart..]);
        }
        return input.Length;
    }

    private void AppendSub(byte b)
    {
        if (_sub.Count >= MaxSubnegotiationLength)
        {
            _subOverflow = true;
            return;
        }
        _sub.Add(b);
    }

    /// <summary>Copies <paramref name="data"/> to <paramref name="destination"/>, doubling each IAC byte.</summary>
    public static void EscapeIac(ReadOnlySpan<byte> data, List<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        foreach (byte b in data)
        {
            destination.Add(b);
            if (b == TelnetCommand.Iac)
            {
                destination.Add(b);
            }
        }
    }
}
