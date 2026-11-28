using System.Globalization;
using System.Text;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Plays a recording of received audio through a listening station in the block size a sound card delivers, and records on the sample clock everything that bears on the channel being busy:
/// each change of the channel with its cause, each candidate's header read and its outcome, each hold the station took, and the datagrams and identifications it heard.
/// </summary>
internal sealed class BenchRecording
{
    /// <summary>A 30 ms capture period, as the bench's sound cards deliver.</summary>
    public const int Block = 1440;

    // The clock of each hold for an identification's tail (its trace line is said as the hold is taken).
    private readonly List<long> _tails = [];

    private BenchRecording()
    {
    }

    /// <summary>Each change of the channel: the clock and the new state, and the cause the trace gave for a change to busy.</summary>
    public List<(long At, bool Busy, string Cause)> Changes { get; } = [];

    /// <summary>Each candidate whose header was read: its first sample, its opening, whether it read, and the clock when the read was taken in.</summary>
    public List<(long Index, FormatOpening Opening, bool Read, long At)> Judged { get; } = [];

    /// <summary>Each candidate the detector gave: first sample, opening, peak.</summary>
    public List<(long Index, FormatOpening Opening, double Peak)> Found { get; } = [];

    /// <summary>The thresholds of the three openings at the end of each block.</summary>
    public List<(long At, double Full, double Mid, double Narrow)> Thresholds { get; } = [];

    /// <summary>Each hold the station took: the clock and the sample it holds to.</summary>
    public List<(long Now, long Until)> Holds { get; } = [];

    /// <summary>Every trace line, Detail level.</summary>
    public List<TraceEvent> Lines { get; } = [];

    /// <summary>Datagrams received.</summary>
    public int Received { get; private set; }

    /// <summary>Trace lines saying a burst was heard and decoded ("Heard ...").</summary>
    public int HeardLines => Lines.Count(l => l.Text.StartsWith("Heard ", StringComparison.Ordinal));

    /// <summary>Stations heard identifying.</summary>
    public int Identified { get; private set; }

    /// <summary>Candidates whose header would not read, or read as nothing a burst can be.</summary>
    public long CameToNothing { get; private set; }

    /// <summary>The samples played.</summary>
    public long Length { get; private set; }

    /// <summary>Candidates the detector gave, all told.</summary>
    public long CandidatesSeen { get; private set; }

    /// <summary>
    /// Plays <paramref name="audio"/> through a new listening station (no identity: it never transmits) in <see cref="Block"/>-sample blocks. Jobs run inline, or (<paramref name="inline"/> false) as a host's
    /// worker runs them: after each block, as if a worker took them up at once, so that a header read is taken in at the next block, as on a station with worker threads.
    /// </summary>
    /// <param name="carrier">Null: no carrier indication, detection alone. Otherwise the indication the host gives from the start, held there.</param>
    public static BenchRecording Play(float[] audio, bool inline = true, bool? carrier = null, int seed = 1)
    {
        var r = new BenchRecording();
        var station = new Station(new Random(seed), runJobsInline: inline) { TraceLevel = TraceLevel.Detail };
        if (carrier is { } c)
        {
            station.SetExternalBusy(c);
        }

        string cause = "";
        station.Trace += e =>
        {
            r.Lines.Add(e);
            if (e.Text.StartsWith("Holding all traffic", StringComparison.Ordinal))
            {
                r._tails.Add(station.SampleCount);
            }

            const string prefix = "Channel busy: ";
            if (e.Text.StartsWith(prefix, StringComparison.Ordinal))
            {
                cause = e.Text[prefix.Length..];
            }
        };
        station.ChannelBusy += e =>
        {
            r.Changes.Add((station.SampleCount, e.Busy, ""));
        };
        station.CandidateJudged = (index, opening, read, at) => r.Judged.Add((index, opening, read, at));
        station.BurstKnown = (now, until) => r.Holds.Add((now, until));
        station.CandidateFound = (index, opening, peak) => r.Found.Add((index, opening, peak));
        station.Received += _ => r.Received++;
        station.Identified += _ => r.Identified++;
        for (int at = 0; at < audio.Length; at += Block)
        {
            int n = Math.Min(Block, audio.Length - at);
            station.Push(audio.AsSpan(at, n));
            if (!inline)
            {
                station.RunPending();
            }
            if ((at / Block) % 10 == 0)
            {
                r.Thresholds.Add((station.SampleCount, station.DetectorThreshold(FormatOpening.Full), station.DetectorThreshold(FormatOpening.UpTo3000Hz), station.DetectorThreshold(FormatOpening.UpTo1500Hz)));
            }

            // The busy line comes after the event in the trace: attach the cause to the last change.
            if (r.Changes.Count > 0 && r.Changes[^1].Busy && r.Changes[^1].Cause.Length == 0 && cause.Length > 0)
            {
                r.Changes[^1] = (r.Changes[^1].At, true, cause);
                cause = "";
            }
        }

        r.Length = audio.Length;
        r.CandidatesSeen = station.CandidatesSeen;
        r.CameToNothing = station.CandidatesCameToNothing;
        return r;
    }

    /// <summary>The busy spells: start, end (the recording's end if still busy) and the cause at the start.</summary>
    public List<(long Start, long End, string Cause)> Spells()
    {
        var spells = new List<(long, long, string)>();
        long? start = null;
        string cause = "";
        foreach ((long at, bool busy, string c) in Changes)
        {
            if (busy && start is null)
            {
                start = at;
                cause = c;
            }
            else if (!busy && start is { } s)
            {
                spells.Add((s, at, cause));
                start = null;
            }
        }

        if (start is { } open)
        {
            spells.Add((open, Length, cause));
        }

        return spells;
    }

    /// <summary>The samples the channel read busy in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public long BusySamples(long from, long to)
    {
        long total = 0;
        foreach ((long s, long e, _) in Spells())
        {
            long a = Math.Max(s, from);
            long b = Math.Min(e, to);
            if (b > a)
            {
                total += b - a;
            }
        }

        return total;
    }

    /// <summary>The longest busy spell starting in [<paramref name="from"/>, <paramref name="to"/>), clipped to the range, samples.</summary>
    public long LongestSpell(long from, long to)
    {
        long longest = 0;
        foreach ((long s, long e, _) in Spells())
        {
            long a = Math.Max(s, from);
            long b = Math.Min(e, to);
            longest = Math.Max(longest, b - a);
        }

        return longest;
    }

    /// <summary>The bursts whose header read: the first sample and the sample the station held the channel to for it (the latest hold taken as the header was taken in or later from its decode, such as an identification's tail).</summary>
    public List<(long Start, long End)> Bursts()
    {
        var bursts = new List<(long, long)>();
        foreach (var j in Judged)
        {
            if (!j.Read)
            {
                continue;
            }

            long end = j.Index;
            foreach ((long now, long until) in Holds)
            {
                if (now == j.At)
                {
                    end = Math.Max(end, until);
                }
            }

            bursts.Add((j.Index, end));
        }

        // An identification's tail is taken when its decode is applied, up to a frame after the burst's end: it extends the burst it follows.
        for (int i = 0; i < bursts.Count; i++)
        {
            (long start, long end) = bursts[i];
            foreach ((long now, long until) in Holds)
            {
                if (_tails.Contains(now) && now >= end && now <= end + StreamingBurstDetector.Hop + Block && until > end)
                {
                    end = until;
                }
            }

            bursts[i] = (start, end);
        }

        return bursts;
    }

    /// <summary>
    /// The samples in [<paramref name="from"/>, <paramref name="to"/>) the channel read busy outside every burst whose header read (from its first sample to its end, <see cref="Bursts"/>): the total, and the longest unbroken run of them.
    /// </summary>
    public (long Total, long Longest) Unexplained(long from, long to)
    {
        List<(long Start, long End)> bursts = Bursts();
        long total = 0;
        long longest = 0;
        foreach ((long s, long e, _) in Spells())
        {
            long run = 0;
            for (long x = Math.Max(s, from); x < Math.Min(e, to); x++)
            {
                bool inside = false;
                foreach ((long bs, long be) in bursts)
                {
                    inside |= x >= bs && x < be;
                }

                if (inside)
                {
                    run = 0;
                    continue;
                }

                total++;
                longest = Math.Max(longest, ++run);
            }
        }

        return (total, longest);
    }

    /// <summary>A readable account: every spell, candidate and hold, by the sample clock in seconds.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        static string S(long x) => (x / 48000.0).ToString("0.000", CultureInfo.InvariantCulture);
        sb.Append(CultureInfo.InvariantCulture, $"length {S(Length)} s, candidates {CandidatesSeen}, judged {Judged.Count}, read {Judged.Count(j => j.Read)}, received {Received}, identified {Identified}\n");
        foreach ((long s, long e, string c) in Spells())
        {
            sb.Append(CultureInfo.InvariantCulture, $"SPELL {S(s)} .. {S(e)} ({S(e - s)} s) {c}\n");
        }

        foreach (var j in Judged)
        {
            sb.Append(CultureInfo.InvariantCulture, $"CAND {S(j.Index)} {j.Opening} read={j.Read} at {S(j.At)}\n");
        }

        foreach (var f in Found)
        {
            sb.Append(CultureInfo.InvariantCulture, $"FOUND {S(f.Index)} {f.Opening} {f.Peak:0.0000}\n");
        }

        foreach (var t in Thresholds)
        {
            sb.Append(CultureInfo.InvariantCulture, $"THR {S(t.At)} {t.Full:0.0000} {t.Mid:0.0000} {t.Narrow:0.0000}\n");
        }

        foreach (var h in Holds)
        {
            sb.Append(CultureInfo.InvariantCulture, $"HOLD at {S(h.Now)} until {S(h.Until)}\n");
        }

        foreach (TraceEvent l in Lines)
        {
            sb.Append("TRACE ").Append(l.ToString()).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Reads a 16-bit mono 48 kHz WAV into floats in [-1, 1).</summary>
    public static float[] ReadWav(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
        {
            throw new InvalidDataException($"{path} is not a WAV file.");
        }

        int p = 12;
        int channels = 0, rate = 0, bits = 0;
        while (p + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b, p, 4);
            int size = BitConverter.ToInt32(b, p + 4);
            int body = p + 8;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToInt16(b, body + 14);
            }
            else if (id == "data")
            {
                if (channels != 1 || rate != 48000 || bits != 16)
                {
                    throw new InvalidDataException($"{path}: {channels} channels, {rate} Hz, {bits} bits; 1, 48000 and 16 expected.");
                }

                int n = Math.Min(size, b.Length - body) / 2;
                var audio = new float[n];
                for (int i = 0; i < n; i++)
                {
                    audio[i] = BitConverter.ToInt16(b, body + (2 * i)) / 32768f;
                }

                return audio;
            }

            p = body + size + (size & 1);
        }

        throw new InvalidDataException($"{path} has no data chunk.");
    }
}

/// <summary>
/// Not a test of behaviour: plays every WAV in the directory <c>SM_BENCH_WAV</c> names through a listening station and writes what <see cref="BenchRecording.Describe"/> says beside each, as <c>name.busy.txt</c>.
/// Skipped unless the variable is set.
/// </summary>
public sealed class BenchRecordingDiagnostics
{
    [Fact]
    public void Describe_every_recording_in_SM_BENCH_WAV()
    {
        string? dir = Environment.GetEnvironmentVariable("SM_BENCH_WAV");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "set SM_BENCH_WAV to a directory of 48 kHz mono recordings");
        foreach (string path in Directory.GetFiles(dir!, "*.wav").Order(StringComparer.Ordinal))
        {
            float[] audio = BenchRecording.ReadWav(path);
            File.WriteAllText(Path.ChangeExtension(path, ".busy.txt"), BenchRecording.Play(audio).Describe());
            File.WriteAllText(Path.ChangeExtension(path, ".worker.busy.txt"), BenchRecording.Play(audio, inline: false).Describe());
        }
    }
}
