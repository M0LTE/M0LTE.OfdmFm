namespace M0LTE.OfdmFm;

/// <summary>
/// A station's name as the host gives it: opaque bytes that are compared and never interpreted (a host passes a callsign and SSID
/// in whatever form it likes). Equal when the bytes are equal. The bytes are copied in, so the value never changes.
/// </summary>
public readonly struct StationName : IEquatable<StationName>
{
    private readonly byte[]? _bytes;
    private readonly bool _everyone;

    /// <summary>Makes a name from a copy of <paramref name="bytes"/>.</summary>
    public StationName(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();

    private StationName(bool everyone)
    {
        _everyone = everyone;
        _bytes = [];
    }

    /// <summary>
    /// The reserved name of everyone: <see cref="Station.Send"/> to it goes out as a burst for every station, with no report, and events about it (<see cref="TransmittedEvent"/>, <see cref="CongestionEvent"/>)
    /// carry it. It is not equal to any name made from bytes, the empty one included, and cannot be bound.
    /// </summary>
    public static StationName Everyone { get; } = new(true);

    /// <summary>True for <see cref="Everyone"/>.</summary>
    public bool IsEveryone => _everyone;

    /// <summary>True for a name made from no bytes at all, which is the value of <c>default</c>; it names nobody.</summary>
    public bool IsEmpty => !_everyone && Bytes.IsEmpty;

    /// <summary>The name's bytes (empty for a default value).</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Makes a name from the ASCII bytes of <paramref name="text"/>. A convenience for tests and simple hosts; the library never reads a name as text.</summary>
    public static StationName FromAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new StationName(System.Text.Encoding.ASCII.GetBytes(text));
    }

    /// <inheritdoc />
    public bool Equals(StationName other) => _everyone == other._everyone && Bytes.SequenceEqual(other.Bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StationName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(_everyone);
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    /// <summary>The bytes in hex, for logs ("everyone" for <see cref="Everyone"/>).</summary>
    public override string ToString() => _everyone ? "everyone" : Convert.ToHexString(Bytes);

    /// <summary>Equal when the bytes are equal.</summary>
    public static bool operator ==(StationName left, StationName right) => left.Equals(right);

    /// <summary>Not equal when the bytes differ.</summary>
    public static bool operator !=(StationName left, StationName right) => !left.Equals(right);
}

/// <summary>An opaque tag the host puts on a datagram so that it can recognise the datagram in events and cancel it. The library compares it and never interprets it.</summary>
public readonly record struct HostTag(long Value);

/// <summary>How urgent a datagram is: ordinary ones wait their turn for the channel; an expedited one (an acknowledgement or response) may use the first slot after a burst.</summary>
public enum SendPriority
{
    /// <summary>Waits for the channel to be clear for the priority window plus a random slot delay.</summary>
    Ordinary = 0,

    /// <summary>Goes in the first slot after a burst.</summary>
    Expedited = 1,
}

/// <summary>Why a datagram never went out.</summary>
public enum DropReason
{
    /// <summary>The host cancelled it.</summary>
    Cancelled = 0,

    /// <summary>It waited in the queue too long. Not raised yet: a queue is bounded in airtime and the host cancels what it no longer wants.</summary>
    Expired = 1,

    /// <summary>The station it was for was forgotten. Not raised yet: a station forgotten after ten minutes is sent to again from first contact.</summary>
    LinkForgotten = 2,

    /// <summary>The link's format fell after the datagram was accepted, and the datagram is now larger than one burst at the format carries. The host sees the new room in <see cref="LinkStateEvent"/>.</summary>
    NoLongerFits = 3,
}

/// <summary>Where the station's idea of a link's rate came from.</summary>
public enum LinkKnowledge
{
    /// <summary>First contact: nothing known yet.</summary>
    FirstContact = 0,

    /// <summary>The modem's own estimate from listening.</summary>
    OwnEstimate = 1,

    /// <summary>A report from the other station.</summary>
    Report = 2,
}

/// <summary>What kind of burst the station sent that the host did not ask for.</summary>
public enum ModemSentKind
{
    /// <summary>A lone report.</summary>
    Report = 0,

    /// <summary>An identification burst.</summary>
    Identification = 1,
}

/// <summary>What <see cref="Station.Send"/> did with a datagram.</summary>
public enum SendResult
{
    /// <summary>The datagram is queued. Its fate is told by <see cref="Station.Transmitted"/> or <see cref="Station.Dropped"/>.</summary>
    Accepted = 0,

    /// <summary>The name is not bound to a station number (see <see cref="Station.Bind"/>).</summary>
    NotBound = 1,

    /// <summary>The datagram is larger than one burst to that station carries at the format it would go at now (<see cref="LinkStateEvent.DatagramRoom"/>). The modem never splits a datagram.</summary>
    TooLarge = 2,

    /// <summary>The queue to that station holds its limit of airtime. <see cref="Station.Congestion"/> says when it is writable again.</summary>
    QueueFull = 3,
}
