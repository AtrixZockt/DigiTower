using DcsAtc.Host;
using DcsAtc.Srs;

namespace DcsAtc.Tests;

public class FrequencyFloorTests
{
    private long _now = 1_000_000;
    private static readonly SrsFrequency Ground = SrsFrequency.Mhz(250.0), Tower = SrsFrequency.Mhz(270.0);
    private readonly object _dhafra = new(), _minhad = new();

    private FrequencyFloor Floor() => new(() => _now);

    [Fact]
    public void One_transmitter_per_frequency()
    {
        var floor = Floor();
        var lease = floor.TryTake([Ground], _dhafra);
        Assert.NotNull(lease);
        Assert.Null(floor.TryTake([Ground], _minhad));          // shared frequency: the other airfield waits
        Assert.NotNull(floor.TryTake([Tower], _minhad)?.Also(l => l.Dispose()));
        lease!.Dispose();
        Assert.NotNull(floor.TryTake([Ground], _minhad));
    }

    [Fact]
    public void Nobody_starts_while_someone_else_talks_or_right_after()
    {
        var floor = Floor();
        floor.Heard([Ground]);
        Assert.False(floor.IsFree([Ground], _dhafra));
        Assert.True(floor.IsFree([Tower], _dhafra));
        _now += 400;
        Assert.False(floor.IsFree([Ground], _dhafra));
        _now += 100;
        Assert.True(floor.IsFree([Ground], _dhafra));
    }

    [Fact]
    public void A_player_gets_room_to_read_back()
    {
        var floor = Floor();
        var lease = floor.TryTake([Ground], _dhafra)!;
        lease.LeaveReadbackRoom = true;
        lease.Dispose();

        // Nobody (the same airfield's next call included) may start while the player has the chance to answer.
        _now += 1000;
        Assert.False(floor.IsFree([Ground], _dhafra));
        Assert.False(floor.IsFree([Ground], _minhad));
        _now += 3000;
        Assert.True(floor.IsFree([Ground], _dhafra));   // 4 s and no readback: go on

        // With a readback: free as soon as the player has finished.
        lease = floor.TryTake([Ground], _dhafra)!;
        lease.LeaveReadbackRoom = true;
        lease.Dispose();
        _now += 1500;
        floor.Heard([Ground]);   // the player reads back
        _now += 300;
        Assert.False(floor.IsFree([Ground], _dhafra));
        _now += 200;
        Assert.True(floor.IsFree([Ground], _dhafra));
    }

    [Fact]
    public async Task Waiting_for_the_frequency_takes_it_when_free()
    {
        long now = 0;
        var floor = new FrequencyFloor(() => Interlocked.Read(ref now));
        var first = floor.TryTake([Ground], _dhafra)!;
        var waiting = floor.TakeAsync([Ground], _minhad, CancellationToken.None);
        await Task.Delay(250);
        Assert.False(waiting.IsCompleted);
        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(floor.IsFree([Ground], _dhafra));
    }
}

internal static class FloorTestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
