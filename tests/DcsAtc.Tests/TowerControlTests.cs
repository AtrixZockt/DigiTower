using System.Text.Json;
using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Atc;
using DcsAtc.Bridge;
using DcsAtc.Voice;

namespace DcsAtc.Tests;

public class TowerControlTests
{
    /// <summary>Al Dhafra (real DCS layout and chart labels) with wind from 310° at 10 knots.</summary>
    private sealed class World
    {
        public required FakeDcs Dcs { get; init; }
        public required GroundControl Ground { get; init; }
        public required TowerControl Tower { get; init; }
        public required FlightRegistry Flights { get; init; }
        public required FakeRadio Radio { get; init; }
        public required AtcMenu Menu { get; init; }
        public required Airfield Layout { get; init; }
        public Dictionary<string, UnitState> Units { get; } = [];
        public Runway Rwy31R => Layout.Runways.Single(r => r.End("31R") is not null);
        public RunwayEnd End31R => Rwy31R.End("31R")!;
        public string LastText => Dcs.Texts[^1].Text;
        public required AirbaseWeather Weather { get; init; }

        public UnitState Put(UnitState u)
        {
            Units[u.Name] = u;
            return u;
        }

        /// <summary>A point on final for 31R, <paramref name="metres"/> before the threshold.</summary>
        public Vec2 OnFinal31R(double metres) => End31R.Threshold - Vec2.FromHeading(End31R.TrueHeading) * metres;

        /// <summary>A point on runway 31R, <paramref name="metres"/> past its threshold.</summary>
        public Vec2 OnRunway31R(double metres) => End31R.Threshold + Vec2.FromHeading(End31R.TrueHeading) * metres;
    }

    private static async Task<World?> CreateAsync()
    {
        if (TestPaths.Terrains is null) return null;
        string dump = Path.Combine(TestPaths.RepoRoot, "data", "dcs", "Al Dhafra AFB.json");
        if (!File.Exists(dump)) return null;
        var info = JsonSerializer.Deserialize<AirbaseInfo>(File.ReadAllText(dump), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var weather = new AirbaseWeather { WindX = -5.14 * Math.Cos(310 * Math.PI / 180), WindZ = -5.14 * Math.Sin(310 * Math.PI / 180), Pressure = 101130, Elevation = 16, Temperature = 303 };
        var airbase = new AirbaseSummary { Name = "Al Dhafra AFB", Id = 4, Category = 0, Coalition = 2, X = info.X, Y = info.Y, Z = info.Z };
        var dcs = new FakeDcs([airbase], info, weather);
        var catalog = new AirfieldCatalog(dcs, Path.GetDirectoryName(Path.GetDirectoryName(TestPaths.Terrains)), Path.Combine(TestPaths.RepoRoot, "data"));
        catalog.SetMission("PersianGulf", [airbase]);
        var now = new DateTime(2026, 10, 18, 12, 0, 0);
        var flights = new FlightRegistry(dcs);
        var radio = new FakeRadio();
        var ground = new GroundControl(dcs, catalog, flights, clock: () => now, radio: radio);
        var tower = new TowerControl(dcs, catalog, ground, flights, clock: () => now = now.AddSeconds(1), radio: radio);
        var layout = (await catalog.GetAsync(airbase))!.Layout;
        var menu = new AtcMenu(catalog, ground, tower, flights);
        return new World { Dcs = dcs, Ground = ground, Tower = tower, Flights = flights, Radio = radio, Menu = menu, Layout = layout, Weather = weather };
    }

    private static UnitState Aircraft(string name, int group, Vec2 p, bool inAir = false, double agl = 0, double hdg = 0, double speed = 0, string? player = null, double climb = 0) => new()
    {
        Id = 1000 + group, Name = name, Type = "F-16C_50", Cat = "plane", Coal = 2, Group = name, GroupId = group,
        Player = player, Callsign = "", X = p.X, Y = 16 + agl, Z = p.Z, Agl = agl, InAir = inAir, Hdg = hdg,
        Vx = speed * Math.Cos(hdg * Math.PI / 180), Vy = climb, Vz = speed * Math.Sin(hdg * Math.PI / 180),
    };

    private static UnitState Viper(Vec2 p, bool inAir = false, double agl = 0, double hdg = 0, double speed = 0, double climb = 0) =>
        Aircraft("Viper 1-1", 1, p, inAir, agl, hdg, speed, "Viper 1-1 | DiGii", climb);

    private const double Ft = 0.3048, Nm = 1852;

    /// <summary>Taxis Viper 1-1 from spot 106 to the 31R holding point and reports ready.</summary>
    private static async Task<Vec2> TaxiToHoldingPointAsync(World w)
    {
        var spot = w.Layout.Parking.Single(p => p.Index == 106).Position;
        await w.Ground.RequestTaxiAsync(w.Put(Viper(spot)), w.Units);
        var hold = w.Ground.Clearances[1].Route.Geometry[^1];
        w.Put(Viper(hold));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Equal(TaxiPhase.HoldingShort, w.Ground.Clearances[1].Phase);
        return hold;
    }

    [SkippableFact]
    public async Task Departure_cleared_takes_off_and_is_handed_to_approach()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        await TaxiToHoldingPointAsync(w!);
        await w!.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal("Viper 1-1, unrestricted climb approved, wind 310 degrees, 10 knots, runway 31R, cleared for takeoff.", w.LastText);
        Assert.Empty(w.Ground.Clearances);
        Assert.Empty(w.Dcs.Drawings);

        w.Put(Viper(w.OnRunway31R(2500), inAir: true, agl: 60, hdg: 308, speed: 90));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(TowerPhase.Departed, w.Tower.Flights[1].Phase);

        w.Put(Viper(w.OnRunway31R(9000), inAir: true, agl: 1200, hdg: 308, speed: 150));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, contact Approach on 257.0, good day.", w.LastText);
        Assert.Empty(w.Tower.Flights);
    }

    [SkippableFact]
    public async Task Departure_holds_while_the_runway_is_occupied()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        await TaxiToHoldingPointAsync(w!);
        w!.Put(Aircraft("Hornet 1-1", 2, w.OnRunway31R(1500)));
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Tower, hold short runway 31R, runway occupied.", w.LastText);

        // Still occupied: no repeat.
        int texts = w.Dcs.Texts.Count;
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(texts, w.Dcs.Texts.Count);

        w.Units.Remove("Hornet 1-1");
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.EndsWith("runway 31R, cleared for takeoff.", w.LastText);
    }

    [SkippableFact]
    public async Task Departure_holds_for_traffic_on_final()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        await TaxiToHoldingPointAsync(w!);
        w!.Put(Aircraft("Tanker", 3, w.OnFinal31R(4000), inAir: true, agl: 250, hdg: 308, speed: 80));
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Tower, hold short runway 31R, traffic on final.", w.LastText);

        // Traffic far out (beyond 5 NM) no longer blocks.
        w.Put(Aircraft("Tanker", 3, w.OnFinal31R(15000), inAir: true, agl: 900, hdg: 308, speed: 80));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.EndsWith("cleared for takeoff.", w.LastText);
    }

    [SkippableFact]
    public async Task Second_departure_lines_up_behind_a_departing_aircraft()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        var hold = await TaxiToHoldingPointAsync(w!);
        await w!.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        // Viper 1-1 rolls; Viper 1-2 is ready at the same holding point.
        w.Put(Viper(w.OnRunway31R(800), hdg: 308, speed: 50));
        var two = w.Put(Aircraft("Viper 1-2", 4, hold, player: "Viper 1-2 | Wingman"));
        await w.Tower.ReadyForDepartureAsync(two, w.Units);
        Assert.Equal("Viper 1-2, runway 31R, line up and wait.", w.LastText);

        // Viper 1-1 climbs out ahead: Viper 1-2 is kept on runway heading and low until there is room.
        w.Put(Viper(w.OnRunway31R(3000), inAir: true, agl: 100, hdg: 308, speed: 100, climb: 20));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-2, fly runway heading, climb and maintain 3000 feet until advised, wind 310 degrees, 10 knots, runway 31R, cleared for takeoff.", w.LastText);

        // Airborne, then climbing through 3000 feet with Viper 1-1 only 3 miles ahead.
        w.Put(Aircraft("Viper 1-2", 4, w.OnRunway31R(2500), inAir: true, agl: 60, hdg: 308, speed: 90, player: "Viper 1-2 | Wingman", climb: 15));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(3000 + 8 * Nm), inAir: true, agl: 3000, hdg: 308, speed: 150, climb: 20));
        w.Put(Aircraft("Viper 1-2", 4, w.OnRunway31R(3000 + 5 * Nm), inAir: true, agl: 3500 * Ft, hdg: 308, speed: 150, player: "Viper 1-2 | Wingman", climb: 15));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-2, check altitude, maintain 3000 feet.", w.LastText);

        // 5 miles between them: released, then handed off.
        w.Put(Viper(w.OnRunway31R(3000 + 11 * Nm), inAir: true, agl: 4000, hdg: 308, speed: 150, climb: 20));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-2, climb unrestricted, resume own navigation.", w.LastText);
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-2, contact Approach on 257.0, good day.", w.LastText);
    }

    [SkippableFact]
    public async Task Departure_north_turns_out_and_resumes_own_navigation_at_the_hand_off()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        await TaxiToHoldingPointAsync(w!);
        await w!.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units, DepartureIntent.North);
        Assert.Equal("Viper 1-1, after departure turn right heading 360, unrestricted climb approved, wind 310 degrees, 10 knots, runway 31R, cleared for takeoff.", w.LastText);

        w.Put(Viper(w.OnRunway31R(2500), inAir: true, agl: 60, hdg: 320, speed: 90, climb: 20));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(9000), inAir: true, agl: 1200, hdg: 360, speed: 150, climb: 20));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, resume own navigation, contact Approach on 257.0, good day.", w.LastText);
    }

    [SkippableFact]
    public async Task Departure_stays_low_under_pattern_traffic_until_3_miles()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        await TaxiToHoldingPointAsync(w!);
        // A jet on the downwind at 1500 feet, two miles abeam the runway.
        var downwind = w!.Rwy31R.Center + w.Rwy31R.Axis.Left * (2 * Nm);
        w.Put(Aircraft("Hornet 1-1", 2, downwind, inAir: true, agl: 1500 * Ft, hdg: 128, speed: 120));
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units, DepartureIntent.West);
        Assert.Equal("Viper 1-1, fly runway heading, maintain at or below 500 feet until 3 miles, then turn left heading 270, unrestricted climb, " +
                     "wind 310 degrees, 10 knots, runway 31R, cleared for takeoff.", w.LastText);

        // Climbing to 1000 feet one and a half miles out: too high.
        w.Put(Viper(w.OnRunway31R(2500), inAir: true, agl: 60, hdg: 308, speed: 90, climb: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(w.Rwy31R.Length + 1.5 * Nm), inAir: true, agl: 1000 * Ft, hdg: 308, speed: 150, climb: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, check altitude, maintain at or below 500 feet until 3 miles.", w.LastText);

        // Beyond 3 miles the restriction ends by itself: handed off.
        w.Put(Viper(w.OnRunway31R(w.Rwy31R.Length + 4 * Nm), inAir: true, agl: 2000 * Ft, hdg: 270, speed: 150, climb: 20));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, resume own navigation, contact Approach on 257.0, good day.", w.LastText);
    }

    [SkippableFact]
    public async Task Closed_traffic_stays_with_tower_touches_and_goes_and_full_stops()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        await TaxiToHoldingPointAsync(w!);
        await w!.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units, DepartureIntent.ClosedTraffic);
        Assert.Equal("Viper 1-1, right closed traffic approved, wind 310 degrees, 10 knots, runway 31R, cleared for takeoff.", w.LastText);

        // Airborne and on the downwind: no hand-off.
        w.Put(Viper(w.OnRunway31R(2500), inAir: true, agl: 60, hdg: 308, speed: 90, climb: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.Rwy31R.Center + w.Rwy31R.Axis.Left * (-1.5 * Nm), inAir: true, agl: 1500 * Ft, hdg: 128, speed: 120));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(TowerPhase.Inbound, w.Tower.Flights[1].Phase);
        Assert.DoesNotContain("contact Approach", w.LastText);

        // On final: cleared to land; touch and go back into the pattern without "vacate".
        w.Put(Viper(w.OnFinal31R(3000), inAir: true, agl: 200, hdg: 308, speed: 80));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, runway 31R, wind 310 degrees, 10 knots, cleared to land.", w.LastText);
        w.Put(Viper(w.OnRunway31R(600), hdg: 308, speed: 70));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(2000), inAir: true, agl: 20, hdg: 308, speed: 80, climb: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(TowerPhase.Inbound, w.Tower.Flights[1].Phase);
        Assert.DoesNotContain("vacate", w.LastText);

        // Second landing to a full stop.
        w.Put(Viper(w.OnFinal31R(3000), inAir: true, agl: 200, hdg: 308, speed: 80));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(600), hdg: 308, speed: 60));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(1500), hdg: 308, speed: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, welcome to Al Dhafra, vacate runway when able, contact Ground on 250.0.", w.LastText);
    }

    [SkippableFact]
    public async Task Arrival_cleared_to_land_goes_around_lands_and_taxis_to_parking()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        var viper = w!.Put(Viper(w.OnFinal31R(15000), inAir: true, agl: 900, hdg: 308, speed: 120));
        await w.Tower.RequestLandingAsync(viper, w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Tower, runway 31R, wind 310 degrees, 10 knots, QNH 1013, altimeter 29.92, report final.", w.LastText);

        // 8 NM out: too far for a landing clearance.
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(TowerPhase.Inbound, w.Tower.Flights[1].Phase);

        w.Put(Viper(w.OnFinal31R(9000), inAir: true, agl: 450, hdg: 308, speed: 80));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, runway 31R, wind 310 degrees, 10 knots, cleared to land.", w.LastText);

        // Someone taxis onto the runway with Viper 1-1 at half a mile: go around.
        w.Put(Aircraft("Hornet 1-1", 2, w.OnRunway31R(300)));
        w.Put(Viper(w.OnFinal31R(900), inAir: true, agl: 50, hdg: 308, speed: 75));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, go around, I say again, go around, runway occupied.", w.LastText);
        Assert.Equal(TowerPhase.Inbound, w.Tower.Flights[1].Phase);

        // Second approach: runway clear again, final reported early.
        w.Units.Remove("Hornet 1-1");
        w.Put(Viper(w.OnFinal31R(14000), inAir: true, agl: 800, hdg: 308, speed: 90));
        await w.Tower.ReportFinalAsync(w.Units["Viper 1-1"], w.Units);
        Assert.EndsWith("cleared to land.", w.LastText);

        // Down on the runway, then off it.
        w.Put(Viper(w.OnRunway31R(1500), hdg: 308, speed: 20));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, welcome to Al Dhafra, vacate runway when able, contact Ground on 250.0.", w.LastText);
        var exit = w.Rwy31R.HoldPoints.MinBy(h => h.Position.DistanceTo(w.OnRunway31R(1500)))!.Position;
        w.Put(Viper(exit));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Empty(w.Tower.Flights);

        await w.Ground.RequestTaxiToParkingAsync(w.Units["Viper 1-1"], w.Units);
        var clearance = w.Ground.Clearances[1];
        Assert.NotNull(clearance.Spot);
        Assert.Matches(@"^Viper 1-1, Al Dhafra Ground, taxi to [A-Z][\w -]+( via [A-Z0-9, ]+)?\.$", w.LastText);
        Assert.True(clearance.Route.Geometry[^1].DistanceTo(clearance.Spot!.Position) < 1);
        Assert.True(w.Dcs.Drawings.ContainsKey("taxi-1"));

        w.Put(Viper(clearance.Spot.Position));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Ground, parking reached.", w.LastText);
        Assert.Empty(w.Ground.Clearances);
    }

    private static UnitState Hangman(int number, Vec2 p, int group, bool inAir = false, double agl = 0, double hdg = 0, double speed = 0) =>
        Aircraft($"Hangman 1-{number}", group, p, inAir, agl, hdg, speed, $"Hangman 1-{number} | Pilot{number}");

    [SkippableFact]
    public async Task Flights_are_declared_by_callsign_prefix_and_complete_themselves()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = w!.Layout.Parking.Single(p => p.Index == 106).Position;

        var two = w.Put(Hangman(2, spot, 12));
        w.Put(Viper(spot + new Vec2(0, 60)));
        await w.Flights.DeclareAsync(two, 2, w.Units);
        Assert.Equal("Hangman 1 flight of 2: Hangman 1-2 (lead Hangman 1-2). Waiting for 1 more.", w.LastText);

        // Hangman 1-1 spawns: joins, and as number one becomes lead.
        w.Put(Hangman(1, spot + new Vec2(30, 0), 11));
        await w.Flights.OnUnitsAsync(w.Units);
        Assert.Equal("Hangman 1-1 joined. Hangman 1 flight of 2: Hangman 1-1, Hangman 1-2 (lead Hangman 1-1).", w.LastText);
        Assert.Equal(new[] { 11, 12 }, w.Dcs.Texts.TakeLast(2).Select(t => t.Group).Order());
        var party = w.Flights.PartyFor(two, w.Units);
        Assert.Equal(("Hangman 1 flight", 11, "Hangman 1-1"), (party.Callsign, party.Key, party.LeadUnit));
        Assert.Equal(Phraseology.Callsign(w.Units["Viper 1-1"]), w.Flights.PartyFor(w.Units["Viper 1-1"], w.Units).Callsign);

        // Leaving makes Hangman 1-2 single ship again.
        await w.Flights.LeaveAsync(two, w.Units);
        Assert.Equal("Hangman 1-2", w.Flights.PartyFor(two, w.Units).Callsign);
    }

    [SkippableFact]
    public async Task Callsigns_without_a_flight_number_cannot_form_a_flight()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var digii = w!.Put(Aircraft("DiGii", 5, new Vec2(0, 0), player: "DiGii"));
        await w.Flights.DeclareAsync(digii, 2, w.Units);
        Assert.Contains("use a callsign with a flight number", w.LastText);
        Assert.Empty(w.Flights.Flights);
    }

    [SkippableFact]
    public async Task A_flight_gets_one_clearance_and_its_members_are_not_traffic()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = w!.Layout.Parking.Single(p => p.Index == 106).Position;
        var one = w.Put(Hangman(1, spot, 11));
        var two = w.Put(Hangman(2, spot + new Vec2(25, 0), 12));
        await w.Flights.DeclareAsync(one, 2, w.Units);

        // The wingman asks; the clearance is for the flight, from the lead's position, to both.
        int before = w.Dcs.Texts.Count;
        await w.Ground.RequestTaxiAsync(two, w.Units);
        var sent = w.Dcs.Texts.Skip(before).ToList();
        Assert.Equal(new[] { 11, 12 }, sent.Select(t => t.Group).Order());
        Assert.All(sent, t => Assert.StartsWith("Hangman 1 flight, Al Dhafra Ground, taxi to holding point runway 31R via A, B.", t.Text));
        var clearance = w.Ground.Clearances[11];
        Assert.Equal("Hangman 1-1", clearance.UnitName);

        // Lead at the holding point, wingman already lined up on the runway: the flight is cleared.
        var hold = clearance.Route.Geometry[^1];
        w.Put(Hangman(1, hold, 11));
        w.Put(Hangman(2, w.OnRunway31R(100), 12, hdg: 308));
        await w.Ground.OnUnitsAsync(w.Units);
        await w.Tower.ReadyForDepartureAsync(w.Units["Hangman 1-2"], w.Units);
        Assert.Equal("Hangman 1 flight, unrestricted climb approved, wind 310 degrees, 10 knots, runway 31R, cleared for takeoff.", w.LastText);

        // Back for landing: the wingman landed first and is still on the runway; the lead at half a mile is not sent around.
        w.Units.Clear();
        w.Put(Hangman(1, w.OnFinal31R(9000), 11, inAir: true, agl: 450, hdg: 308, speed: 80));
        w.Put(Hangman(2, w.OnFinal31R(8000), 12, inAir: true, agl: 400, hdg: 308, speed: 80));
        await w.Tower.RequestLandingAsync(w.Units["Hangman 1-1"], w.Units);
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Hangman 1 flight, runway 31R, wind 310 degrees, 10 knots, cleared to land.", w.LastText);
        w.Put(Hangman(2, w.OnRunway31R(600), 12, hdg: 308, speed: 30));
        w.Put(Hangman(1, w.OnFinal31R(900), 11, inAir: true, agl: 50, hdg: 308, speed: 75));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal(TowerPhase.ClearedToLand, w.Tower.Flights[11].Phase);
        Assert.DoesNotContain("go around", w.LastText);
    }

    [SkippableFact]
    public async Task The_menu_offers_only_the_calls_that_can_be_made_now()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        async Task<string> Menu() => MenuText.Describe(await w!.Menu.ForAsync(w.Units["Viper 1-1"], w.Units));
        const string flight = "Flight: Flight of 2, Flight of 3, Flight of 4";
        const string ready = "Ready for departure: Own navigation, Departure north, Departure east, Departure south, Departure west, Closed traffic";

        // Parked: taxi only (no taxi to parking from a parking spot).
        var spot = w!.Layout.Parking.Single(p => p.Index == 106).Position;
        w.Put(Viper(spot));
        Assert.Equal($"Ground: Request taxi | {flight}", await Menu());

        // Taxiing, then at the holding point.
        await w.Ground.RequestTaxiAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal($"Ground: Cancel taxi, Say again | {flight}", await Menu());
        w.Put(Viper(w.Ground.Clearances[1].Route.Geometry[^1]));
        await w.Ground.OnUnitsAsync(w.Units);
        Assert.Equal($"Ground: Cancel taxi, Say again | {ready} | {flight}", await Menu());

        // Waiting for departure (runway occupied), then cleared: only Tower's say again.
        w.Put(Aircraft("Hornet 1-1", 2, w.OnRunway31R(1500)));
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal($"{ready} | Tower: Say again | {flight}", await Menu());
        w.Units.Remove("Hornet 1-1");
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal($"Tower: Say again | {flight}", await Menu());

        // Handed off and away: landing request within 30 miles only.
        w.Put(Viper(w.OnRunway31R(2500), inAir: true, agl: 60, hdg: 308, speed: 90, climb: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(9000), inAir: true, agl: 1200, hdg: 308, speed: 150));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal($"Tower: Request landing | {flight}", await Menu());
        w.Put(Viper(w.OnRunway31R(80 * Nm), inAir: true, agl: 5000, hdg: 308, speed: 200));
        Assert.Equal(flight, await Menu());

        // Inbound: report final until reported; landed: taxi to parking.
        w.Put(Viper(w.OnFinal31R(15000), inAir: true, agl: 900, hdg: 308, speed: 120));
        await w.Tower.RequestLandingAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal($"Tower: Report final, Say again | {flight}", await Menu());
        await w.Tower.ReportFinalAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal($"Tower: Say again | {flight}", await Menu());
        w.Put(Viper(w.OnRunway31R(1200), hdg: 308, speed: 40));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal($"Ground: Request taxi to parking | Tower: Say again | {flight}", await Menu());
    }

    [SkippableFact]
    public async Task Flight_entries_depend_on_the_callsign_and_membership()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var spot = w!.Layout.Parking.Single(p => p.Index == 106).Position;

        var digii = w.Put(Aircraft("DiGii", 5, spot, player: "DiGii"));
        Assert.Equal("Ground: Request taxi", MenuText.Describe(await w.Menu.ForAsync(digii, w.Units)));

        var one = w.Put(Hangman(1, spot + new Vec2(0, 200), 11));
        w.Put(Hangman(2, spot + new Vec2(0, 230), 12));
        await w.Flights.DeclareAsync(one, 2, w.Units);
        Assert.EndsWith("Flight: Single ship", MenuText.Describe(await w.Menu.ForAsync(w.Units["Hangman 1-2"], w.Units)));
    }

    [SkippableFact]
    public async Task Controllers_speak_on_their_frequencies_and_every_clip_is_prepared_in_advance()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        // A whole sortie: taxi, hold for traffic, take off west under pattern traffic (and climb too early), hand-off;
        // arrival, go-around, landing, taxi in.
        await TaxiToHoldingPointAsync(w!);
        w!.Put(Aircraft("Hornet 1-1", 2, w.OnRunway31R(1500)));
        await w.Tower.ReadyForDepartureAsync(w.Units["Viper 1-1"], w.Units, DepartureIntent.West);
        w.Units.Remove("Hornet 1-1");
        w.Put(Aircraft("Hornet 2-1", 5, w.Rwy31R.Center + w.Rwy31R.Axis.Left * (2 * Nm), inAir: true, agl: 1500 * Ft, hdg: 128, speed: 120));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(2500), inAir: true, agl: 60, hdg: 308, speed: 90, climb: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Viper(w.OnRunway31R(w.Rwy31R.Length + 1.5 * Nm), inAir: true, agl: 1000 * Ft, hdg: 308, speed: 150, climb: 10));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Units.Remove("Hornet 2-1");
        w.Put(Viper(w.OnRunway31R(w.Rwy31R.Length + 4 * Nm), inAir: true, agl: 1200, hdg: 270, speed: 150, climb: 20));
        await w.Tower.OnUnitsAsync(w.Units);

        w.Put(Viper(w.OnFinal31R(15000), inAir: true, agl: 900, hdg: 308, speed: 120));
        await w.Tower.RequestLandingAsync(w.Units["Viper 1-1"], w.Units);
        w.Put(Viper(w.OnFinal31R(9000), inAir: true, agl: 450, hdg: 308, speed: 80));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Put(Aircraft("Hornet 1-1", 2, w.OnRunway31R(300)));
        w.Put(Viper(w.OnFinal31R(900), inAir: true, agl: 50, hdg: 308, speed: 75));
        await w.Tower.OnUnitsAsync(w.Units);
        w.Units.Remove("Hornet 1-1");
        await w.Tower.ReportFinalAsync(w.Units["Viper 1-1"], w.Units);
        w.Put(Viper(w.OnRunway31R(1200), hdg: 308, speed: 40));
        await w.Tower.OnUnitsAsync(w.Units);
        await w.Ground.RequestTaxiToParkingAsync(w.Units["Viper 1-1"], w.Units);

        // Ground speaks on 250.0, Tower on 270.0, to the blue coalition; everything said is also shown.
        var calls = w.Radio.Calls;
        Assert.Contains(calls, c => c.Text.Contains("taxi to holding point runway 31R"));
        Assert.Contains(calls, c => c.Text.Contains("go around"));
        Assert.Contains(calls, c => c.Text.Contains("maintain at or below 500 feet until 3 miles, then turn left heading 270"));
        Assert.Contains(calls, c => c.Text.Contains("check altitude"));
        Assert.All(calls, c => Assert.Equal(2, c.Coalition));
        Assert.All(calls.Where(c => c.Text.Contains(" Ground, ")), c => Assert.Equal(("Al Dhafra Ground", 250.0), (c.Station, c.FrequencyMhz)));
        Assert.All(calls.Where(c => c.Text.Contains("cleared") || c.Text.Contains("go around")), c => Assert.Equal(("Al Dhafra Tower", 270.0), (c.Station, c.FrequencyMhz)));
        Assert.All(calls, c => Assert.Contains(w.Dcs.Texts, t => t.Text == c.Text));

        // What the app prepares at mission start (plus the callsign as the pilot spawns) covers every clip of every call.
        var labels = w.Layout.Labels!;
        var prepared = Phraseology.Samples("Al Dhafra", w.Layout.Runways, labels.Taxiways.Select(t => t.Name).Distinct(), labels.Areas.Select(a => a.Name), labels.Frequencies, w.Weather, labels.Departure)
            .Concat(Phraseology.CallsignSamples("Viper 1-1"))
            .SelectMany(SpokenText.Units).Select(u => u.Text).ToHashSet();
        var missing = calls.SelectMany(c => SpokenText.Units(c.Text)).Select(u => u.Text).Where(u => !prepared.Contains(u)).Distinct().ToList();
        Assert.Empty(missing);
    }
}

file static class MenuText
{
    /// <summary>"Ground: Request taxi | Tower: Say again | …": the menu as a pilot reads it (submenu, then entry).</summary>
    public static string Describe(IReadOnlyList<MenuEntry> menu) =>
        string.Join(" | ", menu.Where(e => e.Path[^1] != "Debug").GroupBy(e => e.Path[^1]).Select(g => $"{g.Key}: {string.Join(", ", g.Select(e => e.Label))}"));
}

/// <summary>Records what controllers would say on the radio.</summary>
internal sealed class FakeRadio : IAtcRadio
{
    public List<RadioCall> Calls { get; } = [];
    public List<string> Prepared { get; } = [];

    public void Transmit(RadioCall call) => Calls.Add(call);
    public void Prepare(IEnumerable<string> texts, bool soon = false) => Prepared.AddRange(texts);
}
