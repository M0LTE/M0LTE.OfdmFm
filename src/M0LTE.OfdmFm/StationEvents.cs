namespace M0LTE.OfdmFm;

// The events of the host interface (docs/station-design.md B, and the host interface document in packet-net/pdn-soundmodem).
// Plain immutable values. Times are on the sample clock, in seconds, unless a name says otherwise.

/// <summary>A datagram's burst has left the audio device. The start point for the host's retry timer.</summary>
/// <param name="Tag">The host's tag on the datagram.</param>
/// <param name="Station">The station it was for.</param>
/// <param name="Burst">The burst number it went in.</param>
/// <param name="Format">The standard format it went at.</param>
/// <param name="RateBitsPerSecond">That format's rate, bit/s.</param>
/// <param name="AirtimeSeconds">How long the burst was on air.</param>
/// <param name="TimeOutSeconds">When the burst left the audio device, on the sample clock.</param>
public readonly record struct TransmittedEvent(HostTag Tag, LinkAddress Station, int Burst, int Format, double RateBitsPerSecond, double AirtimeSeconds, double TimeOutSeconds);

/// <summary>A datagram that will not go out.</summary>
/// <param name="Tag">The host's tag on the datagram.</param>
/// <param name="Reason">Why.</param>
public readonly record struct DroppedEvent(HostTag Tag, DropReason Reason);

/// <summary>A datagram arrived.</summary>
/// <param name="Datagram">The datagram.</param>
/// <param name="Sender">The sender's name if the station has it bound, else null.</param>
/// <param name="SenderNumber">The sender's on-air station number.</param>
/// <param name="ToEveryone">True for a datagram addressed to everyone; false when it was for this station.</param>
/// <param name="QualityDb">How well the burst was received, dB.</param>
public readonly record struct ReceivedEvent(ReadOnlyMemory<byte> Datagram, LinkAddress? Sender, int SenderNumber, bool ToEveryone, double QualityDb);

/// <summary>A report came back about the path to a station.</summary>
/// <param name="Station">The station that sent it.</param>
/// <param name="RecommendedFormat">The format it recommends, or null if none.</param>
/// <param name="Failed">True if it heard a burst from this station and could not decode it.</param>
public readonly record struct PathReportEvent(LinkAddress Station, int? RecommendedFormat, bool Failed);

/// <summary>A station heard a burst from this one and could not decode it. A hint: the library resends nothing.</summary>
/// <param name="Station">The station that could not decode.</param>
/// <param name="Tags">The tags of the datagrams in that burst.</param>
public readonly record struct BurstNotDecodedEvent(LinkAddress Station, IReadOnlyList<HostTag> Tags);

/// <summary>What the station knows of a link changed.</summary>
/// <param name="Station">The other station.</param>
/// <param name="Format">The format now used to it, or null if none yet.</param>
/// <param name="RateBitsPerSecond">That format's rate, bit/s, or null.</param>
/// <param name="DatagramRoom">The largest datagram, in bytes, one burst to it now carries.</param>
/// <param name="Knowledge">Where the format came from.</param>
/// <param name="AgeSeconds">How old that knowledge is.</param>
/// <param name="Gone">True when the station forgot the link after ten minutes of silence.</param>
public readonly record struct LinkStateEvent(LinkAddress Station, int? Format, double? RateBitsPerSecond, int DatagramRoom, LinkKnowledge Knowledge, double AgeSeconds, bool Gone);

/// <summary>The channel became busy or clear.</summary>
/// <param name="Busy">True when busy.</param>
public readonly record struct ChannelBusyEvent(bool Busy);

/// <summary>Push-back for a slow link.</summary>
/// <param name="Station">The station the queue is for.</param>
/// <param name="Writable">False when its queue is full and a send would be refused; true when it has drained.</param>
public readonly record struct CongestionEvent(LinkAddress Station, bool Writable);

/// <summary>The station put a burst on air that the host did not ask for.</summary>
/// <param name="Kind">What kind.</param>
/// <param name="Station">The station it was for, or null for everyone.</param>
/// <param name="Format">The format it went at.</param>
public readonly record struct ModemSentEvent(ModemSentKind Kind, LinkAddress? Station, int Format);

/// <summary>An identification burst from another station was decoded: that number says it is that callsign.</summary>
/// <param name="StationNumber">The sender's on-air number.</param>
/// <param name="Callsign">The callsign it sent, as it sent it.</param>
/// <param name="Capability">The capability word it sent.</param>
/// <param name="Name">The name events about that number carry from now on: the callsign as a name, or the host's own name for the number when the host bound one with <see cref="Station.Bind"/> (the host's name is never replaced).</param>
public readonly record struct IdentifiedEvent(int StationNumber, string Callsign, int Capability, LinkAddress Name);

/// <summary>The identification interval has run out on the sample clock.</summary>
/// <param name="Seconds">The sample clock when it came due.</param>
public readonly record struct IdentificationDueEvent(double Seconds);

/// <summary>
/// The station asks the audio layer to transmit: these samples, starting at receive-sample <paramref name="EarliestSample"/> or as soon after as it can.
/// The audio layer answers with <see cref="Station.TransmitStarted"/> and <see cref="Station.TransmitEnded"/>, or the station withdraws the request.
/// </summary>
/// <param name="Id">The request's id, unique within the station.</param>
/// <param name="Samples">The 48 kHz samples to play. Valid until the request ends or is withdrawn.</param>
/// <param name="EarliestSample">The earliest receive-sample index at which to start.</param>
public readonly record struct TransmitRequestEvent(long Id, ReadOnlyMemory<float> Samples, long EarliestSample);

/// <summary>The station withdraws a request that has not been keyed. The audio layer may already have started it; it then reports the start as usual.</summary>
/// <param name="Id">The request's id.</param>
public readonly record struct WithdrawRequestEvent(long Id);
