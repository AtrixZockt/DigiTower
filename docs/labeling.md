# Labelling an airfield (taxiway names)

DCS's taxi networks have no taxiway names. Names come from a chart and are stored per airfield in
`data/airfields/<theatre>/<rn5 name>.labels.json`, e.g. `data/airfields/PersianGulf/Dhafra_AFB.labels.json`.
The app and `rn5tool` pick the file up automatically.

## File format

```json
{
  "airfield": "Al Dhafra AFB",                 // DCS airbase name (exact)
  "rn5": "Dhafra_AFB",                         // taxi network file name
  "source": "chart and method used",
  "georeference": { "controlPoints": [ { "chart": [183, 179], "x": -209950.1, "z": -174622.4 }, ... ] },
  "frequencies": { "atis": 270.5, "ground": 250.0, "tower": 270.0, "approach": 257.0 },
  "taxiways": [
    { "name": "A", "path": [[-209800, -174218], [-210114, -173805], ...] },   // waypoints on the DCS line
    { "name": "D2", "path": [[-210645, -173437]] }                            // one waypoint: the edge under it
  ],
  "areas": [ { "name": "Apron 1 West", "x": ..., "z": ..., "chart": [440, 268] } ]
}
```

Coordinates are DCS map coordinates (x north, z east, metres). Several waypoints name every taxiway edge
the network route between them uses; one waypoint names the edge under it. Names then extend up to
300 m along straight continuations to close small gaps (never across runways or into parking lead-ins).

A parallel taxiway's letter may be printed only once, in an island beside it where no link reaches the runway
(Al Minhad's H, Al Ain's F); the letters next to the links name the links. A taxiway the chart leaves unnamed but
Ground has to say gets a lower-case description instead: `"parallel taxiway"` at Al Bateen, said *"via the
parallel taxiway, F"*. Left unnamed, the links' names would extend along it. Name a parallel edge by edge (one
waypoint on each edge of its line, where no other edge is nearer) and list it first: a path of waypoints along it
can route through the links' curves and the aprons and name those too.

How a route is said: consecutive pieces of one taxiway are one name, pieces of other taxiways under 200 m in all
between two pieces of the same one are left out (an angled exit overlapping a parallel taxiway would otherwise give
*"A, A6, A, A3"*), and a roll of up to 100 m on a runway between two taxiways is a crossing (*"cross runway 13R"*;
DCS draws some angled crossings that way).

## Workflow

1. **Chart.** Prefer a chart made for the DCS map (community charts such as Master Arms, or ED's
   kneeboard charts); real-world charts may not match DCS's layout. Do not commit the chart image;
   record its source in `source`.
2. **Georeference.** Note the pixel position of each runway end on the chart and take the DCS
   positions from `rn5tool airfield <file.rn5>` (it prints the runway ends). Put them in
   `georeference.controlPoints`; four runway ends give residuals of a few metres if the chart is to scale.
   A single runway's two ends are enough for a chart that is not mirrored (the third point is taken at a
   right angle). Some charts draw the full pavement or the real-world runway length while DCS's runway ends
   sit inside the pavement (Sharjah 300–430 m, Dubai 500–850 m): then use the points where DCS's taxi
   network ends on the extended centreline, or the links at the very ends. Tell-tale signs in the overlay: the
   network's end loops run past the chart's runway, and links drift off the chart's links towards the ends.
3. **Overlay.** Render the chart under the network, zoomed, with a 100 m grid:
   `rn5tool overlay <file.rn5> chart.png out/zoom.svg <centre x> <centre z> 1900 0.55`
   In that view `z = minZ + (px − 60) / 0.55` and `x = maxX − (py − 100) / 0.55`, with
   `minZ = centre z − size/2` and `maxX = centre x + size/2`.
4. **Match by topology, not by label position.** Chart labels sit beside their taxiways and parts of a
   chart may be schematic (at Al Dhafra, R, J6, F and the south-east corner are drawn up to ~200 m
   off). For each chart taxiway, find the DCS line that connects the same places and put waypoints on
   that line, away from junctions. DCS networks contain overlapping edges (a long edge on top of several
   short ones): a one-waypoint path names the edge nearest to it, so put the waypoint where only the
   intended edge lies, and list the main taxiways before the links so they keep their names where edges
   overlap. Chart taxiways that DCS does not have are left out; say which in `source`.
5. **Check.** `rn5tool airfield <file.rn5> data/dcs/<airbase>.json out/named.svg` prints warnings
   (waypoints off the network, detours, conflicting names) and draws every named edge in its own colour.
   `rn5tool names <file.rn5>` lists every named edge. `rn5tool route ...` prints the resulting "via" list
   for real routes.
