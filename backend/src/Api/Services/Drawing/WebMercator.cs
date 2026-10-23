using NetTopologySuite.Geometries;

namespace Api.Services.Drawing;

/// <summary>
/// EPSG:3857 ↔ EPSG:4326 for the editing transactions (spec 8.6: drawing in 3857, storage in WGS84). EPSG:3857 is
/// defined as the spherical Mercator of WGS84 lon/lat, so these closed formulas are the exact transformation. Only
/// coordinates are converted here – nothing metric is ever computed from 3857 (spec 7.3).
/// </summary>
public static class WebMercator
{
    private const double R = 6378137;
    private const double MaxLatitude = 85.0511287798066;

    public static Geometry ToWgs84(Geometry geometry) => Transform(geometry, 4326, (x, y) =>
        (x / R * 180 / Math.PI, (2 * Math.Atan(Math.Exp(y / R)) - Math.PI / 2) * 180 / Math.PI));

    public static Geometry FromWgs84(Geometry geometry) => Transform(geometry, 3857, (lon, lat) =>
    {
        var clamped = Math.Clamp(lat, -MaxLatitude, MaxLatitude);
        return (lon * Math.PI / 180 * R, Math.Log(Math.Tan(Math.PI / 4 + clamped * Math.PI / 360)) * R);
    });

    public static Envelope EnvelopeToWgs84(Envelope e)
    {
        var lowerLeft = ToWgs84(new Point(e.MinX, e.MinY)).Coordinate;
        var upperRight = ToWgs84(new Point(e.MaxX, e.MaxY)).Coordinate;
        return new Envelope(lowerLeft.X, upperRight.X, lowerLeft.Y, upperRight.Y);
    }

    private static Geometry Transform(Geometry geometry, int srid, Func<double, double, (double, double)> convert)
    {
        var copy = geometry.Copy();
        copy.Apply(new CoordinateConverter(convert));
        copy.GeometryChanged();
        copy.SRID = srid;
        return copy;
    }

    private sealed class CoordinateConverter(Func<double, double, (double, double)> convert) : ICoordinateSequenceFilter
    {
        public void Filter(CoordinateSequence seq, int i)
        {
            var (x, y) = convert(seq.GetX(i), seq.GetY(i));
            seq.SetX(i, x);
            seq.SetY(i, y);
        }

        public bool Done => false;
        public bool GeometryChanged => true;
    }
}
