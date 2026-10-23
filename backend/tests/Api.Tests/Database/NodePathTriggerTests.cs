using Api.Tests.Infrastructure;
using Npgsql;

namespace Api.Tests.Database;

/// <summary>The path triggers of db/sql/node_path.sql (spec 8.3, FR-H8): insert, multi-level move, cycle rejection.</summary>
[Collection(PostgisCollection.Name)]
public sealed class NodePathTriggerTests(PostgisFixture postgis)
{
    [Fact]
    public async Task Insert_BuildsPathFromAncestorIds()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var org = await sql.OrganizationAsync();
        var site = await sql.SiteAsync(org);

        var root = await sql.NodeAsync(org, site, null);
        var child = await sql.NodeAsync(org, site, root.Id, "level");
        var grandchild = await sql.NodeAsync(org, site, child.Id, "room");

        Assert.Equal($"{root.Id}", root.Path);
        Assert.Equal($"{root.Id}.{child.Id}", child.Path);
        Assert.Equal($"{root.Id}.{child.Id}.{grandchild.Id}", grandchild.Path);
    }

    [Fact]
    public async Task Move_RewritesPathsOfTheWholeSubtree()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var org = await sql.OrganizationAsync();
        var site = await sql.SiteAsync(org);
        // a ─ b ─ c ─ d      x
        var a = await sql.NodeAsync(org, site, null);
        var b = await sql.NodeAsync(org, site, a.Id);
        var c = await sql.NodeAsync(org, site, b.Id);
        var d = await sql.NodeAsync(org, site, c.Id);
        var x = await sql.NodeAsync(org, site, null);

        // b (with c and d) moves under x
        await sql.MoveAsync(b.Id, x.Id);

        Assert.Equal($"{a.Id}", await sql.PathAsync(a.Id));
        Assert.Equal($"{x.Id}.{b.Id}", await sql.PathAsync(b.Id));
        Assert.Equal($"{x.Id}.{b.Id}.{c.Id}", await sql.PathAsync(c.Id));
        Assert.Equal($"{x.Id}.{b.Id}.{c.Id}.{d.Id}", await sql.PathAsync(d.Id));

        // c (with d) moves to the root
        await sql.MoveAsync(c.Id, null);

        Assert.Equal($"{c.Id}", await sql.PathAsync(c.Id));
        Assert.Equal($"{c.Id}.{d.Id}", await sql.PathAsync(d.Id));
        Assert.Equal($"{x.Id}.{b.Id}", await sql.PathAsync(b.Id));
    }

    [Fact]
    public async Task Move_UnderOwnDescendant_IsRejectedAsCycle()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var org = await sql.OrganizationAsync();
        var site = await sql.SiteAsync(org);
        var a = await sql.NodeAsync(org, site, null);
        var b = await sql.NodeAsync(org, site, a.Id);
        var c = await sql.NodeAsync(org, site, b.Id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => sql.MoveAsync(a.Id, c.Id));

        Assert.Equal("node_no_cycle", ex.ConstraintName);
        Assert.Equal($"{a.Id}.{b.Id}.{c.Id}", await sql.PathAsync(c.Id));
    }

    [Fact]
    public async Task Move_UnderItself_IsRejectedAsCycle()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var org = await sql.OrganizationAsync();
        var site = await sql.SiteAsync(org);
        var a = await sql.NodeAsync(org, site, null);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => sql.MoveAsync(a.Id, a.Id));

        Assert.Equal("node_no_cycle", ex.ConstraintName);
    }

    [Fact]
    public async Task Parent_OnAnotherSite_IsRejected()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var org = await sql.OrganizationAsync();
        var site1 = await sql.SiteAsync(org);
        var site2 = await sql.SiteAsync(org);
        var parent = await sql.NodeAsync(org, site1, null);
        var other = await sql.NodeAsync(org, site2, null);

        var insert = await Assert.ThrowsAsync<PostgresException>(() => sql.NodeAsync(org, site2, parent.Id));
        var move = await Assert.ThrowsAsync<PostgresException>(() => sql.MoveAsync(other.Id, parent.Id));

        Assert.Equal("fk_node_parent_same_site", insert.ConstraintName);
        Assert.Equal("fk_node_parent_same_site", move.ConstraintName);
    }

    [Fact]
    public async Task Parent_NotFound_IsRejected()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var org = await sql.OrganizationAsync();
        var site = await sql.SiteAsync(org);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => sql.NodeAsync(org, site, long.MaxValue));

        Assert.Equal("node_parent_exists", ex.ConstraintName);
    }

    [Fact]
    public async Task Node_InAnotherOrganizationThanItsSite_IsRejected()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var org1 = await sql.OrganizationAsync();
        var org2 = await sql.OrganizationAsync();
        var site = await sql.SiteAsync(org1);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => sql.NodeAsync(org2, site, null));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task SubtreeAndAncestorQueries_UsePath()
    {
        await using var sql = await SqlTestData.OpenAsync(postgis);
        var org = await sql.OrganizationAsync();
        var site = await sql.SiteAsync(org);
        var a = await sql.NodeAsync(org, site, null);
        var b = await sql.NodeAsync(org, site, a.Id);
        var c = await sql.NodeAsync(org, site, b.Id);
        await sql.NodeAsync(org, site, null);

        var subtree = await sql.ScalarAsync<long>("SELECT count(*) FROM node WHERE path <@ @p::ltree", ("p", b.Path));
        var ancestors = await sql.ScalarAsync<long>("SELECT count(*) FROM node WHERE path @> @p::ltree", ("p", $"{a.Id}.{b.Id}.{c.Id}"));

        Assert.Equal(2, subtree);   // b, c
        Assert.Equal(3, ancestors); // a, b, c
    }
}
