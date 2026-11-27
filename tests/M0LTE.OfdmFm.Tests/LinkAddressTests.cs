using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

public class LinkAddressTests
{
    /// <summary>The key worked out the slow way, from the definition in the protocol document: CRC-32, polynomial 0x04C11DB7, started at all ones, each byte most significant bit first, nothing reflected, nothing inverted.</summary>
    private static uint Slow(byte[] bytes)
    {
        uint reg = 0xFFFFFFFF;
        foreach (byte b in bytes)
        {
            for (int j = 7; j >= 0; j--)
            {
                uint top = reg >> 31;
                reg <<= 1;
                if ((top ^ (uint)((b >> j) & 1)) != 0)
                {
                    reg ^= 0x04C11DB7;
                }
            }
        }

        return reg;
    }

    [Fact]
    public void The_Worked_Example_In_The_Document_Is_What_The_Key_Is()
    {
        // docs/adaptation-protocol.md, "Link address keys": the address M0LTE-1 (ASCII 4D 30 4C 54 45 2D 31) has the key 6EEC7110, sent as the four bytes 6E EC 71 10.
        LinkAddress address = LinkAddress.FromAscii("M0LTE-1");
        Convert.ToHexString(address.Bytes).Should().Be("4D304C54452D31");
        address.Key.Should().Be(0x6EEC7110u);
        var wire = new byte[4];
        PayloadCodec.WriteKey(address.Key, wire);
        Convert.ToHexString(wire).Should().Be("6EEC7110");
        PayloadCodec.ReadKey(wire).Should().Be(0x6EEC7110u);
    }

    [Theory]
    [InlineData("M0LTE-1")]
    [InlineData("G9XYZ")]
    [InlineData("A")]
    [InlineData("12345678901234567890123456789012")]
    public void The_Key_Is_The_Payloads_Own_Check_Run_Over_The_Bytes(string text)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
        LinkAddress.FromAscii(text).Key.Should().Be(Slow(bytes));
        LinkAddress.KeyOf(bytes).Should().Be(Slow(bytes));
    }

    [Fact]
    public void An_Address_Is_One_To_Thirty_Two_Opaque_Bytes_Compared_By_Value()
    {
        byte[] source = [1, 2, 3];
        var a = new LinkAddress(source);
        source[0] = 99;
        a.Bytes.ToArray().Should().Equal(1, 2, 3);
        a.Should().Be(new LinkAddress(new byte[] { 1, 2, 3 }));
        (a == new LinkAddress(new byte[] { 1, 2, 3 })).Should().BeTrue();
        a.GetHashCode().Should().Be(new LinkAddress(new byte[] { 1, 2, 3 }).GetHashCode());
        a.Should().NotBe(new LinkAddress(new byte[] { 1, 2 }));
        (a != new LinkAddress(new byte[] { 1, 2, 4 })).Should().BeTrue();
        new LinkAddress(new byte[32]).Bytes.Length.Should().Be(32);
        ((Action)(() => new LinkAddress(new byte[33]))).Should().Throw<ArgumentException>();
        ((Action)(() => new LinkAddress(ReadOnlySpan<byte>.Empty))).Should().Throw<ArgumentException>();
        LinkAddress.FromAscii("M0LTE").Should().Be(new LinkAddress("M0LTE"u8));
    }

    [Fact]
    public void Everyone_Is_A_Value_Of_Its_Own_That_No_Bytes_Equal_And_Has_No_Key()
    {
        LinkAddress.Everyone.IsEveryone.Should().BeTrue();
        LinkAddress.Everyone.Should().NotBe(default(LinkAddress));
        LinkAddress.Everyone.Should().NotBe(LinkAddress.FromAscii("everyone"));
        default(LinkAddress).IsEmpty.Should().BeTrue();
        LinkAddress.Everyone.IsEmpty.Should().BeFalse();
        LinkAddress.Everyone.ToString().Should().Be("everyone");
        ((Action)(() => _ = LinkAddress.Everyone.Key)).Should().Throw<InvalidOperationException>();
        ((Action)(() => _ = default(LinkAddress).Key)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void The_Address_Block_Sizes_Are_What_The_Layout_Says()
    {
        // Length byte, then From (tag, length, 6 bytes a run), then For (tag, length, 4 bytes).
        PayloadCodec.AddressBlockSize(1, false).Should().Be(1 + 2 + 6);
        PayloadCodec.AddressBlockSize(3, false).Should().Be(1 + 2 + 18);
        PayloadCodec.AddressBlockSize(1, true).Should().Be(1 + 2 + 6 + 2 + 4);
        PayloadCodec.AddressBlockSize(0, false).Should().Be(1);
    }

    private static byte[] Run(LinkAddress from, byte protocol, int count)
    {
        var value = new byte[PayloadCodec.RunBytes];
        PayloadCodec.WriteRun(from.Key, protocol, count, value);
        return value;
    }

    private static string Packed(PayloadPrelude? prelude, ExtensionItem[] items, params byte[][] datagrams)
    {
        var buffer = new byte[200];
        int n = PayloadCodec.Pack(prelude, items, [.. datagrams.Select(d => (ReadOnlyMemory<byte>)d)], buffer);
        return Convert.ToHexString(buffer.AsSpan(0, n));
    }

    [Fact]
    public void The_Worked_Examples_Of_The_Payload_In_The_Document_Are_Byte_For_Byte_What_Packs()
    {
        // docs/adaptation-protocol.md, "Link addresses": the datagram "Hi!" sent from M0LTE-1 with the protocol 0xF0, for G9XYZ.
        LinkAddress from = LinkAddress.FromAscii("M0LTE-1");
        LinkAddress other = LinkAddress.FromAscii("G9XYZ");
        other.Key.Should().Be(0xB97C7FB1u);
        byte[] hi = "Hi!"u8.ToArray();
        var fromItem = new ExtensionItem(PayloadCodec.FromTag, Run(from, LinkProtocol.NoLayer3, 1));
        var forValue = new byte[4];
        PayloadCodec.WriteKey(other.Key, forValue);
        var forItem = new ExtensionItem(PayloadCodec.ForTag, forValue);

        Packed(new PayloadPrelude(false, 0x28, false, null), [fromItem], hi).Should().Be("2828" + "08" + "0106" + "6EEC7110F001" + "000348" + "6921");
        Packed(null, [fromItem, forItem], hi).Should().Be("0E" + "0106" + "6EEC7110F001" + "0204B97C7FB1" + "0003486921");
        Packed(null, [fromItem], hi).Should().Be("08" + "0106" + "6EEC7110F001" + "0003486921");

        // Two datagrams, "Hi!" from M0LTE-1 as IP and "OK" from G9XYZ with no layer 3: two runs.
        byte[] runs = [.. Run(from, LinkProtocol.Ip, 1), .. Run(other, LinkProtocol.NoLayer3, 1)];
        Packed(null, [new ExtensionItem(PayloadCodec.FromTag, runs)], hi, "OK"u8.ToArray()).Should().Be("0E" + "010C" + "6EEC7110CC01" + "B97C7FB1F001" + "0003486921" + "00024F4B");
        PayloadCodec.AddressBlockSize(2, false).Should().Be(1 + 2 + 12);

        // An announcement of G9XYZ's address: a data burst to one station with a recommendation of 0x28 and no datagrams, whose From has one run with the protocol byte 0 and a count of 0.
        Packed(new PayloadPrelude(false, 0x28, false, null), [new ExtensionItem(PayloadCodec.FromTag, Run(other, 0, 0))]).Should().Be("2828" + "08" + "0106" + "B97C7FB1" + "0000");
    }

    [Fact]
    public void An_Identification_Payload_Does_Not_Begin_With_A_Block()
    {
        // The callsign's length first, as it always was: the document's worked example, unchanged.
        var buffer = new byte[40];
        int n = PayloadCodec.PackIdentification("M0LTE-1"u8, 0, default, buffer);
        Convert.ToHexString(buffer.AsSpan(0, n)).Should().Be("074D304C54452D31" + "0000");
    }

    [Fact]
    public void An_Address_Can_Be_Made_From_A_Key_Alone_And_Is_Equal_Only_To_Another_Made_The_Same_Way()
    {
        LinkAddress full = LinkAddress.FromAscii("M0LTE-1");
        LinkAddress byKey = LinkAddress.FromKey(full.Key);
        byKey.IsKeyOnly.Should().BeTrue();
        byKey.IsEmpty.Should().BeFalse();
        byKey.IsEveryone.Should().BeFalse();
        byKey.Key.Should().Be(0x6EEC7110u);
        byKey.Bytes.IsEmpty.Should().BeTrue();
        byKey.Should().Be(LinkAddress.FromKey(0x6EEC7110u));
        byKey.GetHashCode().Should().Be(LinkAddress.FromKey(0x6EEC7110u).GetHashCode());
        byKey.Should().NotBe(full, "the key says which address it is on the air; as a value it is not the bytes");
        byKey.Should().NotBe(LinkAddress.FromKey(1));
        byKey.ToString().Should().Be("key 6EEC7110");
    }

    [Fact]
    public void The_Protocol_Constants_Are_The_Values_The_Document_Lists()
    {
        LinkProtocol.Ax25Frame.Should().Be(0x00);
        LinkProtocol.Ip.Should().Be(0xCC);
        LinkProtocol.Arp.Should().Be(0xCD);
        LinkProtocol.NetRom.Should().Be(0xCF);
        LinkProtocol.NoLayer3.Should().Be(0xF0);
    }
}
