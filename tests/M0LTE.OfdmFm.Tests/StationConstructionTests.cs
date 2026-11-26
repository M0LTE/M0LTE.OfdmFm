using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

public class StationConstructionTests
{
    [Fact]
    public void A_station_keeps_its_settings()
    {
        var station = new Station("M0LTE", 63, new Random(1));
        station.Callsign.Should().Be("M0LTE");
        station.StationNumber.Should().Be(63);
        new Station("G4XYZ-1", 1, new Random(1)).StationNumber.Should().Be(1);
    }

    [Fact]
    public void A_missing_callsign_is_refused()
    {
        ((Action)(() => new Station(null!, 1, new Random(1)))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new Station("", 1, new Random(1)))).Should().Throw<ArgumentException>();
        ((Action)(() => new Station("  ", 1, new Random(1)))).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(-1)]
    [InlineData(1000)]
    public void A_station_number_out_of_range_is_refused(int number)
    {
        ((Action)(() => new Station("M0LTE", number, new Random(1)))).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_missing_random_source_is_refused()
    {
        ((Action)(() => new Station("M0LTE", 1, null!))).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Station_names_compare_by_their_bytes_and_are_copied()
    {
        byte[] bytes = [1, 2, 3];
        var a = new StationName(bytes);
        bytes[0] = 9;
        a.Should().Be(new StationName(new byte[] { 1, 2, 3 }));
        a.GetHashCode().Should().Be(new StationName(new byte[] { 1, 2, 3 }).GetHashCode());
        (a == new StationName(new byte[] { 1, 2, 4 })).Should().BeFalse();
        StationName.FromAscii("M0LTE-1").Should().Be(StationName.FromAscii("M0LTE-1"));
        default(StationName).Should().Be(new StationName(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Event_values_compare_by_their_fields()
    {
        new DroppedEvent(new HostTag(5), DropReason.Cancelled).Should().Be(new DroppedEvent(new HostTag(5), DropReason.Cancelled));
        new PathReportEvent(StationName.FromAscii("A"), 12, false).Should().NotBe(new PathReportEvent(StationName.FromAscii("A"), 12, true));
    }
}
