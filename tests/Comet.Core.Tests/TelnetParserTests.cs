using System.Text;
using Comet.Core.Net;

namespace Comet.Core.Tests;

public class TelnetParserTests
{
    private sealed class Recorder : ITelnetHandler
    {
        public StringBuilder Data { get; } = new();
        public List<(byte Command, byte Option)> Negotiations { get; } = [];
        public List<(byte Option, byte[] Payload)> Subnegotiations { get; } = [];
        public List<byte> Commands { get; } = [];
        public byte? StopOnOption { get; init; }

        public void OnData(ReadOnlySpan<byte> data) => Data.Append(Encoding.Latin1.GetString(data));
        public void OnNegotiation(byte command, byte option) => Negotiations.Add((command, option));
        public void OnCommand(byte command) => Commands.Add(command);

        public bool OnSubnegotiation(byte option, ReadOnlySpan<byte> payload)
        {
            Subnegotiations.Add((option, payload.ToArray()));
            return option == StopOnOption;
        }
    }

    [Fact]
    public void PlainDataPassesThrough()
    {
        var recorder = new Recorder();
        int consumed = new TelnetParser().Process("hello"u8, recorder);

        Assert.Equal(5, consumed);
        Assert.Equal("hello", recorder.Data.ToString());
    }

    [Fact]
    public void NegotiationIsExtractedFromData()
    {
        var recorder = new Recorder();
        new TelnetParser().Process([(byte)'a', 255, 251, 1, (byte)'b'], recorder);

        Assert.Equal("ab", recorder.Data.ToString());
        Assert.Equal([(TelnetCommand.Will, TelnetOption.Echo)], recorder.Negotiations);
    }

    [Fact]
    public void EscapedIacIsData()
    {
        var recorder = new Recorder();
        new TelnetParser().Process([(byte)'a', 255, 255, (byte)'b'], recorder);

        Assert.Equal("aÿb", recorder.Data.ToString());
    }

    [Fact]
    public void SequenceSplitAcrossReadsIsReassembled()
    {
        var recorder = new Recorder();
        var parser = new TelnetParser();
        byte[] stream = [(byte)'x', 255, 250, 201, (byte)'A', 255, 255, (byte)'B', 255, 240, (byte)'y', 255, 249];

        foreach (byte b in stream)
        {
            parser.Process([b], recorder);
        }

        Assert.Equal("xy", recorder.Data.ToString());
        (byte option, byte[] payload) = Assert.Single(recorder.Subnegotiations);
        Assert.Equal(TelnetOption.Gmcp, option);
        Assert.Equal([(byte)'A', 255, (byte)'B'], payload);
        Assert.Equal([TelnetCommand.GoAhead], recorder.Commands);
    }

    [Fact]
    public void StopsAfterSubnegotiationWhenHandlerAsks()
    {
        var recorder = new Recorder { StopOnOption = TelnetOption.Compress2 };
        byte[] stream = [(byte)'a', 255, 250, 86, 255, 240, 0x78, 0x9c, 1, 2];

        int consumed = new TelnetParser().Process(stream, recorder);

        Assert.Equal(6, consumed);
        Assert.Equal("a", recorder.Data.ToString());
    }

    [Fact]
    public void OversizedSubnegotiationIsDropped()
    {
        var recorder = new Recorder();
        var parser = new TelnetParser();
        parser.Process([255, 250, 201], recorder);
        parser.Process(new byte[TelnetParser.MaxSubnegotiationLength + 10], recorder);
        parser.Process([255, 240, (byte)'z'], recorder);

        Assert.Empty(recorder.Subnegotiations);
        Assert.Equal("z", recorder.Data.ToString());
    }

    [Fact]
    public void EscapeIacDoublesByte255()
    {
        var output = new List<byte>();
        TelnetParser.EscapeIac([1, 255, 2], output);

        Assert.Equal([1, 255, 255, 2], output);
    }
}
