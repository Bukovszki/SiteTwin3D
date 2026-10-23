using Api.Tests.Infrastructure;
using Npgsql;

namespace Api.Tests.Database;

/// <summary>The anchor_kind_fields CHECK (spec 8.2) and the organization consistency of anchors.</summary>
[Collection(PostgisCollection.Name)]
public sealed class AnchorConstraintTests(PostgisFixture postgis)
{
    private const string Position = "ST_SetSRID(ST_MakePoint(19.04, 47.5), 4326)";
    private const string Footprint = "ST_GeomFromText('POLYGON((19 47, 19.001 47, 19.001 47.001, 19 47))', 4326)";

    public static TheoryData<string, string> Valid => new()
    {
        { "point", "kind" },
        { "region box", "kind, region_shape, size_x, size_y, size_z, heading_deg) VALUES (@org, @node, " + Position + ", 'region', 'box', 1, 2, 3, 0" },
        { "region prism", "kind, region_shape, footprint, z_min, z_max) VALUES (@org, @node, " + Position + ", 'region', 'prism', " + Footprint + ", 0, 2.5" },
    };

    public static TheoryData<string, string> Invalid => new()
    {
        { "box without size", "kind, region_shape, heading_deg) VALUES (@org, @node, " + Position + ", 'region', 'box', 0" },
        { "box with zero size", "kind, region_shape, size_x, size_y, size_z, heading_deg) VALUES (@org, @node, " + Position + ", 'region', 'box', 1, 0, 3, 0" },
        { "box without heading", "kind, region_shape, size_x, size_y, size_z) VALUES (@org, @node, " + Position + ", 'region', 'box', 1, 2, 3" },
        { "prism with z_max <= z_min", "kind, region_shape, footprint, z_min, z_max) VALUES (@org, @node, " + Position + ", 'region', 'prism', " + Footprint + ", 2, 2" },
        { "prism without z_max", "kind, region_shape, footprint, z_min) VALUES (@org, @node, " + Position + ", 'region', 'prism', " + Footprint + ", 0" },
        { "prism without footprint", "kind, region_shape, z_min, z_max) VALUES (@org, @node, " + Position + ", 'region', 'prism', 0, 2" },
        { "region without shape", "kind) VALUES (@org, @node, " + Position + ", 'region'" },
        { "model_feature without asset", "kind, feature_ref) VALUES (@org, @node, " + Position + ", 'model_feature', 'GUID-1'" },
        { "panorama_point without panorama", "kind, yaw_deg, pitch_deg) VALUES (@org, @node, " + Position + ", 'panorama_point', 10, 0" },
        { "unknown kind", "kind) VALUES (@org, @node, " + Position + ", 'whatever'" },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public async Task ValidAnchor_IsAccepted(string _, string columnsAndValues)
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var (org, node) = await NodeAsync(sql);

        await InsertAsync(sql, org, node, columnsAndValues);
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task InvalidAnchor_IsRejected(string _, string columnsAndValues)
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var (org, node) = await NodeAsync(sql);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(sql, org, node, columnsAndValues));

        Assert.Equal("anchor_kind_fields", ex.ConstraintName);
    }

    [Fact]
    public async Task PanoramaPointAndModelFeature_WithAllFields_AreAccepted()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var (org, node) = await NodeAsync(sql);
        var site = await sql.ScalarAsync<long>("SELECT site_id FROM node WHERE id = @id", ("id", node));
        var panorama = await sql.ScalarAsync<long>($"""
            INSERT INTO panorama (organization_id, node_id, storage_key, sha256, position)
            VALUES (@org, @node, 'p/1.jpg', 'abc', {Position}) RETURNING id
            """, ("org", org), ("node", node));
        var model = await sql.ScalarAsync<long>(
            "INSERT INTO model_asset (organization_id, site_id, kind, storage_key) VALUES (@org, @site, '3dtiles', 'm/tileset.json') RETURNING id",
            ("org", org), ("site", site));

        await sql.ExecuteAsync($"""
            INSERT INTO anchor (organization_id, node_id, position, kind, panorama_id, yaw_deg, pitch_deg)
            VALUES (@org, @node, {Position}, 'panorama_point', @panorama, 120, -5)
            """, ("org", org), ("node", node), ("panorama", panorama));
        await sql.ExecuteAsync($"""
            INSERT INTO anchor (organization_id, node_id, position, kind, model_asset_id, feature_ref)
            VALUES (@org, @node, {Position}, 'model_feature', @model, '2O2Fr$t4X7Zf8NOew3FLOH')
            """, ("org", org), ("node", node), ("model", model));
    }

    [Fact]
    public async Task Anchor_OfAnotherOrganizationsNode_IsRejected()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var (_, node) = await NodeAsync(sql);
        var otherOrg = await sql.OrganizationAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(sql, otherOrg, node, "kind"));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task DeletingNode_DeletesItsAnchors()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var (org, node) = await NodeAsync(sql);
        await InsertAsync(sql, org, node, "kind");

        await sql.ExecuteAsync("DELETE FROM node WHERE id = @id", ("id", node));

        Assert.Equal(0L, await sql.ScalarAsync<long>("SELECT count(*) FROM anchor WHERE node_id = @id", ("id", node)));
    }

    private static async Task<(long Org, long Node)> NodeAsync(SqlTestData sql)
    {
        var org = await sql.OrganizationAsync();
        var site = await sql.SiteAsync(org);
        var node = await sql.NodeAsync(org, site, null, "equipment");
        return (org, node.Id);
    }

    /// <summary>"kind" alone means a plain point anchor.</summary>
    private static Task InsertAsync(SqlTestData sql, long org, long node, string columnsAndValues) =>
        sql.ExecuteAsync(columnsAndValues == "kind"
            ? $"INSERT INTO anchor (organization_id, node_id, position, kind) VALUES (@org, @node, {Position}, 'point')"
            : $"INSERT INTO anchor (organization_id, node_id, position, {columnsAndValues})",
            ("org", org), ("node", node));
}
