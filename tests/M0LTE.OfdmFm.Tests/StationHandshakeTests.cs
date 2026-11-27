using AwesomeAssertions;
using M0LTE.OfdmFm;

namespace M0LTE.OfdmFm.Tests;

/// <summary>The transmit handshake between the station and the audio layer, every path including the races.</summary>
public class StationHandshakeTests
{
    private static (Station Station, FakeAudioLayer Layer) Make()
    {
        var station = new Station(1, new Random(1)).WithIdentity("M0LTE");
        return (station, new FakeAudioLayer(station));
    }

    [Fact]
    public void A_request_is_heard_started_and_ended()
    {
        var (station, layer) = Make();
        layer.Run(1000);
        long id = station.RequestTransmit(new float[240], 1200);

        layer.Requests.Should().ContainSingle().Which.Should().Match<TransmitRequestEvent>(r => r.Id == id && r.EarliestSample == 1200 && r.Samples.Length == 240);
        station.StateOf(id).Should().Be(TransmitState.Requested);
        station.IsTransmitting.Should().BeFalse();

        layer.Play(layer.Requests[0], 1300);
        layer.Run(1300);
        station.StateOf(id).Should().Be(TransmitState.Started);
        station.StartedAt(id).Should().Be(1300);
        station.IsTransmitting.Should().BeTrue();

        layer.Run(2000);
        station.StateOf(id).Should().Be(TransmitState.Ended);
        station.EndedAt(id).Should().Be(1540);
        station.IsTransmitting.Should().BeFalse();
    }

    [Fact]
    public void Ids_are_unique_and_increase()
    {
        var (station, _) = Make();
        long a = station.RequestTransmit(new float[1], 0);
        long b = station.RequestTransmit(new float[1], 0);
        b.Should().BeGreaterThan(a);
    }

    [Fact]
    public void An_audio_layer_that_answers_late_is_tolerated()
    {
        var (station, layer) = Make();
        long id = station.RequestTransmit(new float[100], 0);
        layer.Run(50000);
        station.StateOf(id).Should().Be(TransmitState.Requested);
        layer.Play(layer.Requests[0], 49000);
        layer.Run(60000);
        station.StateOf(id).Should().Be(TransmitState.Ended);
        station.StartedAt(id).Should().Be(49000);
    }

    [Fact]
    public void The_station_can_take_back_a_request_that_has_not_started()
    {
        var (station, layer) = Make();
        long id = station.RequestTransmit(new float[100], 500);
        station.WithdrawTransmit(id);
        layer.Withdrawals.Should().Equal(id);
        station.StateOf(id).Should().Be(TransmitState.Withdrawn);
        station.IsTransmitting.Should().BeFalse();
    }

    [Fact]
    public void A_start_that_crosses_a_withdrawal_is_tolerated_and_the_station_counts_as_transmitting()
    {
        var (station, layer) = Make();
        layer.Run(1000);
        long id = station.RequestTransmit(new float[100], 1000);
        station.WithdrawTransmit(id);

        // The layer had keyed it before it saw the withdrawal.
        station.TransmitStarted(id, 1000);
        station.StateOf(id).Should().Be(TransmitState.Started);
        station.StartedAfterWithdrawal(id).Should().BeTrue();
        station.IsTransmitting.Should().BeTrue();

        layer.Run(1100);
        station.TransmitEnded(id, 1100);
        station.IsTransmitting.Should().BeFalse();
        station.StateOf(id).Should().Be(TransmitState.Ended);
    }

    [Fact]
    public void A_scripted_crossing_through_the_fake_layer()
    {
        var (station, layer) = Make();
        layer.At(500, () => station.WithdrawTransmit(station.RequestTransmit(new float[10], 500)));
        layer.StartAt(1, 500);
        layer.EndAt(1, 510);
        layer.Run(600);
        station.StateOf(1).Should().Be(TransmitState.Ended);
        station.StartedAfterWithdrawal(1).Should().BeTrue();
    }

    [Fact]
    public void A_withdrawn_request_that_never_starts_stays_withdrawn_and_cannot_end()
    {
        var (station, _) = Make();
        long id = station.RequestTransmit(new float[10], 0);
        station.WithdrawTransmit(id);
        Action end = () => station.TransmitEnded(id, 0);
        end.Should().Throw<InvalidOperationException>().WithMessage("*ended before it started*");
    }

    [Fact]
    public void Ended_before_started_is_an_error()
    {
        var (station, _) = Make();
        long id = station.RequestTransmit(new float[10], 0);
        Action end = () => station.TransmitEnded(id, 0);
        end.Should().Throw<InvalidOperationException>().WithMessage("*ended before it started*");
        station.StateOf(id).Should().Be(TransmitState.Requested);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    [InlineData(99L)]
    public void An_id_never_issued_is_an_error(long id)
    {
        var (station, _) = Make();
        ((Action)(() => station.TransmitStarted(id, 0))).Should().Throw<InvalidOperationException>().WithMessage("*never issued*");
        ((Action)(() => station.TransmitEnded(id, 0))).Should().Throw<InvalidOperationException>().WithMessage("*never issued*");
        ((Action)(() => station.WithdrawTransmit(id))).Should().Throw<InvalidOperationException>().WithMessage("*never issued*");
    }

    [Fact]
    public void Starting_or_ending_twice_is_an_error()
    {
        var (station, _) = Make();
        long id = station.RequestTransmit(new float[10], 0);
        station.TransmitStarted(id, 0);
        ((Action)(() => station.TransmitStarted(id, 0))).Should().Throw<InvalidOperationException>().WithMessage("*already started*");
        station.TransmitEnded(id, 0);
        ((Action)(() => station.TransmitEnded(id, 0))).Should().Throw<InvalidOperationException>().WithMessage("*already ended*");
        ((Action)(() => station.TransmitStarted(id, 0))).Should().Throw<InvalidOperationException>().WithMessage("*already started*");
    }

    [Fact]
    public void A_second_start_while_one_is_on_air_is_an_error()
    {
        var (station, _) = Make();
        long a = station.RequestTransmit(new float[10], 0);
        long b = station.RequestTransmit(new float[10], 0);
        station.TransmitStarted(a, 0);
        ((Action)(() => station.TransmitStarted(b, 0))).Should().Throw<InvalidOperationException>().WithMessage("*still transmitting*");
        station.TransmitEnded(a, 0);
        station.TransmitStarted(b, 0);
        station.IsTransmitting.Should().BeTrue();
    }

    [Fact]
    public void Indexes_must_be_in_range()
    {
        var (station, layer) = Make();
        layer.Run(1000);
        long id = station.RequestTransmit(new float[10], 500);
        ((Action)(() => station.TransmitStarted(id, 499))).Should().Throw<InvalidOperationException>().WithMessage("*before its earliest*");
        ((Action)(() => station.TransmitStarted(id, 1001))).Should().Throw<InvalidOperationException>().WithMessage("*future*");
        station.StateOf(id).Should().Be(TransmitState.Requested);
        station.TransmitStarted(id, 800);
        ((Action)(() => station.TransmitEnded(id, 799))).Should().Throw<InvalidOperationException>().WithMessage("*before it started*");
        ((Action)(() => station.TransmitEnded(id, 1001))).Should().Throw<InvalidOperationException>().WithMessage("*future*");
        station.IsTransmitting.Should().BeTrue();
    }

    [Fact]
    public void Withdrawing_a_request_that_has_started_ended_or_been_withdrawn_is_an_error()
    {
        var (station, _) = Make();
        long a = station.RequestTransmit(new float[10], 0);
        station.TransmitStarted(a, 0);
        ((Action)(() => station.WithdrawTransmit(a))).Should().Throw<InvalidOperationException>().WithMessage("*started*cannot be withdrawn*");
        station.TransmitEnded(a, 0);
        ((Action)(() => station.WithdrawTransmit(a))).Should().Throw<InvalidOperationException>().WithMessage("*ended*cannot be withdrawn*");
        long b = station.RequestTransmit(new float[10], 0);
        station.WithdrawTransmit(b);
        ((Action)(() => station.WithdrawTransmit(b))).Should().Throw<InvalidOperationException>().WithMessage("*withdrawn*cannot be withdrawn*");
    }

    [Fact]
    public void A_request_forgotten_after_a_later_one_starts_is_reported_as_finished()
    {
        var (station, _) = Make();
        long a = station.RequestTransmit(new float[10], 0);
        long b = station.RequestTransmit(new float[10], 0);
        station.TransmitStarted(a, 0);
        station.TransmitEnded(a, 0);
        station.TransmitStarted(b, 0);
        station.StateOf(a).Should().BeNull();
        ((Action)(() => station.TransmitEnded(a, 0))).Should().Throw<InvalidOperationException>().WithMessage("*finished and forgotten*");
    }

    [Fact]
    public void Dropped_samples_still_move_the_clock_and_a_transmission_spans_them()
    {
        var (station, layer) = Make();
        long id = station.RequestTransmit(new float[3000], 0);
        layer.StartAt(id, 1000);
        layer.DropAt(2000, 480);
        layer.EndAt(id, 4000);
        layer.Run(5000);

        station.SampleCount.Should().Be(5000);
        station.StateOf(id).Should().Be(TransmitState.Ended);
        station.StartedAt(id).Should().Be(1000);
        station.EndedAt(id).Should().Be(4000);
    }

    [Fact]
    public void A_drop_across_the_earliest_sample_lets_the_start_come_after_the_gap()
    {
        var (station, layer) = Make();
        long id = station.RequestTransmit(new float[10], 1500);
        layer.DropAt(1000, 2000);
        layer.StartAt(id, 3000);
        layer.EndAt(id, 3010);
        layer.Run(4000);
        station.StateOf(id).Should().Be(TransmitState.Ended);
    }

    [Fact]
    public void A_fake_layer_that_plays_every_request_with_latency()
    {
        var (station, layer) = Make();
        layer.PlayEveryRequest(100);
        station.ScheduleAt(1000, () => station.RequestTransmit(new float[240], station.SampleCount));
        station.ScheduleAt(3000, () => station.RequestTransmit(new float[240], 5000));
        layer.Run(10000);

        station.StateOf(2).Should().Be(TransmitState.Ended);
        station.StartedAt(2).Should().Be(5000);
        station.EndedAt(2).Should().Be(5240);
        station.IsTransmitting.Should().BeFalse();
    }
}
