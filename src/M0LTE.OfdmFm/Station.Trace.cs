using System.Globalization;

namespace M0LTE.OfdmFm;

/// <summary>How much of what the station does a trace handler is given.</summary>
public enum TraceLevel
{
    /// <summary>A line or two for each burst: what was heard, what was told and sent, what went unanswered, what was found and lost, who is using which number.</summary>
    Flow = 0,

    /// <summary>Everything in <see cref="Flow"/>, and each measurement, each choice of rate and every decision about when to transmit, with the format numbers in brackets.</summary>
    Detail = 1,
}

/// <summary>
/// One line of the station's trace: what the station did or decided, in a short plain sentence. The words are the library's, so that every host prints the same ones; the host adds nothing but where to write them.
/// </summary>
/// <param name="Seconds">The station's clock when it happened, seconds (<see cref="Station.Seconds"/>).</param>
/// <param name="Level">The level the line belongs to.</param>
/// <param name="Text">The sentence: a fragment that starts with a capital and has no full stop. Plain ASCII.</param>
public readonly record struct TraceEvent(double Seconds, TraceLevel Level, string Text)
{
    /// <summary>The line as a host writes it: the clock to two places in a column seven wide, two spaces, the text.</summary>
    public override string ToString() => Seconds.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(7) + "  " + Text;
}

// The station's trace: a record of what it did and why, for diagnosing a flow (docs/trace.md lists every line).
//
// Nothing is built when nobody is listening. Every line is made inside a check, WantsFlow() or WantsDetail(), that is false without a handler (and, for Detail, without the level): the text, the names and the numbers in it are only worked out
// once the check has passed, so a station with no handler formats nothing, boxes nothing and makes no closure. Lines never draw on the random source and never change what the station does.
public sealed partial class Station
{
    private TraceLevel _traceLevel;
    private long _thrownAway;
    private long _useCounter;
    private readonly Dictionary<uint, (LinkAddress Address, long Used)> _hostAddresses = [];

    /// <summary>The most host addresses the trace remembers the bytes of; past it the least recently used goes.</summary>
    internal const int MaxHostAddresses = 64;

    /// <summary>
    /// Raised for each thing the station does or decides that a person diagnosing a flow would want to read, at the level set by <see cref="TraceLevel"/>. On the thread that made the call that caused it, like the other events, and under the same guard:
    /// a handler must not call the audio-thread members (they throw <see cref="InvalidOperationException"/>). A handler that throws is ignored: the station carries on as if it had returned, so a faulty handler cannot leave it half-way through a change.
    /// Nothing is built or allocated for a line while there is no handler, and no Detail line is built while the level is <see cref="TraceLevel.Flow"/>.
    /// Attach the handler before the host's first <see cref="Send"/>: an address the host sent from before that is shown by its key (<c>address 3fa2c1d0</c>) until it sends from it again.
    /// </summary>
    public event Action<TraceEvent>? Trace;

    /// <summary>The level of lines <see cref="Trace"/> is given. <see cref="TraceLevel.Flow"/> until set. Audio thread, serialised with <see cref="Push"/>.</summary>
    public TraceLevel TraceLevel
    {
        get => _traceLevel;
        set => _traceLevel = value;
    }

    /// <summary>How many line texts were handed to be written when nobody wanted them (no handler, or a Detail line at the Flow level): text built and thrown away. For tests: it must stay zero.</summary>
    internal long TextsThrownAway => _thrownAway;

    /// <summary>How many host addresses the trace holds the bytes of. For tests.</summary>
    internal int HostAddressesKept => _hostAddresses.Count;

    /// <summary>Whether the trace still holds the bytes of the host address with this key. For tests.</summary>
    internal bool HostAddressKept(uint key) => _hostAddresses.ContainsKey(key);

    /// <summary>True when a Flow line is wanted. The check every Flow line is made behind.</summary>
    private bool WantsFlow() => Trace is not null;

    /// <summary>True when a Detail line is wanted. The check every Detail line is made behind.</summary>
    private bool WantsDetail() => Trace is not null && _traceLevel == TraceLevel.Detail;

    private void Flow(string text)
    {
        if (Trace is null)
        {
            _thrownAway++;
            return;
        }

        Emit(new TraceEvent(Seconds, TraceLevel.Flow, text));
    }

    private void Detail(string text)
    {
        if (Trace is null || _traceLevel != TraceLevel.Detail)
        {
            _thrownAway++;
            return;
        }

        Emit(new TraceEvent(Seconds, TraceLevel.Detail, text));
    }

    /// <summary>The one place the handler is called: under the guard the other events are raised under, and a throw is ignored.</summary>
    private void Emit(in TraceEvent line)
    {
        Action<TraceEvent>? handler = Trace;
        if (handler is null)
        {
            return;
        }

        bool was = _advancing;
        _advancing = true;
        try
        {
            handler(line);
        }
        catch (Exception)
        {
            // A faulty handler must not leave the station half-way through what it was doing.
        }
        finally
        {
            _advancing = was;
        }
    }

    // ---- naming ----

    /// <summary>Remembers a host address, when someone is listening, so that a line can show its bytes. At most <see cref="MaxHostAddresses"/>: past that the least recently used is forgotten.</summary>
    private void NoteHostAddress(LinkAddress address)
    {
        if (Trace is null || address.IsEveryone || address.IsEmpty || address.IsKeyOnly)
        {
            return;
        }

        if (!_hostAddresses.ContainsKey(address.Key) && _hostAddresses.Count >= MaxHostAddresses)
        {
            uint oldest = 0;
            long least = long.MaxValue;
            foreach (KeyValuePair<uint, (LinkAddress Address, long Used)> pair in _hostAddresses)
            {
                if (pair.Value.Used < least)
                {
                    least = pair.Value.Used;
                    oldest = pair.Key;
                }
            }

            _hostAddresses.Remove(oldest);
        }

        _hostAddresses[address.Key] = (address, ++_useCounter);
    }

    private static bool Printable(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return false;
        }

        foreach (byte b in bytes)
        {
            if (b is < 0x21 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An address key as a line shows it when nothing better is known: <c>address 3fa2c1d0</c>.</summary>
    internal static string KeyText(uint key) => "address " + key.ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>How a line shows a link address: its bytes when they are printable ASCII (<c>G4ABC-1</c>), else its key (<c>address 3fa2c1d0</c>); <c>everyone</c> for everyone.</summary>
    internal static string AddressText(LinkAddress address)
    {
        if (address.IsEveryone)
        {
            return "everyone";
        }

        return !address.IsKeyOnly && Printable(address.Bytes) ? System.Text.Encoding.ASCII.GetString(address.Bytes) : KeyText(address.Key);
    }

    /// <summary>How a line shows the address with this key: the host's bytes if it has sent to or from it (and they are printable), else the key.</summary>
    private string AddressText(uint key)
    {
        if (_byKey.TryGetValue(key, out AddressQueue? q) && !q.Address.IsKeyOnly)
        {
            return AddressText(q.Address);
        }

        return _hostAddresses.TryGetValue(key, out (LinkAddress Address, long Used) known) ? AddressText(known.Address) : KeyText(key);
    }

    /// <summary>How a line shows the address a datagram is for: as <see cref="AddressText(LinkAddress)"/>, but an address the host made from its key alone is shown by the bytes the station has seen for that key.</summary>
    private string DestText(LinkAddress address) => address.IsKeyOnly ? AddressText(address.Key) : AddressText(address);

    /// <summary>When the host last used the address with this key to send to or from, or 0.</summary>
    private long UsedAt(uint key)
    {
        long used = _hostAddresses.TryGetValue(key, out (LinkAddress Address, long Used) known) ? known.Used : 0;
        return _byKey.TryGetValue(key, out AddressQueue? q) ? Math.Max(used, q.UsedAt) : used;
    }

    /// <summary>
    /// How a line shows station <paramref name="number"/>: by the address it is known by, and when it has several, the one the host used most recently (one the host has never used ranks below any it has, and among those the latest heard, then the lowest key,
    /// so that the same run is named the same way every time); as <c>station 17</c> when no address is known.
    /// </summary>
    internal string StationName(int number)
    {
        uint best = 0;
        bool found = false;
        long bestUsed = -1;
        double bestHeard = double.NegativeInfinity;
        foreach (KeyValuePair<uint, AddressTable.Entry> pair in _table.Entries)
        {
            if (pair.Value.Number != number)
            {
                continue;
            }

            long used = UsedAt(pair.Key);
            bool better = !found
                || used > bestUsed
                || (used == bestUsed && (pair.Value.HeardAt > bestHeard || (pair.Value.HeardAt == bestHeard && pair.Key < best)));
            if (better)
            {
                best = pair.Key;
                bestUsed = used;
                bestHeard = pair.Value.HeardAt;
                found = true;
            }
        }

        return found ? AddressText(best) : "station " + number.ToString(CultureInfo.InvariantCulture);
    }

    // ---- composite pieces shared by several lines ----

    /// <summary>What is waiting to go, as the channel lines say it, or null when nothing is: queued datagrams and those of a burst built and not yet keyed.</summary>
    private string? WaitingText()
    {
        int total = 0;
        int queues = 0;
        AddressQueue? only = null;
        foreach (AddressQueue q in _queueList)
        {
            int count = q.Count + (_burst.Built && !_burst.Started && ReferenceEquals(_burst.Queue, q) ? _burst.Items.Count : 0);
            if (count > 0)
            {
                total += count;
                queues++;
                only = q;
            }
        }

        if (total == 0)
        {
            return null;
        }

        return queues == 1 ? TraceText.Waiting(total, DestText(only!.Address)) : TraceText.WaitingMany(total, queues);
    }

    /// <summary>The channel turned busy or clear: the lines for it. Flow when something is waiting to go, Detail otherwise.</summary>
    private void TraceChannel(bool busy)
    {
        if (Trace is null)
        {
            return;
        }

        AccessCause cause = busy ? _access.CauseNow() : AccessCause.None;
        string? waiting = cause == AccessCause.OwnTransmission ? null : WaitingText();
        if (waiting is not null && WantsFlow())
        {
            Flow(busy ? TraceText.ChannelBusyWaiting(waiting) : TraceText.ChannelClearWaiting(waiting));
        }

        if (WantsDetail())
        {
            if (busy)
            {
                // Our own transmission is said when it is keyed (OnTransmitStarted), not when it is granted: a request the audio layer refuses was never on air.
                if (cause != AccessCause.OwnTransmission)
                {
                    Detail(TraceText.ChannelBusy(TraceText.CauseText(cause)));
                }
            }
            else if (_access.SpellWasJudgingAlone)
            {
                // The channel was busy only while candidates were being judged, and none was a burst.
                Detail(TraceText.NothingCameOfIt());
            }
            else if (waiting is null)
            {
                Detail(TraceText.ChannelClear());
            }
        }
    }

    /// <summary>What a request carries, for the lines about asking to transmit.</summary>
    private string SubjectOf(ChannelAccess.Request request)
    {
        if (ReferenceEquals(request, _data))
        {
            return TraceWords.SubjectDatagrams;
        }

        if (ReferenceEquals(request, _reportRequest))
        {
            // Built, it is what it was built as; waiting, it is an announcement when that is all that is owed.
            bool announcement = _report.Built ? _report.Announcement : NextReady() < 0 && NextAnnouncement() >= 0;
            return announcement ? TraceWords.SubjectAnnouncement : TraceWords.SubjectReport;
        }

        if (ReferenceEquals(request, _identRequest))
        {
            return TraceWords.SubjectIdentification;
        }

        return ReferenceEquals(request, _clashRequest) ? TraceWords.SubjectNotice : TraceWords.SubjectAudio;
    }

    /// <summary>A note from the channel-access rules, as a Detail line.</summary>
    private void OnAccessNote(AccessNote note)
    {
        if (!WantsDetail())
        {
            return;
        }

        ChannelAccess.Request request = note.Request;
        switch (note.Kind)
        {
            case AccessNoteKind.Requested:
                // A report (and an identification) needs the first slot; so does an expedited datagram. Everything else waits the longer clear time and a random wait.
                string wait = request.Class == TransmitClass.Ordinary
                    ? TraceText.WaitOrdinary(ChannelAccess.OrdinaryClear / (double)SampleRate)
                    : TraceText.WaitFirstSlot(ChannelAccess.FirstSlotClear / (double)SampleRate);
                Detail(TraceText.AskedToSend(SubjectOf(request), wait));
                break;
            case AccessNoteKind.Drawn when note.B > 0:
                Detail(TraceText.DrewWaitSlots(note.A * _access.NoticeSamples / (double)SampleRate, note.A + 1, note.B, _access.NoticeSamples / (double)SampleRate));
                break;
            case AccessNoteKind.Drawn:
                // The single delay (a test's rule) draws samples, not slots, and has no line.
                break;
            case AccessNoteKind.Scheduled:
                Detail(TraceText.CanSendAt(ClockSeconds(note.Sample)));
                break;
            case AccessNoteKind.Granted:
                Detail(TraceText.ClearToSend(SubjectOf(request), ClockSeconds(note.Sample - request.QueuedAt)));
                break;
            case AccessNoteKind.Withdrawn:
                Detail(TraceText.HeldBack(SubjectOf(request), TraceText.CauseText(note.Cause)));
                break;
        }
    }

    /// <summary>The mean of what is estimated of a path across the carriers, dB, or NaN when nothing is.</summary>
    private static double MeanEstimateDb(LinkNeighbour? neighbour)
    {
        if (neighbour is null)
        {
            return double.NaN;
        }

        double sum = 0;
        int count = 0;
        foreach (double value in neighbour.EstimateDb)
        {
            if (!double.IsNaN(value))
            {
                sum += value;
                count++;
            }
        }

        return count == 0 ? double.NaN : sum / count;
    }

    /// <summary>The lines for a data burst that was heard: what it was, then what it did to the estimate and the path.</summary>
    private void TraceHeardData(BurstHeader h, BurstJob job, bool decoded, bool usable, int count, uint? forKey, NeighbourBefore before)
    {
        int sender = h.Sender;
        double signal = job.HasQuality ? MeanQualityDb(job.Quality, StandardFormats.Describe(h.Format).Carriers) : double.NaN;
        if (WantsFlow())
        {
            string who = StationName(sender);
            if (!decoded)
            {
                Flow(TraceText.HeardNotDecoded(who));
            }
            else if (!usable)
            {
                Flow(TraceText.HeardUnreadable(who));
            }
            else if (_number != 0 && h.Destination == _number)
            {
                Flow(TraceText.HeardForUs(who, count, signal));
            }
            else if (h.Destination == 0)
            {
                Flow(forKey is uint asked ? TraceText.HeardForEveryoneAsking(who, count, AddressText(asked), signal) : TraceText.HeardForEveryone(who, count, signal));
            }
            else
            {
                Flow(TraceText.HeardForOther(who, count, StationName(h.Destination), signal));
            }

            if (job.Sudden)
            {
                Flow(TraceText.LandedOn(who));
            }
        }

        TraceMeasured(sender, h.Format, decoded, signal, before);
    }

    /// <summary>An address was learned: a line for it, at Flow when the host has sent to it and at Detail when not.</summary>
    private void TraceFound(uint key, int now, int was)
    {
        if (Trace is null)
        {
            return;
        }

        bool interesting = _byKey.TryGetValue(key, out AddressQueue? q);
        if (interesting ? !WantsFlow() : !WantsDetail())
        {
            return;
        }

        string address = AddressText(key);
        string text = was != 0 ? TraceText.FoundMoved(address, now, was) : interesting && q!.Forgotten ? TraceText.FoundAgain(address) : TraceText.Found(address);
        if (interesting)
        {
            Flow(text);
        }
        else
        {
            Detail(text);
        }
    }

    /// <summary>What was held about a neighbour before a burst from it was given to the rules, for the lines that say what the burst changed.</summary>
    private readonly record struct NeighbourBefore(int? Recommended, int Measures, int? ReportFormat);

    private NeighbourBefore CaptureNeighbour(int who) =>
        _link.TryGetNeighbour(who, out LinkNeighbour? nb) && nb is not null ? new NeighbourBefore(nb.Recommended, nb.Measures, nb.ReportFormat) : new NeighbourBefore(null, 0, null);

    /// <summary>The lines for what a burst heard from <paramref name="sender"/> did to the estimate and to the rate its path is good for. Called after the line that says the burst was heard.</summary>
    private void TraceMeasured(int sender, int heardFormat, bool decoded, double signalDb, NeighbourBefore before)
    {
        if (!_link.TryGetNeighbour(sender, out LinkNeighbour? nb) || nb is null)
        {
            return;
        }

        if (WantsDetail() && nb.Measures != before.Measures && nb.Recommended is int recommended)
        {
            Detail(TraceText.Measured(StationName(sender), signalDb, MeanEstimateDb(nb), nb.Margin, StandardFormats.Rate(recommended), recommended, nb.Measures, !decoded, nb.Pending, nb.Cap));
        }

        if (nb.Recommended is int now && now != before.Recommended && WantsFlow())
        {
            Flow(TraceText.PathGood(StationName(sender), StandardFormats.Rate(now), before.Recommended is int was ? StandardFormats.Rate(was) : null));
        }
    }

    /// <summary>Why the rules chose the format they did for a burst to a station, for the Detail line.</summary>
    private string ChoiceReason(int dest, LinkChoice choice, double now)
    {
        string reason = TraceWords.ChoseNothingKnown;
        if (_link.TryGetNeighbour(dest, out LinkNeighbour? nb) && nb is not null)
        {
            if (nb.Silences >= 3)
            {
                reason = TraceWords.ChoseThreeSilences;
            }
            else if (nb.Silences == 2)
            {
                reason = TraceWords.ChoseTwoSilences;
            }
            else if (nb.ReportFormat is int told)
            {
                reason = TraceWords.ChoseReportTemplate
                    .Replace("{rate}", TraceText.Rate(StandardFormats.Rate(told)), StringComparison.Ordinal)
                    .Replace("{age}", TraceText.Seconds(now - nb.ReportTime.GetValueOrDefault(now)), StringComparison.Ordinal);
            }
            else if (nb.Recommended is not null || nb.Measures > 0)
            {
                reason = TraceWords.ChoseOwnEstimate;
            }
        }

        return choice.ReportWanted ? reason + TraceWords.ChoseAsking : reason;
    }

    private static string SafeKind(NoteReason reason) => reason switch
    {
        NoteReason.UnknownVersion => TraceWords.HeaderVersion,
        NoteReason.UnsupportedKind => TraceWords.HeaderKind,
        _ => TraceWords.HeaderInvalid,
    };
}
