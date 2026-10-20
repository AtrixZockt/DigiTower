using DcsAtc.Airfields;
using DcsAtc.Airfields.Taxi;

namespace DcsAtc.Atc;

public enum TrafficKind
{
    /// <summary>Paths cross (a junction): the one giving way holds short of the other's taxiway.</summary>
    Crossing,
    /// <summary>Opposite directions on the same taxiway: the one giving way holds before the shared stretch.</summary>
    HeadOn,
    /// <summary>Catching up with an aircraft ahead (moving or standing): follow it.</summary>
    Behind,
}

/// <summary>
/// An aircraft on an airfield's taxiways and where it is expected to go: a cleared flight along its route, anyone
/// else (AI, players without a clearance) along the taxiways straight ahead. <see cref="Path"/> starts at the
/// aircraft; a standing aircraft has a speed below <see cref="GroundTraffic.Moving"/>. <see cref="Group"/>: its DCS group.
/// </summary>
public sealed record GroundMover(string UnitName, string Type, Vec2 Position, Vec2 Direction, double Speed, IReadOnlyList<Vec2> Path, TaxiClearance? Clearance, int? Group = null)
{
    public bool IsCleared => Clearance is not null;
    public bool IsMoving => Speed >= GroundTraffic.Moving && Path.Count >= 2;

    /// <summary>Where it is after <paramref name="t"/> seconds (it stops at the end of its path).</summary>
    public Vec2 At(double t) => IsMoving ? Polyline.PointAt(Path, Speed * t) : Position;

    /// <summary>Its direction of travel after <paramref name="t"/> seconds.</summary>
    public Vec2 DirectionAt(double t)
    {
        if (!IsMoving) return Direction;
        double s = Speed * t;
        var d = Polyline.PointAt(Path, s + 4) - Polyline.PointAt(Path, Math.Max(0, s - 4));
        return d.Length > 0.1 ? d.Normalized : Direction;
    }
}

/// <summary>Two aircraft predicted to come within <see cref="GroundTraffic.Separation"/> of each other.</summary>
public sealed record TrafficConflict(GroundMover A, GroundMover B, TrafficKind Kind, double Time);

/// <summary>
/// What the aircraft giving way is told: hold at <see cref="HoldAlong"/> metres along its path (0: hold position now),
/// short of <see cref="HoldShortOf"/> if that is another taxiway with a name; for <see cref="TrafficKind.Behind"/> only
/// "follow".
/// </summary>
public sealed record TrafficPlan(GroundMover Yields, GroundMover Other, TrafficKind Kind, double Time, double HoldAlong, Vec2 HoldPoint, string? HoldShortOf, string? OtherTaxiway)
{
    public bool HoldNow => Kind != TrafficKind.Behind && HoldAlong <= 0;
}

/// <summary>
/// Ground traffic conflicts (M7): predicts where taxiing aircraft go in the next <see cref="Horizon"/> seconds and
/// decides who gives way. AI and players without a clearance cannot be told: cleared players give way to them.
/// Between two cleared flights departures go first, then the rules of the air (ICAO Annex 2 3.2.2, SERA.3210):
/// converging, the one with the other on its right gives way; catching up, the one behind follows.
/// </summary>
public static class GroundTraffic
{
    /// <summary>Closer than this counts as a conflict (metres): a wingspan and a margin.</summary>
    public const double Separation = 45;

    /// <summary>How far ahead in time conflicts are predicted (seconds).</summary>
    public const double Horizon = 40;

    /// <summary>A standing aircraft within this distance of a path is in the way (metres).</summary>
    public const double OnPath = 15;

    /// <summary>Slower than this counts as standing (m/s).</summary>
    public const double Moving = 1.0;

    /// <summary>The speed a held aircraft is assumed to resume at (m/s, about 14 knots).</summary>
    public const double ResumeSpeed = 7;

    /// <summary>Further than this from any taxiway, an aircraft is assumed to go straight ahead (metres).</summary>
    private const double OffNetwork = 25;

    /// <summary>A continuation turning more than this at a junction ends the predicted path (degrees).</summary>
    private const double MaxTurn = 75;

    /// <summary>The part of a cleared route ahead of <paramref name="position"/>, at most <paramref name="distance"/> long.</summary>
    public static IReadOnlyList<Vec2> RouteAhead(IReadOnlyList<Vec2> route, Vec2 position, double distance)
    {
        var (point, along, _) = Polyline.Project(route, position);
        double length = Polyline.Length(route);
        var ahead = Polyline.Slice(route, along, Math.Min(length, along + distance));
        ahead[0] = point;
        if (position.DistanceTo(point) > 1) ahead.Insert(0, position);
        return ahead;
    }

    /// <summary>
    /// Where an aircraft without a clearance goes: along the taxiway it is on, in its direction of travel, and at each
    /// junction the straightest way on (it ends at dead ends and where only sharp turns are left). Off the taxiways
    /// (aprons, grass) straight ahead.
    /// </summary>
    public static IReadOnlyList<Vec2> NetworkAhead(TaxiGraph graph, Vec2 position, Vec2 direction, double distance)
    {
        if (direction.Length < 1e-6) return [position];
        direction = direction.Normalized;
        var (link, point, along, offset) = graph.ProjectOntoTaxiway(position);
        if (link is null || offset > OffNetwork) return [position, position + direction * distance];

        var tangent = Polyline.PointAt(link.Points, along + 2) - Polyline.PointAt(link.Points, Math.Max(0, along - 2));
        bool towardsB = tangent.Dot(direction) >= 0;
        var path = new List<Vec2> { position, point };
        path.AddRange(towardsB ? Polyline.Slice(link.Points, along, link.Length).Skip(1) : Polyline.Slice(link.Points, 0, along).AsEnumerable().Reverse().Skip(1));
        int vertex = towardsB ? link.B : link.A;
        var arriving = towardsB ? link.DirArriving(link.B) : link.DirArriving(link.A);
        var current = link;
        double walked = Polyline.Length(path);
        for (int hops = 0; walked < distance && hops < 200; hops++)
        {
            var next = graph.LinksOf(vertex)
                .Where(l => l.Id != current.Id && l.Kind != LinkKind.Runway)
                .Select(l => (Link: l, Turn: TaxiGraph.TurnAngle(arriving, l.DirLeaving(vertex))))
                .Where(c => c.Turn <= MaxTurn)
                .OrderBy(c => c.Turn)
                .Select(c => c.Link)
                .FirstOrDefault();
            if (next is null) break;
            var points = next.PointsFrom(vertex);
            path.AddRange(points.Skip(1));
            walked += next.Length;
            vertex = next.Other(vertex);
            arriving = next.DirArriving(vertex);
            current = next;
        }
        return walked > distance ? Polyline.Slice(path, 0, distance) : path;
    }

    /// <summary>The first time within <see cref="Horizon"/> the two come closer than <see cref="Separation"/>, if they do.</summary>
    public static TrafficConflict? FirstConflict(GroundMover a, GroundMover b)
    {
        if (!a.IsMoving && !b.IsMoving) return null;
        if (!b.IsMoving) return Standing(a, b);
        if (!a.IsMoving) return Standing(b, a);

        // Already this close and moving apart: they have met (or pass side by side), nothing to solve.
        double now = a.Position.DistanceTo(b.Position);
        if (now < Separation && a.At(1).DistanceTo(b.At(1)) >= now) return null;
        for (double t = 0; t <= Horizon; t += 1)
        {
            if (a.At(t).DistanceTo(b.At(t)) < Separation)
                return new TrafficConflict(a, b, KindOf(a.DirectionAt(t), b.DirectionAt(t)), t);
        }
        return null;
    }

    /// <summary>A moving aircraft reaching a standing one on its path: catching up from behind.</summary>
    private static TrafficConflict? Standing(GroundMover moving, GroundMover standing)
    {
        var (_, along, distance) = Polyline.Project(moving.Path, standing.Position);
        if (distance > OnPath || along < 1) return null;
        double t = Math.Max(0, along - Separation) / moving.Speed;
        return t <= Horizon ? new TrafficConflict(moving, standing, TrafficKind.Behind, t) : null;
    }

    private static TrafficKind KindOf(Vec2 a, Vec2 b)
    {
        double cos = a.Dot(b);
        return cos < -0.77 ? TrafficKind.HeadOn : cos > 0.77 ? TrafficKind.Behind : TrafficKind.Crossing;
    }

    /// <summary>
    /// Who gives way, or null when nobody can be told (two aircraft without a clearance, or one without a clearance
    /// catching up with a cleared one).
    /// </summary>
    public static (GroundMover Yields, GroundMover Other)? WhoGivesWay(TrafficConflict c)
    {
        var (a, b) = (c.A, c.B);
        if (!a.IsCleared && !b.IsCleared) return null;
        if (c.Kind == TrafficKind.Behind)
        {
            var (behind, ahead) = IsBehind(a, b, c.Time) ? (a, b) : (b, a);
            return behind.IsCleared ? (behind, ahead) : null;
        }
        if (a.IsCleared != b.IsCleared) return a.IsCleared ? (a, b) : (b, a);

        // Only one of them can still stop clear (the other is already on the shared stretch or in the junction): it gives way,
        // whatever the priorities; holding the other where it is would block both.
        double ra = Room(a, b, c.Kind), rb = Room(b, a, c.Kind);
        bool aCan = ra >= HoldRoom, bCan = rb >= HoldRoom;
        if (aCan != bCan) return aCan ? (a, b) : (b, a);

        bool aOut = a.Clearance!.Departure is not null, bOut = b.Clearance!.Departure is not null;
        if (aOut != bOut) return aOut ? (b, a) : (a, b);
        if (c.Kind == TrafficKind.Crossing)
        {
            // The one that has the other on its right gives way.
            bool bOnRightOfA = (b.At(c.Time) - a.At(c.Time)).Dot(a.DirectionAt(c.Time).Left) < 0;
            return bOnRightOfA ? (a, b) : (b, a);
        }
        // Head-on: the one that reaches the shared stretch later holds before it (it has more room); a tie by key.
        if (Math.Abs(ra - rb) > 1) return ra > rb ? (a, b) : (b, a);
        return a.Clearance.Key > b.Clearance.Key ? (a, b) : (b, a);
    }

    /// <summary>Room to stop before a conflict (metres): enough for this, and an aircraft can give way.</summary>
    public const double HoldRoom = 30;

    /// <summary>How far <paramref name="y"/> can go before it reaches the shared stretch (head-on) or the other's path (crossing).</summary>
    private static double Room(GroundMover y, GroundMover o, TrafficKind kind) => kind == TrafficKind.HeadOn ? SharedStart(y, o) : ZoneStart(y, o);

    private static bool IsBehind(GroundMover a, GroundMover b, double t)
    {
        var dir = b.IsMoving ? b.DirectionAt(t) : a.DirectionAt(t);
        return (a.At(t) - b.At(t)).Dot(dir) < 0;
    }

    /// <summary>Distance along <paramref name="y"/>'s path where it joins <paramref name="o"/>'s path in the opposite direction.</summary>
    public static double SharedStart(GroundMover y, GroundMover o)
    {
        if (!y.IsMoving) return 0;
        var other = o.IsMoving ? o.Path : [o.Position, o.Position + o.Direction];
        double length = Polyline.Length(y.Path);
        for (double s = 0; s <= length; s += 5)
        {
            var p = Polyline.PointAt(y.Path, s);
            var (_, along, distance) = Polyline.Project(other, p);
            if (distance > 20) continue;
            var dirY = Polyline.PointAt(y.Path, s + 4) - Polyline.PointAt(y.Path, Math.Max(0, s - 4));
            var dirO = Polyline.PointAt(other, along + 4) - Polyline.PointAt(other, Math.Max(0, along - 4));
            if (dirY.Length > 0.1 && dirO.Length > 0.1 && dirY.Normalized.Dot(dirO.Normalized) < -0.5) return s;
        }
        return length;
    }

    /// <summary>Distance along <paramref name="y"/>'s path where it first comes within reach of <paramref name="o"/>'s path.</summary>
    public static double ZoneStart(GroundMover y, GroundMover o)
    {
        if (!y.IsMoving) return 0;
        var other = o.IsMoving ? o.Path : [o.Position];
        double length = Polyline.Length(y.Path);
        for (double s = 0; s <= length; s += 5)
            if (Polyline.Project(other, Polyline.PointAt(y.Path, s)).Distance < Separation * 0.7) return s;
        return length;
    }

    /// <summary>What to tell the aircraft giving way: where to hold and short of which taxiway.</summary>
    public static TrafficPlan Plan(TrafficConflict c, GroundMover yields, GroundMover other, Airfield layout)
    {
        string? otherTaxiway = TaxiwayAt(layout, other.Position, other.Direction);
        if (c.Kind == TrafficKind.Behind) return new TrafficPlan(yields, other, c.Kind, c.Time, -1, yields.Position, null, otherTaxiway);

        // Crossing: stop short of where the paths come within reach; head-on: before joining the other's path.
        double start = c.Kind == TrafficKind.HeadOn ? SharedStart(yields, other) : ZoneStart(yields, other);
        double back = c.Kind == TrafficKind.HeadOn ? 40 : 15;
        double hold = Math.Max(0, start - back);
        if (hold < 10) hold = 0;
        var holdPoint = hold > 0 ? Polyline.PointAt(yields.Path, hold) : yields.Position;

        // "Hold short of B": the named taxiway the other is on where the paths meet, if it is not the one we are on.
        var meet = Polyline.PointAt(yields.Path, start);
        string? ahead = c.Kind == TrafficKind.HeadOn
            ? TaxiwayAt(layout, Polyline.PointAt(yields.Path, start + 20), yields.DirectionAt(start / Math.Max(yields.Speed, 0.1)))
            : TaxiwayAt(layout, meet, other.IsMoving ? other.DirectionAt(c.Time) : other.Direction);
        string? here = TaxiwayAt(layout, holdPoint, yields.Direction);
        string? holdShortOf = hold > 0 && ahead is not null && ahead != here ? ahead : null;
        return new TrafficPlan(yields, other, c.Kind, c.Time, hold, holdPoint, holdShortOf, otherTaxiway ?? ahead);
    }

    /// <summary>The name of the taxiway at a point that runs in <paramref name="direction"/> (either way), if named.</summary>
    public static string? TaxiwayAt(Airfield layout, Vec2 p, Vec2 direction)
    {
        var dir = direction.Length > 0 ? direction.Normalized : direction;
        TaxiLink? best = null;
        double bestDistance = 25;
        foreach (var link in layout.Graph.Links)
        {
            if (link.Kind != LinkKind.Taxiway || link.IsConnector || layout.NameOf(link) is null) continue;
            var (_, along, distance) = Polyline.Project(link.Points, p);
            if (distance >= bestDistance) continue;
            var tangent = Polyline.PointAt(link.Points, along + 4) - Polyline.PointAt(link.Points, Math.Max(0, along - 4));
            if (dir.Length > 0 && tangent.Length > 0.1 && Math.Abs(tangent.Normalized.Dot(dir)) < 0.7) continue;
            (best, bestDistance) = (link, distance);
        }
        return best is null ? null : layout.NameOf(best);
    }
}
