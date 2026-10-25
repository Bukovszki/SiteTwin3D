using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Api.Data;
using Api.Model;
using Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Services.Models;

public class ConverterOptions
{
    public const string Section = "Converter";
    public const string ClientName = "converter";
    /// <summary>The internal converter service (compose: http://converter:8090).</summary>
    public string Url { get; set; } = "http://converter:8090";
    public bool RunWorker { get; set; } = true;
    /// <summary>A large IFC or point cloud takes minutes; past this the conversion counts as failed.</summary>
    public int TimeoutMinutes { get; set; } = 120;
    /// <summary>Attempts while the converter cannot be reached (it may be starting).</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>Wakes the conversion worker when a model is registered (else it looks every 30 s).</summary>
public class ModelConversionQueue
{
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Wake() => _signal.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await _signal.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

/// <summary>The converter's answer (converter/app.py).</summary>
/// <param name="Format">The model found in a ZIP (ifc, mesh, pointcloud, glb, 3dtiles); null for a plain upload.</param>
public record ConversionResult(string Kind, string Key, bool Segmented, string? ElementIdProperty, int? Elements, int? Points, long? SourcePoints,
    bool? Georeferenced, ConvertedPlacement? Placement, List<string>? Warnings, string? Format = null, string? Main = null);

/// <param name="AbsoluteHeightM">Height of the origin (e.g. IfcMapConversion.OrthogonalHeight), not yet relative to the site.</param>
public record ConvertedPlacement(double Lon, double Lat, double AbsoluteHeightM, double HeadingDeg, string? Crs);

/// <summary>
/// Converts uploaded IFC, mesh and point-cloud files (spec 9.2) with the converter service: the job goes to it, it
/// reads the sources from and writes the GLB / 3D Tiles to the object store (the bytes never pass the API, NFR-3).
/// The model then points to the result; an IFC becomes segmented by GlobalId, a georeferenced IFC is placed by itself.
/// Runs without a user: every organization's models, the organization filter bypassed on purpose.
/// </summary>
public class ModelConversionService(AppDbContext db, IObjectStorage storage, IHttpClientFactory http, IOptions<ConverterOptions> options,
    ILogger<ModelConversionService> logger)
{
    /// <summary>Converts the oldest waiting model; false if there was none.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var asset = await db.ModelAssets.IgnoreQueryFilters().Where(m => m.Status == ModelStatus.Converting).OrderBy(m => m.Id).FirstOrDefaultAsync(ct);
        if (asset is null) return false;
        var site = await db.Sites.IgnoreQueryFilters().AsNoTracking().FirstAsync(s => s.Id == asset.SiteId, ct);
        var attrs = JsonNode.Parse(asset.Attrs.RootElement.GetRawText())!.AsObject();
        var folder = ModelService.FolderOf(asset.StorageKey);
        var job = new
        {
            format = asset.SourceFormat,
            main = asset.StorageKey,
            keys = (await storage.ListAsync(folder, ct)).Select(o => o.Key).Where(k => !k.StartsWith(folder + "converted/")).ToList(),
            outputPrefix = folder + "converted/",
            srs = ModelService.AttrString(asset, "srs"),
        };

        HttpResponseMessage response;
        try
        {
            var client = http.CreateClient(ConverterOptions.ClientName);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(options.Value.TimeoutMinutes));
            // A buffered body with its length (the converter's plain HTTP server reads Content-Length).
            using var body = new StringContent(JsonSerializer.Serialize(job, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json");
            response = await client.PostAsync("convert", body, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The converter is not reachable (starting, restarting): try again later, then give up.
            var attempts = (attrs["conversion_attempts"]?.GetValue<int>() ?? 0) + 1;
            attrs["conversion_attempts"] = attempts;
            if (attempts >= options.Value.MaxAttempts) Fail(asset, "converter_unreachable");
            asset.Attrs = JsonSerializer.SerializeToDocument(attrs);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Model {Id}: converter not reachable ({Attempt}/{Max}): {Message}", asset.Id, attempts, options.Value.MaxAttempts, e.Message);
            return attempts < options.Value.MaxAttempts ? false : true;
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await ErrorAsync(response, ct);
            Fail(asset, error);
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Model {Id} ({Format}) could not be converted: {Error}", asset.Id, asset.SourceFormat, error);
            return true;
        }

        var result = (await response.Content.ReadFromJsonAsync<ConversionResult>(JsonSerializerOptions.Web, ct))!;
        if (!result.Key.StartsWith(folder, StringComparison.Ordinal)) throw new InvalidOperationException("The converter wrote outside the model's folder.");
        asset.Kind = result.Kind;
        asset.StorageKey = result.Key;
        // A ZIP: from now on the model counts as what was in it (e.g. an IFC stays segmented, its ids editable).
        if (result.Format is { } inner) asset.SourceFormat = inner;
        asset.Status = ModelStatus.Ready;
        asset.ProcessingError = null;
        if (result.Segmented && result.ElementIdProperty is { } property)
        {
            asset.Segmented = true;
            attrs["element_id_property"] = property;
        }
        // A georeferenced IFC places itself, unless the user gave a placement; else the site point.
        var warnings = result.Warnings ?? [];
        if (attrs["placement"] is null && result.Georeferenced != true)
        {
            var (lon, lat, height, heading) = result.Placement is { } p
                ? (p.Lon, p.Lat, p.AbsoluteHeightM - site.BaseHeightM, p.HeadingDeg)
                : (site.RefPoint.X, site.RefPoint.Y, 0.0, 0.0);
            if (result.Placement is null) warnings.Add("placed_at_site_point");
            attrs["placement"] = new JsonObject { ["lon"] = lon, ["lat"] = lat, ["height_m"] = Math.Round(height, 3), ["heading_deg"] = heading };
        }
        attrs["conversion"] = new JsonObject
        {
            ["source"] = result.Main is { } inZip ? $"{job.main[folder.Length..]} › {inZip}" : job.main[folder.Length..], ["format"] = asset.SourceFormat, ["elements"] = result.Elements, ["points"] = result.Points,
            ["source_points"] = result.SourcePoints, ["crs"] = result.Placement?.Crs ?? ModelService.AttrString(asset, "srs"),
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
        };
        attrs.Remove("conversion_attempts");
        asset.Attrs = JsonSerializer.SerializeToDocument(attrs);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Model {Id} converted from {Format}: {Kind}, {Elements} element(s), {Points} point(s)", asset.Id, asset.SourceFormat,
            result.Kind, result.Elements, result.Points);
        return true;
    }

    /// <summary>Errors and warnings are codes with details (<c>code</c> or <c>code:detail|detail</c>, as the converter's); the UI words them.</summary>
    private static void Fail(ModelAsset asset, string error)
    {
        asset.Status = ModelStatus.Failed;
        asset.ProcessingError = error.Length > 1000 ? error[..1000] : error;
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (body.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } message) return message;
        }
        catch (JsonException) { }
        return $"converter_status:{(int)response.StatusCode}";
    }
}

public class ModelConversionWorker(IServiceScopeFactory scopes, ModelConversionQueue queue, ILogger<ModelConversionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (true)
                {
                    using var scope = scopes.CreateScope();
                    if (!await scope.ServiceProvider.GetRequiredService<ModelConversionService>().ProcessNextAsync(stoppingToken)) break;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Model conversion error");
            }
            await queue.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
