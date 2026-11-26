using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>The identification burst's payload (the callsign and capability word, then tagged items) and the burst that carries it.</summary>
public class IdentificationPayloadTests
{
    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Packed(string callsign, int capability, params ExtensionItem[] items)
    {
        var buffer = new byte[400];
        int n = PayloadCodec.PackIdentification(Ascii(callsign), capability, items, buffer);
        n.Should().BeGreaterThan(0);
        return buffer[..n];
    }

    private static bool Unpack(byte[] payload, out string callsign, out int capability, out List<(byte Tag, string Value)> items)
    {
        items = [];
        callsign = "";
        if (!PayloadCodec.TryUnpackIdentification(payload, out ReadOnlySpan<byte> name, out capability, out ExtensionReader reader))
        {
            return false;
        }

        callsign = Encoding.ASCII.GetString(name);
        while (reader.TryNext(out byte tag, out ReadOnlySpan<byte> value))
        {
            items.Add((tag, Convert.ToHexString(value)));
        }

        return true;
    }

    [Fact]
    public void The_Worked_Examples_In_The_Document_Are_Byte_For_Byte_What_Packs()
    {
        const string withItem = "07 4D 30 4C 54 45 2D 31 01 23 7F 03 AA BB CC";
        const string plain = "07 4D 30 4C 54 45 2D 31 00 00";
        string doc = File.ReadAllText(DocPath());
        doc.Should().Contain("`" + withItem + "`").And.Contain("`" + plain + "`");

        byte[] packed = Packed("M0LTE-1", 0x0123, new ExtensionItem(0x7F, new byte[] { 0xAA, 0xBB, 0xCC }));
        Convert.ToHexString(packed).Should().Be(withItem.Replace(" ", ""));
        Unpack(packed, out string callsign, out int capability, out var items).Should().BeTrue();
        (callsign, capability).Should().Be(("M0LTE-1", 0x0123));
        items.Should().Equal((0x7F, "AABBCC"));

        Convert.ToHexString(Packed("M0LTE-1", 0)).Should().Be(plain.Replace(" ", ""));
    }

    [Theory]
    [InlineData("M0LTE", 0x0000)]
    [InlineData("M0LTE-1", 0x1234)]
    [InlineData("F/M0LTE/P", 0xFFFF)]
    [InlineData("A", 0x0001)]
    [InlineData("VK2/M0LTE/QRP-15", 0x8000)]
    public void A_Callsign_And_Capability_Word_Round_Trip_Through_Padding(string callsign, int capability)
    {
        byte[] packed = Packed(callsign, capability);
        packed.Length.Should().Be(1 + callsign.Length + 2);
        packed[0].Should().Be((byte)callsign.Length);
        packed[^2].Should().Be((byte)(capability >> 8), "the more significant byte first");
        packed[^1].Should().Be((byte)capability);

        byte[] padded = [.. packed, .. new byte[60]];
        Unpack(padded, out string back, out int word, out var items).Should().BeTrue();
        (back, word).Should().Be((callsign, capability));
        items.Should().BeEmpty("the zero padding is not an item");
    }

    [Fact]
    public void The_Longest_And_Shortest_Callsigns_Fit_And_The_Burst_Stays_Short_At_The_Most_Robust_Format()
    {
        PayloadCodec.MaxCallsignBytes.Should().Be(16);
        foreach (string callsign in new[] { "A", "M0LTE", "GB7RDG-15", new string('9', 16) })
        {
            byte[] packed = Packed(callsign, 0);
            int symbols = PayloadCodec.SymbolsFor(StandardFormats.FirstContact, packed.Length)!.Value;
            var renderer = new BurstRenderer();
            double seconds = (renderer.SamplesFor(StandardFormats.FirstContact, symbols) - renderer.LeadSamples) / 48000.0;
            Console.WriteLine($"{callsign.Length,2} characters: {packed.Length,2} bytes, {symbols,2} payload symbols, {seconds:F2} s without the lead-in");
            seconds.Should().BeLessThan(2.0);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("m0lte")]
    [InlineData("M0 LTE")]
    [InlineData("M0LTE_1")]
    [InlineData("M0LTEé")]
    [InlineData("12345678901234567")]
    public void A_Callsign_That_Cannot_Go_On_The_Air_Is_Refused_By_The_Packer_And_The_Station(string callsign)
    {
        PayloadCodec.IsValidCallsign(callsign).Should().BeFalse();
        var buffer = new byte[100];
        ((Action)(() => PayloadCodec.PackIdentification(Encoding.UTF8.GetBytes(callsign), 0, default, buffer))).Should().Throw<ArgumentException>();
        if (callsign.Length > 0)
        {
            ((Action)(() => new Station(callsign, 1, new Random(1)))).Should().Throw<ArgumentException>();
        }
    }

    [Fact]
    public void The_Packer_Refuses_A_Tag_Of_Zero_A_Long_Value_And_A_Word_Out_Of_Range_And_Says_No_To_A_Small_Buffer()
    {
        var buffer = new byte[100];
        ((Action)(() => PayloadCodec.PackIdentification(Ascii("M0LTE"), 0, [new ExtensionItem(0, new byte[1])], buffer))).Should().Throw<ArgumentException>();
        ((Action)(() => PayloadCodec.PackIdentification(Ascii("M0LTE"), 0, [new ExtensionItem(1, new byte[256])], new byte[400]))).Should().Throw<ArgumentException>();
        ((Action)(() => PayloadCodec.PackIdentification(Ascii("M0LTE"), 65536, default, buffer))).Should().Throw<ArgumentOutOfRangeException>();
        ((Action)(() => PayloadCodec.PackIdentification(Ascii("M0LTE"), -1, default, buffer))).Should().Throw<ArgumentOutOfRangeException>();
        PayloadCodec.PackIdentification(Ascii("M0LTE"), 0, default, new byte[7]).Should().Be(-1);
        PayloadCodec.PackIdentification(Ascii("M0LTE"), 0, default, new byte[8]).Should().Be(8);
    }

    [Fact]
    public void Items_That_Are_Not_Known_Are_Skipped_And_Each_Is_Read_Whole()
    {
        byte[] packed = Packed("M0LTE", 0x0102, new ExtensionItem(0x7F, new byte[] { 1, 2, 3 }), new ExtensionItem(9, default), new ExtensionItem(0xFF, new byte[255]));
        Unpack([.. packed, 0, 0, 0, 0], out string callsign, out int capability, out var items).Should().BeTrue();
        (callsign, capability).Should().Be(("M0LTE", 0x0102));
        items.Select(i => (i.Tag, i.Value.Length)).Should().Equal(((byte)0x7F, 6), ((byte)9, 0), ((byte)0xFF, 510));
    }

    [Fact]
    public void A_Tag_Of_Zero_Ends_The_Items_And_So_Does_One_That_Runs_Past_The_End()
    {
        byte[] head = Packed("M0LTE", 0);

        // An item, then padding, then bytes that look like another item: the zero tag ends them.
        Unpack([.. head, 0x20, 0x01, 0xAA, 0x00, 0x00, 0x30, 0x01, 0xBB], out _, out _, out var items).Should().BeTrue();
        items.Should().Equal((0x20, "AA"));

        // An item whose length runs past the end ends them, and the item before it is kept.
        Unpack([.. head, 0x20, 0x01, 0xAA, 0x30, 0x05, 0xBB], out _, out _, out items).Should().BeTrue();
        items.Should().Equal((0x20, "AA"));

        // A lone tag byte with no length.
        Unpack([.. head, 0x20], out _, out _, out items).Should().BeTrue();
        items.Should().BeEmpty();
    }

    [Fact]
    public void Malformed_Payloads_Are_Refused_Without_Throwing()
    {
        Unpack([], out _, out _, out _).Should().BeFalse();
        Unpack([0], out _, out _, out _).Should().BeFalse("a callsign of no characters");
        Unpack([5, 0x4D, 0x30], out _, out _, out _).Should().BeFalse("the callsign runs past the end");
        Unpack([5, 0x4D, 0x30, 0x4C, 0x54, 0x45, 0x00], out _, out _, out _).Should().BeFalse("no room for the whole capability word");
        Unpack([17, .. Enumerable.Repeat((byte)'A', 17), 0, 0], out _, out _, out _).Should().BeFalse("longer than 16");
        Unpack([5, 0x4D, 0x30, 0x6C, 0x54, 0x45, 0, 0], out _, out _, out _).Should().BeFalse("a lower-case l");
        Unpack([5, 0x4D, 0x30, 0x20, 0x54, 0x45, 0, 0], out _, out _, out _).Should().BeFalse("a space");
        Unpack([5, 0x4D, 0x30, 0x00, 0x54, 0x45, 0, 0], out _, out _, out _).Should().BeFalse("a zero byte");
        Unpack([5, 0x4D, 0x30, 0xC9, 0x54, 0x45, 0, 0], out _, out _, out _).Should().BeFalse("a byte over 127");

        // Every prefix of a good payload and a few hundred random ones: no exception, and the answer agrees with the rules.
        byte[] good = Packed("F/M0LTE/P", 0xABCD, new ExtensionItem(3, new byte[] { 1, 2 }));
        for (int n = 0; n <= good.Length; n++)
        {
            Unpack(good[..n], out _, out _, out _).Should().Be(n >= 1 + 9 + 2);
        }

        var rng = new Random(11);
        for (int i = 0; i < 500; i++)
        {
            byte[] junk = new byte[rng.Next(0, 40)];
            rng.NextBytes(junk);
            bool ok = Unpack(junk, out string callsign, out _, out _);
            ok.Should().Be(junk.Length > 0 && junk[0] is >= 1 and <= 16 && junk.Length >= 3 + junk[0] && junk.Skip(1).Take(junk[0]).All(b => PayloadCodec.IsValidCallsign(Encoding.ASCII.GetString([b]))));
            if (ok)
            {
                callsign.Length.Should().Be(junk[0]);
            }
        }
    }

    [Fact]
    public void Unpacking_Allocates_Nothing()
    {
        byte[] payload = [.. Packed("M0LTE-1", 0x0102, new ExtensionItem(0x7F, new byte[] { 1, 2, 3 })), 0, 0, 0, 0];
        Walk(payload);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Walk(payload);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Walk(byte[] payload)
    {
        int sum = 0;
        if (PayloadCodec.TryUnpackIdentification(payload, out ReadOnlySpan<byte> name, out int capability, out ExtensionReader items))
        {
            sum += name.Length + capability;
            while (items.TryNext(out byte tag, out ReadOnlySpan<byte> value))
            {
                sum += tag + value.Length;
            }
        }

        return sum;
    }

    [Fact]
    public void An_Identification_Burst_Renders_Is_Found_Read_And_Received_Like_A_Data_Burst_To_Everyone()
    {
        byte[] payload = Packed("M0LTE-1", 0x0123, new ExtensionItem(0x7F, new byte[] { 0xAA, 0xBB, 0xCC }));
        int symbols = PayloadCodec.SymbolsFor(StandardFormats.FirstContact, payload.Length)!.Value;
        BurstHeader header = BurstHeader.Identification(7, StandardFormats.FirstContact, symbols, 5);
        header.Should().Be(new BurstHeader(0, BurstKind.Identification, 7, 0, 192, symbols, 5));

        var renderer = new BurstRenderer();
        float[] audio = new float[renderer.SamplesFor(StandardFormats.FirstContact, symbols)];
        renderer.RenderData(header, payload, audio).Should().Be(audio.Length);

        FormatOpening kind = StandardFormats.Describe(StandardFormats.FirstContact).Opening;
        BurstCandidate found = BurstFinder.Find(audio, kind).Single();
        var reader = new HeaderReader();
        reader.TryRead(audio, found.Index, kind, out BurstHeader read).Should().BeTrue();
        read.Should().Be(header);

        var receiver = new PayloadReceiver();
        byte[] received = new byte[PayloadReceiver.PayloadRoom(read)];
        PayloadReceiveResult result = receiver.Receive(audio, found.Index, read, reader.Bits, received);
        result.CheckPassed.Should().BeTrue();
        Unpack(received[..result.PayloadBytes], out string callsign, out int capability, out var items).Should().BeTrue();
        (callsign, capability).Should().Be(("M0LTE-1", 0x0123));
        items.Should().Equal((0x7F, "AABBCC"));
    }

    [Fact]
    public void The_Renderer_Takes_An_Identification_Header_But_Only_For_Everyone_And_Still_Refuses_A_Report()
    {
        var renderer = new BurstRenderer();
        byte[] payload = Packed("M0LTE", 0);
        int symbols = PayloadCodec.SymbolsFor(StandardFormats.FirstContact, payload.Length)!.Value;
        float[] audio = new float[renderer.SamplesFor(StandardFormats.FirstContact, symbols)];
        ((Action)(() => renderer.RenderData(new BurstHeader(0, BurstKind.Identification, 1, 5, StandardFormats.FirstContact, symbols, 0), payload, audio))).Should().Throw<ArgumentException>();
        ((Action)(() => renderer.RenderData(BurstHeader.Report(1, 2, 100, false), payload, audio))).Should().Throw<ArgumentException>();
        ((Action)(() => renderer.RenderData(new BurstHeader(1, BurstKind.Identification, 1, 0, StandardFormats.FirstContact, symbols, 0), payload, audio))).Should().Throw<ArgumentException>();
        renderer.RenderData(BurstHeader.Identification(1, StandardFormats.FirstContact, symbols, 0), payload, audio).Should().Be(audio.Length);
    }

    private static string DocPath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "docs", "adaptation-protocol.md"));
}
