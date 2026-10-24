using System.Text.Json;
using Api.Data;
using Api.Model;
using Api.Services.Hierarchy;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Api.Services.Anchors;

/// <summary>
/// One box of a table row. Numbers may be text (CSV, decimal comma) or numbers (XLSX). <c>East</c>/<c>North</c>: the
/// centre in the source system (EOV: Y / X; WGS84: longitude / latitude); <c>Bottom</c>: the box's underside above
/// the level floor (empty: on the floor); <c>Heading</c>: from the source grid's north, clockwise.
/// </summary>
public record BoxImportRow(string? Code, string? Level, JsonElement? East, JsonElement? North, JsonElement? Bottom, JsonElement? SizeX,
    JsonElement? SizeY, JsonElement? SizeZ, JsonElement? Heading);

/// <param name="Srs">The source system of the coordinates, EPSG:&lt;code&gt; (e.g. EPSG:23700 EOV, EPSG:4326 WGS84).</param>
public record BoxImportRequest(string Srs, List<BoxImportRow> Rows);

/// <param name="Created">New boxes.</param>
/// <param name="Updated">Existing boxes changed in place (their history is kept).</param>
/// <param name="Removed">Boxes of the imported records not in the table any more.</param>
/// <param name="Records">Records whose boxes were set.</param>
public record BoxImportResult(int Created, int Updated, int Removed, int Records);

/// <summary>
/// Bulk import of bounding boxes from a table (user decision, FR-I4): the rows of a record (by its code) replace its
/// boxes – one row for most records, several for a linear element. The centre is transformed from the source system
/// to WGS84 by PostGIS; the heading is turned by the grid convergence (grid north → true north). Existing boxes are
/// updated in place, the rest added or removed; finalized records get their history (one reason for the import).
/// All or nothing, every error at once.
/// </summary>
public partial class BoxImportService(AppDbContext db)
{
    public const int MaxRows = 5_000;
    /// <summary>A transformed centre farther than this from the site point is refused (e.g. EOV X and Y swapped).</summary>
    public const double MaxDistanceFromSiteM = 10_000;

    private record Box(int Row, Node Node, long? LevelId, double East, double North, float Bottom, float SizeX, float SizeY, float SizeZ,
        float Heading);

    private record Projected(int Ord, double Lon, double Lat, double Convergence, double Distance);

    public async Task<BoxImportResult> ImportAsync(long siteId, BoxImportRequest request, CancellationToken ct)
    {
        var site = await db.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == siteId, ct)
            ?? throw AppException.NotFound("site.not_found", $"Site {siteId} not found.");
        if (request.Rows is not { Count: > 0 })
            throw AppException.BadRequest("import.empty", "The import contains no rows.");
        if (request.Rows.Count > MaxRows)
            throw AppException.BadRequest("import.too_many_rows", $"At most {MaxRows} rows can be imported at once.");
        var srid = await SridAsync(request.Srs, ct);

        var codes = request.Rows.Select(r => r.Code?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
        var byCode = (await db.Nodes.Where(n => n.SiteId == siteId && n.Code != null && codes.Contains(n.Code)).ToListAsync(ct))
            .GroupBy(n => n.Code!).ToDictionary(g => g.Key, g => g.ToList());
        var levels = await db.Nodes.Where(n => n.SiteId == siteId && db.NodeTypes.Any(t => t.Id == n.TypeId && t.IsLevel))
            .Select(n => new { n.Id, n.Code, n.Name }).ToListAsync(ct);

        var errors = new List<ImportError>();
        var boxes = new List<Box>();
        for (var i = 0; i < request.Rows.Count; i++)
        {
            var rowNumber = i + 1;
            var row = request.Rows[i];
            var before = errors.Count;
            var code = row.Code?.Trim();
            Node? node = null;
            if (string.IsNullOrEmpty(code)) errors.Add(new(rowNumber, "import.code_required", "code", "The record's code is required."));
            else if (!byCode.TryGetValue(code, out var found)) errors.Add(new(rowNumber, "import.unknown_code", "code", $"No record with code '{code}'."));
            else if (found.Count > 1) errors.Add(new(rowNumber, "import.ambiguous_code", "code", $"Several records have code '{code}'."));
            else node = found[0];

            long? levelId = null;
            var level = row.Level?.Trim();
            if (!string.IsNullOrEmpty(level))
            {
                var matches = levels.Where(l => l.Code == level).ToList();
                if (matches.Count == 0) matches = levels.Where(l => string.Equals(l.Name, level, StringComparison.CurrentCultureIgnoreCase)).ToList();
                if (matches.Count == 1) levelId = matches[0].Id;
                else errors.Add(new(rowNumber, "import.unknown_level", "level", $"No single level with code or name '{level}'."));
            }

            var east = Number(row.East, "east", rowNumber, errors, required: true);
            var north = Number(row.North, "north", rowNumber, errors, required: true);
            var bottom = Number(row.Bottom, "bottom", rowNumber, errors, required: false) ?? 0;
            var sizeX = Size(row.SizeX, "sizeX", rowNumber, errors);
            var sizeY = Size(row.SizeY, "sizeY", rowNumber, errors);
            var sizeZ = Size(row.SizeZ, "sizeZ", rowNumber, errors);
            var heading = Number(row.Heading, "heading", rowNumber, errors, required: false) ?? 0;
            if (errors.Count > before) continue;
            boxes.Add(new Box(rowNumber, node!, levelId, east!.Value, north!.Value, (float)bottom, sizeX, sizeY, sizeZ, (float)heading));
        }

        // The centres in WGS84, the grid convergence at each and the distance from the site point (one query).
        var projected = boxes.Count == 0 ? [] : await ProjectAsync(boxes, srid, site.RefPoint, ct);
        var positions = new Dictionary<int, (Point Position, float Heading)>();
        foreach (var box in boxes)
        {
            var p = projected[boxes.IndexOf(box)];
            if (!double.IsFinite(p.Lon) || !double.IsFinite(p.Lat))
                errors.Add(new(box.Row, "import.invalid_coordinates", "east", "The coordinates cannot be transformed."));
            else if (p.Distance > MaxDistanceFromSiteM)
                errors.Add(new(box.Row, "import.far_from_site", "east",
                    $"The centre is {p.Distance / 1000:0.#} km from the site point (coordinate system or axes swapped?)."));
            else
                positions[box.Row] = (new Point(p.Lon, p.Lat) { SRID = 4326 }, Normalize(box.Heading + (float)p.Convergence));
        }
        if (errors.Count > 0)
            throw AppException.BadRequest("import.invalid", $"{errors.Count} error(s); nothing was imported.", errors.OrderBy(e => e.Row).ToList());

        // The rows of a record replace its boxes: the existing ones are updated in order, extra ones removed.
        int created = 0, updated = 0, removed = 0;
        var nodeIds = boxes.Select(b => b.Node.Id).Distinct().ToList();
        var existing = (await db.Anchors.Where(a => nodeIds.Contains(a.NodeId) && a.Kind == AnchorKinds.Region && a.RegionShape == RegionShapes.Box)
            .OrderBy(a => a.Id).ToListAsync(ct)).GroupBy(a => a.NodeId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var group in boxes.GroupBy(b => b.Node.Id))
        {
            var old = existing.GetValueOrDefault(group.Key) ?? [];
            var index = 0;
            foreach (var box in group)
            {
                var anchor = index < old.Count ? old[index] : null;
                if (anchor is null)
                {
                    anchor = new Anchor { NodeId = box.Node.Id, Kind = AnchorKinds.Region, RegionShape = RegionShapes.Box, Position = null! };
                    db.Anchors.Add(anchor);
                    created++;
                }
                else updated++;
                index++;
                var (position, heading) = positions[box.Row];
                anchor.Position = position;
                anchor.LevelId = box.LevelId;
                anchor.ZRelM = box.Bottom + box.SizeZ / 2;
                anchor.SizeX = box.SizeX;
                anchor.SizeY = box.SizeY;
                anchor.SizeZ = box.SizeZ;
                anchor.HeadingDeg = heading;
                anchor.Attrs = AnchorService.AttrsWithSource(anchor.Attrs, PositionSources.Import, trusted: true);
            }
            foreach (var extra in old.Skip(index))
            {
                db.Anchors.Remove(extra);
                removed++;
            }
        }
        await db.SaveChangesAsync(ct);
        return new BoxImportResult(created, updated, removed, nodeIds.Count);
    }

    /// <summary>The EPSG code of a known, metric or WGS84 system.</summary>
    private async Task<int> SridAsync(string? srs, CancellationToken ct)
    {
        var match = EpsgRegex().Match(srs?.Trim() ?? "");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var srid))
            throw AppException.BadRequest("import.invalid_srs", "srs: EPSG:<code>, e.g. EPSG:23700.");
        if (srid == 4326) return srid;
        var known = await db.Database.SqlQuery<bool>(
            $"SELECT EXISTS (SELECT 1 FROM spatial_ref_sys WHERE srid = {srid} AND proj4text LIKE '%+units=m%') AS \"Value\"").SingleAsync(ct);
        return known ? srid : throw AppException.BadRequest("import.invalid_srs", $"EPSG:{srid} is not a known projected (metre) system.");
    }

    private async Task<List<Projected>> ProjectAsync(List<Box> boxes, int srid, Point site, CancellationToken ct)
    {
        var xs = boxes.Select(b => b.East).ToArray();
        var ys = boxes.Select(b => b.North).ToArray();
        // Convergence: the true azimuth of the grid's north at the point (a point 10 m grid-north of it); none for WGS84.
        var rows = await db.Database.SqlQuery<Projected>($"""
            SELECT p.ord::int AS ord, ST_X(t.w) AS lon, ST_Y(t.w) AS lat,
                   CASE WHEN {srid} = 4326 THEN 0 ELSE degrees(ST_Azimuth(t.w::geography, t.n::geography)) END AS convergence,
                   ST_Distance(t.w::geography, ST_SetSRID(ST_MakePoint({site.X}, {site.Y}), 4326)::geography) AS distance
            FROM unnest({xs}, {ys}) WITH ORDINALITY AS p(x, y, ord)
            CROSS JOIN LATERAL (
                SELECT ST_Transform(ST_SetSRID(ST_MakePoint(p.x, p.y), {srid}), 4326) AS w,
                       ST_Transform(ST_SetSRID(ST_MakePoint(p.x, p.y + CASE WHEN {srid} = 4326 THEN 0.0001 ELSE 10 END), {srid}), 4326) AS n
            ) t
            ORDER BY p.ord
            """).ToListAsync(ct);
        // Convergence as a signed angle (azimuth 359° = −1°).
        return rows.Select(r => r with { Convergence = r.Convergence > 180 ? r.Convergence - 360 : r.Convergence }).ToList();
    }

    private static double? Number(JsonElement? value, string field, int row, List<ImportError> errors, bool required)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
        {
            if (required) errors.Add(new(row, "import.value_required", field, $"{field} is required."));
            return null;
        }
        if (ImportService.ToNumber(v, integer: false)?.GetValue<double>() is { } number && double.IsFinite(number)) return number;
        errors.Add(new(row, "import.invalid_value", field, $"'{v}' is not a number."));
        return null;
    }

    private static float Size(JsonElement? value, string field, int row, List<ImportError> errors)
    {
        var size = Number(value, field, row, errors, required: true);
        if (size is null) return 0;
        if (size is >= 0.05 and <= 500) return (float)size.Value;
        errors.Add(new(row, "anchor.invalid_size", field, "Box sizes: 0.05…500 m."));
        return 0;
    }

    private static float Normalize(float heading) => (float)Math.Round(((heading % 360) + 360) % 360, 2);

    [System.Text.RegularExpressions.GeneratedRegex(@"^EPSG:(\d{4,6})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EpsgRegex();
}
