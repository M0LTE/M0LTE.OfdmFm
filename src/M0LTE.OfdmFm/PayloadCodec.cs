namespace M0LTE.OfdmFm;

/// <summary>What a data burst's prelude says. A burst to everyone has none.</summary>
/// <param name="Wanted">"Report wanted".</param>
/// <param name="Recommended">The format recommended to the destination (0 to 255), or null when none is given.</param>
/// <param name="Failed">"Failed".</param>
/// <param name="Capability">The sender's 16-bit capability word, or null when it is not sent.</param>
public readonly record struct PayloadPrelude(bool Wanted, int? Recommended, bool Failed, int? Capability);

/// <summary>One item of a prelude's extension block: a tag (1 to 255) and its value (0 to 253 bytes).</summary>
/// <param name="Tag">The item's tag. 0 is reserved and never sent.</param>
/// <param name="Value">The item's value.</param>
public readonly record struct ExtensionItem(byte Tag, ReadOnlyMemory<byte> Value);

/// <summary>
/// Walks the items of an unpacked payload's extension block, without allocating. It stops at the end of the block, and
/// at an item whose length runs past the block: what follows such a point is ignored.
/// </summary>
public ref struct ExtensionReader
{
    private readonly ReadOnlySpan<byte> _block;
    private int _position;

    internal ExtensionReader(ReadOnlySpan<byte> block)
    {
        _block = block;
        _position = 0;
    }

    /// <summary>The next item, whatever its tag. False at the end of the block or at an item that does not fit in it.</summary>
    public bool TryNext(out byte tag, out ReadOnlySpan<byte> value)
    {
        tag = 0;
        value = default;
        if (_position + 2 > _block.Length)
        {
            return false;
        }

        int length = _block[_position + 1];
        if (_position + 2 + length > _block.Length)
        {
            _position = _block.Length;
            return false;
        }

        tag = _block[_position];
        value = _block.Slice(_position + 2, length);
        _position += 2 + length;
        return true;
    }
}

/// <summary>Walks the datagrams of an unpacked payload, without allocating.</summary>
public ref struct DatagramReader
{
    private readonly ReadOnlySpan<byte> _payload;
    private int _position;

    internal DatagramReader(ReadOnlySpan<byte> payload, int position)
    {
        _payload = payload;
        _position = position;
    }

    /// <summary>The next datagram. False at a length of 0, a length that runs past the room left, or too little room for another length.</summary>
    public bool TryNext(out ReadOnlySpan<byte> datagram)
    {
        datagram = default;
        if (_position + 2 > _payload.Length)
        {
            return false;
        }

        int n = (_payload[_position] << 8) | _payload[_position + 1];
        if (n == 0 || _position + 2 + n > _payload.Length)
        {
            return false;
        }

        datagram = _payload.Slice(_position + 2, n);
        _position += 2 + n;
        return true;
    }
}

/// <summary>
/// Between a payload's bytes (prelude, capability word, extension block, datagrams) and a burst's information bits (padding,
/// scrambling, 32-bit check), as <c>docs/adaptation-protocol.md</c> sets out. Pinned to the prototype's
/// <c>link.py</c>. Bits are held one to a byte (0 or 1). Nothing here allocates.
/// </summary>
public static class PayloadCodec
{
    /// <summary>Bits of check at the end of the information bits.</summary>
    public const int CheckBits = 32;

    private const int Wanted = 0x80;
    private const int HasCapability = 0x40;
    private const int Given = 0x20;
    private const int FailedFlag = 0x10;
    private const int HasExtension = 0x08;

    /// <summary>The most bytes of items an extension block holds (its length is one byte).</summary>
    public const int MaxExtensionBytes = 255;

    /// <summary>
    /// The tag of the From item: one or more runs (<see cref="RunBytes"/> each: a key, a protocol byte and a count). The next count datagrams in the burst were sent from that address and are of that protocol; the runs are in the datagrams' order and their counts add up
    /// to the number of datagrams. In the extension block of a burst to one station and in the leading block of a burst to everyone.
    /// </summary>
    public const byte FromTag = 1;

    /// <summary>The tag of the For item, in the leading block of a burst to everyone: one key, the address the burst is really for. Absent when the burst is for everyone.</summary>
    public const byte ForTag = 2;

    /// <summary>Bytes of a link address key on the air.</summary>
    public const int KeyBytes = 4;

    /// <summary>Bytes of a run in the From item: a key, a protocol byte and a count (1 to 255).</summary>
    public const int RunBytes = KeyBytes + 2;

    /// <summary>Writes a run of the From item.</summary>
    public static void WriteRun(uint key, byte protocol, int count, Span<byte> destination)
    {
        WriteKey(key, destination);
        destination[KeyBytes] = protocol;
        destination[KeyBytes + 1] = (byte)count;
    }

    /// <summary>Writes <paramref name="key"/> as it travels on the air, most significant byte first.</summary>
    public static void WriteKey(uint key, Span<byte> destination)
    {
        destination[0] = (byte)(key >> 24);
        destination[1] = (byte)(key >> 16);
        destination[2] = (byte)(key >> 8);
        destination[3] = (byte)key;
    }

    /// <summary>Reads a key written by <see cref="WriteKey"/>.</summary>
    public static uint ReadKey(ReadOnlySpan<byte> source) => ((uint)source[0] << 24) | ((uint)source[1] << 16) | ((uint)source[2] << 8) | source[3];

    /// <summary>
    /// The bytes the block of items that every data burst carries takes in its payload, with a From item of <paramref name="runs"/> runs and, when <paramref name="forKey"/> is set, a For item:
    /// the block's length byte, the From item (a tag, a length and the keys) and the For item. For a burst to one station this is the whole extension block; for one to everyone, the leading block.
    /// </summary>
    public static int AddressBlockSize(int runs, bool forKey) => 1 + (runs > 0 ? 2 + (runs * RunBytes) : 0) + (forKey ? 2 + KeyBytes : 0);

    /// <summary>Bytes of prelude and datagrams (lengths included) that a format carries in this many payload symbols. Negative when the burst is too small to hold even its check.</summary>
    public static int Capacity(int format, int payloadSymbols) => (StandardFormats.InfoBits(format, payloadSymbols) - CheckBits) >> 3;

    /// <summary>As <see cref="Capacity(int, int)"/>, less the extension block, whose size (its length byte included) comes from <see cref="ExtensionBlockSize"/>.</summary>
    public static int Capacity(int format, int payloadSymbols, int extensionBlockBytes) => Capacity(format, payloadSymbols) - extensionBlockBytes;

    /// <summary>
    /// The bytes an extension block takes in a payload: 0 for no items, otherwise one length byte and the items.
    /// Throws when a tag is 0 or the items do not fit in 255 bytes (an item's value is at most 253 bytes).
    /// </summary>
    public static int ExtensionBlockSize(ReadOnlySpan<ExtensionItem> extensions)
    {
        if (extensions.IsEmpty)
        {
            return 0;
        }

        int n = 0;
        foreach (ExtensionItem item in extensions)
        {
            if (item.Tag == 0)
            {
                throw new ArgumentException("Tag 0 is reserved.", nameof(extensions));
            }

            n += 2 + item.Value.Length;
            if (n > MaxExtensionBytes)
            {
                throw new ArgumentException("The items do not fit in an extension block of 255 bytes.", nameof(extensions));
            }
        }

        return 1 + n;
    }

    /// <summary>The shortest burst of the format, in payload symbols (a multiple of four), that carries <paramref name="loadBytes"/> (never fewer than 8 are asked for), or null if even the longest does not.</summary>
    public static int? SymbolsFor(int format, int loadBytes) => SymbolsFor(format, loadBytes, 0);

    /// <summary>As <see cref="SymbolsFor(int, int)"/>, for a load that also carries an extension block of <paramref name="extensionBlockBytes"/> (see <see cref="ExtensionBlockSize"/>).</summary>
    public static int? SymbolsFor(int format, int loadBytes, int extensionBlockBytes)
    {
        int need = Math.Max(loadBytes + extensionBlockBytes, 8);
        for (int symbols = 4; symbols <= StandardFormats.MaxSymbols; symbols += 4)
        {
            if (Capacity(format, symbols) >= need)
            {
                return symbols;
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the payload's bytes before padding: the prelude (unless <paramref name="prelude"/> is null, a burst to
    /// everyone, which has the leading block instead, empty here) and the datagrams, each behind a two-byte length. Returns the length, or -1 when it does not fit
    /// in <paramref name="destination"/>. Datagrams are 1 to 65535 bytes.
    /// </summary>
    public static int Pack(PayloadPrelude? prelude, ReadOnlySpan<ReadOnlyMemory<byte>> datagrams, Span<byte> destination) =>
        Pack(prelude, default, datagrams, destination);

    /// <summary>
    /// As <see cref="Pack(PayloadPrelude?, ReadOnlySpan{ReadOnlyMemory{byte}}, Span{byte})"/>, with extension items. When
    /// there are any, the prelude's extension flag is set and the block (one length byte, then each item as a tag, a
    /// length and its value) follows the capability word and precedes the datagrams. With none the bytes are exactly
    /// those of the shorter overload. A burst to everyone has no prelude and begins with a block of its own, in the same
    /// form, always present: one length byte (0 for no items), then the items, then the datagrams. A tag of 0, or items that do not fit in 255 bytes, throw.
    /// </summary>
    public static int Pack(PayloadPrelude? prelude, ReadOnlySpan<ExtensionItem> extensions, ReadOnlySpan<ReadOnlyMemory<byte>> datagrams, Span<byte> destination)
    {
        int block = ExtensionBlockSize(extensions);
        int p = 0;
        if (prelude is null)
        {
            // A burst to everyone: the leading block is always there, even with no items.
            int lead = Math.Max(block, 1);
            if (destination.Length < lead)
            {
                return -1;
            }

            destination[0] = (byte)(lead - 1);
            p = 1;
            foreach (ExtensionItem item in extensions)
            {
                destination[p] = item.Tag;
                destination[p + 1] = (byte)item.Value.Length;
                item.Value.Span.CopyTo(destination[(p + 2)..]);
                p += 2 + item.Value.Length;
            }
        }
        else if (prelude is { } pre)
        {
            if (pre.Recommended is < 0 or > 255 || pre.Capability is < 0 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(prelude));
            }

            int need = (pre.Capability is null ? 2 : 4) + block;
            if (destination.Length < need)
            {
                return -1;
            }

            destination[0] = (byte)((pre.Wanted ? Wanted : 0) | (pre.Capability is not null ? HasCapability : 0) | (pre.Recommended is not null ? Given : 0) | (pre.Failed ? FailedFlag : 0) | (block > 0 ? HasExtension : 0));
            destination[1] = (byte)(pre.Recommended ?? 0);
            p = 2;
            if (pre.Capability is { } cap)
            {
                destination[2] = (byte)(cap >> 8);
                destination[3] = (byte)cap;
                p = 4;
            }

            if (block > 0)
            {
                destination[p++] = (byte)(block - 1);
                foreach (ExtensionItem item in extensions)
                {
                    destination[p] = item.Tag;
                    destination[p + 1] = (byte)item.Value.Length;
                    item.Value.Span.CopyTo(destination[(p + 2)..]);
                    p += 2 + item.Value.Length;
                }
            }
        }

        foreach (ReadOnlyMemory<byte> d in datagrams)
        {
            if (d.Length is 0 or > 65535)
            {
                throw new ArgumentException("A datagram is 1 to 65535 bytes.", nameof(datagrams));
            }

            if (p + 2 + d.Length > destination.Length)
            {
                return -1;
            }

            destination[p] = (byte)(d.Length >> 8);
            destination[p + 1] = (byte)d.Length;
            d.Span.CopyTo(destination[(p + 2)..]);
            p += 2 + d.Length;
        }

        return p;
    }

    /// <summary>
    /// Reads a payload's bytes: the prelude (default values when <paramref name="toEveryone"/>) and a reader for the
    /// datagrams, which stop at a zero length. False when the payload is too short for its prelude (or the capability
    /// word it flags), or empty for a burst to everyone, which begins with its block; the prototype would read a short word there, which only a payload that no station sends can show.
    /// </summary>
    public static bool TryUnpack(ReadOnlySpan<byte> payload, bool toEveryone, out PayloadPrelude prelude, out DatagramReader datagrams) =>
        TryUnpack(payload, toEveryone, out prelude, out _, out datagrams);

    /// <summary>
    /// As <see cref="TryUnpack(ReadOnlySpan{byte}, bool, out PayloadPrelude, out DatagramReader)"/>, also exposing the
    /// extension block's items (none when the prelude does not flag a block; for a burst to everyone, the items of its leading block). The datagram
    /// reader always begins at the length byte plus that length, so a block of malformed items cannot move it; a
    /// block that runs past the end of the payload leaves no datagrams. Spare prelude bits are ignored.
    /// </summary>
    public static bool TryUnpack(ReadOnlySpan<byte> payload, bool toEveryone, out PayloadPrelude prelude, out ExtensionReader extensions, out DatagramReader datagrams)
    {
        prelude = new PayloadPrelude(false, null, false, null);
        extensions = new ExtensionReader(default);
        datagrams = new DatagramReader(payload, 0);
        if (toEveryone)
        {
            if (payload.Length < 1)
            {
                return false;
            }

            int lead = payload[0];
            int first = Math.Min(1, payload.Length);
            extensions = new ExtensionReader(payload.Slice(first, Math.Min(lead, payload.Length - first)));
            datagrams = new DatagramReader(payload, 1 + lead);
            return true;
        }

        if (payload.Length < 2)
        {
            return false;
        }

        int flags = payload[0];
        int position = 2;
        int? cap = null;
        if ((flags & HasCapability) != 0)
        {
            if (payload.Length < 4)
            {
                return false;
            }

            cap = (payload[2] << 8) | payload[3];
            position = 4;
        }

        if ((flags & HasExtension) != 0)
        {
            int n = position < payload.Length ? payload[position] : 0;
            int start = Math.Min(position + 1, payload.Length);
            extensions = new ExtensionReader(payload.Slice(start, Math.Min(n, payload.Length - start)));
            position += 1 + n;
        }

        prelude = new PayloadPrelude((flags & Wanted) != 0, (flags & Given) != 0 ? payload[1] : null, (flags & FailedFlag) != 0, cap);
        datagrams = new DatagramReader(payload, position);
        return true;
    }

    /// <summary>The most bytes a callsign in an identification payload has: room for a prefix, a base call, a suffix and an SSID (<c>VK2/M0LTE/QRP-15</c>).</summary>
    public const int MaxCallsignBytes = 16;

    /// <summary>
    /// True when <paramref name="callsign"/> can go in an identification burst: 1 to <see cref="MaxCallsignBytes"/> characters, each an upper-case letter A to Z, a digit, a slash or a hyphen.
    /// </summary>
    public static bool IsValidCallsign(ReadOnlySpan<char> callsign)
    {
        if (callsign.Length is 0 or > MaxCallsignBytes)
        {
            return false;
        }

        foreach (char c in callsign)
        {
            if (!IsCallsignChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsCallsignChar(int c) => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '/' or '-';

    /// <summary>
    /// Writes an identification burst's payload before padding: one length byte N, the callsign's N bytes, the capability word (the more significant byte first), then each item as a tag, a length and its value.
    /// Returns the length, or -1 when it does not fit in <paramref name="destination"/>. Throws when the callsign is not valid (<see cref="IsValidCallsign"/>), the capability word is outside 0 to 65535,
    /// or an item has tag 0 (which ends the items on receipt: it is the padding) or a value over 255 bytes.
    /// </summary>
    public static int PackIdentification(ReadOnlySpan<byte> callsign, int capability, ReadOnlySpan<ExtensionItem> items, Span<byte> destination)
    {
        if (callsign.Length is 0 or > MaxCallsignBytes)
        {
            throw new ArgumentException($"A callsign is 1 to {MaxCallsignBytes} bytes.", nameof(callsign));
        }

        foreach (byte c in callsign)
        {
            if (!IsCallsignChar(c))
            {
                throw new ArgumentException("A callsign is upper-case letters, digits, '/' and '-'.", nameof(callsign));
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegative(capability);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capability, 65535);
        int need = 1 + callsign.Length + 2;
        foreach (ExtensionItem item in items)
        {
            if (item.Tag == 0)
            {
                throw new ArgumentException("Tag 0 is reserved.", nameof(items));
            }

            if (item.Value.Length > 255)
            {
                throw new ArgumentException("An item's value is at most 255 bytes.", nameof(items));
            }

            need += 2 + item.Value.Length;
        }

        if (destination.Length < need)
        {
            return -1;
        }

        destination[0] = (byte)callsign.Length;
        callsign.CopyTo(destination[1..]);
        int p = 1 + callsign.Length;
        destination[p] = (byte)(capability >> 8);
        destination[p + 1] = (byte)capability;
        p += 2;
        foreach (ExtensionItem item in items)
        {
            destination[p] = item.Tag;
            destination[p + 1] = (byte)item.Value.Length;
            item.Value.Span.CopyTo(destination[(p + 2)..]);
            p += 2 + item.Value.Length;
        }

        return p;
    }

    /// <summary>
    /// Reads an identification burst's payload (the bytes as received, padding included). False, and nothing else said, when the payload is too short for its callsign and capability word, or the callsign is not valid
    /// (<see cref="IsValidCallsign"/>). Otherwise the callsign (a slice of <paramref name="payload"/>), the capability word and a reader for the items that follow it: they run to the end of the payload, and a tag of 0 ends them
    /// (zero is never a tag: it is the padding), as does an item whose length runs past the end, and what follows such a point is ignored. Never throws and never allocates.
    /// </summary>
    public static bool TryUnpackIdentification(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> callsign, out int capability, out ExtensionReader items)
    {
        callsign = default;
        capability = 0;
        items = new ExtensionReader(default);
        if (payload.Length < 1)
        {
            return false;
        }

        int n = payload[0];
        if (n is 0 or > MaxCallsignBytes || payload.Length < 1 + n + 2)
        {
            return false;
        }

        ReadOnlySpan<byte> name = payload.Slice(1, n);
        foreach (byte c in name)
        {
            if (!IsCallsignChar(c))
            {
                return false;
            }
        }

        int p = 1 + n;
        int word = (payload[p] << 8) | payload[p + 1];
        p += 2;
        int end = p;
        while (end + 2 <= payload.Length && payload[end] != 0 && end + 2 + payload[end + 1] <= payload.Length)
        {
            end += 2 + payload[end + 1];
        }

        callsign = name;
        capability = word;
        items = new ExtensionReader(payload[p..end]);
        return true;
    }

    /// <summary>
    /// The information bits for a payload: its bytes, zero padding up to the burst's size, scrambling by the burst's
    /// sender, destination and burst number, and the 32-bit check over the scrambled bits. <paramref name="bits"/>
    /// must be exactly <paramref name="infoBits"/> long (see <see cref="StandardFormats.InfoBits"/>) and the payload
    /// no longer than (infoBits - 32) / 8 bytes.
    /// </summary>
    public static void Encode(ReadOnlySpan<byte> payload, int infoBits, int sender, int destination, int burstNumber, Span<byte> bits)
    {
        int room = (infoBits - CheckBits) >> 3;
        if (bits.Length != infoBits || infoBits < CheckBits || payload.Length > room)
        {
            throw new ArgumentException("The bits span must be infoBits long and the payload must fit.");
        }

        int dataBits = infoBits - CheckBits;
        bits.Clear();
        for (int i = 0; i < payload.Length; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                bits[(i * 8) + j] = (byte)((payload[i] >> (7 - j)) & 1);
            }
        }

        PayloadScrambler.Apply(sender, destination, burstNumber, bits[..dataBits]);
        uint crc = BitCrc.Crc32(bits[..dataBits]);
        for (int i = 0; i < CheckBits; i++)
        {
            bits[dataBits + i] = (byte)((crc >> (CheckBits - 1 - i)) & 1);
        }
    }

    /// <summary>
    /// Checks the 32-bit check over the scrambled bits and, if it holds, writes the unscrambled payload bytes
    /// (all of the burst's room, padding included) into <paramref name="payload"/>, which must be at least
    /// (bits.Length - 32) / 8 bytes. False when the check fails.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bits, int sender, int destination, int burstNumber, Span<byte> payload, out int length)
    {
        length = 0;
        if (bits.Length < CheckBits)
        {
            return false;
        }

        int dataBits = bits.Length - CheckBits;
        uint crc = BitCrc.Crc32(bits[..dataBits]);
        for (int i = 0; i < CheckBits; i++)
        {
            if ((bits[dataBits + i] & 1) != ((crc >> (CheckBits - 1 - i)) & 1))
            {
                return false;
            }
        }

        int room = dataBits >> 3;
        if (payload.Length < room)
        {
            throw new ArgumentException("The payload span is too small.", nameof(payload));
        }

        uint w = PayloadScrambler.Seed(sender, destination, burstNumber);
        for (int i = 0; i < room; i++)
        {
            int b = 0;
            for (int j = 0; j < 8; j++)
            {
                b = (b << 1) | ((bits[(i * 8) + j] & 1) ^ (int)PayloadScrambler.Next(ref w));
            }

            payload[i] = (byte)b;
        }

        length = room;
        return true;
    }
}

/// <summary>
/// The protocol byte of a datagram, which says what it is so that hosts of different protocols can share a channel. The modem does nothing with it but carry it: no filtering. The values are borrowed from AX.25's PID field, with 0x00 added.
/// A host may use any other value.
/// </summary>
public static class LinkProtocol
{
    /// <summary>A whole AX.25 frame (AX.25's PID field has no value for this; 0x00 is added).</summary>
    public const byte Ax25Frame = 0x00;

    /// <summary>Internet Protocol (AX.25 PID 0xCC).</summary>
    public const byte Ip = 0xCC;

    /// <summary>Address Resolution Protocol (AX.25 PID 0xCD).</summary>
    public const byte Arp = 0xCD;

    /// <summary>NET/ROM (AX.25 PID 0xCF).</summary>
    public const byte NetRom = 0xCF;

    /// <summary>No layer 3 protocol (AX.25 PID 0xF0).</summary>
    public const byte NoLayer3 = 0xF0;
}
