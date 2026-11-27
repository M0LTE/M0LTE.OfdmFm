using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// packet-net/pdn-soundmodem#918: the receiver holds a placeholder on every carrier above the band a burst used, and the station must hand the rules NaN there.
/// What the rules are given is checked as the station gives it, and what comes of it for a neighbour whose wide-band estimate is good.
/// </summary>
public class QualityAboveBandTests
{
    private const int FullBand = 12;
    private const int ThreeKilohertz = (4 * 32) + 8;
    // White noise over the whole stream, the level the simulated links use: a path with a steady quality, which a noiseless one is not.
    private const double PathNoise = 0.004;
    private static readonly int FirstContact = StandardFormats.FirstContact;

    private static List<Station.AdaptationCall> Heard(params int[] formats)
    {
        using var rig = new ReceiveRig(0);
        var builder = new StreamBuilder().Silence(1.5);
        int n = 0;
        foreach (int format in formats)
        {
            builder.Add(TestBurst.Data(7, 1, format, [TestBurst.Datagram(3, 40)], burstNumber: n++));
            builder.Silence(1.5);
        }

        builder.Silence(1.5);
        rig.Push(builder.ToArray(), () => 480);
        rig.Finish();
        return [.. rig.Calls.Where(c => !c.IsReport)];
    }

    [Fact]
    public void What_The_Rules_Are_Given_Is_NaN_Exactly_Above_The_Carriers_The_Payload_Used_And_Finite_Below()
    {
        // One link, three bursts: first contact (1.5 kHz), a 3 kHz format and a full-band format.
        int[] formats = [FirstContact, ThreeKilohertz, FullBand];
        List<Station.AdaptationCall> calls = Heard(formats);
        calls.Should().HaveCount(3);
        for (int i = 0; i < 3; i++)
        {
            Station.AdaptationCall c = calls[i];
            c.Decoded.Should().BeTrue();
            int used = StandardFormats.Describe(formats[i]).Carriers;
            c.Quality.Should().HaveCount(StandardFormats.FullBandCarriers);
            for (int k = 0; k < c.Quality.Length; k++)
            {
                double.IsNaN(c.Quality[k]).Should().Be(k >= used, "format {0}, carrier {1}: payload used {2}", formats[i], k, used);
            }
        }

        StandardFormats.Describe(FirstContact).Carriers.Should().BeLessThan(StandardFormats.Describe(ThreeKilohertz).Carriers);
        StandardFormats.Describe(ThreeKilohertz).Carriers.Should().BeLessThan(StandardFormats.FullBandCarriers);
    }

    [Fact]
    public void A_Clean_First_Contact_Burst_From_A_Neighbour_Heard_Wide_Does_Not_Pull_Its_Estimate_Above_The_Band_Or_Its_Recommendation_Down()
    {
        using var rig = new ReceiveRig(0);
        var builder = new StreamBuilder().Silence(1.5);
        int n = 0;
        for (int i = 0; i < 5; i++)
        {
            builder.Add(TestBurst.Data(7, 1, FullBand, [TestBurst.Datagram(3, 40)], burstNumber: n++));
            builder.Silence(2.0);
        }

        builder.Noise(PathNoise, 21);
        rig.Push(builder.ToArray(), () => 480);
        rig.Finish();
        rig.Station.Adaptation.TryGetNeighbour(7, out LinkNeighbour? nb).Should().BeTrue();
        double[] before = [.. nb!.EstimateDb.ToArray()];
        int? recommended = nb.Recommended;
        recommended.Should().NotBeNull();
        StandardFormats.Rate(recommended!.Value).Should().BeGreaterThan(StandardFormats.Rate(FirstContact) * 10, "the premise: a good wide-band estimate");
        before.Skip(56).Should().OnlyContain(v => double.IsFinite(v));

        // The same link, now a clean burst at first contact, then another.
        var more = new StreamBuilder().Silence(1.5);
        for (int i = 0; i < 2; i++)
        {
            more.Add(TestBurst.Data(7, 1, FirstContact, [TestBurst.Datagram(3, 40)], burstNumber: n++));
            more.Silence(2.0);
        }

        more.Silence(2.0).Noise(PathNoise, 22);
        rig.Push(more.ToArray(), () => 480);
        rig.Finish();
        double[] after = [.. nb.EstimateDb.ToArray()];
        for (int k = 56; k < after.Length; k++)
        {
            Math.Abs(after[k] - before[k]).Should().BeLessThan(2.0, "carrier {0} above the first-contact band moved only by what the measured ones give", k);
        }

        nb.Recommended.Should().Be(recommended, "a clean first-contact burst on an unchanged path leaves the recommendation where it was");
    }
}
