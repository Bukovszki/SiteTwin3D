namespace Api.Services.Models;

/// <summary>WGS84 ellipsoid → ECEF and local east-north-up frames (spec 7: metric work happens in ENU, never in EPSG:3857).</summary>
public static class Geodesy
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257223563;
    private const double E2 = F * (2 - F);

    public static (double X, double Y, double Z) ToEcef(double lonDeg, double latDeg, double heightM)
    {
        var lon = lonDeg * Math.PI / 180;
        var lat = latDeg * Math.PI / 180;
        var n = A / Math.Sqrt(1 - E2 * Math.Sin(lat) * Math.Sin(lat));
        return ((n + heightM) * Math.Cos(lat) * Math.Cos(lon), (n + heightM) * Math.Cos(lat) * Math.Sin(lon), (n * (1 - E2) + heightM) * Math.Sin(lat));
    }

    /// <summary>
    /// Column-major 4×4 matrix (3D Tiles transform): local x = east, y = north, z = up at the point, then turned
    /// <paramref name="headingDeg"/> clockwise around up – local +y points to the heading.
    /// </summary>
    public static double[] EnuTransform(double lonDeg, double latDeg, double heightM, double headingDeg)
    {
        var lon = lonDeg * Math.PI / 180;
        var lat = latDeg * Math.PI / 180;
        double[] east = [-Math.Sin(lon), Math.Cos(lon), 0];
        double[] north = [-Math.Sin(lat) * Math.Cos(lon), -Math.Sin(lat) * Math.Sin(lon), Math.Cos(lat)];
        double[] up = [Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat)];
        var h = headingDeg * Math.PI / 180;
        var x = new double[3];
        var y = new double[3];
        for (var i = 0; i < 3; i++)
        {
            x[i] = east[i] * Math.Cos(h) - north[i] * Math.Sin(h);
            y[i] = east[i] * Math.Sin(h) + north[i] * Math.Cos(h);
        }
        var (ox, oy, oz) = ToEcef(lonDeg, latDeg, heightM);
        return [x[0], x[1], x[2], 0, y[0], y[1], y[2], 0, up[0], up[1], up[2], 0, ox, oy, oz, 1];
    }
}
