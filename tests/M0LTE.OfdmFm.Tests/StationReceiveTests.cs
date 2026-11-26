using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>Step 3: audio pushed into the station comes out as datagrams and as calls on the adaptation rules.</summary>
public class StationReceiveTests
{
    // One format on each opening, robust enough to decode through moderate noise: full band, 3 kHz and 1.5 kHz.
    internal static readonly int[] FormatOfOpening = [12, (4 * 32) + 8, (6 * 32) + 10];

    private static FormatOpening OpeningOf(int format) => StandardFormats.Describe(format).Opening;

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    public void One_Clean_Burst_Of_Each_Opening_Arrives_Once_From_The_Right_Station(int which, int destination)
    {
        int format = FormatOfOpening[which];
        OpeningOf(format).Should().Be((FormatOpening)which);
        using var rig = new ReceiveRig(0);
        byte[] datagram = TestBurst.Datagram(3, 60);
        var builder = new StreamBuilder().Silence(1.5);
        Placed placed = builder.Add(TestBurst.Data(7, destination, format, [datagram]));
        builder.Silence(2.5);

        rig.Push(builder.ToArray(), () => 480);
        rig.Finish();

        rig.Datagrams.Should().HaveCount(1);
        rig.Datagrams[0].Sender.Should().Be(7);
        rig.Datagrams[0].Everyone.Should().Be(destination == 0);
        rig.Datagrams[0].Data.Should().Equal(datagram);
        rig.Calls.Should().ContainSingle().Which.Should().Match<Station.AdaptationCall>(c => c.Decoded && c.Sender == 7 && c.Dest == destination && !c.IsReport);
        rig.Calls[0].Now.Should().BeApproximately(placed.End / 48000.0, 4.0 / 48000);
        rig.Station.Adaptation.TryGetNeighbour(7, out LinkNeighbour? nb).Should().BeTrue();
        nb!.LastHeard.Should().BeApproximately(placed.End / 48000.0, 4.0 / 48000);
        rig.Log.Count(l => l.StartsWith("rx ", StringComparison.Ordinal)).Should().Be(1);
        rig.Log.Where(l => l.StartsWith("link ", StringComparison.Ordinal)).Should().ContainSingle().Which.Should().StartWith("link #7 ");
    }

    [Theory]
    [InlineData(0, 200.0)]
    [InlineData(1, 200.0)]
    [InlineData(2, 200.0)]
    [InlineData(0, -200.0)]
    [InlineData(1, -200.0)]
    [InlineData(2, -200.0)]
    public void A_Burst_From_A_Clock_200_Parts_Per_Million_Off_Is_Received_Through_Noise(int which, double ppm)
    {
        int format = FormatOfOpening[which];
        using var rig = new ReceiveRig(0);
        byte[] datagram = TestBurst.Datagram(4, 120);
        var builder = new StreamBuilder().Silence(1.5);
        builder.Add(TestBurst.Data(9, 1, format, [datagram], burstNumber: 5), ppm);
        builder.Silence(2.5).Noise(0.02, 11);

        rig.Push(builder.ToArray(), () => 480);
        rig.Finish();

        rig.Datagrams.Should().HaveCount(1);
        rig.Datagrams[0].Data.Should().Equal(datagram);
        rig.Datagrams[0].Sender.Should().Be(9);
    }

    [Fact]
    public void A_Stream_Of_Mixed_Bursts_Delivers_The_Right_Datagrams_And_Drives_The_Rules_As_Replaying_Them_By_Hand_Does()
    {
        (float[] samples, List<Placed> bursts) = MixedStream.Build();
        using var rig = new ReceiveRig(0);
        rig.Push(samples, () => 480);
        rig.Finish();

        // The right datagrams, in order, and none for the third station.
        rig.Datagrams.Select(d => (d.Sender, d.Everyone, Convert.ToHexString(d.Data))).Should()
            .Equal(MixedStream.Expected(bursts).Select(d => (d.Sender, d.Everyone, Convert.ToHexString(d.Data))));

        // The calls on the rules: one for each data burst heard (the ones for a third station too) and for each report for this station, in the order the bursts started, at the burst's end.
        var expected = MixedStream.ExpectedCalls(bursts);
        rig.Calls.Select(c => (c.IsReport, c.Sender, c.Dest)).Should().Equal(expected.Select(e => (e.IsReport, e.Sender, e.Dest)));
        for (int i = 0; i < expected.Count; i++)
        {
            rig.Calls[i].Now.Should().BeApproximately(expected[i].End / 48000.0, 4.0 / 48000, $"call {i}");
        }

        rig.Calls.Where(c => !c.IsReport).Should().OnlyContain(c => c.Decoded);

        // Replaying the same measurements by hand, in burst order, into a fresh set of rules gives the same state and the same answers.
        var fresh = new LinkAdaptation(MixedStream.Me);
        foreach (Station.AdaptationCall c in rig.Calls)
        {
            if (c.IsReport)
            {
                fresh.HeardReport(c.Sender, c.TheirReport!.Value.Format, c.TheirReport.Value.Failed, c.Now);
            }
            else
            {
                fresh.HeardData(c.Sender, c.Dest, c.Format, c.Decoded, c.Quality, c.Now, c.FootingDb, c.Sudden, c.TheirReport, c.Capability).Should().Be(c.Returned);
            }
        }

        ReceiveRig.Neighbours(rig.Station.Adaptation).Should().Be(ReceiveRig.Neighbours(fresh)).And.NotBeEmpty();
        rig.Station.Adaptation.TryGetNeighbour(2, out LinkNeighbour? two).Should().BeTrue();
        two!.Capability.Should().Be(0x1234);
        two.ReportFormat.Should().Be(80, "the last recommendation it gave: in the prelude of its tenth burst, after its report of 90");
        rig.Station.Adaptation.TryGetNeighbour(3, out LinkNeighbour? three).Should().BeTrue();
        three!.ReportFormat.Should().Be(120, "its report of 150 and then the recommendation in the prelude of its sixth burst");
        rig.Station.BurstsApplied.Should().Be(bursts.Count);
        rig.Station.BurstsDropped.Should().Be(0);
        rig.Station.BurstsLost.Should().Be(0);
    }
}
