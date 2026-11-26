namespace M0LTE.OfdmFm.Tests;

/// <summary>The README's render and receive example, run as written, over a clean channel.</summary>
public class ReadmeExampleTests
{
    [Fact]
    public void A_Rendered_Burst_Is_Found_Read_And_Received()
    {
        byte[] datagram = Enumerable.Range(0, 40).Select(i => (byte)(i * 7)).ToArray();

        // Transmit: pack a datagram, pick the shortest burst of a format that carries it, render it.
        int format = StandardFormats.MostRobust;
        byte[] payload = new byte[256];
        int length = PayloadCodec.Pack(null, new[] { new ReadOnlyMemory<byte>(datagram) }, payload);
        int symbols = PayloadCodec.SymbolsFor(format, length)!.Value;
        var header = BurstHeader.Data(sender: 1, destination: 0, format, symbols, burstNumber: 0);
        var renderer = new BurstRenderer();
        float[] audio = new float[renderer.SamplesFor(format, symbols)];
        renderer.RenderData(header, payload.AsSpan(0, length), audio);

        // Receive: find the burst, read its header, receive the payload.
        FormatOpening opening = StandardFormats.Describe(format).Opening;
        BurstCandidate found = BurstFinder.Find(audio, opening).Single();
        var headerReader = new HeaderReader();
        headerReader.TryRead(audio, found.Index, opening, out BurstHeader read).Should().BeTrue();
        read.Should().Be(header);

        var receiver = new PayloadReceiver();
        byte[] received = new byte[PayloadReceiver.PayloadRoom(read)];
        PayloadReceiveResult result = receiver.Receive(audio, found.Index, read, headerReader.Bits, received);
        result.Accepted.Should().BeTrue();
        result.CheckPassed.Should().BeTrue();

        PayloadCodec.TryUnpack(received.AsSpan(0, result.PayloadBytes), toEveryone: true, out _, out DatagramReader datagrams).Should().BeTrue();
        datagrams.TryNext(out ReadOnlySpan<byte> back).Should().BeTrue();
        back.ToArray().Should().Equal(datagram);
    }
}
