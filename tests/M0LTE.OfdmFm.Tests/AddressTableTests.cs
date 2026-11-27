using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

public class AddressTableTests
{
    [Fact]
    public void An_Address_Lives_Where_The_Latest_Burst_Said()
    {
        var table = new AddressTable();
        table.TryGet(7, out _).Should().BeFalse();
        table.Learn(7, 3, 10.0);
        table.TryGet(7, out AddressTable.Entry e).Should().BeTrue();
        e.Should().Be(new AddressTable.Entry(3, 10.0));
        table.Learn(7, 9, 20.0);
        table.TryGet(7, out e).Should().BeTrue();
        e.Should().Be(new AddressTable.Entry(9, 20.0));
        table.Count.Should().Be(1);
    }

    [Fact]
    public void Forgetting_A_Station_Forgets_Every_Address_At_It_And_No_Other()
    {
        var table = new AddressTable();
        table.Learn(1, 3, 0);
        table.Learn(2, 3, 0);
        table.Learn(3, 4, 0);
        table.ForgetStation(3).Should().Be(2);
        table.TryGet(1, out _).Should().BeFalse();
        table.TryGet(2, out _).Should().BeFalse();
        table.TryGet(3, out _).Should().BeTrue();
        table.ForgetStation(3).Should().Be(0);
        table.Forget(3).Should().BeTrue();
        table.Forget(3).Should().BeFalse();
    }

    [Fact]
    public void Learning_And_Forgetting_An_Address_Already_Known_Allocate_Nothing()
    {
        var table = new AddressTable();
        for (uint k = 0; k < 20; k++)
        {
            table.Learn(k, 2, 0);
        }

        table.ForgetStation(2);
        for (uint k = 0; k < 20; k++)
        {
            table.Learn(k, 2, 0);
        }

        Action work = () =>
        {
            for (int i = 0; i < 100; i++)
            {
                for (uint k = 0; k < 20; k++)
                {
                    table.Learn(k, 1 + (i & 1), i);
                    table.TryGet(k, out _);
                }

                table.ForgetStation(1);
                table.ForgetStation(2);
                for (uint k = 0; k < 20; k++)
                {
                    table.Learn(k, 2, i);
                }
            }
        };
        AllocationMeasure.Minimum(work).Should().Be(0);
    }
}
