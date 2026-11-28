using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Step two of docs/station-numbers.md, the parts that need one station: how a number and an id are chosen, what an identification burst carries (the station id, a Clash), what a station does on hearing a Clash,
/// and what it does with a burst it had built when its number changed. Everything is on the sample clock; nothing waits for real time.
/// </summary>
public class StationNumbersTests
{
    private static readonly int[] Seeds = [1, 2, 3, 4, 5, 6, 7, 8];

    // ---- taking a number ----

    [Fact]
    public void A_Station_Takes_A_Number_From_1_To_63_And_An_Id_When_Given_Its_Identity_And_The_Same_Seed_Gives_The_Same_Choice()
    {
        var numbers = new List<int>();
        foreach (int seed in Seeds)
        {
            var a = new Station(new Random(seed));
            a.StationNumber.Should().Be(0);
            a.StationId.Should().Be(0u);
            a.SetIdentity("M0LTE").Should().Be(IdentityResult.Accepted);
            a.StationNumber.Should().BeInRange(1, 63);

            var b = new Station(new Random(seed));
            b.SetIdentity("M0LTE");
            (b.StationNumber, b.StationId).Should().Be((a.StationNumber, a.StationId), "one seed, one choice");
            numbers.Add(a.StationNumber);

            // Giving the identity again changes nothing.
            int before = a.StationNumber;
            a.SetIdentity("M0LTE").Should().Be(IdentityResult.Accepted);
            a.StationNumber.Should().Be(before);
            a.SetIdentity("M0OTH").Should().Be(IdentityResult.Different);
            a.StationNumber.Should().Be(before);
        }

        Hostile.Say("station numbers taken with seeds 1 to 8: " + string.Join(",", numbers));
        numbers.Distinct().Count().Should().BeGreaterThan(1, "the seed matters");
    }

    [Fact]
    public void A_Refused_Identity_Gives_No_Number()
    {
        var station = new Station(new Random(1));
        station.SetIdentity("m0lte").Should().Be(IdentityResult.Invalid);
        station.StationNumber.Should().Be(0);
        station.StationId.Should().Be(0u);
    }

    [Fact]
    public void A_Number_Heard_In_The_Last_Ten_Minutes_Is_Never_Taken()
    {
        foreach (int seed in Seeds)
        {
            // Everything but 61 to 63 was heard a moment ago.
            var station = new Station(new Random(seed));
            station.Skip(5 * 48000);
            for (int n = 1; n <= 60; n++)
            {
                station.HearNumber(n);
            }

            station.SetIdentity("M0LTE");
            station.StationNumber.Should().BeInRange(61, 63, $"seed {seed}");
        }
    }

    [Fact]
    public void A_Number_Last_Heard_More_Than_Ten_Minutes_Ago_Counts_As_Unheard_And_One_Heard_Ten_Minutes_Ago_Does_Not()
    {
        var totals = new SortedDictionary<int, int>();
        foreach (int seed in Seeds)
        {
            var station = new Station(new Random(seed));
            for (int n = 1; n <= 63; n++)
            {
                station.HearNumber(n); // all at clock 0
            }

            station.Skip(600 * 48000 + 1); // 600 s and a sample on: all of them are now more than ten minutes old
            for (int n = 1; n <= 63; n++)
            {
                if (n is not (7 or 40))
                {
                    station.HearNumber(n);
                }
            }

            station.SetIdentity("M0LTE");
            station.StationNumber.Should().BeOneOf([7, 40], $"seed {seed}: only 7 and 40 were last heard more than ten minutes ago");
            totals[station.StationNumber] = totals.GetValueOrDefault(station.StationNumber) + 1;
        }

        Hostile.Say("of 7 and 40, over seeds 1 to 8: " + string.Join(", ", totals.Select(t => $"{t.Key} x{t.Value}")));

        // Exactly ten minutes is still inside the window: with 7 heard at 0 and everything else heard later, at 600 s on the dot none is free and the one unheard longest, 7, is taken.
        var edge = new Station(new Random(1));
        edge.HearNumber(7);
        edge.Skip(100 * 48000);
        for (int n = 1; n <= 63; n++)
        {
            if (n != 7)
            {
                edge.HearNumber(n);
            }
        }

        edge.Skip(500 * 48000);
        edge.SampleCount.Should().Be(600L * 48000);
        edge.SetIdentity("M0LTE");
        edge.StationNumber.Should().Be(7);
    }

    [Fact]
    public void When_All_63_Were_Heard_The_One_Unheard_Longest_Is_Taken_Whatever_The_Seed()
    {
        // Heard one after another, a second apart, in an order that is not the numbers' own; the first of them, 29, has been unheard longest.
        int[] order = [.. Enumerable.Range(1, 63).Select(k => ((k * 17) % 63) + 1).Distinct()];
        order.Should().HaveCount(63);
        foreach (int seed in Seeds)
        {
            var station = new Station(new Random(seed));
            foreach (int n in order)
            {
                station.HearNumber(n);
                station.Skip(48000);
            }

            station.SetIdentity("M0LTE");
            station.StationNumber.Should().Be(order[0], $"seed {seed}");
        }
    }

    [Fact]
    public void A_Station_That_Moves_Never_Takes_The_Number_It_Left_And_Never_Returns_A_Number_Outside_1_To_63()
    {
        var station = new Station(5, 0x11111111u, new Random(3), runJobsInline: true).WithIdentity("M0LTE");
        station.StationNumber.Should().Be(5);
        station.StationId.Should().Be(0x11111111u);
        for (int i = 0; i < 300; i++)
        {
            int before = station.StationNumber;
            uint idBefore = station.StationId;
            station.HearHeaderFrom(before);
            station.StationNumber.Should().NotBe(before).And.BeInRange(1, 63);
            station.StationId.Should().NotBe(idBefore, "a new number comes with a new id (32 bits: seed 3 draws no repeat)");
        }

        station.NumberChanges.Should().Be(300);
    }

    [Fact]
    public void A_Station_With_No_Identity_Has_No_Number_And_Does_Not_Move_On_Hearing_One()
    {
        var station = new Station(new Random(1));
        station.HearHeaderFrom(5);
        station.StationNumber.Should().Be(0);
        station.NumberChanges.Should().Be(0);
        station.SetIdentity("M0LTE");
        station.StationNumber.Should().NotBe(5, "5 was just heard as a sender");
    }

    [Fact]
    public void A_Station_That_Hears_Its_Own_Number_While_It_Is_Transmitting_Does_Not_Move()
    {
        var station = new Station(5, 0x11111111u, new Random(1), runJobsInline: true).WithIdentity("M0LTE");
        var layer = new FakeAudioLayer(station);
        station.Send(TestBurst.Datagram(1, 20), LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        layer.Run(40 * 48000);
        layer.Requests.Should().HaveCount(1);
        TransmitRequestEvent request = layer.Requests[0];
        station.TransmitStarted(request.Id, station.SampleCount);
        station.IsTransmitting.Should().BeTrue();
        station.HearHeaderFrom(5);
        station.NumberChanges.Should().Be(0, "it is not told what it said itself");
        station.Push(new float[10000]);
        station.TransmitEnded(request.Id, station.SampleCount);
        station.HearHeaderFrom(5);
        station.NumberChanges.Should().Be(0, "nor inside the mute tail after it");
        station.Skip(4800);
        station.HearHeaderFrom(5);
        station.NumberChanges.Should().Be(1, "but once the tail is over a burst with its number is another station's");
    }

    [Fact]
    public void The_Same_Seed_Gives_The_Same_Numbers_Ids_And_Run_Down_To_Every_Event()
    {
        List<string> Run(int seed)
        {
            var rig = new SimRig(3, seed: seed, randomNumbers: true);
            foreach (Station s in rig.Stations)
            {
                s.IdentificationSuspended = false;
                s.IdentificationInterval = 20L * 48000;
            }

            rig.RunTo(6);
            for (int round = 0; round < 3; round++)
            {
                for (int from = 1; from <= 3; from++)
                {
                    rig.Send(from, (from % 3) + 1, (round * 3) + from).Should().Be(SendResult.Accepted);
                    rig.Run(7);
                }
            }

            rig.Run(30);
            return [.. rig.Channel.Log, .. rig.Stations.Select(s => $"{s.StationNumber}:{s.StationId:X8}")];
        }

        List<string> once = Run(4);
        once.Should().Contain(l => l.Contains(" rx from ", StringComparison.Ordinal));
        Run(4).Should().Equal(once);
        Run(5).Should().NotEqual(once, "the seeds matter");
    }

    // ---- the station id on the air ----

    [Fact]
    public void The_Identification_Items_Pack_And_Unpack_And_Match_The_Worked_Examples_In_The_Protocol_Document()
    {
        var id = new byte[PayloadCodec.KeyBytes];
        PayloadCodec.WriteKey(0xDEADBEEF, id);
        var clash = new byte[PayloadCodec.ClashBytes];
        PayloadCodec.WriteClash(5, 0x01020304, clash);
        clash.Should().Equal(0x05, 0x01, 0x02, 0x03, 0x04);

        var packed = new byte[64];
        int n = PayloadCodec.PackIdentification("M0LTE-1"u8, 0x0123, [new ExtensionItem(PayloadCodec.StationIdTag, id)], packed);
        Convert.ToHexString(packed.AsSpan(0, n)).Should().Be("074D304C54452D3101230104DEADBEEF");

        n = PayloadCodec.PackIdentification("M0LTE-1"u8, 0x0123, [new ExtensionItem(PayloadCodec.StationIdTag, id), new ExtensionItem(PayloadCodec.ClashTag, clash)], packed);
        Convert.ToHexString(packed.AsSpan(0, n)).Should().Be("074D304C54452D3101230104DEADBEEF02050501020304");

        PayloadCodec.TryUnpackIdentification(packed.AsSpan(0, n + 6), out ReadOnlySpan<byte> callsign, out int capability, out ExtensionReader items).Should().BeTrue();
        System.Text.Encoding.ASCII.GetString(callsign).Should().Be("M0LTE-1");
        capability.Should().Be(0x0123);
        items.TryNext(out byte tag, out ReadOnlySpan<byte> value).Should().BeTrue();
        (tag, PayloadCodec.ReadKey(value)).Should().Be((PayloadCodec.StationIdTag, 0xDEADBEEFu));
        items.TryNext(out tag, out value).Should().BeTrue();
        (tag, value[0], PayloadCodec.ReadKey(value[1..])).Should().Be((PayloadCodec.ClashTag, (byte)5, 0x01020304u));
        items.TryNext(out _, out _).Should().BeFalse("the padding ends the items");
    }

    [Fact]
    public void An_Identification_Burst_Carries_The_Stations_Id_After_The_Capability_Word_And_Others_Read_It_Back()
    {
        var rig = new SimRig(2, ids: [0xDEADBEEF, 0x0BADF00D], numbers: [5, 9], locates: (_, _) => false);
        var built = new List<byte[]>();
        rig.Stations[0].IdentificationPayloadBuilt = built.Add;
        rig.Stations[0].IdentificationSuspended = false;
        rig.RunTo(6);
        rig.Send(1, LinkAddress.Everyone, 1).Should().Be(SendResult.Accepted);
        rig.Run(20);

        built.Should().ContainSingle("one transmission, one identification");
        // The callsign M0S1 (a length byte and four characters), the capability word 0, then item 1, four bytes: the id.
        Convert.ToHexString(built[0]).Should().Be("044D305331" + "0000" + "0104DEADBEEF");
        rig.Stations[1].IdHeardOn(5).Should().Be(0xDEADBEEFu, "read back by the station that heard it");
        rig.Stations[1].IdHeardOn(9).Should().BeNull();
        rig.Nodes[1].Identified.Should().ContainSingle().Which.Event.Callsign.Should().Be("M0S1");
    }

    [Fact]
    public void Every_Identification_Burst_A_Station_Sends_Carries_Its_Id_And_It_Does_Not_Change_While_The_Number_Does_Not()
    {
        var rig = new SimRig(2, ids: [0xDEADBEEF, 0x0BADF00D], numbers: [5, 9], locates: (_, _) => false);
        var built = new List<byte[]>();
        rig.Stations[0].IdentificationPayloadBuilt = built.Add;
        rig.Stations[0].IdentificationSuspended = false;
        rig.Stations[0].IdentificationInterval = 12L * 48000;
        rig.RunTo(6);
        for (int i = 0; i < 6; i++)
        {
            rig.Send(1, LinkAddress.Everyone, i).Should().Be(SendResult.Accepted);
            rig.Run(14);
        }

        built.Count.Should().BeGreaterThanOrEqualTo(4);
        built.Should().OnlyContain(p => Convert.ToHexString(p) == "044D305331" + "0000" + "0104DEADBEEF");
        rig.Nodes[1].Identified.Count.Should().Be(built.Count);
        rig.Stations[1].IdHeardOn(5).Should().Be(0xDEADBEEFu);
    }

    [Fact]
    public void A_Stations_Number_Is_Not_Taken_Until_The_Identity_Is_And_Then_Bursts_To_It_Are_For_It()
    {
        // Station 3 has no identity and so no number: a burst "to number 3" is not for it, and a burst to everyone is delivered as ever. It still measures what it hears and learns where addresses live.
        var rig = new SimRig(3, identified: i => i != 3);
        rig.RunTo(6);
        rig.Send(1, 3, 1).Should().Be(SendResult.Accepted); // station 1 has it located at number 3
        rig.Run(10);
        rig.Nodes[2].Received.Should().BeEmpty("a burst to number 3 is not for a station that has no number");
        rig.Send(1, LinkAddress.Everyone, 2).Should().Be(SendResult.Accepted);
        rig.Run(10);
        rig.Stations[2].StationNumber.Should().Be(0);
        rig.Nodes[2].Received.Should().ContainSingle().Which.Everyone.Should().BeTrue();
        rig.Stations[2].Adaptation.TryGetNeighbour(1, out LinkNeighbour? heard).Should().BeTrue();
        heard!.Measures.Should().BeGreaterThan(0);
        rig.Stations[2].LearnedAddresses.TryGet(SimRig.Address(1).Key, out AddressTable.Entry entry).Should().BeTrue();
        entry.Number.Should().Be(1);
        rig.Stations[2].Send(TestBurst.Datagram(1, 10), SimRig.Address(1), SimRig.Address(3), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.NoIdentity);

        rig.Stations[2].SetIdentity("M0S3").Should().Be(IdentityResult.Accepted);
        rig.Stations[2].StationNumber.Should().NotBe(1, "1 was heard as a sender");
    }

    // ---- a burst built before the move ----

    /// <summary>A station on its own, with a stand-in audio layer that answers nothing until told to: for what happens to a burst the station has built and the audio layer has not keyed.</summary>
    private sealed class Lone
    {
        public readonly Station Station;
        public readonly FakeAudioLayer Layer;
        public readonly List<DroppedEvent> Dropped = [];

        public Lone(int number, uint id, int seed = 1, bool suspendIdentification = true)
        {
            Station = new Station(number, id, new Random(seed), runJobsInline: true) { IdentificationSuspended = suspendIdentification };
            Station.WithIdentity("M0LTE");
            Layer = new FakeAudioLayer(Station);
            Station.Dropped += Dropped.Add;
        }

        public void Run(double seconds) => Layer.Run(Station.SampleCount + (long)(seconds * 48000));

        /// <summary>Runs until the station has made <paramref name="count"/> requests (it never keys them).</summary>
        public void RunUntilRequests(int count)
        {
            for (int i = 0; i < 2000 && Layer.Requests.Count < count; i++)
            {
                Run(0.1);
            }

            Layer.Requests.Should().HaveCount(count);
        }

        /// <summary>Hears this burst's audio and then a little silence.</summary>
        public void Hear(TestBurst burst, double afterSeconds = 0.5)
        {
            Station.Push(burst.Audio);
            Run(afterSeconds);
        }

        /// <summary>Plays request <paramref name="index"/>: keyed now, ended after its samples.</summary>
        public void Play(int index)
        {
            TransmitRequestEvent r = Layer.Requests[index];
            Station.TransmitStarted(r.Id, Station.SampleCount);
            Station.Push(new float[r.Samples.Length]);
            Station.TransmitEnded(r.Id, Station.SampleCount);
        }
    }

    /// <summary>What a listening station makes of the audio of one request: the sender's number in the header, and whatever datagrams it delivers.</summary>
    private static (int Sender, List<byte[]> Datagrams, List<string> Callsigns) Listen(ReadOnlyMemory<float> samples, int listenerNumber = 40)
    {
        var listener = new Station(listenerNumber, new Random(9), runJobsInline: true).WithIdentity("M0LSN");
        var datagrams = new List<byte[]>();
        var callsigns = new List<string>();
        int sender = 0;
        listener.Received += e =>
        {
            sender = listener.HearingSender;
            datagrams.Add(e.Datagram.ToArray());
        };
        listener.Identified += e =>
        {
            sender = listener.HearingSender;
            callsigns.Add(e.Callsign);
        };
        listener.SetExternalBusy(true); // a listener: it never keys
        listener.Push(samples.Span);
        listener.Push(new float[2 * 48000]);
        return (sender, datagrams, callsigns);
    }

    [Fact]
    public void A_Data_Burst_Built_Before_The_Move_Is_Taken_Back_And_Built_Again_With_The_New_Number_And_Loses_No_Datagram()
    {
        var totals = new List<string>();
        foreach (int seed in Seeds)
        {
            var lone = new Lone(5, 0x11111111u, seed);
            // Three datagrams to everyone: two ordinary and an expedited one that goes first.
            lone.Station.Send(TestBurst.Datagram(1, 8), LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
            lone.Station.Send(TestBurst.Datagram(2, 9), LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.Accepted);
            lone.Station.Send(TestBurst.Datagram(3, 10), LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(3), priority: SendPriority.Expedited).Should().Be(SendResult.Accepted);
            lone.RunUntilRequests(1);
            lone.Station.BurstBuilt.Should().BeTrue();
            lone.Station.QueuedTotal.Should().Be(0, "all three are in the burst that was built");
            float[] old = lone.Layer.Requests[0].Samples.ToArray(); // a copy: the station reuses its audio buffers
            long oldId = lone.Layer.Requests[0].Id;

            lone.Station.HearHeaderFrom(5);
            int number = lone.Station.StationNumber;
            number.Should().NotBe(5);
            lone.Layer.Withdrawals.Should().Equal([oldId], "the request that had not been keyed was taken back");
            lone.Layer.Requests.Should().HaveCount(2, "and made again at once: the channel was clear");
            lone.Station.BurstBuilt.Should().BeTrue();

            // The old audio says 5 in its header, the new says the new number, and both carry the same three datagrams in the same order.
            (int oldSender, List<byte[]> oldDatagrams, _) = Listen(old);
            (int newSender, List<byte[]> newDatagrams, _) = Listen(lone.Layer.Requests[1].Samples);
            oldSender.Should().Be(5);
            newSender.Should().Be(number);
            newDatagrams.Should().HaveCount(3);
            newDatagrams.Select(d => d.Length).Should().Equal([10, 8, 9], "expedited first, then the others in order");
            newDatagrams.Should().Equal(oldDatagrams, (a, b) => a.SequenceEqual(b));

            // Played, each goes out exactly once and nothing is dropped.
            lone.Play(1);
            lone.Dropped.Should().BeEmpty();
            lone.Station.QueuedTotal.Should().Be(0);
            lone.Station.BurstBuilt.Should().BeFalse();
            totals.Add($"seed {seed}: 5 -> {number}");
        }

        Hostile.Say("a data burst built before the move: " + string.Join("; ", totals));
    }

    [Fact]
    public void An_Identification_Burst_Built_Before_The_Move_Is_Built_Again_With_The_New_Number_And_The_New_Id()
    {
        foreach (int seed in Seeds)
        {
            var lone = new Lone(5, 0x11111111u, seed, suspendIdentification: false);
            var built = new List<byte[]>();
            lone.Station.IdentificationPayloadBuilt = built.Add;
            lone.Station.Send(TestBurst.Datagram(1, 30), LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
            lone.RunUntilRequests(1); // the identification: it goes first, ahead of the datagram that made it due
            built.Should().HaveCount(1);
            Convert.ToHexString(built[0]).Should().EndWith("0104" + "11111111", "the id is 11111111");

            float[] oldIdent = lone.Layer.Requests[0].Samples.ToArray();
            lone.Station.HearHeaderFrom(5);
            int number = lone.Station.StationNumber;
            uint id = lone.Station.StationId;
            number.Should().NotBe(5);
            lone.Layer.Requests.Should().HaveCount(2, "the identification was taken back and made again");
            built.Should().HaveCount(2);
            built[1].Skip(built[1].Length - 4).Should().Equal(BitConverter.GetBytes(id).Reverse(), "built again with the new id, most significant byte first");
            (int sender, _, List<string> callsigns) = Listen(lone.Layer.Requests[1].Samples);
            sender.Should().Be(number, $"seed {seed}");
            callsigns.Should().Equal("M0LTE");
            (int oldSender, _, _) = Listen(oldIdent);
            oldSender.Should().Be(5);
        }
    }

    [Fact]
    public void An_Announcement_Built_Before_The_Move_Is_Built_Again_With_The_New_Number_And_Is_Still_Owed()
    {
        foreach (int seed in Seeds)
        {
            var lone = new Lone(5, 0x11111111u, seed);
            lone.Station.Send(TestBurst.Datagram(1, 30), SimRig.Address(7), SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
            lone.RunUntilRequests(1);
            lone.Play(0); // the host has sent from address S1, so the station answers for it
            // Another station (number 20) asks for S1 with a burst to everyone.
            lone.Run(1); // past the mute tail of its own burst
            lone.Hear(TestBurst.Data(20, 0, StandardFormats.FirstContact, [TestBurst.Datagram(9, 20)], forKey: SimRig.Address(1).Key, from: [LinkAddress.FromAscii("OTHER").Key]));
            lone.Station.AnnouncementsOwed.Should().Be(1);
            lone.RunUntilRequests(2); // the announcement goes alone when the 1.5 s wait runs out
            lone.Station.AnnouncementsOwed.Should().Be(0, "it is built");

            lone.Station.HearHeaderFrom(5);
            int number = lone.Station.StationNumber;
            number.Should().NotBe(5);
            lone.Layer.Requests.Should().HaveCount(3, $"seed {seed}: the announcement was taken back and built again");
            lone.Layer.Withdrawals.Should().Contain(lone.Layer.Requests[1].Id);
            lone.Play(2);
            lone.Station.AnnouncementsOwed.Should().Be(0);
        }
    }

    // ---- hearing a Clash ----

    private static ExtensionItem[] Items(uint id, (int Number, uint Id)? clash = null)
    {
        var idValue = new byte[PayloadCodec.KeyBytes];
        PayloadCodec.WriteKey(id, idValue);
        if (clash is null)
        {
            return [new ExtensionItem(PayloadCodec.StationIdTag, idValue)];
        }

        var value = new byte[PayloadCodec.ClashBytes];
        PayloadCodec.WriteClash(clash.Value.Number, clash.Value.Id, value);
        return [new ExtensionItem(PayloadCodec.StationIdTag, idValue), new ExtensionItem(PayloadCodec.ClashTag, value)];
    }

    private static TestBurst Ident(int sender, uint id, (int Number, uint Id)? clash = null, int burstNumber = 0) => TestBurst.Identification(sender, "M0OTH", 0, burstNumber, Items(id, clash));

    [Fact]
    public void A_Clash_That_Names_The_Stations_Number_And_Id_Makes_It_Move_At_Once_One_That_Names_Another_Id_Or_Number_Does_Not()
    {
        foreach (int seed in Seeds)
        {
            var lone = new Lone(5, 0x11111111u, seed);
            lone.Run(6);
            lone.Hear(Ident(9, 0xAAAAAAAA, (5, 0x22222222)));
            lone.Station.NumberChanges.Should().Be(0, "it names the number but another id: for the other station");
            lone.Hear(Ident(9, 0xAAAAAAAA, (6, 0x11111111)), 1);
            lone.Station.NumberChanges.Should().Be(0, "it names this station's id but another number");
            lone.Station.StationNumber.Should().Be(5);

            lone.Hear(Ident(9, 0xAAAAAAAA, (5, 0x11111111)), 1);
            lone.Station.NumberChanges.Should().Be(1, $"seed {seed}");
            lone.Station.StationNumber.Should().NotBe(5).And.NotBe(9, "9 was just heard as the sender");
            lone.Station.StationId.Should().NotBe(0x11111111u);
        }
    }

    /// <summary>A station (number 20) that has heard number 5 with one id and then another, within ten minutes, and so owes a Clash for the later id. With <paramref name="held"/> the channel is held busy, so the Clash is owed and not built.</summary>
    private static Lone OwingAClash(int seed, bool held)
    {
        var lone = new Lone(20, 0x22222222u, seed);
        lone.Run(6);
        lone.Hear(Ident(5, 0x1111AAAA), 1);
        if (held)
        {
            lone.Station.SetExternalBusy(true);
        }

        lone.Hear(Ident(5, 0x2222BBBB), 1);
        lone.Station.ClashesOwedCount.Should().Be(1);
        return lone;
    }

    [Fact]
    public void A_Station_About_To_Send_A_Clash_Does_Not_When_It_Hears_One_For_The_Same_Number_From_Anyone()
    {
        foreach (int seed in Seeds)
        {
            // Without the other Clash it goes out, once.
            Lone alone = OwingAClash(seed, held: false);
            alone.Run(25);
            alone.Layer.Requests.Should().HaveCount(1, $"seed {seed}: the Clash was built");
            alone.Play(0);
            alone.Station.ClashesSent.Should().Be(1);
            alone.Station.ClashesPending.Should().Be(0);

            // Owed and not built (the channel is held), then another station's Clash for the same number: not owed any more.
            Lone owed = OwingAClash(seed, held: true);
            owed.Station.ClashesPending.Should().Be(1);
            owed.Hear(Ident(9, 0x9999CCCC, (5, 0x2222BBBB)), 1);
            owed.Station.ClashesPending.Should().Be(0, "somebody else said it");
            owed.Station.SetExternalBusy(false);
            owed.Run(25);
            owed.Layer.Requests.Should().BeEmpty();
            owed.Station.ClashesSent.Should().Be(0);

            // Built and handed to the audio layer, not keyed: taken back.
            Lone built = OwingAClash(seed, held: false);
            built.Run(25);
            built.Layer.Requests.Should().HaveCount(1);
            built.Hear(Ident(9, 0x9999CCCC, (5, 0x2222BBBB)), 1);
            built.Station.ClashesPending.Should().Be(0);
            built.Layer.Withdrawals.Should().Equal([built.Layer.Requests[0].Id]);
            built.Run(25);
            built.Layer.Requests.Should().HaveCount(1, "no new request");
            built.Station.ClashesSent.Should().Be(0);

            // A Clash for another number does not stop it, and nor does one for the same number that names the earlier id.
            Lone other = OwingAClash(seed, held: false);
            other.Hear(Ident(9, 0x9999CCCC, (6, 0x2222BBBB)), 1);
            other.Station.ClashesPending.Should().Be(1);
        }
    }

    [Fact]
    public void The_Clash_Burst_Names_The_Number_And_The_Id_Heard_More_Recently_Goes_After_The_Identification_And_Does_Not_Move_Its_Clock()
    {
        var lone = new Lone(20, 0x22222222u, 1, suspendIdentification: false);
        var built = new List<byte[]>();
        lone.Station.IdentificationPayloadBuilt = built.Add;
        var sent = new List<ModemSentEvent>();
        lone.Station.ModemSent += sent.Add;
        var due = new List<IdentificationDueEvent>();
        lone.Station.IdentificationDue += due.Add;
        lone.Run(6);
        lone.Hear(Ident(5, 0x1111AAAA), 1);
        lone.Hear(Ident(5, 0x2222BBBB), 1);
        lone.Run(25);
        lone.Layer.Requests.Should().HaveCount(1, "the identification: the station has never identified, and a notice is a transmission of its own accord, so the identification goes first");
        lone.Play(0);
        long identStart = lone.Station.LastIdentificationStart!.Value;
        lone.RunUntilRequests(2); // the notice goes straight on after it
        lone.Play(1);
        lone.Run(1);

        built.Should().HaveCount(2);
        Convert.ToHexString(built[0]).Should().Be("054D304C5445" + "0000" + "010422222222");
        // The callsign M0LTE, the capability word, this station's own id, then the Clash: number 5 and the later id.
        Convert.ToHexString(built[1]).Should().Be("054D304C5445" + "0000" + "010422222222" + "020505" + "2222BBBB");
        sent.Select(e => e.Kind).Should().Equal(ModemSentKind.Identification, ModemSentKind.Clash);
        sent[1].Format.Should().Be(StandardFormats.FirstContact);
        due.Should().ContainSingle("the identification, once, before the notice; the notice is not an identification and raises nothing");
        lone.Station.IdentificationsDue.Should().Be(1);
        lone.Station.IdentificationsSent.Should().Be(1);
        lone.Station.LastIdentificationStart.Should().Be(identStart, "the notice did not move the clock");
        lone.Station.IdentificationOwed.Should().BeFalse();
        lone.Station.IsTransmitting.Should().BeFalse();

        // A datagram afterwards, inside the ten minutes, is covered by that identification: nothing more is owed.
        lone.Station.Send(TestBurst.Datagram(1, 20), LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        lone.RunUntilRequests(3);
        lone.Play(2);
        due.Should().ContainSingle();
        lone.Station.IdentificationsSent.Should().Be(1);
        built.Should().HaveCount(2);
    }

    [Fact]
    public void A_Different_Id_On_A_Known_Number_Forgets_What_Was_Known_Of_It_And_Owes_A_Clash_Only_If_The_Earlier_Id_Was_Heard_Inside_Ten_Minutes()
    {
        TestBurst dataFrom5 = TestBurst.Data(5, 0, StandardFormats.FirstContact, [TestBurst.Datagram(1, 20)], from: [LinkAddress.FromAscii("ADDR5").Key]);
        uint key = LinkAddress.FromAscii("ADDR5").Key;

        // The earlier id heard 610 s before the later one, with data from the number heard in between (so that the number was never forgotten for silence): reset, no Clash.
        var late = new Lone(20, 0x22222222u, 1);
        late.Run(6);
        late.Hear(Ident(5, 0x1111AAAA), 1);
        late.Run(400);
        late.Hear(dataFrom5, 1);
        late.Station.LearnedAddresses.TryGet(key, out _).Should().BeTrue();
        late.Station.Adaptation.TryGetNeighbour(5, out LinkNeighbour? before).Should().BeTrue();
        late.Run(610 - 400 - 10);
        late.Hear(Ident(5, 0x2222BBBB), 1);
        late.Station.LearnedAddresses.TryGet(key, out _).Should().BeFalse("the number changed hands: the addresses it had are forgotten");
        late.Station.Adaptation.TryGetNeighbour(5, out LinkNeighbour? after).Should().BeTrue("the identification burst itself was measured afresh");
        after.Should().NotBeSameAs(before);
        after!.Measures.Should().Be(1);
        late.Station.ClashesOwedCount.Should().Be(0, "the earlier id was heard more than ten minutes before");
        late.Station.IdHeardOn(5).Should().Be(0x2222BBBBu);

        // The same, 590 s apart: the same reset, and a Clash is owed.
        var soon = new Lone(20, 0x22222222u, 1);
        soon.Run(6);
        soon.Hear(Ident(5, 0x1111AAAA), 1);
        soon.Run(400);
        soon.Hear(dataFrom5, 1);
        soon.Run(590 - 400 - 10);
        soon.Hear(Ident(5, 0x2222BBBB), 1);
        soon.Station.LearnedAddresses.TryGet(key, out _).Should().BeFalse();
        soon.Station.ClashesOwedCount.Should().Be(1);
    }

    [Fact]
    public void The_Same_Id_Again_Changes_Nothing_And_An_Identification_With_No_Id_Is_Not_A_Change()
    {
        var lone = new Lone(20, 0x22222222u, 1);
        lone.Run(6);
        lone.Hear(Ident(5, 0x1111AAAA), 1);
        lone.Hear(Ident(5, 0x1111AAAA, burstNumber: 1), 1);
        lone.Hear(TestBurst.Identification(5, "M0OTH", 0, 2), 1);
        lone.Hear(TestBurst.Identification(5, "M0OTH", 0, 3, [new ExtensionItem(PayloadCodec.StationIdTag, new byte[3])]), 1);
        lone.Station.ClashesOwedCount.Should().Be(0);
        lone.Station.IdHeardOn(5).Should().Be(0x1111AAAAu);
    }

    [Fact]
    public void The_Id_Record_For_A_Number_Is_Cleared_When_A_Clash_Is_Sent_Or_Heard_So_The_Next_Identification_Is_Only_Recorded()
    {
        // Sent: after the station's own Clash for number 5, the next id heard there is recorded and owes nothing.
        foreach (int seed in Seeds)
        {
            Lone sent = OwingAClash(seed, held: false);
            sent.Station.IdHeardOn(5).Should().Be(0x2222BBBBu);
            sent.Run(25);
            sent.Play(0);
            sent.Station.IdHeardOn(5).Should().BeNull();
            sent.Run(1);
            sent.Hear(Ident(5, 0x1111AAAA, burstNumber: 2), 1);
            sent.Station.IdHeardOn(5).Should().Be(0x1111AAAAu);
            sent.Station.ClashesOwedCount.Should().Be(1, $"seed {seed}: the survivor's identification owes nothing");

            // Heard: somebody else's Clash clears it too.
            var heard = new Lone(20, 0x22222222u, seed);
            heard.Run(6);
            heard.Hear(Ident(5, 0x1111AAAA), 1);
            heard.Hear(Ident(9, 0x9999CCCC, (5, 0x2222BBBB)), 1);
            heard.Station.IdHeardOn(5).Should().BeNull();
            heard.Hear(Ident(5, 0x1111AAAA, burstNumber: 1), 1);
            heard.Station.ClashesOwedCount.Should().Be(0);
        }
    }

    [Fact]
    public void A_Clash_Waits_Up_To_Ten_Seconds_Before_It_Asks_For_The_Channel()
    {
        var delays = new List<double>();
        foreach (int seed in Seeds)
        {
            Lone lone = OwingAClash(seed, held: false);
            long owed = lone.Station.SampleCount;
            for (int i = 0; i < 400 && lone.Layer.Requests.Count == 0; i++)
            {
                lone.Run(0.1);
            }

            lone.Layer.Requests.Should().HaveCount(1);
            double waited = (lone.Station.SampleCount - owed) / 48000.0;
            delays.Add(waited);
            waited.Should().BeLessThan(10 + 0.8 + 0.3 + 3, "at most the drawn 10 s, the clear time, the slots and the owing burst's own end");
        }

        Hostile.Say("a Clash waited (s, from just after the identification that owed it to the request) with seeds 1 to 8: " + string.Join(", ", delays.Select(d => d.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))));
        delays.Max().Should().BeGreaterThan(delays.Min() + 1, "the delay is drawn, not fixed");
    }

    [Fact]
    public void Datagrams_Queued_For_An_Address_At_A_Number_Whose_Id_Changes_Stay_Queued_And_Go_To_Everyone()
    {
        foreach (int seed in Seeds)
        {
            var lone = new Lone(20, 0x22222222u, seed);
            var address = LinkAddress.FromAscii("G9XYZ");
            var sent = new List<TransmittedEvent>();
            var states = new List<LinkStateEvent>();
            lone.Station.Transmitted += sent.Add;
            lone.Station.LinkState += states.Add;
            lone.Run(6);
            lone.Hear(Ident(5, 0x1111AAAA), 1);
            lone.Station.Locate(address, 5);
            lone.Station.SetExternalBusy(true);
            lone.Station.Send(TestBurst.Datagram(1, 20), address, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
            lone.Station.LocatedNow(address).Should().Be(5);

            lone.Hear(Ident(5, 0x2222BBBB), 1);
            lone.Station.LocatedNow(address).Should().Be(0, "the address is no longer located");
            lone.Station.QueuedFor(address).Should().Be(1, "and what waited for it stays queued");
            lone.Dropped.Should().BeEmpty();
            states.Should().Contain(l => l.Address == address && !l.Located && l.Gone, "the host is told it has gone, as after three silences");

            lone.Station.SetExternalBusy(false);
            lone.Run(25);
            lone.Layer.Requests.Should().NotBeEmpty();
            float[] audio = lone.Layer.Requests[0].Samples.ToArray();
            lone.Play(0);
            sent.Should().ContainSingle().Which.Address.Should().Be(address);
            (_, List<byte[]> datagrams, _) = Listen(audio);
            datagrams.Should().ContainSingle().Which.Should().Equal(TestBurst.Datagram(1, 20), $"seed {seed}: it went as a burst to everyone");
        }
    }
}
