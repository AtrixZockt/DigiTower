using DcsAtc.Srs;

namespace DcsAtc.Host;

/// <summary>
/// Who may talk on which frequency, so nothing DigiTower sends steps on anyone: one of our transmitters at a time per
/// channel, never while anyone else (a player, another program) is transmitting there or until it has been quiet for
/// <see cref="Quiet"/>, and after a controller's call to a player, room for the player's readback: until the player
/// has read back, or <see cref="ReadbackRoom"/> has passed. Thread-safe; time from <c>clock</c> (milliseconds).
/// </summary>
public sealed class FrequencyFloor(Func<long>? clock = null)
{
    /// <summary>How long a channel must have been quiet after someone else's transmission.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(500);
    /// <summary>How long a player gets to start reading back a call.</summary>
    public static readonly TimeSpan ReadbackRoom = TimeSpan.FromSeconds(4);

    private readonly Func<long> _clock = clock ?? (() => Environment.TickCount64);
    private readonly Lock _lock = new();
    private readonly Dictionary<(long, int), object> _holders = [];
    private readonly Dictionary<(long, int), long> _lastHeard = [];
    private readonly Dictionary<(long, int), (long Since, long Until)> _rooms = [];

    // A channel: the frequency to the kHz, and its modulation.
    private static (long, int) Key(SrsFrequency f) => ((long)Math.Round(f.Hz / 1000), (int)f.Modulation);

    /// <summary>Someone else is transmitting on these frequencies (an SRS voice packet arrived).</summary>
    public void Heard(IEnumerable<SrsFrequency> frequencies)
    {
        long now = _clock();
        lock (_lock)
        {
            foreach (var f in frequencies) _lastHeard[Key(f)] = now;
        }
    }

    /// <summary>Whether <paramref name="me"/> may start on all these frequencies now.</summary>
    public bool IsFree(IReadOnlyList<SrsFrequency> frequencies, object me)
    {
        long now = _clock();
        lock (_lock) return frequencies.Select(Key).All(k => Free(k, me, now));
    }

    private bool Free((long, int) key, object me, long now)
    {
        if (_holders.TryGetValue(key, out var holder) && holder != me) return false;
        long heard = _lastHeard.GetValueOrDefault(key, long.MinValue / 2);
        if (now - heard < Quiet.TotalMilliseconds) return false;
        if (_rooms.TryGetValue(key, out var room) && now < room.Until)
        {
            // The player has read back (someone transmitted after the call and has finished): the room is over.
            bool readBack = heard > room.Since;
            if (!readBack) return false;
        }
        return true;
    }

    /// <summary>Takes the frequencies if they are free; dispose the lease to give them back.</summary>
    public Lease? TryTake(IReadOnlyList<SrsFrequency> frequencies, object me)
    {
        long now = _clock();
        lock (_lock)
        {
            var keys = frequencies.Select(Key).Distinct().ToList();
            if (!keys.All(k => Free(k, me, now))) return null;
            foreach (var k in keys)
            {
                _holders[k] = me;
                _rooms.Remove(k);
            }
            return new Lease(this, keys, me);
        }
    }

    /// <summary>Waits until the frequencies are free (checking every 100 ms) and takes them.</summary>
    public async Task<Lease> TakeAsync(IReadOnlyList<SrsFrequency> frequencies, object me, CancellationToken ct)
    {
        while (true)
        {
            if (TryTake(frequencies, me) is { } lease) return lease;
            await Task.Delay(100, ct);
        }
    }

    private void Release(IReadOnlyList<(long, int)> keys, object me, bool readbackRoom)
    {
        long now = _clock();
        lock (_lock)
        {
            foreach (var k in keys)
            {
                if (_holders.TryGetValue(k, out var holder) && holder == me) _holders.Remove(k);
                if (readbackRoom) _rooms[k] = (now, now + (long)ReadbackRoom.TotalMilliseconds);
            }
        }
    }

    /// <summary>Frequencies taken by one transmitter.</summary>
    public sealed class Lease(FrequencyFloor floor, IReadOnlyList<(long, int)> keys, object me) : IDisposable
    {
        private bool _released;

        /// <summary>After the last call was to a player: keep the frequencies quiet for the readback.</summary>
        public bool LeaveReadbackRoom { get; set; }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            floor.Release(keys, me, LeaveReadbackRoom);
        }
    }
}
