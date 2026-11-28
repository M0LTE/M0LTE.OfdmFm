using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>
/// Identify first, the order of things on the air: a lone report never waits behind an identification, however the identification came to be waiting, and the "straight on" that follows an identification belongs to the transmission it was
/// set for. One station that has never identified, a stand-in audio layer that plays what it is told to, and bursts heard by hand.
/// </summary>
public class StationIdentifyFirstOrderTests
{
    private sealed class Cold
    {
        public readonly Station Station;
        public readonly FakeAudioLayer Layer;
        public readonly List<ModemSentKind> Sent = [];
        public readonly List<string> Trace = [];

        public Cold(int seed = 1)
        {
            Station = new Station(1, 0xC0DE0001u, new Random(seed), runJobsInline: true).WithIdentity("M0LTE");
            Station.TraceLevel = TraceLevel.Detail;
            Station.Trace += e => Trace.Add(e.ToString());
            Layer = new FakeAudioLayer(Station);
            Station.ModemSent += e => Sent.Add(e.Kind);
        }

        public void Run(double seconds) => Layer.Run(Station.SampleCount + (long)(seconds * 48000));

        public void RunUntilRequests(int count)
        {
            for (int i = 0; i < 4000 && Layer.Requests.Count < count; i++)
            {
                Run(0.01);
            }

            Layer.Requests.Should().HaveCount(count);
        }

        public void Hear(TestBurst burst, double afterSeconds = 0.3)
        {
            Station.Push(burst.Audio);
            Run(afterSeconds);
        }

        /// <summary>An asked-for report is owed to <paramref name="sender"/>: a data burst for this station that says "report wanted".</summary>
        public static TestBurst Asking(int sender) => TestBurst.Data(sender, 1, StandardFormats.FirstContact, [TestBurst.Datagram(sender, 20)], new PayloadPrelude(true, null, false, null));

        /// <summary>Plays request <paramref name="index"/>: keyed now, ended after its samples. Returns the sample it ended at.</summary>
        public long Play(int index)
        {
            TransmitRequestEvent r = Layer.Requests[index];
            Station.TransmitStarted(r.Id, Station.SampleCount);
            Station.Push(new float[r.Samples.Length]);
            Station.TransmitEnded(r.Id, Station.SampleCount);
            return Station.SampleCount;
        }
    }

    [Fact]
    public void Two_Lone_Reports_Owed_Go_One_After_The_Other_And_The_Identification_Follows_Both()
    {
        var x = new Cold();
        x.Run(6);
        x.Station.NoteBurst(x.Station.SampleCount + (20 * 48000L)); // the channel is busy while both bursts are heard, so both reports are ready together
        x.Hear(Cold.Asking(2));
        x.Hear(Cold.Asking(3));
        x.Run(5);
        x.Layer.Requests.Should().BeEmpty();
        x.RunUntilRequests(1);
        x.Play(0);
        x.RunUntilRequests(2);
        x.Play(1);
        x.RunUntilRequests(3);
        x.Play(2);

        x.Sent.Should().Equal(new[] { ModemSentKind.Report, ModemSentKind.Report, ModemSentKind.Identification }, "the second report does not wait behind the identification the first one made due");
    }

    [Fact]
    public void An_Identification_Put_Back_After_It_Was_Handed_Over_Does_Not_Hold_A_Lone_Report_Behind_It()
    {
        var x = new Cold();
        x.Run(5.5);
        x.Station.Send(TestBurst.Datagram(1, 20), LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        x.RunUntilRequests(1); // the identification, handed over and not played
        int dueBefore = (int)x.Station.IdentificationsDue;
        x.Station.NoteBurst(x.Station.SampleCount + (20 * 48000L)); // another station's burst: it is taken back
        x.Layer.Withdrawals.Should().HaveCount(1);
        x.Hear(Cold.Asking(2)); // a report becomes owed while the identification waits
        x.Run(5);
        x.RunUntilRequests(2);
        x.Play(1);
        x.RunUntilRequests(3);
        x.Play(2);

        x.Sent.Take(2).Should().Equal(new[] { ModemSentKind.Report, ModemSentKind.Identification }, "the report goes before the identification that was put back");
        x.Station.IdentificationsDue.Should().Be(dueBefore, "and it was not raised again");
    }

    [Fact]
    public void The_Straight_On_Set_For_An_Announcement_Is_Not_Left_For_Data_That_Waits_Behind_A_Later_Identification()
    {
        var x = new Cold();
        x.Station.IdentificationInterval = 20 * 48000L;
        LinkAddress other = LinkAddress.FromAscii("ADDR20");
        x.Station.Locate(other, 20);
        x.Run(5.5);
        x.Station.Send(TestBurst.Datagram(1, 20), SimRig.Address(7), SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        x.RunUntilRequests(1);
        x.Play(0); // the identification
        x.RunUntilRequests(2);
        x.Play(1); // the datagram, to everyone

        // Station 20 asks for the address the host sent from: an announcement goes alone, and the host has data for 20 by the time it ends.
        x.Run(0.5);
        x.Hear(TestBurst.Data(20, 0, StandardFormats.FirstContact, [TestBurst.Datagram(9, 20)], forKey: SimRig.Address(1).Key, from: [LinkAddress.FromAscii("OTHER").Key]));
        x.RunUntilRequests(3);
        TransmitRequestEvent announcement = x.Layer.Requests[2];
        x.Station.TransmitStarted(announcement.Id, x.Station.SampleCount);
        x.Station.Send(TestBurst.Datagram(2, 20), other, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(2)).Should().Be(SendResult.Accepted);
        x.Station.Push(new float[announcement.Samples.Length]);
        x.Station.TransmitEnded(announcement.Id, x.Station.SampleCount);
        x.RunUntilRequests(4);
        x.Play(3); // the datagram goes straight on after the announcement
        x.Sent.Should().EndWith(ModemSentKind.Announcement);

        // Past the interval: a lone report is owed to station 30 (no data for it), data for 20 is queued while the report is on the air, and an identification is due when the report ends.
        int traced = x.Trace.Count;
        x.Run(30);
        x.Hear(Cold.Asking(30));
        x.RunUntilRequests(5);
        x.Station.TransmitStarted(x.Layer.Requests[4].Id, x.Station.SampleCount);
        x.Station.Send(TestBurst.Datagram(3, 20), other, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(3)).Should().Be(SendResult.Accepted);
        x.Station.Push(new float[x.Layer.Requests[4].Samples.Length]);
        x.Station.TransmitEnded(x.Layer.Requests[4].Id, x.Station.SampleCount);
        x.Sent.Should().EndWith(ModemSentKind.Report);
        x.RunUntilRequests(6);
        x.Play(5).Should().BeGreaterThan(0);
        x.Sent.Should().EndWith(ModemSentKind.Identification);
        long identEnd = x.Station.SampleCount;
        x.RunUntilRequests(7);
        (x.Station.SampleCount - identEnd).Should().BeGreaterThan(38400, "the datagram waits its ordinary time after the identification: the straight-on belonged to the announcement, not to it");
        x.Trace.Skip(traced).Count(l => l.Contains("Going straight on after the identification", StringComparison.Ordinal)).Should().Be(0);
    }

    [Fact]
    public void An_Age_Given_After_An_Identification_Has_Fallen_Due_Is_Refused()
    {
        var x = new Cold();
        x.Run(5.5);
        x.Station.Send(TestBurst.Datagram(1, 20), LinkAddress.Everyone, SimRig.Address(1), LinkProtocol.NoLayer3, new HostTag(1)).Should().Be(SendResult.Accepted);
        x.RunUntilRequests(1); // the identification is due and handed over, not yet on the air
        x.Station.Invoking(s => s.SetLastIdentification(30)).Should().Throw<InvalidOperationException>();
    }
}
