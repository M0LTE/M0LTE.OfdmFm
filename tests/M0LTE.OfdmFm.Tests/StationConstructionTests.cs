using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

public class StationConstructionTests
{
    [Fact]
    public void A_station_has_no_identity_and_no_number_until_given_an_identity()
    {
        var station = new Station(new Random(1));
        station.Callsign.Should().BeNull();
        station.StationNumber.Should().Be(0);
        station.StationId.Should().Be(0u);
        station.SetIdentity("M0LTE").Should().Be(IdentityResult.Accepted);
        station.StationNumber.Should().BeInRange(1, 63);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(-1)]
    [InlineData(1000)]
    public void A_forced_station_number_out_of_range_is_refused(int number)
    {
        ((Action)(() => new Station(number, new Random(1)))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_forced_number_is_taken_with_the_identity_and_not_before()
    {
        var station = new Station(63, 0xDEADBEEFu, new Random(1));
        station.StationNumber.Should().Be(0);
        station.WithIdentity("M0LTE");
        station.StationNumber.Should().Be(63);
        station.StationId.Should().Be(0xDEADBEEFu);
        new Station(1, new Random(1)).WithIdentity("M0LTE").StationNumber.Should().Be(1);
    }

    [Fact]
    public void A_missing_random_source_is_refused()
    {
        ((Action)(() => new Station(null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Link_addresses_compare_by_their_bytes_and_are_copied()
    {
        byte[] bytes = [1, 2, 3];
        var a = new LinkAddress(bytes);
        bytes[0] = 9;
        a.Should().Be(new LinkAddress(new byte[] { 1, 2, 3 }));
        a.GetHashCode().Should().Be(new LinkAddress(new byte[] { 1, 2, 3 }).GetHashCode());
        (a == new LinkAddress(new byte[] { 1, 2, 4 })).Should().BeFalse();
        LinkAddress.FromAscii("M0LTE-1").Should().Be(LinkAddress.FromAscii("M0LTE-1"));
        default(LinkAddress).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Event_values_compare_by_their_fields()
    {
        new DroppedEvent(new HostTag(5), DropReason.Cancelled).Should().Be(new DroppedEvent(new HostTag(5), DropReason.Cancelled));
        new PathReportEvent(LinkAddress.FromAscii("A"), 12, false).Should().NotBe(new PathReportEvent(LinkAddress.FromAscii("A"), 12, true));
    }
}
