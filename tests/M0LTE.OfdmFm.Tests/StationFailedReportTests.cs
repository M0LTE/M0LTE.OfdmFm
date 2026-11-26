using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// A bug the step 8 soak found (seed 5, a mean gap of 2 s, detection alone): a station sent a "failed" report about an older burst after a newer burst from the same station had decoded, and the sender's hint named the newer burst, which
/// had arrived. The failed bit says "your last burst reached me and I could not decode it", which was no longer true.
/// </summary>
public class StationFailedReportTests
{
    private static SimRig Settled(double gain = 0.1, int bursts = 8)
    {
        var rig = new SimRig(2, gain: gain);
        rig.RunTo(6);
        for (int i = 0; i < bursts; i++)
        {
            rig.Send(1, 2, 100 + i, reportWanted: true).Should().Be(SendResult.Accepted);
            rig.Run(6);
        }

        return rig;
    }

    [Fact]
    public void A_Failed_Verdict_Followed_By_A_Burst_From_The_Same_Station_That_Decoded_Is_Reported_As_The_Recommendation_Alone_And_The_Hint_Does_Not_Name_The_Burst_That_Arrived()
    {
        SimRig rig = Settled();
        SimNode a = rig.Nodes[0];
        SimNode b = rig.Nodes[1];
        int sent = a.Transmitted.Count;
        int received = b.Received.Count;

        // B's verdicts are parked so that both land together, as they did in the soak when the second burst was applied before the report had been granted.
        rig.Stations[1].ReceivesHeld = true;
        double gain = rig.Channel.Link(a, b).Gain;
        rig.Channel.Link(a, b).Gain = gain * SimChannel.Db(-10);
        rig.Send(1, 2, 500).Should().Be(SendResult.Accepted);
        for (int i = 0; i < 120 && a.Transmitted.Count == sent; i++)
        {
            rig.Run(0.25);
        }

        a.Transmitted.Count.Should().Be(sent + 1, "the first burst, the one that will fail, has gone");
        rig.Channel.Link(a, b).Gain = gain;
        rig.Send(1, 2, 501, priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
        for (int i = 0; i < 120 && a.Transmitted.Count == sent + 1; i++)
        {
            rig.Run(0.25);
        }

        a.Transmitted.Count.Should().Be(sent + 2, "the second burst has gone, before any word from B");
        rig.Run(3.0);
        a.NotDecoded.Should().BeEmpty();

        rig.Stations[1].ReceivesHeld = false;
        rig.Run(12);

        // B decoded the second burst and not the first.
        b.Received.Count.Should().Be(received + 1);
        b.Received[^1].Data.Should().Equal(SimRig.Datagram(501, 30));

        // B owed a failed report for the first and then decoded the second: the report that went says nothing failed, and A is not told that the second burst (which arrived) was not decoded.
        a.NotDecoded.SelectMany(h => h.Tags).Should().NotContain(501L, "datagram 501 arrived");
        a.PathReports.Count(p => p.Failed).Should().Be(0);
    }
}
