namespace M0LTE.OfdmFm;

/// <summary>
/// A station's name as the host gives it: opaque bytes that are compared and never interpreted (a host passes a callsign and SSID
/// in whatever form it likes). Equal when the bytes are equal. The bytes are copied in, so the value never changes.
/// </summary>
public readonly struct StationName : IEquatable<StationName>
{
    private readonly byte[]? _bytes;

    /// <summary>Makes a name from a copy of <paramref name="bytes"/>.</summary>
    public StationName(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();

    /// <summary>The name's bytes (empty for a default value).</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Makes a name from the ASCII bytes of <paramref name="text"/>. A convenience for tests and simple hosts; the library never reads a name as text.</summary>
    public static StationName FromAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new StationName(System.Text.Encoding.ASCII.GetBytes(text));
    }

    /// <inheritdoc />
    public bool Equals(StationName other) => Bytes.SequenceEqual(other.Bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StationName other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    /// <summary>The bytes in hex, for logs.</summary>
    public override string ToString() => Convert.ToHexString(Bytes);

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

    /// <summary>It waited in the queue too long.</summary>
    Expired = 1,

    /// <summary>The station it was for was forgotten.</summary>
    LinkForgotten = 2,
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
