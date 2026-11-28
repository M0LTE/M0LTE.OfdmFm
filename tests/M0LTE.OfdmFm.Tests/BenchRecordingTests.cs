using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Short excerpts of what two unsquelched FM receivers on a bench gave the modem (48 kHz, 16-bit mono, as the modem saw it, 28 November 2026), played through a listening station in a sound card's 30 ms blocks,
/// each way the jobs can run: inline, and as a host's worker runs them (taken up after each block, a header read taken in at the next). In Recordings:
/// <list type="bullet">
/// <item><c>noise-1.wav</c>, <c>noise-2.wav</c>: 7 s each of open-channel hiss, nobody transmitting, from each of the two receivers (about -21 dBFS rms).</item>
/// <item><c>bursts-and-noise.wav</c>: 18 s from one receiver: hiss, a datagram burst from the other station, its identification burst followed by its callsign in Morse, a second datagram, and hiss again.</item>
/// </list>
/// With the receiver's squelch open, the modem hears loud hiss whenever nobody transmits. Noise gives candidates, more of them for a while after another station's carrier has been heard. Each candidate whose header did not read
/// used to hold the channel for its opening and a second more, and they ran together into spells of tens of seconds in which nothing was on the air. These pin what the channel now does on that noise, and that real bursts in it are
/// still found, decoded and taken as busy from their start.
/// </summary>
public class BenchRecordingTests
{
    private const int Rate = 48000;

    /// <summary>The station transmits nothing in its first 5 s (the threshold's start-up floor); the channel is judged from there.</summary>
    private const long From = 5 * Rate;

    /// <summary>
    /// Noise alone may hold the channel for at most this share of the time. A candidate now holds it only while it waits to be judged: until the detector has correlated its opening (at most a detector frame, 0.42 s) and the header has been
    /// read. On these receivers noise gives a few candidates a minute on open channel and several a second just after a carrier, so the share is a few percent at most, whatever the level of the noise; a hold of a second for each,
    /// as before, made it 10 to 45 percent.
    /// </summary>
    private const double MostBusyShare = 0.05;

    /// <summary>
    /// Noise alone may hold the channel for at most this long in one spell: a detector frame (0.42 s) and a few blocks for the reads of the candidates that frame gave. Before, single spells of 2 s were common on open channel and
    /// ran to 7 to 17 s after a carrier.
    /// </summary>
    private const double LongestSpellSeconds = 0.6;

    private static float[] Recording(string name) => BenchRecording.ReadWav(Path.Combine(AppContext.BaseDirectory, "Recordings", name));

    /// <summary>The noise excerpt played <paramref name="times"/> times over, so that the station has 30 s and more of it (the detector's estimate of the noise covers 30 s).</summary>
    private static float[] Looped(string name, int times)
    {
        float[] one = Recording(name);
        var audio = new float[one.Length * times];
        for (int i = 0; i < times; i++)
        {
            one.CopyTo(audio, i * one.Length);
        }

        return audio;
    }

    [Theory]
    [InlineData("noise-1.wav", true)]
    [InlineData("noise-1.wav", false)]
    [InlineData("noise-2.wav", true)]
    [InlineData("noise-2.wav", false)]
    public void Open_channel_noise_holds_the_channel_for_a_small_share_of_the_time_and_never_for_long(string name, bool inline)
    {
        float[] audio = Looped(name, 5);
        BenchRecording r = BenchRecording.Play(audio, inline);

        r.Judged.Should().NotContain(j => j.Read, "nothing was transmitting");
        long busy = r.BusySamples(From, r.Length);
        ((double)busy / (r.Length - From)).Should().BeLessThan(MostBusyShare, r.Describe());
        (r.LongestSpell(From, r.Length) / (double)Rate).Should().BeLessThan(LongestSpellSeconds, r.Describe());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void After_an_identification_and_its_Morse_the_noise_does_not_hold_the_channel(bool inline)
    {
        BenchRecording r = BenchRecording.Play(Recording("bursts-and-noise.wav"), inline);

        (long total, long longest) = r.Unexplained(From, r.Length);
        ((double)total / (r.Length - From)).Should().BeLessThan(MostBusyShare, r.Describe());
        (longest / (double)Rate).Should().BeLessThan(LongestSpellSeconds, r.Describe());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_real_bursts_in_the_noise_are_found_decoded_and_busy_from_their_start(bool inline)
    {
        BenchRecording r = BenchRecording.Play(Recording("bursts-and-noise.wav"), inline);

        // A datagram at 0.9 s, the identification at 6.7 s with 6.8 s of Morse after it, and a datagram at 14.0 s (all to another station, which is what this one overheard).
        List<(long Start, long End)> bursts = r.Bursts();
        bursts.Select(b => b.Start / (double)Rate).Should().HaveCount(3).And.SatisfyRespectively(
            a => a.Should().BeApproximately(0.89, 0.02),
            b => b.Should().BeApproximately(6.67, 0.02),
            c => c.Should().BeApproximately(13.98, 0.02));
        r.Identified.Should().Be(1, "the identification was decoded");
        r.HeardLines.Should().Be(3, "each burst was decoded:\n" + r.Describe());

        // Busy from no later than the station can know of a burst (the detection latency of the slowest opening, counted from its first sample) to its end, the identification's Morse included.
        List<(long Start, long End, string Cause)> spells = r.Spells();
        foreach ((long start, long end) in bursts)
        {
            spells.Should().Contain(s => s.Start <= start + Station.DetectionLatencySamples && s.End >= end, $"the burst at {start / (double)Rate:0.00} s is busy from its start to its end");
        }

        (bursts[1].End - bursts[1].Start).Should().BeGreaterThan(13 * Rate / 2, "the identification holds the channel through its Morse");
    }

    [Theory]
    [InlineData("noise-1.wav", true)]
    [InlineData("noise-1.wav", false)]
    [InlineData("noise-2.wav", true)]
    [InlineData("noise-2.wav", false)]
    public void With_a_carrier_indication_reading_no_carrier_the_noise_holds_the_channel_for_none_of_the_time(string name, bool inline)
    {
        BenchRecording r = BenchRecording.Play(Looped(name, 5), inline, carrier: false);

        r.CandidatesSeen.Should().BeGreaterThan(0, "the noise still gives candidates, and each is still read");
        r.BusySamples(0, r.Length).Should().Be(0, r.Describe());
    }
}
