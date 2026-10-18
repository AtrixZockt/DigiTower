using DcsAtc.Airfields;

namespace DcsAtc.Tests;

public class NavaidTests
{
    [Fact]
    public void Beacons_are_read_from_the_terrain_format()
    {
        const string lua = """
            beaconsTableFormat = 2
            beacons = {
            	{
            		display_name = _('');
            		beaconId = 'airfield4_6';
            		type = BEACON_TYPE_ILS_LOCALIZER;
            		callsign = 'LMA';
            		frequency = 108700000.000000;
            		channel = 24;
            		position = { -214278.000000, 16.000015, -171226.875000 };
            		direction = -52.100006;
            		sceneObjects = {'t:155714264'};
            		chartOffsetX = 4290.000000;
            	};
            	{
            		display_name = _('AlDhafra');
            		beaconId = 'airfield4_8';
            		type = BEACON_TYPE_VORTAC;
            		callsign = 'MA';
            		frequency = 114900000.000000;
            		channel = 96;
            		position = { -211188.156250, 16.000016, -173477.812500 };
            		direction = 37.615738;
            	};
            	{
            		display_name = _('Kish');
            		beaconId = 'world_0';
            		type = BEACON_TYPE_VOR_DME;
            		callsign = 'KIS';
            		frequency = 117400000.000000;
            		position = { 42492.121094, 24.612057, -226939.156250 };
            	};
            }
            """;
        var navaids = BeaconReader.Parse(lua);
        Assert.Equal(3, navaids.Count);
        var loc = navaids[0];
        Assert.Equal((NavaidType.IlsLocalizer, "LMA", 4), (loc.Type, loc.Callsign, loc.AirdromeId));
        Assert.Equal(108.7, loc.FrequencyMhz, 6);
        Assert.Equal(307.9, loc.Direction, 1);
        Assert.Equal(new Vec2(-214278, -171226.875), loc.Position);
        Assert.Equal("96X", navaids[1].TacanChannel);
        Assert.Null(navaids[2].AirdromeId);
    }

    [SkippableFact]
    public void Al_Dhafra_has_an_ILS_for_every_runway_end_and_the_MA_TACAN()
    {
        Skip.If(TestPaths.Terrains is null, "DCS install not available");
        string beacons = Path.Combine(TestPaths.Terrains!, "PersianGulf", "beacons.lua");
        Skip.IfNot(File.Exists(beacons), "Persian Gulf not installed");
        var layout = TestPaths.AlDhafra();
        Skip.If(layout is null, "Al Dhafra layout not available");

        var navaids = new AirfieldNavaids(BeaconReader.Read(beacons).Where(n => n.AirdromeId == 4).ToList());
        Assert.Equal(("MA", "96X"), (navaids.Tacan!.Callsign, navaids.Tacan.TacanChannel));
        var ils = layout!.Runways.SelectMany(r => new[] { r.EndA, r.EndB }.Select(e => (e.Name, Loc: navaids.LocalizerFor(r, e))))
            .ToDictionary(x => x.Name, x => x.Loc is null ? "none" : $"{x.Loc.Callsign} {x.Loc.FrequencyMhz:0.0#}");
        Assert.Equal(4, ils.Count);
        Assert.DoesNotContain("none", ils.Values);
        Assert.Equal(4, ils.Values.Distinct().Count());
    }
}
