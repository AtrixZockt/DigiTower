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
    /// <summary>Stations assemble what they hear on their frequency into transmissions (<see cref="SrsRadio.Heard"/>).</summary>
    public bool Listen { get; init; }
}

/// <summary>
/// Controllers on SRS: every airfield's controllers (per coalition) are one SRS client, "Al Dhafra ATC", since its
/// positions share frequencies: it says their calls one after another, in the ATC voice, each on the frequencies of
/// the position calling (<see cref="RadioCall.Frequencies"/>). Clips are made ahead of time on a low-priority thread
/// (<see cref="Prepare"/>), so a call normally only stitches cached clips. Broadcasts (ATIS) are clients of their own
/// that repeat their text while any SRS client has a radio on one of their frequencies. With
/// <see cref="SrsRadioOptions.Listen"/>, every airfield's client reports what pilots say on its frequencies (<see cref="Heard"/>).
/// </summary>
public sealed class SrsRadio : IAtcRadio, IAsyncDisposable
{
    private readonly SrsRadioOptions _options;
    private readonly AtcVoice _voice;
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<(string Facility, int Coalition), Station> _stations = new();
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

    /// <summary>A pilot's transmission heard by an airfield's controllers (the airfield, "Al Dhafra"), when listening is on.</summary>
    public event Action<string, SrsTransmission>? Heard;

    public void Transmit(RadioCall call) => StationFor(call.Facility, call.Stations, call.Coalition).Enqueue(call);

    public void Listen(string facility, AirfieldStations stations, int coalition)
    {
        if (_options.Listen) StationFor(facility, stations, coalition);
    }

    /// <summary>The SRS name of an airfield's controllers.</summary>
    public static string ClientName(string facility) => $"{facility} ATC";

    private bool IsStation(string name) => _broadcasts.ContainsKey(name) || _stations.Keys.Any(k => ClientName(k.Facility) == name);

    private Station StationFor(string facility, AirfieldStations stations, int coalition) =>
        _stations.GetOrAdd((facility, coalition), key => new Station(this, key.Facility, key.Coalition, [.. stations.All.Select(f => SrsFrequency.Mhz(f))]));

    public void Prepare(IEnumerable<string> texts, bool soon = false)
    {
        var queue = soon ? _prepareSoon : _prepare;
        foreach (var unit in texts.SelectMany(SpokenText.Units).Select(u => u.Text).Distinct())
        {
            if (!queue.IsAddingCompleted && !_voice.IsCached(unit)) queue.Add(unit);
        }
    }

    public void Broadcast(string station, IReadOnlyList<double> frequencies, int coalition, string? text)
    {
        if ((text is null && !_broadcasts.ContainsKey(station)) || frequencies.Count == 0) return;
        _broadcasts.GetOrAdd(station, name => new Broadcaster(this, name, coalition, [.. frequencies.Select(f => SrsFrequency.Mhz(f))])).Text = text;
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

    /// <summary>An airfield's controllers on SRS.</summary>
    private sealed class Station
    {
        private readonly SrsRadio _radio;
        private readonly string _name;
        private readonly SrsClient _client;
        private readonly SrsReceiver? _receiver;
        private readonly Channel<(RadioCall Call, long Queued)> _queue = Channel.CreateUnbounded<(RadioCall, long)>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Task _run, _work;
        private long _lastError = -1;

        public Station(SrsRadio radio, string facility, int coalition, IReadOnlyList<SrsFrequency> frequencies)
        {
            _radio = radio;
            _name = ClientName(facility);
            // It has a radio on each of the airfield's frequencies, so SRS sends it what pilots say there.
            _client = new SrsClient(new SrsOptions
            {
                Host = radio._options.Host,
                Port = radio._options.Port,
                Name = _name,
                Coalition = coalition,
                Receive = [.. frequencies.Take(SrsProtocol.MaxRadios - 1)],
            }, radio._log);
            if (radio._options.Listen)
            {
                _receiver = new SrsReceiver(_client);
                // Pilots only: another airfield's controllers can share a frequency.
                _receiver.TransmissionReceived += t =>
                {
                    if (t.Sender?.Name is not { } sender || !radio.IsStation(sender)) radio.Heard?.Invoke(facility, t);
                };
            }
            _run = _client.RunAsync(radio._stop.Token);
            _work = Task.Run(WorkAsync);
        }

        public void Enqueue(RadioCall call) => _queue.Writer.TryWrite((call, Stopwatch.GetTimestamp()));

        public async Task StopAsync()
        {
            _queue.Writer.TryComplete();
            await Task.WhenAll(_run, _work);
            _receiver?.Dispose();
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
                        var frequencies = call.Frequencies.Select(f => SrsFrequency.Mhz(f)).ToList();
                        _radio._log($"radio {call.Station} {string.Join(", ", frequencies)}: {call.Text}");
                        await _client.TransmitAsync(Audio.ToPcm16(audio.Samples), frequencies, ct);
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
        private readonly IReadOnlyList<SrsFrequency> _frequencies;
        private readonly SrsClient _client;
        private readonly Task _run, _work;
        private (string Text, short[] Pcm)? _rendered;

        public Broadcaster(SrsRadio radio, string name, int coalition, IReadOnlyList<SrsFrequency> frequencies)
        {
            _radio = radio;
            _name = name;
            _frequencies = frequencies;
            _client = new SrsClient(new SrsOptions { Host = radio._options.Host, Port = radio._options.Port, Name = name, Coalition = coalition }, radio._log);
            _run = _client.RunAsync(radio._stop.Token);
            _work = Task.Run(WorkAsync);
        }

        /// <summary>The text to repeat; null: silent.</summary>
        public volatile string? Text;

        /// <summary>Someone has a radio on one of the frequencies (SRS tells every client the others' radios).</summary>
        private bool Listened => _client.Peers.Values.Any(p => p.Radios.Any(r => _frequencies.Any(r.SameChannel)));

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
                    if (!said) _radio._log($"radio {_name} {string.Join(", ", _frequencies)}: broadcasting ({text.Length} characters)");
                    said = true;
                    try
                    {
                        await _client.TransmitAsync(_rendered.Value.Pcm, _frequencies, ct);
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
