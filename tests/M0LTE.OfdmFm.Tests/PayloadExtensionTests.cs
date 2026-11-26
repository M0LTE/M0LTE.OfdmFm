using System.Runtime.CompilerServices;
using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

public class PayloadExtensionTests
{
    private static readonly PayloadPrelude Plain = new(false, 40, false, null);

    private static ReadOnlyMemory<byte>[] Grams(params string[] hex) => hex.Select(h => (ReadOnlyMemory<byte>)Convert.FromHexString(h)).ToArray();

    private static ExtensionItem Item(byte tag, string hex) => new(tag, Convert.FromHexString(hex));

    private static byte[] Packed(PayloadPrelude? pre, ExtensionItem[] items, ReadOnlyMemory<byte>[] grams, int room = 2000)
    {
        var buffer = new byte[room];
        int n = PayloadCodec.Pack(pre, items, grams, buffer);
        n.Should().BeGreaterThan(0);
        return buffer[..n];
    }

    private static List<string> Read(byte[] payload, out List<(byte Tag, string Value)> items, out PayloadPrelude pre)
    {
        PayloadCodec.TryUnpack(payload, false, out pre, out ExtensionReader ext, out DatagramReader reader).Should().BeTrue();
        items = [];
        while (ext.TryNext(out byte tag, out var value))
        {
            items.Add((tag, Convert.ToHexString(value)));
        }

        var got = new List<string>();
        while (reader.TryNext(out var g))
        {
            got.Add(Convert.ToHexString(g));
        }

        return got;
    }

    [Fact]
    public void The_Worked_Example_In_The_Document_Is_Byte_For_Byte_What_Packs()
    {
        const string hex = "28 28 05 7F 03 AA BB CC 00 03 48 69 21";
        string doc = File.ReadAllText(DocPath());
        doc.Should().Contain("`" + hex + "`");

        byte[] packed = Packed(Plain, [Item(0x7F, "AABBCC")], Grams("486921"));
        Convert.ToHexString(packed).Should().Be(hex.Replace(" ", ""));

        Read(packed, out var items, out var pre).Should().Equal("486921");
        items.Should().Equal((0x7F, "AABBCC"));
        pre.Should().Be(Plain);
    }

    [Fact]
    public void A_Burst_Without_Items_Is_Byte_For_Byte_The_Shorter_Overload_And_Does_Not_Set_The_Flag()
    {
        var grams = Grams("0102", "03");
        var pre = new PayloadPrelude(true, 7, false, 0x1234);
        var a = new byte[100];
        var b = new byte[100];
        int na = PayloadCodec.Pack(pre, grams, a);
        int nb = PayloadCodec.Pack(pre, [], grams, b);
        nb.Should().Be(na);
        a.Should().Equal(b);
        (a[0] & 0x08).Should().Be(0);
    }

    [Fact]
    public void The_Block_Follows_The_Capability_Word_And_Precedes_The_Datagrams()
    {
        var pre = new PayloadPrelude(true, null, false, 0xABCD);
        byte[] p = Packed(pre, [Item(9, "01")], Grams("EE"));
        Convert.ToHexString(p).Should().Be("C800ABCD03090101" + "0001EE");
        Read(p, out var items, out var back).Should().Equal("EE");
        back.Should().Be(pre);
        items.Should().Equal((9, "01"));
    }

    [Fact]
    public void Several_Items_Are_Read_In_Order()
    {
        byte[] p = Packed(Plain, [Item(1, ""), Item(2, "AA"), Item(200, "0102030405")], Grams("11", "2222"));
        Read(p, out var items, out _).Should().Equal("11", "2222");
        items.Should().Equal((1, ""), (2, "AA"), (200, "0102030405"));
    }

    [Fact]
    public void An_Empty_Block_Is_Skipped_And_The_Datagrams_Still_Read()
    {
        byte[] p = Convert.FromHexString("0828" + "00" + "000211" + "22");
        Read(p, out var items, out _).Should().Equal("1122");
        items.Should().BeEmpty();
    }

    [Fact]
    public void The_Largest_Block_Holds_255_Bytes()
    {
        var items = new[] { Item(1, new string('A', 2 * 253)), };
        PayloadCodec.ExtensionBlockSize(items).Should().Be(256);
        byte[] p = Packed(Plain, items, Grams("55"));
        p[2].Should().Be(255);
        Read(p, out var got, out _).Should().Equal("55");
        got.Should().HaveCount(1);
        got[0].Value.Length.Should().Be(2 * 253);

        var several = new[] { Item(1, new string('B', 2 * 100)), Item(2, new string('C', 2 * 100)), Item(3, new string('D', 2 * 49)) };
        PayloadCodec.ExtensionBlockSize(several).Should().Be(256);
        Packed(Plain, several, Grams("55"))[2].Should().Be(255);
    }

    [Fact]
    public void Item_Lengths_That_Overrun_The_Block_Are_Ignored_And_Do_Not_Move_The_Datagrams()
    {
        // N = 4: a whole item (tag 5, value 33), then a lone byte; the datagrams start 4 bytes after the length byte.
        byte[] p;
        p = Convert.FromHexString("0828" + "04" + "05013307" + "00021122");
        Read(p, out var items, out _).Should().Equal("1122");
        items.Should().Equal((5, "33"));

        // N = 4: one item claiming 9 bytes of value.
        p = Convert.FromHexString("0828" + "04" + "0709AABB" + "00021122");
        Read(p, out items, out _).Should().Equal("1122");
        items.Should().BeEmpty();
    }

    [Fact]
    public void A_Block_Cut_Off_By_The_End_Of_The_Payload_Leaves_No_Datagrams_And_Does_Not_Throw()
    {
        foreach (string hex in new[] { "0828", "082805", "08280501", "0828050102AA", "0828FF" + "0102AA", "C8000123" })
        {
            byte[] p = Convert.FromHexString(hex);
            PayloadCodec.TryUnpack(p, false, out _, out ExtensionReader ext, out DatagramReader reader).Should().BeTrue(hex);
            while (ext.TryNext(out _, out _))
            {
            }

            reader.TryNext(out _).Should().BeFalse(hex);
        }
    }

    [Fact]
    public void Spare_Prelude_Bits_Are_Ignored()
    {
        foreach (int spare in new[] { 0x01, 0x02, 0x04, 0x07 })
        {
            byte[] p = Convert.FromHexString("A0" + "21" + "00021122");
            p[0] |= (byte)spare;
            PayloadCodec.TryUnpack(p, false, out var pre, out ExtensionReader ext, out DatagramReader reader).Should().BeTrue();
            pre.Should().Be(new PayloadPrelude(true, 0x21, false, null));
            ext.TryNext(out _, out _).Should().BeFalse();
            reader.TryNext(out var g).Should().BeTrue();
            Convert.ToHexString(g).Should().Be("1122");
        }
    }

    [Fact]
    public void The_Short_Overload_Skips_The_Block_Too()
    {
        byte[] p = Packed(Plain, [Item(0x7F, "AABBCC")], Grams("486921"));
        PayloadCodec.TryUnpack(p, false, out var pre, out DatagramReader reader).Should().BeTrue();
        pre.Should().Be(Plain);
        reader.TryNext(out var g).Should().BeTrue();
        Convert.ToHexString(g).Should().Be("486921");
    }

    [Fact]
    public void A_Burst_To_Everyone_Has_No_Block()
    {
        PayloadCodec.TryUnpack(Convert.FromHexString("0828" + "00" + "0001EE"), true, out _, out ExtensionReader ext, out DatagramReader reader).Should().BeTrue();
        ext.TryNext(out _, out _).Should().BeFalse();
        reader.TryNext(out _).Should().BeFalse();
        Action a = () => PayloadCodec.Pack(null, [Item(1, "00")], Grams("01"), new byte[50]);
        a.Should().Throw<ArgumentException>();
        PayloadCodec.Pack(null, [], Grams("01"), new byte[50]).Should().Be(3);
    }

    [Fact]
    public void Packing_Refuses_Tag_0_An_Item_Too_Long_And_A_Block_That_Does_Not_Fit_The_Burst()
    {
        var dest = new byte[2000];
        Action zero = () => PayloadCodec.Pack(Plain, [Item(0, "01")], Grams("01"), dest);
        zero.Should().Throw<ArgumentException>();
        Action longItem = () => PayloadCodec.Pack(Plain, [Item(1, new string('A', 2 * 254))], Grams("01"), dest);
        longItem.Should().Throw<ArgumentException>();
        Action longBlock = () => PayloadCodec.Pack(Plain, [Item(1, new string('A', 2 * 200)), Item(2, new string('A', 2 * 100))], Grams("01"), dest);
        longBlock.Should().Throw<ArgumentException>();

        // 2 prelude + 1 length + 4 item + 3 datagram = 10.
        PayloadCodec.Pack(Plain, [Item(1, "AABB")], Grams("01"), new byte[9]).Should().Be(-1);
        PayloadCodec.Pack(Plain, [Item(1, "AABB")], Grams("01"), new byte[10]).Should().Be(10);
        PayloadCodec.Pack(Plain, [Item(1, "AABB")], [], new byte[6]).Should().Be(-1);
    }

    [Fact]
    public void Capacity_And_Symbols_For_Account_For_The_Block()
    {
        int size = PayloadCodec.ExtensionBlockSize([Item(1, "AABB")]);
        size.Should().Be(5);
        PayloadCodec.ExtensionBlockSize([]).Should().Be(0);
        foreach (int fmt in new[] { 192, 24, 5 * 32 + 20 })
        {
            PayloadCodec.Capacity(fmt, 40, 0).Should().Be(PayloadCodec.Capacity(fmt, 40));
            PayloadCodec.Capacity(fmt, 40, size).Should().Be(PayloadCodec.Capacity(fmt, 40) - 5);
            PayloadCodec.SymbolsFor(fmt, 100, 0).Should().Be(PayloadCodec.SymbolsFor(fmt, 100));
            int? s = PayloadCodec.SymbolsFor(fmt, 100, size);
            s.Should().Be(PayloadCodec.SymbolsFor(fmt, 105));
            PayloadCodec.Capacity(fmt, s!.Value, size).Should().BeGreaterThanOrEqualTo(100);
        }
    }

    [Fact]
    public void Round_Trips_Through_The_Scrambler_And_The_Check()
    {
        var rng = new Random(5);
        foreach (int fmt in new[] { 192, 24, 5 * 32 + 20 })
        {
            var items = new[] { Item(0x7F, "AABBCC"), Item(3, ""), Item(250, "0102030405060708") };
            var grams = Grams("0102030405", "FF");
            int block = PayloadCodec.ExtensionBlockSize(items);
            int symbols = PayloadCodec.SymbolsFor(fmt, 2 + 2 + 7 + 3 + 1, block) ?? 252;
            int info = StandardFormats.InfoBits(fmt, symbols);
            var room = new byte[(info - 32) / 8];
            int n = PayloadCodec.Pack(new PayloadPrelude(true, 40, false, 0x1234), items, grams, room);
            n.Should().BeGreaterThan(0);
            var bits = new byte[info];
            PayloadCodec.Encode(room.AsSpan(0, n), info, 3, 4, rng.Next(16), bits);
            // decode with the burst number used
            int burst = 0;
            for (; burst < 16; burst++)
            {
                var back = new byte[room.Length];
                if (PayloadCodec.TryDecode(bits, 3, 4, burst, back, out _) && back[..n].AsSpan().SequenceEqual(room.AsSpan(0, n)))
                {
                    break;
                }
            }

            burst.Should().BeLessThan(16);
            var again = new byte[room.Length];
            PayloadCodec.TryDecode(bits, 3, 4, burst, again, out _).Should().BeTrue();
            Read(again, out var got, out var pre).Should().Equal("0102030405", "FF");
            got.Should().Equal((0x7F, "AABBCC"), (3, ""), (250, "0102030405060708"));
            pre.Should().Be(new PayloadPrelude(true, 40, false, 0x1234));
        }
    }

    [Fact]
    public void Unpacking_Allocates_Nothing()
    {
        byte[] p = Packed(new PayloadPrelude(true, 40, false, 0x1234), [Item(0x7F, "AABBCC"), Item(2, "")], Grams("0102", "03"));
        Walk(p);
        long before = GC.GetAllocatedBytesForCurrentThread();
        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            total += Walk(p);
        }

        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        total.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_Reports_Spare_Burst_Number_Bits_Do_Not_Change_What_Is_Read()
    {
        for (int low = 0; low < 8; low++)
        {
            new BurstHeader(0, BurstKind.Report, 1, 2, 40, 0, low).Failed.Should().BeFalse();
            new BurstHeader(0, BurstKind.Report, 1, 2, 40, 0, 8 | low).Failed.Should().BeTrue();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Walk(ReadOnlySpan<byte> p)
    {
        PayloadCodec.TryUnpack(p, false, out _, out ExtensionReader ext, out DatagramReader reader);
        int n = 0;
        while (ext.TryNext(out byte tag, out var v))
        {
            n += tag + v.Length;
        }

        while (reader.TryNext(out var g))
        {
            n += g.Length;
        }

        return n;
    }

    private static string DocPath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "docs", "adaptation-protocol.md"));
}
