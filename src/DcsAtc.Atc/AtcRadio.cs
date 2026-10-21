namespace DcsAtc.Atc;

/// <summary>A controller transmission: what <paramref name="Station"/> ("Al Dhafra Ground") says on a frequency.</summary>
/// <param name="Coalition">Who it is for: on a coalition-secure SRS server only that coalition hears it.</param>
/// <summary>
/// A controller's transmission: <see cref="Station"/> ("Al Dhafra Ground") says <see cref="Text"/> on every frequency of
/// its position (<see cref="Frequencies"/>). The positions of an airfield share frequencies, so one radio per airfield
/// says their calls one after another and listens on all of the airfield's frequencies (<see cref="Stations"/>).
/// </summary>
public sealed record RadioCall(string Facility, string Role, int Coalition, string Text, AirfieldStations Stations)
{
    /// <summary>"Al Dhafra Ground"; a FARP site, which has one position, is just its name ("FOB Laos").</summary>
    public string Station => Role.Length == 0 ? Facility : $"{Facility} {Role}";
    public IReadOnlyList<double> Frequencies => Stations.For(Role);

    /// <summary>An AI pilot's call before it, in the AI pilots' voice ("Al Dhafra Approach, Colt 2-1, …, inbound for landing").</summary>
    public string? PilotCall { get; init; }

    /// <summary>The AI pilot's readback after it ("Hold over the TACAN, 7000 feet, Colt 2-1.").</summary>
    public string? PilotReadback { get; init; }

    /// <summary>To an AI pilot: no room is left for a player's readback after it.</summary>
    public bool ToAi { get; init; }
}

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

    /// <summary>The same for what AI pilots say, in their own voice (nothing without one).</summary>
    void PreparePilot(IEnumerable<string> texts, bool soon = false)
    {
    }

    /// <summary>
    /// Keeps saying <paramref name="text"/> on these frequencies, again and again, while anyone is listening (an ATIS);
    /// a new text replaces the old one, null stops the broadcast.
    /// </summary>
    void Broadcast(string station, IReadOnlyList<double> frequencies, int coalition, string? text);

    /// <summary>Puts an airfield's controllers on the air before they first speak, so they hear pilots calling them (voice requests).</summary>
    void Listen(string facility, AirfieldStations stations, int coalition)
    {
    }
}
