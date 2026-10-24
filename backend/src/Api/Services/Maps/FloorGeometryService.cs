using Api.Data;
using Api.Model;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Api.Services.Maps;

/// <summary>A vector element of a level; <see cref="Source"/>: draw (own drawing layer) | floorplan (MapManager WFS).</summary>
public record FloorElement(string Source, string Layer, Geometry Geometry);

/// <param name="Partial">The MapManager could not be reached: only the own drawing layers are included.</param>
public record FloorGeometry(List<FloorElement> Elements, bool Partial);

/// <summary>
/// Vector geometry of a level in WGS84 (spec 9.3, 10): the snap targets of the client. The level's own drawing layers,
/// and the features of its MapManager floorplans read through WFS by this backend with the service token (the
/// browser never reaches MapManager). External WMS floorplans are raster only and give nothing to snap to.
/// </summary>
public class FloorGeometryService(AppDbContext db, MapManagerClient mapManager, ILogger<FloorGeometryService> logger)
{
    /// <summary>Per floorplan layer: a level is a building storey, not a city.</summary>
    private const int MaxFeatures = 5000;

    public async Task<FloorGeometry> GetAsync(long siteId, long levelId, CancellationToken ct)
    {
        var level = await db.Nodes.AsNoTracking().Include(n => n.Type).FirstOrDefaultAsync(n => n.Id == levelId, ct);
        if (level is null || level.SiteId != siteId || !level.Type.IsLevel)
            throw AppException.NotFound("level.not_found", $"Level {levelId} not found on site {siteId}.");

        var elements = await db.DrawGeometries.AsNoTracking()
            .Join(db.DrawLayers.Where(l => l.LevelId == levelId), g => g.LayerId, l => l.Id, (g, l) => new { l.Name, g.Geom })
            .Select(x => new FloorElement("draw", x.Name, x.Geom))
            .ToListAsync(ct);

        var partial = false;
        foreach (var floorplan in await db.Floorplans.AsNoTracking().Where(f => f.LevelId == levelId && f.Source == LayerSources.MapManager)
                     .OrderBy(f => f.SortOrder).ToListAsync(ct))
        {
            try
            {
                elements.AddRange((await mapManager.GeometriesAsync(floorplan.Layer, floorplan.CqlFilter, MaxFeatures, ct))
                    .Select(g => new FloorElement("floorplan", floorplan.Name, g)));
            }
            catch (AppException e) when (e.Status == StatusCodes.Status503ServiceUnavailable)
            {
                logger.LogWarning("Floorplan {Floorplan} geometry unavailable: {Message}", floorplan.Id, e.Message);
                partial = true;
            }
        }
        return new FloorGeometry(elements, partial);
    }
}
