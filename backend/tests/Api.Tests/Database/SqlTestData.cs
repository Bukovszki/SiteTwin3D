using Api.Tests.Infrastructure;
using Npgsql;

namespace Api.Tests.Database;

/// <summary>Plain SQL test data for database-level tests (triggers, constraints) – no API, no filter.</summary>
public sealed class SqlTestData(NpgsqlConnection connection) : IAsyncDisposable
{
    public static async Task<SqlTestData> OpenAsync(PostgisFixture postgis) => new(await postgis.OpenConnectionAsync());

    public NpgsqlConnection Connection => connection;

    public async Task<long> OrganizationAsync() =>
        await ScalarAsync<long>("INSERT INTO organization (code, name) VALUES (@code, 'Test') RETURNING id", ("code", TestApi.UniqueCode()));

    public async Task<long> SiteAsync(long organizationId) =>
        await ScalarAsync<long>("""
            INSERT INTO site (organization_id, name, ref_point, base_height_m)
            VALUES (@org, 'Site', ST_SetSRID(ST_MakePoint(19.04, 47.5), 4326), 100) RETURNING id
            """, ("org", organizationId));

    public async Task<long> TypeIdAsync(string code) =>
        await ScalarAsync<long>("SELECT id FROM node_type WHERE organization_id IS NULL AND code = @code", ("code", code));

    public async Task<(long Id, string Path)> NodeAsync(long organizationId, long siteId, long? parentId, string typeCode = "building")
    {
        await using var command = Command("""
            INSERT INTO node (organization_id, site_id, parent_id, type_id, name)
            VALUES (@org, @site, @parent, (SELECT id FROM node_type WHERE organization_id IS NULL AND code = @type), 'Node')
            RETURNING id, path::text
            """, ("org", organizationId), ("site", siteId), ("parent", (object?)parentId ?? DBNull.Value), ("type", typeCode));
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetString(1));
    }

    /// <summary>A panorama row without a file (for anchor and link tests); status approved unless given.</summary>
    public async Task<long> PanoramaAsync(long organizationId, long nodeId, double lon, double lat, long? levelId, string status = "approved",
        double? heading = null) =>
        await ScalarAsync<long>("""
            INSERT INTO panorama (organization_id, node_id, storage_key, sha256, position, level_id, privacy_status, heading_deg)
            VALUES (@org, @node, 'test/' || gen_random_uuid() || '.jpg', 'x', ST_SetSRID(ST_MakePoint(@lon, @lat), 4326), @level, @status, @heading)
            RETURNING id
            """, ("org", organizationId), ("node", nodeId), ("lon", lon), ("lat", lat), ("level", (object?)levelId ?? DBNull.Value),
            ("status", status), ("heading", (object?)heading ?? DBNull.Value));

    public async Task<string> PathAsync(long nodeId) =>
        await ScalarAsync<string>("SELECT path::text FROM node WHERE id = @id", ("id", nodeId));

    public async Task MoveAsync(long nodeId, long? parentId) =>
        await ExecuteAsync("UPDATE node SET parent_id = @parent WHERE id = @id", ("id", nodeId), ("parent", (object?)parentId ?? DBNull.Value));

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Command(sql, parameters);
        await command.ExecuteNonQueryAsync();
    }

    private NpgsqlCommand Command(string sql, params (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
