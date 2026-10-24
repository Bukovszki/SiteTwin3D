namespace Api.Services.Locations;

/// <summary>A box (spec 8.2) with its centre: WGS84, metres above the site base (level elevation + z_rel_m).</summary>
public record BoxShape(double Lon, double Lat, double ElevationM, double SizeX, double SizeY, double SizeZ, double HeadingDeg)
{
    public double Volume => SizeX * SizeY * SizeZ;
}

/// <summary>
/// Containment of a point in a turned box (spec 9.3), in real metres: the point is taken into the east-north-up plane
/// of the box centre (radii of curvature of the WGS84 ellipsoid; exact enough over a building), the turn is undone
/// and the axis-aligned test applied with a small margin – the same rule as the 3D view (box-math.ts).
/// </summary>
public static class BoxContainment
{
    public const double Margin = 0.05;
    private const double A = 6378137.0;
    private const double E2 = (1 / 298.257223563) * (2 - 1 / 298.257223563);

    public static bool Contains(BoxShape box, double lon, double lat, double elevationM)
    {
        var rad = Math.PI / 180;
        var s = Math.Sin(box.Lat * rad);
        var w = Math.Sqrt(1 - E2 * s * s);
        var east = (lon - box.Lon) * rad * A / w * Math.Cos(box.Lat * rad);
        var north = (lat - box.Lat) * rad * A * (1 - E2) / (w * w * w);
        var h = box.HeadingDeg * rad;
        var x = east * Math.Cos(h) - north * Math.Sin(h);
        var y = east * Math.Sin(h) + north * Math.Cos(h);
        return Math.Abs(x) <= box.SizeX / 2 + Margin && Math.Abs(y) <= box.SizeY / 2 + Margin
            && Math.Abs(elevationM - box.ElevationM) <= box.SizeZ / 2 + Margin;
    }
}
