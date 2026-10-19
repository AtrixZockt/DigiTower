using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;
using DcsAtc.Atc;
using DcsAtc.Bridge;
using DcsAtc.Voice;

namespace DcsAtc.Tests;

/// <summary>Approach control, flown by simulated pilots who do what they are told.</summary>
public partial class TowerControlTests
{
    /// <summary>
    /// A pilot who flies the last heading and altitude Approach gave (3°/s turns, 2500 ft/min), orbits in the hold, and once
    /// cleared captures final and follows a 3° glide path.
    /// </summary>
    private sealed class SimPilot(string name, int group, Vec2 position, double feet, double heading)
    {
        public string Name => name;
        public int Group => group;
        public Vec2 Position { get; private set; } = position;
        public double Feet { get; private set; } = feet;
        public double Heading { get; private set; } = heading;
        public double Knots { get; private set; } = 250;
        public bool Captured { get; private set; }
        public RunwayEnd? End { get; private set; }
        /// <summary>When it was handed to Tower: where, how high, and how far beside final.</summary>
        public (double Time, double ToGo, double Beside, double Feet)? HandedOff { get; set; }
        /// <summary>When it passed 6 NM on final.</summary>
        public double? SixMiles { get; set; }
        private double _climb;

        public UnitState State => Aircraft(name, group, Position, inAir: true, agl: Feet * Ft - 16, hdg: Heading, speed: Knots * 0.5144, player: $"{name} | Sim", climb: _climb);

        public double Along => End is null ? 0 : (Position - End.Threshold).Dot(Vec2.FromHeading(End.TrueHeading));
        public double Beside => End is null ? 0 : (Position - End.Threshold).Dot(Vec2.FromHeading(End.TrueHeading + 90));

        public void Step(ApproachFlight? f, double dt)
        {
            if (f?.End is { } end && f.IsArrival) End = end;
            double targetHeading = Heading, targetFeet = Feet;
            if (f is { Phase: ApproachPhase.Holding })
            {
                targetHeading = Heading + 10;
                targetFeet = f.Altitude ?? Feet;
            }
            else if (f is not null)
            {
                if (f.Heading is int h) targetHeading = h;
                if (f.Altitude is double alt) targetFeet = alt;
            }
            bool cleared = f is { Cleared: true } || (f is null && End is not null);
            if (End is not null && cleared && Math.Abs(Beside) < 1.0 * Nm && Off(Heading, End.TrueHeading) < 50 && Along < 0) Captured = true;
            if (Captured && End is not null)
            {
                targetHeading = End.TrueHeading + Math.Clamp(-Beside / Nm * 40, -20, 20);
                targetFeet = Math.Min(targetFeet, Math.Max(50, -Along / Nm * 318 + 50));
            }
            double turn = ((targetHeading - Heading) % 360 + 540) % 360 - 180;
            Heading = ((Heading + Math.Clamp(turn, -3 * dt, 3 * dt)) % 360 + 360) % 360;
            double climb = Math.Clamp(targetFeet - Feet, -2500 / 60.0 * dt, 2500 / 60.0 * dt);
            Feet += climb;
            _climb = climb * Ft / dt;
            Knots = Captured ? 160 : End is not null && -Along < 20 * Nm ? 210 : 250;
            Position += Vec2.FromHeading(Heading) * (Knots * 0.5144 * dt);
        }

        private static double Off(double a, double b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);
    }

    /// <summary>Flies the pilots with Approach (and Tower once handed over) until all are handed to Tower or time runs out.</summary>
    private static async Task FlyAsync(World w, IReadOnlyList<SimPilot> pilots, double maxSeconds = 3600)
    {
        for (double t = 0; t < maxSeconds && pilots.Any(p => p.HandedOff is null || p.SixMiles is null); t += 1)
        {
            w.Clock.Now = w.Clock.Now.AddSeconds(1);
            foreach (var pilot in pilots)
            {
                var f = w.Approach.Flights.Values.FirstOrDefault(x => x.UnitName == pilot.Name);
                bool wasClient = pilot.HandedOff is null && f is null && w.Approach.HandedToTower.ContainsKey(pilot.Group);
                pilot.Step(f, 1);
                w.Put(pilot.State);
                if (wasClient)
                    pilot.HandedOff = (t, -pilot.Along / Nm, Math.Abs(pilot.Beside) / Nm, pilot.Feet);
                if (pilot.End is not null && pilot.SixMiles is null && -pilot.Along <= 6 * Nm && Math.Abs(pilot.Beside) < Nm) pilot.SixMiles = t;
            }
            await w.Approach.OnUnitsAsync(w.Units);
        }
    }

    /// <summary>Everyone went to Tower established on final: on the centerline, well out.</summary>
    private static void AssertEstablished(IEnumerable<SimPilot> pilots) =>
        Assert.All(pilots, p => Assert.True(p.HandedOff!.Value.Beside < 1 && p.HandedOff.Value.ToGo >= 5, $"{p.Name}: {p.HandedOff}"));

    private static Vec2 FromField(World w, double bearing, double miles) => w.Rwy31R.Center + Vec2.FromHeading(bearing) * (miles * Nm);

    private static List<string> ApproachCalls(World w, string callsign) =>
        w.Radio.Calls.Where(c => c.Station == "Al Dhafra Approach" && c.Text.StartsWith(callsign + ",")).Select(c => c.Text).ToList();

    [SkippableFact]
    public async Task Ils_arrival_from_behind_the_runway_is_vectored_around_onto_final()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        // 40 NM northwest at FL200, heading for the field: runway 31 lands to the northwest, so it must go around.
        var pilot = new SimPilot("Viper 1-1", 1, FromField(w!, 308, 40), 20000, 128);
        w.Put(pilot.State);
        await w.Approach.RequestApproachAsync(w.Units["Viper 1-1"], w.Units, ApproachType.Ils);
        var first = w.Approach.Flights[1];
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(w.LastText,
            @"^Viper 1-1, Al Dhafra Approach, radar contact, \d+ miles northwest of Al Dhafra\. Descend and maintain 6000 feet, QNH 1013, altimeter 29\.92\. " +
            @"Expect ILS approach runway 31[LR], localizer 1\d\d\.\d+\. (Turn (left|right)|Fly) heading \d{3}\.$"), w.LastText);
        Assert.Equal(VectorLeg.Join, first.Leg);

        await FlyAsync(w, [pilot]);
        var calls = ApproachCalls(w, "Viper 1-1");
        Assert.NotNull(pilot.HandedOff);
        var (_, toGo, beside, feet) = pilot.HandedOff!.Value;
        Assert.InRange(toGo, 4, 9);
        Assert.InRange(beside, 0, 1);
        Assert.InRange(feet, 2000, 3500);
        Assert.Contains(calls, c => c.Contains("descend and maintain 4000 feet"));
        Assert.Contains(calls, c => c.Contains("descend and maintain 3000 feet"));
        Assert.Contains(calls, c => System.Text.RegularExpressions.Regex.IsMatch(c,
            @"miles from touchdown, turn (left|right) heading \d{3}, maintain 3000 feet until established on the localizer, cleared ILS approach runway 31[LR]\."));
        Assert.Equal("Viper 1-1, contact Tower on 270.0.", calls[^1]);
        Assert.DoesNotContain(calls, c => c.Contains("check"));
    }

    [SkippableFact]
    public async Task Arrival_in_front_of_the_runway_goes_straight_onto_final()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        // 25 NM southeast of the field, on the extended centerline of 31R, heading in.
        var pilot = new SimPilot("Viper 1-1", 1, w!.End31R.Threshold - Vec2.FromHeading(w.End31R.TrueHeading) * (25 * Nm), 8000, 308);
        w.Put(pilot.State);
        await w.Approach.RequestApproachAsync(w.Units["Viper 1-1"], w.Units, ApproachType.Ils);
        Assert.Equal(VectorLeg.Final, w.Approach.Flights[1].Leg);
        Assert.Matches(@"Expect ILS approach runway 31R, localizer 1\d\d\.\d+\. Fly heading 310\.$", w.LastText);

        await FlyAsync(w, [pilot], 900);
        var calls = ApproachCalls(w, "Viper 1-1");
        Assert.NotNull(pilot.HandedOff);
        Assert.DoesNotContain(calls, c => c.Contains("downwind"));
        Assert.Contains(calls, c => c.Contains("cleared ILS approach runway 31R"));
    }

    [SkippableFact]
    public async Task Three_arrivals_at_once_are_spaced_five_miles_apart_on_final()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        // All to 31R (east of the centerline line-up: the right-hand runway is the closer one), from three directions.
        SimPilot[] pilots =
        [
            new("Viper 1-1", 1, FromField(w!, 160, 35), 15000, 340),
            new("Hornet 1-1", 2, FromField(w, 60, 30), 12000, 240),
            new("Eagle 1-1", 3, FromField(w, 100, 40), 18000, 280),
        ];
        foreach (var p in pilots)
        {
            w.Put(p.State);
            await w.Approach.RequestApproachAsync(w.Units[p.Name], w.Units, ApproachType.Ils);
        }
        Assert.All(w.Approach.Flights.Values, f => Assert.Equal(ApproachPhase.Vectoring, f.Phase));

        await FlyAsync(w, pilots);
        Assert.All(pilots, p => Assert.NotNull(p.HandedOff));
        AssertEstablished(pilots);
        foreach (var group in pilots.GroupBy(p => p.End!.Name))
        {
            var times = group.Select(p => p.SixMiles!.Value).Order().ToList();
            // 5 NM at 160 knots is 112 s.
            for (int i = 1; i < times.Count; i++)
                Assert.True(times[i] - times[i - 1] >= 90, $"runway {group.Key}: {string.Join(", ", times)}\n" +
                    string.Join("\n", w.Radio.Calls.Where(c => c.Station.EndsWith("Approach")).Select(c => c.Text)));
        }
    }

    [SkippableFact]
    public async Task Arrivals_beyond_three_hold_over_the_TACAN_and_are_released_in_turn()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        var pilots = Enumerable.Range(1, 5).Select(i => new SimPilot($"Viper {i}-1", i, FromField(w!, 100 + i * 25, 30 + i * 3), 15000, 280 + i * 25)).ToList();
        foreach (var p in pilots)
        {
            w!.Put(p.State);
            await w.Approach.RequestApproachAsync(w.Units[p.Name], w.Units, ApproachType.Ils);
        }
        Assert.Matches(@"^Viper 4-1, Al Dhafra Approach, radar contact, .*\. Hold over the Al Dhafra TACAN, right turns, maintain 7000 feet, QNH 1013, altimeter 29\.92\. Expect further clearance in 5 minutes\.$",
            ApproachCalls(w!, "Viper 4-1")[0]);
        Assert.Contains("maintain 8000 feet", ApproachCalls(w!, "Viper 5-1")[0]);

        await FlyAsync(w!, pilots, 5400);
        Assert.All(pilots, p => Assert.NotNull(p.HandedOff));
        AssertEstablished(pilots);
        Assert.Contains(ApproachCalls(w!, "Viper 4-1"), c => c.StartsWith("Viper 4-1, leave the hold, "));
        Assert.True(pilots[3].HandedOff!.Value.Time < pilots[4].HandedOff!.Value.Time, "the lower holder goes first");
    }

    [SkippableFact]
    public async Task Tacan_and_visual_approaches()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        var tacan = new SimPilot("Viper 1-1", 1, w!.End31R.Threshold - Vec2.FromHeading(w.End31R.TrueHeading) * (25 * Nm), 8000, 308);
        w.Put(tacan.State);
        await w.Approach.RequestApproachAsync(w.Units["Viper 1-1"], w.Units, ApproachType.Tacan);
        Assert.Contains("Expect TACAN approach runway 31R, TACAN channel 96X.", w.LastText);
        await FlyAsync(w, [tacan], 900);
        Assert.Contains(ApproachCalls(w, "Viper 1-1"), c => c.Contains("until established on final, cleared TACAN approach runway 31R."));

        var visual = new SimPilot("Hornet 1-1", 2, FromField(w, 200, 20), 6000, 20);
        w.Put(visual.State);
        await w.Approach.RequestApproachAsync(w.Units["Hornet 1-1"], w.Units, ApproachType.Visual);
        Assert.Contains("report field in sight.", w.LastText);
        await w.Approach.FieldInSightAsync(w.Units["Hornet 1-1"], w.Units);
        Assert.Matches(@"^Hornet 1-1, cleared visual approach runway 31[LR], contact Tower on 270\.0\.$", w.LastText);
        Assert.False(w.Approach.Flights.ContainsKey(2));
    }

    [SkippableFact]
    public async Task Overhead_arrival_goes_to_initial_then_Tower_approves_the_break()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        // From the southeast, flying to the initial point by itself.
        var start = w!.End31R.Threshold - Vec2.FromHeading(w.End31R.TrueHeading) * (35 * Nm);
        var pilot = new SimPilot("Viper 1-1", 1, start, 10000, 308);
        w.Put(pilot.State);
        await w.Approach.RequestApproachAsync(w.Units["Viper 1-1"], w.Units, ApproachType.Overhead);
        Assert.Contains("Descend and maintain 6000 feet", w.LastText);
        Assert.EndsWith("Expect overhead runway 31R, proceed to initial.", w.LastText);
        await FlyAsync(w, [pilot], 900);
        var calls = ApproachCalls(w, "Viper 1-1");
        Assert.Contains("Viper 1-1, descend and maintain 1500 feet, proceed to initial runway 31R.", calls);
        Assert.Equal("Viper 1-1, contact Tower on 270.0, report initial.", calls[^1]);

        // At the initial point: break approved; on the downwind: cleared to land.
        w.Put(Viper(w.OnFinal31R(5 * Nm), inAir: true, agl: 1500 * Ft, hdg: 308, speed: 150));
        await w.Tower.ReportInitialAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal("Viper 1-1, Al Dhafra Tower, runway 31R, wind 310 degrees, 10 knots, break approved.", w.LastText);
        w.Put(Viper(w.Rwy31R.Center + w.Rwy31R.Axis.Left * (-1.5 * Nm), inAir: true, agl: 1500 * Ft, hdg: 128, speed: 120));
        await w.Tower.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, runway 31R, wind 310 degrees, 10 knots, cleared to land.", w.LastText);
    }

    [SkippableFact]
    public async Task Departure_is_held_below_an_arrival_then_released_and_its_radar_service_ends_at_40_miles()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        // An arrival being vectored at 6000 feet, 8 NM northwest; a departure climbing out below it.
        var arrival = w!.Put(Aircraft("Hornet 1-1", 2, FromField(w, 300, 8), inAir: true, agl: 6000 * Ft - 16, hdg: 130, speed: 120, player: "Hornet 1-1 | B"));
        await w.Approach.RequestApproachAsync(arrival, w.Units, ApproachType.Ils);
        w.Put(Viper(FromField(w, 308, 3), inAir: true, agl: 2500 * Ft - 16, hdg: 308, speed: 150, climb: 20));
        await w.Approach.CheckInAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Matches(@"^Viper 1-1, Al Dhafra Approach, radar contact, \d miles northwest of Al Dhafra\. Maintain 5000 feet until advised\.$", w.LastText);

        // Climbing through 5000: check altitude. The arrival far away: released.
        w.Clock.Now = w.Clock.Now.AddMinutes(1);
        w.Put(Viper(FromField(w, 308, 6), inAir: true, agl: 5600 * Ft - 16, hdg: 308, speed: 150, climb: 20));
        await w.Approach.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, check altitude, maintain 5000 feet.", ApproachCalls(w, "Viper 1-1")[^1]);
        w.Put(Aircraft("Hornet 1-1", 2, FromField(w, 140, 20), inAir: true, agl: 4000 * Ft - 16, hdg: 130, speed: 120, player: "Hornet 1-1 | B"));
        w.Put(Viper(FromField(w, 308, 10), inAir: true, agl: 5000 * Ft - 16, hdg: 308, speed: 150, climb: 20));
        await w.Approach.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, climb unrestricted.", ApproachCalls(w, "Viper 1-1")[^1]);

        w.Put(Viper(FromField(w, 308, 41), inAir: true, agl: 20000 * Ft, hdg: 308, speed: 250));
        await w.Approach.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, radar service terminated, frequency change approved, good day.", ApproachCalls(w, "Viper 1-1")[^1]);
        Assert.False(w.Approach.Flights.ContainsKey(1));
    }

    [SkippableFact]
    public async Task Traffic_is_called_to_aircraft_on_vectors()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        var viper = w!.Put(Viper(FromField(w, 308, 30), inAir: true, agl: 8000 * Ft, hdg: 128, speed: 150));
        await w.Approach.RequestApproachAsync(viper, w.Units, ApproachType.Ils);
        // An AI tanker 4 NM ahead, coming the other way, 1000 feet higher.
        w.Put(Aircraft("Texaco", 9, FromField(w, 308, 26), inAir: true, agl: 9000 * Ft, hdg: 308, speed: 140));
        await w.Approach.OnUnitsAsync(w.Units);
        Assert.Equal("Viper 1-1, traffic, 12 o'clock, 4 miles, opposite direction, 1000 feet above.", ApproachCalls(w, "Viper 1-1")[^1]);

        // Not again right away.
        int count = ApproachCalls(w, "Viper 1-1").Count;
        await w.Approach.OnUnitsAsync(w.Units);
        Assert.Equal(count, ApproachCalls(w, "Viper 1-1").Count);
    }

    [SkippableFact]
    public async Task Every_clip_Approach_says_is_prepared_in_advance()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        // Five arrivals (two hold), a TACAN, an overhead and a visual one; a departure below an arrival; traffic.
        var pilots = Enumerable.Range(1, 5).Select(i => new SimPilot($"Viper {i}-1", i, FromField(w!, 100 + i * 25, 30 + i * 3), 15000, 280 + i * 25)).ToList();
        foreach (var p in pilots)
        {
            w!.Put(p.State);
            await w.Approach.RequestApproachAsync(w.Units[p.Name], w.Units, ApproachType.Ils);
        }
        await FlyAsync(w!, pilots, 5400);
        w!.Units.Clear();
        var tacan = new SimPilot("Hornet 1-1", 11, FromField(w, 300, 30), 9000, 128);
        var overhead = new SimPilot("Eagle 1-1", 12, w.End31R.Threshold - Vec2.FromHeading(w.End31R.TrueHeading) * (35 * Nm), 10000, 308);
        w.Put(tacan.State);
        w.Put(overhead.State);
        await w.Approach.RequestApproachAsync(w.Units["Hornet 1-1"], w.Units, ApproachType.Tacan);
        await w.Approach.RequestApproachAsync(w.Units["Eagle 1-1"], w.Units, ApproachType.Overhead);
        await FlyAsync(w, [tacan, overhead]);
        w.Units.Clear();
        var visual = w.Put(Aircraft("Strike 1-1", 13, FromField(w, 200, 20), inAir: true, agl: 6000 * Ft, hdg: 20, speed: 150, player: "Strike 1-1 | C"));
        await w.Approach.RequestApproachAsync(visual, w.Units, ApproachType.Visual);
        await w.Approach.FieldInSightAsync(visual, w.Units);
        var arrival = w.Put(Aircraft("Hornet 2-1", 14, FromField(w, 300, 8), inAir: true, agl: 6000 * Ft - 16, hdg: 130, speed: 120, player: "Hornet 2-1 | B"));
        await w.Approach.RequestApproachAsync(arrival, w.Units, ApproachType.Ils);
        w.Put(Viper(FromField(w, 308, 3), inAir: true, agl: 2500 * Ft - 16, hdg: 308, speed: 150, climb: 20));
        await w.Approach.CheckInAsync(w.Units["Viper 1-1"], w.Units);
        w.Put(Aircraft("Texaco", 9, FromField(w, 308, 6), inAir: true, agl: 3000 * Ft, hdg: 128, speed: 140));
        await w.Approach.OnUnitsAsync(w.Units);
        w.Units.Remove("Hornet 2-1");
        w.Put(Viper(FromField(w, 308, 45), inAir: true, agl: 20000 * Ft, hdg: 308, speed: 250));
        await w.Approach.OnUnitsAsync(w.Units);

        var calls = w.Radio.Calls.Where(c => c.Station == "Al Dhafra Approach").ToList();
        Assert.Contains(calls, c => c.Text.Contains("Hold over"));
        Assert.Contains(calls, c => c.Text.Contains("traffic, "));
        Assert.Contains(calls, c => c.Text.Contains("radar service terminated"));
        var labels = w.Layout.Labels!;
        var callsigns = new[] { "Viper 1-1", "Viper 2-1", "Viper 3-1", "Viper 4-1", "Viper 5-1", "Hornet 1-1", "Eagle 1-1", "Strike 1-1", "Hornet 2-1" };
        var prepared = Phraseology.ApproachSamples("Al Dhafra", w.Layout.Runways, w.Navaids, w.Catalog.StationsFor(w.Catalog.Airbases[0]), w.Weather, labels.Approach)
            .Concat(callsigns.SelectMany(Phraseology.CallsignSamples))
            .SelectMany(SpokenText.Units).Select(u => u.Text).ToHashSet();
        var missing = calls.SelectMany(c => SpokenText.Units(c.Text)).Select(u => u.Text).Where(u => !prepared.Contains(u)).Distinct().ToList();
        Assert.True(missing.Count == 0, string.Join(" | ", missing));
    }

    [SkippableFact]
    public async Task A_departure_in_radar_service_can_ask_for_an_approach_to_come_back()
    {
        var w = await CreateAsync();
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");

        w!.Put(Viper(FromField(w, 308, 8), inAir: true, agl: 6000 * Ft, hdg: 308, speed: 200));
        await w.Approach.CheckInAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal(ApproachPhase.RadarService, w.Approach.Flights[1].Phase);

        await w.Approach.RequestApproachAsync(w.Units["Viper 1-1"], w.Units, ApproachType.Ils);
        var f = w.Approach.Flights[1];
        Assert.Equal((ApproachPhase.Vectoring, ApproachType.Ils), (f.Phase, f.Type));
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(w.LastText,
            @"^Viper 1-1, roger\. Maintain 6000 feet, QNH 1013, altimeter 29\.92\. Expect ILS approach runway 31[LR], localizer 1\d\d\.\d+\. (Turn (left|right)|Fly) heading \d{3}\.$"), w.LastText);
    }

    [SkippableFact]
    public async Task Tower_expects_the_arrival_Approach_hands_over_and_clears_it_with_or_without_a_check_in()
    {
        foreach (bool checkIn in new[] { true, false })
        {
            var w = await CreateAsync();
            Skip.If(w is null, "DCS install or Al Dhafra dump not available");
            var pilot = new SimPilot("Viper 1-1", 1, w!.End31R.Threshold - Vec2.FromHeading(w.End31R.TrueHeading) * (25 * Nm), 8000, 308);
            w.Put(pilot.State);
            await w.Approach.RequestApproachAsync(w.Units["Viper 1-1"], w.Units, ApproachType.Ils);
            await FlyAsync(w, [pilot], 900);

            // "Contact Tower": Al Dhafra Tower expects Viper 1-1 for 31R; its menu offers the check-in.
            var expected = w.Tower.Flights[1];
            Assert.Equal(("Al Dhafra AFB", "31R", true), (expected.Airfield.Airbase.Name, expected.End.Name, expected.AwaitingCheckIn));
            Assert.Contains("Al Dhafra: Check in (Tower)", MenuText.Describe(await w.Menu.ForAsync(w.Units["Viper 1-1"], w.Units)));

            w.Put(Viper(w.OnFinal31R(7 * Nm), inAir: true, agl: 2200 * Ft, hdg: 308, speed: 80));
            if (checkIn)
            {
                // Checking in while the runway is occupied: continue; cleared once it is free.
                w.Put(Aircraft("Hornet 1-1", 2, w.OnRunway31R(1500)));
                await w.Tower.RequestLandingAsync(w.Units["Viper 1-1"], w.Units);
                Assert.Equal("Viper 1-1, Al Dhafra Tower, runway 31R, continue approach, wind 310 degrees, 10 knots.", w.LastText);
                w.Units.Remove("Hornet 1-1");
            }
            w.Put(Viper(w.OnFinal31R(5 * Nm), inAir: true, agl: 1600 * Ft, hdg: 308, speed: 80));
            await w.Tower.OnUnitsAsync(w.Units);
            Assert.Equal(checkIn
                ? "Viper 1-1, runway 31R, wind 310 degrees, 10 knots, cleared to land."
                : "Viper 1-1, Al Dhafra Tower, runway 31R, wind 310 degrees, 10 knots, cleared to land.", w.LastText);

            // Checking in when the runway is free gets the clearance in one call.
            if (!checkIn) continue;
            var w2 = (await CreateAsync())!;
            var p2 = new SimPilot("Viper 1-1", 1, w2.End31R.Threshold - Vec2.FromHeading(w2.End31R.TrueHeading) * (25 * Nm), 8000, 308);
            w2.Put(p2.State);
            await w2.Approach.RequestApproachAsync(w2.Units["Viper 1-1"], w2.Units, ApproachType.Ils);
            await FlyAsync(w2, [p2], 900);
            w2.Put(Viper(w2.OnFinal31R(7 * Nm), inAir: true, agl: 2200 * Ft, hdg: 308, speed: 80));
            await w2.Tower.RequestLandingAsync(w2.Units["Viper 1-1"], w2.Units);
            Assert.Equal("Viper 1-1, Al Dhafra Tower, runway 31R, wind 310 degrees, 10 knots, cleared to land.", w2.LastText);
        }
    }

    /// <summary>Abu Dhabi International (about 20 NM from Al Dhafra): a real DCS taxi network, no chart data of ours.</summary>
    private static AirbaseSummary? AbuDhabi()
    {
        if (TestPaths.Terrains is null) return null;
        string rn5 = Path.Combine(TestPaths.Terrains, "PersianGulf", "AirfieldsTaxiways", "Abu_Dhabi_International_Airport.rn5");
        if (!File.Exists(rn5)) return null;
        var points = DcsAtc.Airfields.Rn5.Rn5Reader.Read(rn5).Edges.SelectMany(e => e.Points).ToList();
        return new AirbaseSummary
        {
            Name = "Abu Dhabi International Airport", Id = 99, Category = 0, Coalition = 2,
            X = (points.Min(p => p.X) + points.Max(p => p.X)) / 2, Z = (points.Min(p => p.Z) + points.Max(p => p.Z)) / 2,
        };
    }

    [SkippableFact]
    public async Task Airfields_are_listed_nearest_first_in_a_stable_order_and_calls_go_to_the_one_picked()
    {
        var abuDhabi = AbuDhabi();
        Skip.If(abuDhabi is null, "Abu Dhabi network not available");
        var w = await CreateAsync(abuDhabi!);
        Skip.If(w is null, "DCS install or Al Dhafra dump not available");
        var dhafraField = new Vec2(-211188, -173477);
        var abuDhabiField = new Vec2(abuDhabi!.X, abuDhabi.Z);
        Vec2 Between(double fraction) => dhafraField + (abuDhabiField - dhafraField) * fraction;
        async Task<List<string>> Airfields() => (await w!.Menu.ForAsync(w.Units["Viper 1-1"], w.Units))
            .Where(e => e.Path.Count >= 2 && e.Path[1] is not ("Flight" or "Debug")).Select(e => e.Path[1]).Distinct().ToList();

        // Airborne, closer to Abu Dhabi: its layout starts loading (the menu does not wait), then both are listed, nearest first.
        w!.Put(Viper(Between(0.7), inAir: true, agl: 3000 * Ft, hdg: 128, speed: 150));
        Assert.Equal(["Al Dhafra"], await Airfields());
        await w.Catalog.GetAsync(abuDhabi);
        Assert.Equal(["Abu Dhabi", "Al Dhafra"], await Airfields());
        // Flying on towards Al Dhafra: same two airfields, so the order stays (the menu does not jump).
        w.Put(Viper(Between(0.3), inAir: true, agl: 3000 * Ft, hdg: 128, speed: 150));
        Assert.Equal(["Abu Dhabi", "Al Dhafra"], await Airfields());

        // Nearer to Abu Dhabi and heading there, Request landing without a pick goes to Al Dhafra, the airfield we have chart data for.
        w.Put(Viper(Between(0.7), inAir: true, agl: 3000 * Ft, hdg: (abuDhabiField - Between(0.7)).Heading, speed: 150));
        await w.Tower.RequestLandingAsync(w.Units["Viper 1-1"], w.Units);
        Assert.Equal("Al Dhafra AFB", w.Tower.Flights[1].Airfield.Airbase.Name);

        // Picked from the menu: Abu Dhabi (a diversion: it replaces the Al Dhafra flight).
        var entry = (await w.Menu.ForAsync(w.Units["Viper 1-1"], w.Units)).First(e => e.Path[1] == "Abu Dhabi" && e.Label == "Request landing");
        Assert.Equal((99, "tower.landing"), AtcMenu.ParseId(entry.Id));
        var field = (await w.Catalog.GetAsync(abuDhabi))!;
        await w.Tower.RequestLandingAsync(w.Units["Viper 1-1"], w.Units, field);
        Assert.Equal("Abu Dhabi International Airport", w.Tower.Flights[1].Airfield.Airbase.Name);
        Assert.StartsWith("Viper 1-1, Abu Dhabi Tower, runway ", w.LastText);
        Assert.Equal("Abu Dhabi: no ATC frequencies, text messages only.", Phraseology.FrequencyList(field));
    }
}
