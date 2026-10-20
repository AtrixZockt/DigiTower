using DcsAtc.Airfields;

namespace DcsAtc.Tests;

public class GeoreferenceTests
{
    [Fact]
    public void A_north_up_chart_is_fitted_on_the_two_ends_of_a_single_runway()
    {
        // A chart at 2 m per pixel, north up: pixel (px, py) is x = 1000 - 2 py (north), z = 500 + 2 px (east).
        static Vec2 Map(double px, double py) => new(1000 - 2 * py, 500 + 2 * px);
        var a = Map(100, 400);
        var b = Map(300, 150);
        var geo = new Georeference([new ControlPoint([100, 400], a.X, a.Z), new ControlPoint([300, 150], b.X, b.Z)]);

        var fit = geo.Affine();
        foreach (var (px, py) in new[] { (0.0, 0.0), (640.0, 80.0), (250.0, 900.0) })
        {
            var expected = Map(px, py);
            var got = fit.Apply(px, py);
            Assert.True(got.DistanceTo(expected) < 0.01, $"({px}, {py}): {got} instead of {expected}");
        }
    }
}
