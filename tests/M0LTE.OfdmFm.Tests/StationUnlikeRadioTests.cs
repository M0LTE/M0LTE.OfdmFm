using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Evidence for packet-net/pdn-soundmodem#885 and #886 (unlike radios), not a specification. A wide station and one whose receive audio is cut at 3 kHz exchange datagrams for a few minutes of simulated time over
/// a good path; what the stations do with the cut is recorded and pinned as it is today. The adaptation rules are not changed here: when a later change moves these numbers, the test is to be re-read, not
/// bent to pass. Step 5 recorded it with no lone reports; step 6 runs it again with reports flowing. Gated (<c>SM_MASK_GATE=1</c>): each case is about a minute of processor time.
/// </summary>
public class StationUnlikeRadioTests
{
    /// <summary>
    /// What a run did, for the bursts the wide station sent to the narrow one ("to narrow") and the other way ("to wide"): how many went, which (by order sent) were lost, how many went at a format that reaches above 3 kHz,
    /// the format of the last burst, the narrow station's estimate of the path from the wide one at 2 kHz and 6 kHz, in dB, the lone reports each station sent (step 6) and the hints the wide station was given that a burst was heard and not decoded.
    /// </summary>
    public sealed record Outcome(int ToNarrowSent, string ToNarrowLost, int ToNarrowAbove3k, int ToNarrowLast, int ToWideSent, string ToWideLost, int ToWideAbove3k, int ToWideLast, int Estimate2k, int Estimate6k, int ReportsByWide, int ReportsByNarrow, int FailedHints);

    private static Outcome Run(double gain, double? narrowReceiveHz, int pairs)
    {
        var rig = new SimRig(2, 4800, 1, 0.004, gain);
        rig.Nodes[1].ReceiveLowPassHz = narrowReceiveHz;
        rig.RunTo(6);
        for (int i = 0; i < pairs; i++)
        {
            rig.Send(1, 2, 1000 + i).Should().Be(SendResult.Accepted);
            rig.Run(4);
            rig.Send(2, 1, 2000 + i).Should().Be(SendResult.Accepted);
            rig.Run(4);
        }

        rig.Run(10);
        (int Sent, string Lost, int Above, int Last) Direction(SimNode from, SimNode to)
        {
            var got = to.Received.Select(r => r.Data).ToList();
            var lost = new List<int>();
            int wide = 0;
            int index = 0;
            foreach ((long _, TransmittedEvent e) in from.Transmitted)
            {
                if (!got.Any(d => d.SequenceEqual(SimRig.Datagram((int)e.Tag.Value, 30))))
                {
                    lost.Add(index);
                }

                if (StandardFormats.Describe(e.Format).TopHz > 3000)
                {
                    wide++;
                }

                index++;
            }

            return (from.Transmitted.Count, string.Join(",", lost), wide, from.Transmitted[^1].Event.Format);
        }

        var toNarrow = Direction(rig.Nodes[0], rig.Nodes[1]);
        var toWide = Direction(rig.Nodes[1], rig.Nodes[0]);
        rig.Stations[1].Adaptation.TryGetNeighbour(1, out LinkNeighbour? narrowsView).Should().BeTrue();
        int At(double hz) => (int)Math.Round(narrowsView!.EstimateDb[(int)Math.Round(hz / 23.4375) - 9]);
        return new Outcome(toNarrow.Sent, toNarrow.Lost, toNarrow.Above, toNarrow.Last, toWide.Sent, toWide.Lost, toWide.Above, toWide.Last, At(2000), At(6000), rig.Nodes[0].ModemSent.Count, rig.Nodes[1].ModemSent.Count, rig.Nodes[0].NotDecoded.Count);
    }

    private static void Gate() =>
        Assert.SkipWhen(Environment.GetEnvironmentVariable("SM_MASK_GATE") != "1", "set SM_MASK_GATE=1: minutes of simulated traffic between unlike radios, about a minute of processor time each");

    // What a run of 40 datagrams each way gave, measured on 2026-11-26 (build 0.1.0, step 5; step 6 added the last three figures and left every other one as it was, the mute after a station's own transmission having been shortened by then),
    // and again on 27 November 2026 with link addresses: every burst is a few bytes longer (its block of items), which moved which bursts were lost and a few other figures. What did not move: the same bursts go at the same ceilings of formats,
    // each loss is reported at once and named in a hint, nothing arrives twice, and the control loses nothing. "Lost" is the index, in the order sent, of each burst whose datagram never arrived.
    // 27 November 2026, packet-net/pdn-soundmodem#918: the rules were given a placeholder above each burst's band as if it were a measurement, which held every estimate down above 1.5 kHz after any narrow burst. With NaN there instead
    // every row moved, and the old figures are kept beside each new one below ("was"). The same invariants hold on every row: each loss is reported at once and named in a hint (lost bursts equal hints), nothing arrives twice, the control loses nothing.
    public static TheoryData<double, double, Outcome> Cases => new()
    {
        // A good path: the narrow station is sent formats up to 6.5 and 8 kHz wide for 37 of 40 bursts and loses four of them (seven before link addresses), spread right through the run, and each is reported at once and named in a hint (four failed reports from the narrow
        // station, one other, and the wide station's one); reports change nothing about which formats it is sent. It hears the wide station's fast formats well enough to settle on 28
        // (48.5 kbit/s, band 0) and 61 and 62 (band 1) for it, and 70 kbit/s the other way, where nothing is lost. Its estimate of the path from the wide station falls from 40 dB at 2 kHz to 19 dB at 6 kHz (39 and 17 before link addresses): it does know.
        // Now 6 lost of 40 one way (was 4: "7,17,31,39"), 39 of 40 above 3 kHz (was 37), the narrow station sending 9 reports (was 5) and 6 hints (was 4); the wide station is sent above 3 kHz 40 of 40 times (was 38).
        { 1.0, 3000, new Outcome(40, "2,4,10,17,33,39", 39, 28, 40, "", 40, 31, 40, 19, 1, 9, 6) },

        // A weaker path (20 dB down): the same pattern, four lost of 40 one way (five before link addresses) and one the other.
        // Now 5 lost of 40 one way (was 4: "12,17,22,39"), above 3 kHz 39 (was 37) and 40 the other way (was 38), 5 hints (was 4).
        { 0.1, 3000, new Outcome(40, "3,13,17,22,39", 39, 28, 40, "25", 40, 30, 35, 18, 2, 7, 5) },

        // A path 30 dB down. Now the narrow station is sent formats above 3 kHz 39 times of 40, settling at 25 (was: its estimate showed nothing above 3 kHz, -4 dB at 6 kHz, and the wide station settled on the 3 kHz band, format 158, 22.4 kbit/s, losing nothing), loses 2 of 40 (bursts 8 and 13), each named in a hint, and its estimate at 6 kHz is 13 dB. The old figure came from the placeholder above each narrow burst's band, not from the path.
        { 0.03, 3000, new Outcome(40, "8,13", 39, 25, 40, "", 40, 26, 24, 13, 1, 3, 2) },

        // The control: no cut, a good path: nothing lost either way, and the same ceiling of formats.
        // Was 38 and 39 above 3 kHz.
        { 1.0, 0, new Outcome(40, "", 39, 31, 40, "", 40, 31, 54, 46, 1, 1, 0) },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Records_Present_Behaviour_Between_A_Wide_Station_And_One_Cut_At_3_kHz(double gain, double cutHz, Outcome expected)
    {
        Gate();
        Outcome o = Run(gain, cutHz == 0 ? null : cutHz, 40);
        Hostile.Say($"unlike radios, gain {gain}, cut {cutHz}: {o}");
        o.Should().Be(expected, "this records what the rules do today; if a change moves it on purpose, re-read it and update the numbers");
    }
}
