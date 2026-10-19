using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using DcsAtc.Atc;
using DcsAtc.Srs;
using DcsAtc.Voice;

namespace DcsAtc.App;

public sealed record SrsRadioOptions
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 5002;
    /// <summary>A call still waiting this long when its turn comes is dropped (the situation has moved on).</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Silence between two calls of one station.</summary>
    public TimeSpan GapBetweenCalls { get; init; } = TimeSpan.FromMilliseconds(700);
}

/// <summary>
/// Controllers on SRS: every station ("Al Dhafra Ground", per coalition) is an SRS client of its own that says
/// its calls one after another, in the ATC voice, on the call's frequency. Clips are made ahead of time on a
/// low-priority thread (<see cref="Prepare"/>), so a call normally only stitches cached clips. Broadcasts (ATIS) are
/// stations of their own that repeat their text while any SRS client has a radio on their frequency.
/// </summary>
public sealed class SrsRadio : IAtcRadio, IAsyncDisposable
{
    private readonly SrsRadioOptions _options;
    private readonly AtcVoice _voice;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<(string Station, int Coalition), Station> _stations = new();
    private readonly ConcurrentDictionary<string, Broadcaster> _broadcasts = new();
    // Clips to make: callsigns of pilots who just spawned first, then the airfields' phrases.
    private readonly BlockingCollection<string> _prepareSoon = [];
    private readonly BlockingCollection<string> _prepare = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _prepareThread;

    public SrsRadio(SrsRadioOptions options, AtcVoice voice, Action<string> log)
    {
        _options = options;
        _voice = voice;
        _log = log;
        _prepareThread = new Thread(PrepareLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "ATC voice prepare" };
        _prepareThread.Start();
    }

    public void Transmit(RadioCall call)
    {
        var station = _stations.GetOrAdd((call.Station, call.Coalition), key => new Station(this, key.Station, key.Coalition, SrsFrequency.Mhz(call.FrequencyMhz)));
        station.Enqueue(call);
    }

    public void Prepare(IEnumerable<string> texts, bool soon = false)
    {
        var queue = soon ? _prepareSoon : _prepare;
        foreach (var unit in texts.SelectMany(SpokenText.Units).Select(u => u.Text).Distinct())
        {
            if (!queue.IsAddingCompleted && !_voice.IsCached(unit)) queue.Add(unit);
        }
    }

    public void Broadcast(string station, double frequencyMhz, int coalition, string? text)
    {
        if (text is null && !_broadcasts.ContainsKey(station)) return;
        _broadcasts.GetOrAdd(station, name => new Broadcaster(this, name, coalition, SrsFrequency.Mhz(frequencyMhz))).Text = text;
    }

    public async ValueTask DisposeAsync()
    {
        _prepareSoon.CompleteAdding();
        _prepare.CompleteAdding();
        await _stop.CancelAsync();
        await Task.WhenAll(_stations.Values.Select(s => s.StopAsync()).Concat(_broadcasts.Values.Select(b => b.StopAsync())));
        _prepareThread.Join(TimeSpan.FromSeconds(5));
        _voice.Dispose();
    }

    private void PrepareLoop()
    {
        BlockingCollection<string>[] queues = [_prepareSoon, _prepare];
        int made = 0;
        var clock = new Stopwatch();
        try
        {
            while (true)
            {
                // One clip at a time, so a callsign never waits behind a whole airfield.
                BlockingCollection<string>.TakeFromAny(queues, out string? unit, _stop.Token);
                if (!clock.IsRunning) clock.Restart();
                if (unit is not null && _voice.Prepare(unit)) made++;
                if (queues.All(q => q.Count == 0))
                {
                    if (made > 0) _log($"voice: {made} new clips prepared in {clock.Elapsed.TotalSeconds:F1} s");
                    made = 0;
                    clock.Reset();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            // Stopped (TakeFromAny throws ArgumentException once every queue is completed).
        }
    }

    /// <summary>One controller position on SRS.</summary>
    private sealed class Station
    {
        private readonly SrsRadio _radio;
        private readonly string _name;
        private readonly SrsClient _client;
        private readonly Channel<(RadioCall Call, long Queued)> _queue = Channel.CreateUnbounded<(RadioCall, long)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _run, _work;
        private long _lastError = -1;

        public Station(SrsRadio radio, string name, int coalition, SrsFrequency frequency)
        {
            _radio = radio;
            _name = name;
            // It listens on its own frequency too (for voice requests later).
            _client = new SrsClient(new SrsOptions
            {
                Host = radio._options.Host,
                Port = radio._options.Port,
                Name = name,
                Coalition = coalition,
                Receive = [frequency],
            }, radio._log);
            _run = _client.RunAsync(radio._stop.Token);
            _work = Task.Run(WorkAsync);
        }

        public void Enqueue(RadioCall call) => _queue.Writer.TryWrite((call, Stopwatch.GetTimestamp()));

        public async Task StopAsync()
        {
            _queue.Writer.TryComplete();
            await Task.WhenAll(_run, _work);
        }

        private async Task WorkAsync()
        {
            var ct = _radio._stop.Token;
            try
            {
                await foreach (var (call, queued) in _queue.Reader.ReadAllAsync(ct))
                {
                    if (Stopwatch.GetElapsedTime(queued) > _radio._options.MaxDelay)
                    {
                        _radio._log($"radio {_name}: dropped a stale call: {call.Text}");
                        continue;
                    }
                    var audio = await Task.Run(() => _radio._voice.Render(call.Text), ct);
                    try
                    {
                        var frequency = SrsFrequency.Mhz(call.FrequencyMhz);
                        _radio._log($"radio {_name} {frequency}: {call.Text}");
                        await _client.TransmitAsync(Audio.ToPcm16(audio.Samples), [frequency], ct);
                    }
                    catch (SrsException ex)
                    {
                        // Once a minute is enough to say SRS is not there.
                        if (_lastError < 0 || Stopwatch.GetElapsedTime(_lastError) > TimeSpan.FromMinutes(1))
                        {
                            _lastError = Stopwatch.GetTimestamp();
                            _radio._log($"radio {_name}: not transmitted ({ex.Message})");
                        }
                    }
                    await Task.Delay(_radio._options.GapBetweenCalls, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>A recorded broadcast (ATIS): says its text over and over while someone is tuned in.</summary>
    private sealed class Broadcaster
    {
        private static readonly TimeSpan Pause = TimeSpan.FromSeconds(3), Idle = TimeSpan.FromSeconds(2);

        private readonly SrsRadio _radio;
        private readonly string _name;
        private readonly SrsFrequency _frequency;
        private readonly SrsClient _client;
        private readonly Task _run, _work;
        private (string Text, short[] Pcm)? _rendered;

        public Broadcaster(SrsRadio radio, string name, int coalition, SrsFrequency frequency)
        {
            _radio = radio;
            _name = name;
            _frequency = frequency;
            _client = new SrsClient(new SrsOptions { Host = radio._options.Host, Port = radio._options.Port, Name = name, Coalition = coalition }, radio._log);
            _run = _client.RunAsync(radio._stop.Token);
            _work = Task.Run(WorkAsync);
        }

        /// <summary>The text to repeat; null: silent.</summary>
        public volatile string? Text;

        /// <summary>Someone has a radio on the frequency (SRS tells every client the others' radios).</summary>
        private bool Listened => _client.Peers.Values.Any(p => p.Radios.Any(r => r.SameChannel(_frequency)));

        public Task StopAsync() => Task.WhenAll(_run, _work);

        private async Task WorkAsync()
        {
            var ct = _radio._stop.Token;
            bool said = false;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (Text is not { } text || !_client.IsReady || !Listened)
                    {
                        await Task.Delay(Idle, ct);
                        continue;
                    }
                    if (_rendered?.Text != text)
                    {
                        var audio = await Task.Run(() => _radio._voice.Render(text), ct);
                        _rendered = (text, Audio.ToPcm16(audio.Samples));
                    }
                    if (!said) _radio._log($"radio {_name} {_frequency}: broadcasting ({text.Length} characters)");
                    said = true;
                    try
                    {
                        await _client.TransmitAsync(_rendered.Value.Pcm, [_frequency], ct);
                    }
                    catch (SrsException ex)
                    {
                        _radio._log($"radio {_name}: {ex.Message}");
                        await Task.Delay(Idle, ct);
                    }
                    await Task.Delay(Pause, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
