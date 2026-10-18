namespace DcsAtc.Airfields;

/// <summary>A point or vector in DCS map coordinates: X = north, Z = east (metres).</summary>
public readonly record struct Vec2(double X, double Z)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Z + b.Z);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Z - b.Z);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Z * s);

    public double Length => Math.Sqrt(X * X + Z * Z);
    public double Dot(Vec2 o) => X * o.X + Z * o.Z;
    public double DistanceTo(Vec2 o) => (this - o).Length;
    public Vec2 Normalized => Length > 0 ? this * (1 / Length) : this;

    /// <summary>Unit vector for a true heading in degrees (0 = north, 90 = east).</summary>
    public static Vec2 FromHeading(double degrees) => new(Math.Cos(degrees * Math.PI / 180), Math.Sin(degrees * Math.PI / 180));

    /// <summary>True heading of this vector in degrees, 0–360.</summary>
    public double Heading => (Math.Atan2(Z, X) * 180 / Math.PI + 360) % 360;

    /// <summary>The unit vector pointing left of a direction (heading − 90°).</summary>
    public Vec2 Left => new(Z, -X);

    public static Vec2 Average(IReadOnlyCollection<Vec2> points) =>
        new(points.Average(p => p.X), points.Average(p => p.Z));

    // The generated record ToString would print Normalized and Left, which are Vec2s themselves: endless recursion.
    public override string ToString() => $"({X:0.###}, {Z:0.###})";
}

public static class Polyline
{
    public static double Length(IReadOnlyList<Vec2> points)
    {
        double length = 0;
        for (int i = 1; i < points.Count; i++) length += points[i - 1].DistanceTo(points[i]);
        return length;
    }

    /// <summary>The part of a polyline between two distances measured along it.</summary>
    public static List<Vec2> Slice(IReadOnlyList<Vec2> points, double from, double to)
    {
        var result = new List<Vec2> { PointAt(points, from) };
        double walked = 0;
        for (int i = 1; i < points.Count; i++)
        {
            walked += points[i - 1].DistanceTo(points[i]);
            if (walked > from && walked < to) result.Add(points[i]);
        }
        result.Add(PointAt(points, to));
        return result;
    }

    public static Vec2 PointAt(IReadOnlyList<Vec2> points, double distance)
    {
        if (distance <= 0) return points[0];
        double walked = 0;
        for (int i = 1; i < points.Count; i++)
        {
            double segment = points[i - 1].DistanceTo(points[i]);
            if (walked + segment >= distance)
            {
                double t = segment > 0 ? (distance - walked) / segment : 0;
                return points[i - 1] + (points[i] - points[i - 1]) * t;
            }
            walked += segment;
        }
        return points[^1];
    }

    /// <summary>
    /// Removes tiny back-and-forth wiggles: a point is dropped when the polyline turns back (more than
    /// 90°) right before or after it and one of the two segments is shorter than <paramref name="maxLength"/>.
    /// The first and last points are kept.
    /// </summary>
    public static List<Vec2> RemoveSpikes(IReadOnlyList<Vec2> points, double maxLength = 3)
    {
        var result = points.ToList();
        bool changed = true;
        while (changed && result.Count > 2)
        {
            changed = false;
            for (int i = 1; i < result.Count - 1; i++)
            {
                Vec2 into = result[i] - result[i - 1], outOf = result[i + 1] - result[i];
                bool zero = into.Length < 1e-6 || outOf.Length < 1e-6;
                if (zero || ((into.Length < maxLength || outOf.Length < maxLength) && into.Dot(outOf) < 0))
                {
                    result.RemoveAt(i);
                    changed = true;
                    i--;
                }
            }
        }
        return result;
    }

    /// <summary>Douglas–Peucker simplification: drops points closer than <paramref name="tolerance"/> metres to the simplified line.</summary>
    public static List<Vec2> Simplify(IReadOnlyList<Vec2> points, double tolerance)
    {
        if (points.Count < 3) return points.ToList();
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            var (first, last) = stack.Pop();
            double worst = 0;
            int index = -1;
            for (int i = first + 1; i < last; i++)
            {
                double d = Project([points[first], points[last]], points[i]).Distance;
                if (d > worst) (worst, index) = (d, i);
            }
            if (index >= 0 && worst > tolerance)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }
        return points.Where((_, i) => keep[i]).ToList();
    }

    /// <summary>Closest point of a polyline to <paramref name="p"/>: the point, its distance along the line, and its distance from p.</summary>
    public static (Vec2 Point, double Along, double Distance) Project(IReadOnlyList<Vec2> points, Vec2 p)
    {
        (Vec2 Point, double Along, double Distance) best = (points[0], 0, p.DistanceTo(points[0]));
        double walked = 0;
        for (int i = 1; i < points.Count; i++)
        {
            Vec2 a = points[i - 1], ab = points[i] - a;
            double len2 = ab.Dot(ab);
            double t = len2 > 0 ? Math.Clamp((p - a).Dot(ab) / len2, 0, 1) : 0;
            var q = a + ab * t;
            double d = p.DistanceTo(q);
            if (d < best.Distance) best = (q, walked + t * Math.Sqrt(len2), d);
            walked += Math.Sqrt(len2);
        }
        return best;
    }
}
