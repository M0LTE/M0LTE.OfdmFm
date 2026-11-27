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

    [Fact]
    public void A_Host_Sends_A_Datagram_Through_A_Station()
    {
        byte[] datagram = Enumerable.Range(0, 40).Select(i => (byte)(i * 7)).ToArray();
        var sent = new List<TransmittedEvent>();

        var station = new Station(new Random(1), runJobsInline: true);
        station.SetIdentity("M0LTE");                           // the callsign it identifies with; until it has one the station only listens
        var peer = LinkAddress.FromAscii("G9XYZ");
        var me = LinkAddress.FromAscii("M0LTE-1");              // an address of the host's own: datagrams are sent from it

        long id = 0;
        ReadOnlyMemory<float> audio = default;
        station.TransmitRequest += r => (id, audio) = (r.Id, r.Samples);
        station.Transmitted += sent.Add;

        SendResult accepted = station.Send(datagram, peer, me, LinkProtocol.NoLayer3, new HostTag(1));
        float[] input = new float[4800];                        // the radio's audio, 0.1 s at a time (silence here)
        while (id == 0) station.Push(input);                    // the station listens for 5 s first, then asks to transmit
        station.TransmitStarted(id, station.SampleCount);       // play audio.Span to the radio, and say when it began ...
        for (long left = audio.Length; left > 0; left -= input.Length)
        {
            station.Push(input.AsSpan(0, (int)Math.Min(left, input.Length)));
        }

        station.TransmitEnded(id, station.SampleCount);         // ... and when it ended: Transmitted is raised here

        accepted.Should().Be(SendResult.Accepted);
        sent.Should().ContainSingle().Which.Tag.Should().Be(new HostTag(1));
        sent[0].Address.Should().Be(peer);
        sent[0].Format.Should().Be(StandardFormats.FirstContact);
    }
}
