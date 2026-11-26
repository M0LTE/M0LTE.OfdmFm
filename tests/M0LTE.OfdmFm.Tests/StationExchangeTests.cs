using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Two or three stations in one process, exchanging datagrams through the simulated channel (step 5 of docs/station-design.md). Every test steps the channel; none waits. Datagrams are short, so that even
/// at the fastest formats a burst is a few symbols and receiving it is quick.
/// </summary>
public class StationExchangeTests
{
    private static readonly double FirstContactRate = SimRig.FirstContactRate;

    private static byte[] Take(byte[] buffer, int tag)
    {
        byte[] copy = SimRig.Datagram(tag, buffer.Length);
        copy.CopyTo(buffer, 0);
        return copy;
    }

    [Fact]
    public void Two_Stations_With_Traffic_Both_Ways_Deliver_Every_Datagram_Once_In_Order_And_The_Rate_Climbs()
    {
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        var sentByA = new List<byte[]>();
        var sentByB = new List<byte[]>();
        byte[] buffer = new byte[30];
        rig.RunTo(6);
        for (int i = 0; i < 5; i++)
        {
            // The caller's buffer is its own again when Send returns: it is overwritten with junk at once, and the datagram must still arrive as it was.
            sentByA.Add(Take(buffer, 100 + i));
            rig.Stations[0].Send(buffer, SimRig.Name(2), new HostTag(100 + i)).Should().Be(SendResult.Accepted);
            Array.Fill(buffer, (byte)0xEE);
            rig.Run(8);
            sentByB.Add(Take(buffer, 200 + i));
            rig.Stations[1].Send(buffer, SimRig.Name(1), new HostTag(200 + i)).Should().Be(SendResult.Accepted);
            Array.Fill(buffer, (byte)0xEE);
            rig.Run(8);
        }

        // Every datagram once, in order, nothing else.
        b.Received.Select(r => r.Data).Should().BeEquivalentTo(sentByA, o => o.WithStrictOrdering());
        a.Received.Select(r => r.Data).Should().BeEquivalentTo(sentByB, o => o.WithStrictOrdering());
        b.Received.Should().OnlyContain(r => r.Sender == 1 && !r.Everyone);
        a.Received.Should().OnlyContain(r => r.Sender == 2 && !r.Everyone);

        // A Transmitted for every tag, in order, none dropped.
        a.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(Enumerable.Range(100, 5).Select(i => (long)i));
        b.Transmitted.Select(t => t.Event.Tag.Value).Should().Equal(Enumerable.Range(200, 5).Select(i => (long)i));
        a.Dropped.Should().BeEmpty();
        b.Dropped.Should().BeEmpty();

        // The first burst each way goes at first contact; once preludes carry recommendations the rate is far above it: from the fourth burst each way it is more than ten times the first-contact rate.
        a.Transmitted[0].Event.RateBitsPerSecond.Should().Be(FirstContactRate);
        a.Transmitted.Skip(3).Select(t => t.Event.RateBitsPerSecond).Should().OnlyContain(r => r > 10 * FirstContactRate);
        b.Transmitted.Skip(2).Select(t => t.Event.RateBitsPerSecond).Should().OnlyContain(r => r > 10 * FirstContactRate);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Burst).Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public void A_Datagram_To_Everyone_Is_Heard_By_Both_Listeners_With_No_Prelude_At_The_First_Contact_Format()
    {
        var rig = new SimRig(3);
        float[]? audio = null;
        rig.Stations[0].TransmitRequest += r => audio = r.Samples.ToArray();
        rig.RunTo(6);
        byte[] datagram = SimRig.Datagram(1, 30);
        rig.Stations[0].Send(datagram, StationName.Everyone, new HostTag(1), reportWanted: true).Should().Be(SendResult.Accepted);
        rig.Run(14);

        foreach (int listener in new[] { 1, 2 })
        {
            SimReceived got = rig.Nodes[listener].Received.Should().ContainSingle().Subject;
            got.Data.Should().Equal(datagram);
            got.Everyone.Should().BeTrue();
            got.Sender.Should().Be(1);
            rig.Nodes[listener].PathReports.Should().BeEmpty("a burst for everyone carries no report");
        }

        TransmittedEvent sent = rig.Nodes[0].Transmitted.Should().ContainSingle().Subject.Event;
        sent.Station.Should().Be(StationName.Everyone);
        sent.Format.Should().Be(StandardFormats.FirstContact);
        rig.Stations[0].Adaptation.TryGetNeighbour(2, out LinkNeighbour? near).Should().BeFalse("nothing was asked of anyone, so nothing is awaited");
        near.Should().BeNull();

        // Unpack the burst as it went on air: a header for everyone, and a payload that is the datagram behind its length, with no prelude in front.
        audio.Should().NotBeNull();
        FormatOpening opening = StandardFormats.Describe(sent.Format).Opening;
        BurstCandidate found = BurstFinder.Find(audio, opening).Single();
        var headerReader = new HeaderReader();
        headerReader.TryRead(audio, found.Index, opening, out BurstHeader header).Should().BeTrue();
        header.Destination.Should().Be(0);
        header.Sender.Should().Be(1);
        header.Format.Should().Be(StandardFormats.FirstContact);
        header.BurstNumber.Should().Be(sent.Burst);
        var receiver = new PayloadReceiver();
        byte[] payload = new byte[PayloadReceiver.PayloadRoom(header)];
        PayloadReceiveResult result = receiver.Receive(audio, found.Index, header, headerReader.Bits, payload);
        result.CheckPassed.Should().BeTrue();
        payload[0].Should().Be(0);
        payload[1].Should().Be(30, "the first thing in the payload is the datagram's length: nothing precedes it");
        payload.AsSpan(2, 30).ToArray().Should().Equal(datagram);
    }

    [Fact]
    public void A_Burst_Granted_And_Then_Withdrawn_Because_The_Other_Station_Started_First_Still_Arrives_Once_And_Unchanged()
    {
        // Station 1 sends an expedited datagram at 6 s and goes at once (first slot). Station 2 has an ordinary one queued 0.3 s later and is granted before it can have noticed station 1's burst,
        // but its audio layer takes 1.2 s to key (longer than the 0.9 s it can take to notice a burst): it hears station 1 first and withdraws the request.
        var rig = new SimRig(2, seed: 2);
        rig.Nodes[1].StartLatencySamples = 57600;
        rig.RunTo(6);
        rig.Send(1, 2, 1, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        rig.Run(0.3);
        rig.Send(2, 1, 2).Should().Be(SendResult.Accepted);
        rig.Run(30);

        SimNode one = rig.Nodes[0];
        SimNode two = rig.Nodes[1];
        two.WithdrawnBeforeStart.Should().ContainSingle("station 2 was granted, then heard station 1 before it keyed");
        two.Received.Select(r => r.Data).Should().BeEquivalentTo(new[] { SimRig.Datagram(1, 30) }, o => o.WithStrictOrdering());
        one.Received.Select(r => r.Data).Should().BeEquivalentTo(new[] { SimRig.Datagram(2, 30) }, o => o.WithStrictOrdering());

        // The same burst, unchanged, was handed over again: same audio, same burst number, one Transmitted.
        two.Requests.Should().HaveCount(2);
        two.Requests[1].Checksum.Should().Be(two.Requests[0].Checksum);
        two.Requests[1].Length.Should().Be(two.Requests[0].Length);
        two.Requests[1].Clock.Should().BeGreaterThan(two.Requests[0].Clock);
        two.Transmitted.Should().ContainSingle().Which.Event.Burst.Should().Be(0);
        one.Transmitted.Should().ContainSingle();
        two.Dropped.Should().BeEmpty();
        two.Transmissions.Should().ContainSingle("only the second hand-over was keyed");
        two.Transmissions[0].Start.Should().BeGreaterThan(one.Transmissions[0].Start + one.Transmissions[0].Length, "it waited for station 1's burst to end");
        rig.Stations[1].NextBurstNumber.Should().Be(1);
    }

    [Fact]
    public void Silences_Step_The_Format_Down_As_The_Protocol_Says_Same_Then_Two_Rungs_Slower_Then_First_Contact()
    {
        // Station 1 hears one burst from station 2 (so it has its own estimate of that path, and no report), then station 2 goes away: nothing it sends is heard and nothing comes back.
        // With no report every burst asks for one, and after each the station waits 3 s plus twice the burst's airtime: that is one silence. By the protocol the burst after one silence goes at the same format,
        // after two two rungs slower than that, after three or more at first contact.
        var rig = new SimRig(2);
        SimNode a = rig.Nodes[0];
        rig.RunTo(6);
        rig.Send(2, 1, 1).Should().Be(SendResult.Accepted);
        rig.Run(12);
        a.Received.Should().ContainSingle();
        rig.Channel.Link(rig.Nodes[0], rig.Nodes[1]).Gain = 0;
        rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Gain = 0;
        LinkAdaptation rules = rig.Stations[0].Adaptation;
        rules.TryGetNeighbour(2, out LinkNeighbour? far).Should().BeTrue();
        int first = StandardFormats.Slower(far!.Recommended!.Value, LinkAdaptation.Caution);
        first.Should().BeGreaterThan(StandardFormats.Slower(first, 2), "the steps must be visible");
        StandardFormats.Rate(first).Should().BeGreaterThan(StandardFormats.Rate(StandardFormats.FirstContact));

        var formats = new List<int>();
        var silences = new List<int>();
        for (int k = 0; k < 5; k++)
        {
            rig.Send(1, 2, 10 + k).Should().Be(SendResult.Accepted);
            while (a.Transmitted.Count < k + 1)
            {
                rig.Run(0.5);
            }

            formats.Add(a.Transmitted[k].Event.Format);
            SimTransmission tx = a.Transmissions[^1];
            long due = tx.Start + tx.Length + Station.AnswerWaitSamples(tx.Length);
            rig.Channel.Run(due - 1);
            silences.Add(far.Silences);
            rig.Channel.Run(due);
            silences.Add(far.Silences);
        }

        formats.Should().Equal(first, first, StandardFormats.Slower(first, 2), StandardFormats.FirstContact, StandardFormats.FirstContact);
        silences.Should().Equal(0, 1, 1, 2, 2, 3, 3, 4, 4, 5);
        far.ReportFormat.Should().BeNull();
    }

    private static List<string> Scenario(int block, int seed, int noiseSeed = 5)
    {
        var rig = new SimRig(2, block, seed);
        rig.RunTo(6);
        rig.Send(1, 2, 1).Should().Be(SendResult.Accepted);
        rig.Run(8);
        rig.Send(2, 1, 2).Should().Be(SendResult.Accepted);
        rig.Run(8);
        rig.Send(1, 2, 3).Should().Be(SendResult.Accepted);
        rig.Send(1, 2, 4, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        rig.Send(1, StationName.Everyone, 5).Should().Be(SendResult.Accepted);
        rig.Run(12);
        _ = noiseSeed;
        return rig.Channel.Log;
    }

    /// <summary>
    /// The log in two parts for each station. Events raised in the call that makes them happen (a request, a withdrawal, a busy change, a transmission ending) carry the exact clock whatever the block size.
    /// Events that come from applying a received burst's result (a datagram, a path report, a link state) are raised at the end of the call that finds the result ready, so their clock moves with the block size;
    /// their text and their order do not, and the clock is cut from them.
    /// </summary>
    private static Dictionary<string, (List<string> Exact, List<string> Results)> Canonical(List<string> log)
    {
        var parts = new Dictionary<string, (List<string>, List<string>)>();
        foreach (string line in log)
        {
            string[] w = line.Split(' ', 3);
            if (!parts.TryGetValue(w[1], out (List<string> Exact, List<string> Results) p))
            {
                p = ([], []);
                parts[w[1]] = p;
            }

            bool result = w[2].StartsWith("rx ", StringComparison.Ordinal) || w[2].StartsWith("path ", StringComparison.Ordinal) || w[2].StartsWith("link ", StringComparison.Ordinal);
            (result ? p.Results : p.Exact).Add(result ? w[2] : line);
        }

        return parts;
    }

    [Fact]
    public void The_Same_Seeds_Give_The_Same_Log_Of_Every_Event_At_Both_Stations_Whatever_The_Lockstep_Block_Size()
    {
        List<string> reference = Scenario(4800, 1);
        reference.Count(l => l.Contains(" rx from ", StringComparison.Ordinal)).Should().Be(5, "tags 1, 3, 4 and 5 at station 2 and tag 2 at station 1");
        reference.Should().Contain(l => l.Contains("transmitted tag 5 to everyone", StringComparison.Ordinal));
        reference.Count(l => l.Contains(" request ", StringComparison.Ordinal)).Should().BeGreaterThan(3);

        // Run twice: the log is identical, line for line.
        Scenario(4800, 1).Should().Equal(reference);

        // A different block size cuts the steps differently and nothing the stations do depends on it.
        foreach (int block in new[] { 1000, 100000 })
        {
            var other = Canonical(Scenario(block, 1));
            var expected = Canonical(reference);
            other.Keys.Should().Equal(expected.Keys);
            foreach (string station in expected.Keys)
            {
                other[station].Exact.Should().Equal(expected[station].Exact, $"station {station}, block {block}");
                other[station].Results.Should().Equal(expected[station].Results, $"station {station}, block {block}");
            }
        }

        // And the seeds matter: another seed draws other waits.
        Scenario(4800, 2).Should().NotEqual(reference);
    }
}
