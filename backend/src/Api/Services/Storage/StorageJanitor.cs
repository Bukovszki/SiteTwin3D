using System.Text.RegularExpressions;
using Api.Data;
using Api.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Services.Storage;

public class CleanupOptions
{
    public const string Section = "Cleanup";
    /// <summary>Run the periodic cleanup in the background (off in tests).</summary>
    public bool RunWorker { get; set; } = true;
    public int IntervalHours { get; set; } = 6;
    /// <summary>
    /// An unreferenced object is deleted only this long after it was written: uploads are registered after the signed
    /// PUT, and a running upload must never lose its files.
    /// </summary>
    public int OrphanGraceHours { get; set; } = 24;
    /// <summary>Report only, delete nothing (e.g. for the first runs after an update).</summary>
    public bool DryRun { get; set; }
}

/// <param name="Scanned">Objects under org/ in the store.</param>
/// <param name="Orphans">Unreferenced objects older than the grace period (deleted unless <paramref name="DryRun"/>).</param>
/// <param name="Young">Unreferenced objects still within the grace period (possibly a running upload).</param>
/// <param name="ExpiredOriginals">Kept panorama originals whose retention ended (deleted unless dry run).</param>
public record CleanupReport(DateTime StartedAt, bool DryRun, int Scanned, int Referenced, int Orphans, long OrphanBytes, int Young,
    int ExpiredOriginals, int Deleted, List<string> Sample);

/// <summary>
/// Storage housekeeping (spec 6.2, M9): compares the object store with the database. Objects no row refers to – an
/// upload never registered, files left when a row was deleted and the store was unreachable, a removed organization –
/// are deleted after a grace period; originals of approved panoramas are deleted when the organization's retention
/// ends (spec FR-P8). Runs without a user: the organization filter is bypassed on purpose, every organization at once.
/// </summary>
public partial class StorageJanitor(AppDbContext db, IObjectStorage storage, IOptions<CleanupOptions> options, ILogger<StorageJanitor> logger)
{
    private const int SampleSize = 20;

    [GeneratedRegex(@"^org/\d+/models/[0-9a-f]{32}/")]
    private static partial Regex ModelPrefixRegex();

    public async Task<CleanupReport> RunAsync(bool dryRun, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var deleted = 0;

        // 1. Panorama originals whose retention ended (approved: the blurred display version is what viewers see).
        var expired = await db.Panoramas.IgnoreQueryFilters()
            .Where(p => p.PrivacyStatus == PrivacyStatus.Approved && p.DisplayKey != null && p.OriginalRetainedUntil != null
                        && p.OriginalRetainedUntil < started)
            .ToListAsync(ct);
        foreach (var panorama in expired)
        {
            if (dryRun) continue;
            await storage.DeleteAsync(panorama.StorageKey, ct);
            panorama.OriginalRetainedUntil = null; // approved without a retention date = original deleted
            deleted++;
        }
        if (!dryRun) await db.SaveChangesAsync(ct);

        // 2. Orphans: every key the database refers to (exact keys, and the folders of model packages).
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in await db.Panoramas.IgnoreQueryFilters().Select(p => new { p.StorageKey, p.DisplayKey }).ToListAsync(ct))
        {
            referenced.Add(p.StorageKey);
            if (p.DisplayKey is not null) referenced.Add(p.DisplayKey);
        }
        var modelFolders = (await db.ModelAssets.IgnoreQueryFilters().Select(m => m.StorageKey).ToListAsync(ct))
            .Select(k => ModelPrefixRegex().Match(k).Value).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);

        var objects = await storage.ListAsync("org/", ct);
        var limit = started.AddHours(-options.Value.OrphanGraceHours);
        bool IsReferenced(string key)
        {
            if (referenced.Contains(key)) return true;
            var folder = ModelPrefixRegex().Match(key);
            return folder.Success && modelFolders.Contains(folder.Value);
        }
        var unreferenced = objects.Where(o => !IsReferenced(o.Key)).ToList();
        var orphans = unreferenced.Where(o => o.LastModified < limit).ToList();
        if (!dryRun)
            foreach (var orphan in orphans)
            {
                await storage.DeleteAsync(orphan.Key, ct);
                deleted++;
            }

        var report = new CleanupReport(started, dryRun, objects.Count, objects.Count - unreferenced.Count, orphans.Count, orphans.Sum(o => o.Size),
            unreferenced.Count - orphans.Count, expired.Count, deleted, orphans.Take(SampleSize).Select(o => o.Key).ToList());
        logger.LogInformation(
            "Storage cleanup{DryRun}: {Scanned} objects, {Orphans} orphan(s) ({Bytes} bytes), {Young} unreferenced within the grace period, {Expired} expired original(s), {Deleted} deleted",
            dryRun ? " (dry run)" : "", report.Scanned, report.Orphans, report.OrphanBytes, report.Young, report.ExpiredOriginals, report.Deleted);
        return report;
    }
}

/// <summary>Runs the cleanup periodically (first run a few minutes after start).</summary>
public class StorageJanitorWorker(IServiceScopeFactory scopes, IOptions<CleanupOptions> options, ILogger<StorageJanitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromMinutes(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, stoppingToken);
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<StorageJanitor>().RunAsync(options.Value.DryRun, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Storage cleanup failed");
            }
            delay = TimeSpan.FromHours(Math.Max(1, options.Value.IntervalHours));
        }
    }
}
