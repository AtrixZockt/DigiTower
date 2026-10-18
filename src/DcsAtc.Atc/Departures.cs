using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;

namespace DcsAtc.Atc;

/// <summary>Where a pilot wants to go after takeoff (chosen with "Ready for departure").</summary>
public enum DepartureIntent
{
    OwnNavigation,
    North,
    East,
    South,
    West,
    /// <summary>Pattern work: stays with Tower, lands (or touches and goes) on the same runway.</summary>
    ClosedTraffic,
}

/// <summary>
/// What Tower says about the climb-out with the takeoff clearance, from the pilot's intent and the traffic:
/// a turn-out for a direction, "fly runway heading, maintain at or below 500 feet until 3 miles" under pattern
/// traffic, "climb and maintain 3000 feet until advised" behind departing traffic, otherwise an unrestricted climb.
/// </summary>
public sealed record DepartureInstruction
{
    public required DepartureIntent Intent { get; init; }
    public required RunwayEnd Runway { get; init; }
    public required DepartureProcedures Procedures { get; init; }
    /// <summary>Heading to turn to for a direction intent (magnetic, as said); null for own navigation.</summary>
    public int? Heading { get; init; }
    /// <summary>"left" or "right"; null when the heading is within 30° of the runway's (then it is "fly runway heading").</summary>
    public string? Turn { get; init; }
    /// <summary>Pattern traffic above: stay low until the stay-low distance.</summary>
    public bool StayLow { get; init; }
    /// <summary>The departing aircraft ahead: runway heading and the initial altitude until it is far enough away.</summary>
    public string? Following { get; init; }
    /// <summary>"left" or "right" for closed traffic.</summary>
    public string? ClosedTrafficSide { get; init; }

    public bool UntilAdvised => Following is not null;

    public static DepartureInstruction Create(DepartureIntent intent, RunwayEnd runway, DepartureProcedures procedures, bool stayLow = false, string? following = null)
    {
        if (intent == DepartureIntent.ClosedTraffic)
        {
            return new DepartureInstruction
            {
                Intent = intent, Runway = runway, Procedures = procedures,
                ClosedTrafficSide = procedures.ClosedTraffic.GetValueOrDefault(runway.Name, "left"),
            };
        }
        int? heading = intent switch { DepartureIntent.North => 360, DepartureIntent.East => 90, DepartureIntent.South => 180, DepartureIntent.West => 270, _ => null };
        string? turn = null;
        if (heading is int h)
        {
            double change = ((h - runway.TrueHeading) % 360 + 540) % 360 - 180;
            if (Math.Abs(change) > 30) turn = change > 0 ? "right" : "left";
        }
        return new DepartureInstruction { Intent = intent, Runway = runway, Procedures = procedures, Heading = heading, Turn = turn, StayLow = stayLow, Following = following };
    }
}
