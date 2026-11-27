namespace M0LTE.OfdmFm;

/// <summary>
/// A link address, as the host gives it: 1 to 32 opaque bytes that are compared and never interpreted (for AX.25, the bytes of a callsign and SSID in whatever form the host likes).
/// Equal when the bytes are equal. The bytes are copied in, so the value never changes. On the air an address travels as its <see cref="Key"/>.
/// </summary>
public readonly struct LinkAddress : IEquatable<LinkAddress>
{
    /// <summary>The most bytes an address has.</summary>
    public const int MaxBytes = 32;

    private readonly byte[]? _bytes;
    private readonly bool _everyone;
    private readonly bool _keyOnly;
    private readonly uint _key;

    /// <summary>Makes an address from a copy of <paramref name="bytes"/>.</summary>
    /// <exception cref="ArgumentException">There are no bytes, or more than <see cref="MaxBytes"/>.</exception>
    public LinkAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxBytes)
        {
            throw new ArgumentException($"A link address is 1 to {MaxBytes} bytes.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
        _key = KeyOf(bytes);
    }

    private LinkAddress(uint key)
    {
        _keyOnly = true;
        _key = key;
        _bytes = [];
    }

    /// <summary>
    /// An address known only by its <see cref="Key"/>, as a host has when it answers a datagram whose sender it knows from <see cref="ReceivedEvent.FromKey"/> alone. <see cref="Station.Send"/> and <see cref="Station.Cancel(LinkAddress)"/> take it
    /// in place of the full address: the station keeps one queue for each key, so it reaches the same place. It has no bytes, and is equal only to another made from the same key (the events a station raises carry the address
    /// that first made the queue, whichever form that was).
    /// </summary>
    public static LinkAddress FromKey(uint key) => new(key);

    /// <summary>True for an address made by <see cref="FromKey"/>.</summary>
    public bool IsKeyOnly => _keyOnly;

    private LinkAddress(bool everyone)
    {
        _everyone = everyone;
        _bytes = [];
    }

    /// <summary>
    /// The reserved address of everyone: <see cref="Station.Send"/> to it goes out as a burst for every station, with no For and no report, and events about it (<see cref="TransmittedEvent"/>, <see cref="CongestionEvent"/>)
    /// carry it. It is not equal to any address made from bytes, and has no key.
    /// </summary>
    public static LinkAddress Everyone { get; } = new(true);

    /// <summary>True for <see cref="Everyone"/>.</summary>
    public bool IsEveryone => _everyone;

    /// <summary>True for the value of <c>default</c>, which has no bytes and is no address; <see cref="Station.Send"/> refuses it.</summary>
    public bool IsEmpty => !_everyone && !_keyOnly && Bytes.IsEmpty;

    /// <summary>The address's bytes (empty for a default value or <see cref="Everyone"/>).</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;

    /// <summary>Makes an address from the ASCII bytes of <paramref name="text"/>. A convenience for tests and simple hosts; the library never reads an address as text.</summary>
    public static LinkAddress FromAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new LinkAddress(System.Text.Encoding.ASCII.GetBytes(text));
    }

    /// <summary>
    /// The key the address travels as on the air: the payload's own CRC-32 (polynomial 0x04C11DB7, started at all ones, most significant bit first, nothing reflected or inverted) run over the address's bytes,
    /// each byte most significant bit first. Sent most significant byte first. A host compares it with the For key of a <see cref="ReceivedEvent"/> to tell whether a datagram was meant for one of its addresses.
    /// </summary>
    /// <exception cref="InvalidOperationException">The address is <see cref="Everyone"/> or empty: they have no key.</exception>
    public uint Key
    {
        get
        {
            if (_everyone || (!_keyOnly && Bytes.IsEmpty))
            {
                throw new InvalidOperationException("Everyone and the empty address have no key.");
            }

            return _key;
        }
    }

    /// <summary>The key of an address made of these bytes (see <see cref="Key"/>), without making the address. Allocates nothing.</summary>
    public static uint KeyOf(ReadOnlySpan<byte> bytes)
    {
        Span<byte> bits = stackalloc byte[bytes.Length * 8];
        for (int i = 0; i < bytes.Length; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                bits[(i * 8) + j] = (byte)((bytes[i] >> (7 - j)) & 1);
            }
        }

        return BitCrc.Crc32(bits);
    }

    /// <inheritdoc />
    public bool Equals(LinkAddress other) => _everyone == other._everyone && _keyOnly == other._keyOnly && (_keyOnly ? _key == other._key : Bytes.SequenceEqual(other.Bytes));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LinkAddress other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(_everyone);
        hash.Add(_keyOnly);
        if (_keyOnly)
        {
            hash.Add(_key);
        }
        else
        {
            hash.AddBytes(Bytes);
        }

        return hash.ToHashCode();
    }

    /// <summary>The bytes in hex, for logs ("everyone" for <see cref="Everyone"/>).</summary>
    public override string ToString() => _everyone ? "everyone" : _keyOnly ? "key " + _key.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) : Convert.ToHexString(Bytes);

    /// <summary>Equal when the bytes are equal.</summary>
    public static bool operator ==(LinkAddress left, LinkAddress right) => left.Equals(right);

    /// <summary>Not equal when the bytes differ.</summary>
    public static bool operator !=(LinkAddress left, LinkAddress right) => !left.Equals(right);
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

    /// <summary>The address was forgotten. Not raised: a datagram waiting for an address that is forgotten goes to everyone instead.</summary>
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

    /// <summary>An announcement: a data burst with no datagrams that tells the stations hearing it where an address of this station's host lives (the answer to a burst to everyone for that address).</summary>
    Announcement = 2,
}

/// <summary>What <see cref="Station.Send"/> did with a datagram.</summary>
public enum SendResult
{
    /// <summary>The datagram is queued. Its fate is told by <see cref="Station.Transmitted"/> or <see cref="Station.Dropped"/>.</summary>
    Accepted = 0,

    /// <summary>The datagram is larger than one burst to that address carries at the format it would go at now (<see cref="LinkStateEvent.DatagramRoom"/>). The modem never splits a datagram.</summary>
    TooLarge = 1,

    /// <summary>The queue to that address holds its limit of airtime. <see cref="Station.Congestion"/> says when it is writable again.</summary>
    QueueFull = 2,

    /// <summary>The station has no identity yet (<see cref="Station.SetIdentity"/>) and does not transmit. Nothing is queued.</summary>
    NoIdentity = 3,
}

/// <summary>What <see cref="Station.SetIdentity"/> did.</summary>
public enum IdentityResult
{
    /// <summary>The callsign is the station's: it was set, or it was already this one. Nothing changes if it was.</summary>
    Accepted = 0,

    /// <summary>Not 1 to 16 characters of A to Z, 0 to 9, '/' and '-'. Nothing changes.</summary>
    Invalid = 1,

    /// <summary>The station has a different callsign already. Nothing changes: to change callsign, make a new station.</summary>
    Different = 2,
}
