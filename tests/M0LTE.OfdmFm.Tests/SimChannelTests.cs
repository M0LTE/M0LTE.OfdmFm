using System.Globalization;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>The simulated channel's own knobs (the unlike-radio settings of packet-net/pdn-soundmodem#885), each shown to do what it says. Everything off is the default; the long runs are in <see cref="StationUnlikeRadioTests"/>.</summary>
public class SimChannelTests
{
    private static double GainDb(Filter filter, double hz)
    {
        var x = new float[48000];
        for (int i = 0; i < x.Length; i++)
        {
            x[i] = (float)Math.Sin(2 * Math.PI * hz * i / 48000);
        }

        filter.Process(x);
        double sum = 0;
        for (int i = 24000; i < x.Length; i++)
        {
            sum += x[i] * x[i];
        }

        return 10 * Math.Log10(sum / 24000 * 2);
    }

    [Fact]
    public void The_Low_Pass_Passes_Below_3_kHz_And_Cuts_Hard_Above()
    {
        GainDb(new Filter(3000), 1000).Should().BeApproximately(0, 0.5);
        GainDb(new Filter(3000), 3000).Should().BeApproximately(-3, 0.7);
        GainDb(new Filter(3000), 6300).Should().BeLessThan(-24);
        GainDb(new Filter(3000), 8000).Should().BeLessThan(-32);
    }

    [Fact]
    public void The_Fm_Noise_Is_Flat_Below_300_Hz_And_Rises_20_dB_A_Decade_Above()
    {
        var noise = new FmNoise(9, 0.1);
        var block = new float[48000 * 20];
        noise.AddTo(block);
        double rms = Math.Sqrt(block.Select(v => (double)v * v).Average());
        rms.Should().BeApproximately(0.1, 0.005);

        // A first-order rise with its corner at 300 Hz: 350 Hz is 3.8 dB up on the flat part and 3050 Hz is 20 dB, so the two bands differ by about 16 dB.
        static double Band(float[] x, double lo, double hi)
        {
            double sum = 0;
            for (double f = lo; f < hi; f += 10)
            {
                double re = 0, im = 0;
                for (int i = 0; i < 48000 * 2; i++)
                {
                    double w = 2 * Math.PI * f * i / 48000;
                    re += x[i] * Math.Cos(w);
                    im += x[i] * Math.Sin(w);
                }

                sum += (re * re) + (im * im);
            }

            return sum;
        }

        double low = Band(block, 300, 400);
        double high = Band(block, 3000, 3100);
        (10 * Math.Log10(high / low)).Should().BeInRange(14, 19);
    }

    [Fact]
    public void A_Carrier_Indication_Follows_The_Transmission_By_The_Delay_And_Not_At_All_When_Not_Configured()
    {
        long Run(int? delay)
        {
            var rig = new SimRig(2);
            rig.Nodes[1].CarrierDelaySamples = delay;
            rig.RunTo(6);
            rig.Send(1, 2, 1, priority: SendPriority.Expedited);
            rig.Run(4);
            string busy = rig.Channel.Log.First(l => l.Contains(" S2 busy True", StringComparison.Ordinal));
            return long.Parse(busy[..busy.IndexOf(' ')], CultureInfo.InvariantCulture) - rig.Nodes[0].Transmissions[0].Start;
        }

        // Without an indication the station notices the burst by finding it, hundreds of milliseconds in; with one it is told 2400 samples (50 ms) after the carrier is up.
        Run(null).Should().BeGreaterThan(10000);
        Run(2400).Should().Be(2400);
    }

    [Fact]
    public void With_A_Carrier_Indication_A_Burst_Holds_The_Channel_From_The_Carrier_To_Its_End_And_Traffic_Waits()
    {
        var rig = new SimRig(2);
        rig.Nodes[1].CarrierDelaySamples = 2400;
        rig.RunTo(6);
        rig.Send(1, 2, 1, priority: SendPriority.Expedited);
        rig.Run(0.3);
        rig.Send(2, 1, 2);
        rig.Run(20);

        SimTransmission first = rig.Nodes[0].Transmissions[0];
        string[] busy = [.. rig.Channel.Log.Where(l => l.Contains(" S2 busy ", StringComparison.Ordinal))];
        long At(string line) => long.Parse(line[..line.IndexOf(' ')], CultureInfo.InvariantCulture);
        busy[0].Should().EndWith("busy True");
        At(busy[0]).Should().Be(first.Start + 2400, "busy as soon as the radio reports the carrier");
        At(busy[1]).Should().BeGreaterThanOrEqualTo(first.Start + first.Length, "and until the burst is over");
        rig.Nodes[1].Transmissions[0].Start.Should().BeGreaterThan(first.Start + first.Length, "station 2's datagram waits for it");
        rig.Nodes[1].Received.Should().ContainSingle();
    }

    [Fact]
    public void With_A_Carrier_Indication_A_Burst_The_Radio_Does_Not_Report_Is_Still_Decoded_And_Holds_The_Channel_Once_Its_Header_Reads()
    {
        var rig = new SimRig(2);
        rig.Nodes[1].CarrierDelaySamples = 2400;
        rig.Channel.Link(rig.Nodes[0], rig.Nodes[1]).CarrierHeard = false;
        long held = 0;
        long readAt = -1;
        rig.Stations[1].BurstKnown = (now, until) =>
        {
            held = Math.Max(held, until);
            readAt = readAt < 0 ? now : readAt;
        };
        rig.RunTo(6);
        rig.Send(1, 2, 1, priority: SendPriority.Expedited);
        rig.Run(20);

        SimTransmission first = rig.Nodes[0].Transmissions[0];
        rig.Nodes[1].Received.Should().ContainSingle("a candidate is read whatever the indication says");
        held.Should().BeGreaterThanOrEqualTo(first.Start + first.Length - BurstRenderer.DefaultLeadSamples, "the header that read holds the channel to the burst's end");
        string busy = rig.Channel.Log.First(l => l.Contains(" S2 busy True", StringComparison.Ordinal));
        long.Parse(busy[..busy.IndexOf(' ')], CultureInfo.InvariantCulture).Should().Be(readAt, "with no carrier reported, the channel turns busy when the header reads");
    }

    [Fact]
    public void A_Station_Deaf_After_Its_Own_Transmission_Misses_The_Start_Of_A_Reply()
    {
        int Replies(int deaf)
        {
            var rig = new SimRig(2);
            rig.Nodes[0].DeafSamples = deaf;
            rig.RunTo(6);
            rig.Send(1, 2, 1, priority: SendPriority.Expedited);
            rig.Run(1.5);
            rig.Send(2, 1, 2);
            rig.Run(25);
            return rig.Nodes[0].Received.Count;
        }

        // Station 2's ordinary reply starts 0.8 s or more after station 1's burst ends and its opening is over by 2.1 s; a station deaf for 3 s after transmitting hears none of it.
        Replies(0).Should().Be(1);
        Replies(144000).Should().Be(0);
    }

    [Fact]
    public void A_Reply_In_The_First_Slot_Is_Heard_By_The_Station_That_Just_Transmitted()
    {
        // An expedited reply goes 50 ms after the channel clears and its first training sample is a lead-in later, 4224 samples (88 ms), so 138 ms after the end of the burst it answers. Step 4 ignored candidates for 0.25 s after a
        // station's own transmission and so made the station deaf to exactly these replies (step 5 pinned the fault); the mute is now the own tail, 100 ms, which ends before the reply's first training sample with a margin of 38 ms.
        var rig = new SimRig(2);
        rig.RunTo(6);
        rig.Send(1, 2, 1, priority: SendPriority.Expedited);
        rig.Run(1.5);
        rig.Send(2, 1, 2, priority: SendPriority.Expedited);
        rig.Run(20);
        rig.Nodes[1].Received.Should().HaveCount(1);
        long end = rig.Nodes[0].Transmissions[0].Start + rig.Nodes[0].Transmissions[0].Length;
        long reply = rig.Nodes[1].Transmissions[0].Start;
        reply.Should().Be(end + ChannelAccess.FirstSlotClear, "the first slot");
        (reply + BurstRenderer.DefaultLeadSamples - end).Should().Be(6624).And.BeGreaterThan(Station.MuteAfterOwnTransmission);
        rig.Nodes[0].Received.Should().ContainSingle("the reply in the first slot is heard").Which.Data.Should().Equal(SimRig.Datagram(2, 30));
        rig.Nodes[0].Received[0].Clock.Should().BeGreaterThan(reply);
    }

    [Fact]
    public void A_Key_Up_Delay_Longer_Than_The_Lead_In_Cuts_Off_The_Start_Of_The_Burst()
    {
        int Heard(int keyUp)
        {
            var rig = new SimRig(2);
            rig.Nodes[0].KeyUpSamples = keyUp;
            rig.RunTo(6);
            rig.Send(1, 2, 1, priority: SendPriority.Expedited);
            rig.Run(8);
            return rig.Nodes[1].Received.Count;
        }

        // The lead-in is 4224 samples of silence and the first-contact opening has six training symbols after it: losing 2000 samples costs nothing, and losing half a second takes the training and the header.
        Heard(2000).Should().Be(1);
        Heard(24000).Should().Be(0);
    }

    [Fact]
    public void A_Clock_Offset_Between_The_Stations_Is_Resampled_And_The_Burst_Still_Decodes_At_Every_Opening()
    {
        // Each way at 150 ppm in opposite directions; the stations climb off the first-contact format in two bursts, so the later bursts use the other openings.
        var rig = new SimRig(2);
        rig.Channel.Link(rig.Nodes[0], rig.Nodes[1]).Ppm = 150;
        rig.Channel.Link(rig.Nodes[1], rig.Nodes[0]).Ppm = -120;
        rig.RunTo(6);
        for (int i = 0; i < 3; i++)
        {
            rig.Send(1, 2, 10 + i);
            rig.Run(8);
            rig.Send(2, 1, 20 + i);
            rig.Run(8);
        }

        rig.Nodes[1].Received.Should().HaveCount(3);
        rig.Nodes[0].Received.Should().HaveCount(3);
        rig.Nodes[0].Transmitted.Select(t => t.Event.Format).Distinct().Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public void A_Path_Delay_Shifts_What_The_Other_Station_Hears()
    {
        var rig = new SimRig(2, connect: false);
        rig.Channel.Connect(rig.Nodes[0], rig.Nodes[1], 1.0, 0.004, delaySamples: 9600);
        rig.RunTo(6);
        rig.Send(1, 2, 1, priority: SendPriority.Expedited);
        rig.Run(4);
        rig.Nodes[1].Received.Should().HaveCount(1);
        string busy = rig.Channel.Log.First(l => l.Contains(" S2 busy True", StringComparison.Ordinal));
        long at = long.Parse(busy[..busy.IndexOf(' ')], CultureInfo.InvariantCulture);
        at.Should().BeGreaterThan(rig.Nodes[0].Transmissions[0].Start + 9600, "nothing can be heard before it arrives");
    }
}
