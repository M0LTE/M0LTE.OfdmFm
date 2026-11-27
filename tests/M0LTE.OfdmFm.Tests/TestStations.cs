using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

internal static class TestStations
{
    /// <summary>Gives a new station its identity straight away, as most tests want: they are about a station that transmits.</summary>
    public static Station WithIdentity(this Station station, string callsign)
    {
        station.SetIdentity(callsign).Should().Be(IdentityResult.Accepted);
        return station;
    }
}
