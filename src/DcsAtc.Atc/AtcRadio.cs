namespace DcsAtc.Atc;

/// <summary>A controller transmission: what <paramref name="Station"/> ("Al Dhafra Ground") says on a frequency.</summary>
/// <param name="Coalition">Who it is for: on a coalition-secure SRS server only that coalition hears it.</param>
public sealed record RadioCall(string Station, double FrequencyMhz, int Coalition, string Text);

/// <summary>Where controllers' transmissions go to be spoken (SRS). Text messages are sent separately.</summary>
public interface IAtcRadio
{
    /// <summary>Queues a transmission and returns at once; the station says its calls one after another.</summary>
    void Transmit(RadioCall call);

    /// <summary>
    /// Gets these texts ready to be said without delay: an airfield's phrases at mission start, or
    /// (<paramref name="soon"/>, ahead of the rest) a pilot's callsign as they spawn.
    /// </summary>
    void Prepare(IEnumerable<string> texts, bool soon = false);

    /// <summary>
    /// Keeps saying <paramref name="text"/> on a frequency, again and again, while anyone is listening (an ATIS);
    /// a new text replaces the old one, null stops the broadcast.
    /// </summary>
    void Broadcast(string station, double frequencyMhz, int coalition, string? text);
}
