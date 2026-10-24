using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Tests.Database;
using Api.Tests.Infrastructure;

namespace Api.Tests.History;

/// <summary>
/// Temporal relevance of finalized records (user request, after SmartRoute): drafts are edited freely; after
/// finalization every change of descriptive data or anchors needs a reason, has a valid-from day and is kept, so the
/// state of any day since the finalization can be rebuilt.
/// </summary>
public sealed class HistoryTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    private const double Lon = 19.08, Lat = 47.43;

    private static HttpRequestMessage With(HttpMethod method, string url, object? body, string? reason, string? validFrom = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (reason is not null) request.Headers.Add("X-Change-Reason", Uri.EscapeDataString(reason));
        if (validFrom is not null) request.Headers.Add("X-Change-Valid-From", validFrom);
        return request;
    }

    private static object Point(double east) =>
        new { kind = "point", zRelM = 1.0, position = new { type = "Point", coordinates = new[] { Lon + east / 75470.0, Lat } } };

    private async Task<(OrgClient Org, long Site, long Pump, long Anchor)> SetupAsync()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var pump = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("equipment"), "P1")).GetProperty("id").GetInt64();
        var anchor = (await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", Point(0)))).GetProperty("id").GetInt64();
        return (org, site, pump, anchor);
    }

    [Fact]
    public async Task Draft_IsEditedFreely_WithoutHistory()
    {
        var (org, _, pump, anchor) = await SetupAsync();

        var renamed = await org.Http.PatchJsonAsync($"/api/v1/nodes/{pump}", new { name = "P1 új" });
        var moved = await org.Http.PatchJsonAsync($"/api/v1/anchors/{anchor}", Point(2));
        var history = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{pump}/history");

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Empty(history.EnumerateArray());
        Assert.Equal(JsonValueKind.Null, (await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{pump}")).GetProperty("finalizedAt").ValueKind);
    }

    [Fact]
    public async Task Finalized_ChangesNeedAReason_AndAreKept()
    {
        var (org, _, pump, anchor) = await SetupAsync();
        var finalized = await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/finalize", new { validFrom = "2025-01-01" }));

        var noReason = await org.Http.PatchJsonAsync($"/api/v1/nodes/{pump}", new { name = "P1 csere" });
        var future = await org.Http.SendAsync(With(HttpMethod.Patch, $"/api/v1/nodes/{pump}", new { name = "P1 csere" }, "csere", "2999-01-01"));
        var tooEarly = await org.Http.SendAsync(With(HttpMethod.Patch, $"/api/v1/nodes/{pump}", new { name = "P1 csere" }, "csere", "2024-12-31"));
        var renamed = await org.Http.SendAsync(With(HttpMethod.Patch, $"/api/v1/nodes/{pump}",
            new { name = "P1 csere", attrs = new { power_kw = 11 } }, "Szivattyúcsere, nagyobb teljesítmény", "2026-03-01"));
        var unchanged = await org.Http.PatchJsonAsync($"/api/v1/nodes/{pump}", new { name = "P1 csere" }); // nothing changes: no reason needed
        var moveNoReason = await org.Http.PatchJsonAsync($"/api/v1/anchors/{anchor}", Point(3));
        var moved = await org.Http.SendAsync(With(HttpMethod.Patch, $"/api/v1/anchors/{anchor}", Point(3), "Új alapra került", "2026-03-01"));
        var history = await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{pump}/history");

        Assert.Equal(1, finalized.GetProperty("finalized").GetInt32());
        Assert.Equal(HttpStatusCode.PreconditionRequired, noReason.StatusCode);
        Assert.Equal("change.reason_required", await TestApi.ErrorCode(noReason));
        Assert.Equal("change.valid_from_future", await TestApi.ErrorCode(future));
        Assert.Equal("change.valid_from_too_early", await TestApi.ErrorCode(tooEarly));
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, moveNoReason.StatusCode);
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var entries = history.EnumerateArray().ToList();
        Assert.Equal(4, entries.Count); // 2 baselines (node, anchor) + rename + move
        var rename = entries.Single(e => e.GetProperty("action").GetString() == "update" && e.GetProperty("entity").GetString() == "node");
        Assert.Equal("Szivattyúcsere, nagyobb teljesítmény", rename.GetProperty("reason").GetString());
        Assert.Equal("2026-03-01", rename.GetProperty("validFrom").GetString());
        Assert.Equal(["attrs.power_kw", "name"], rename.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).Order());
        Assert.False(string.IsNullOrEmpty(rename.GetProperty("recordedByName").GetString()));
    }

    [Fact]
    public async Task State_IsRebuiltForAnyDay_CorrectionOfTheSameDayWins()
    {
        var (org, _, pump, anchor) = await SetupAsync();
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/finalize", new { validFrom = "2025-01-01" });
        await org.Http.SendAsync(With(HttpMethod.Patch, $"/api/v1/nodes/{pump}", new { name = "P1 csere" }, "csere", "2026-03-01"));
        await org.Http.SendAsync(With(HttpMethod.Patch, $"/api/v1/anchors/{anchor}", Point(3), "áthelyezés", "2026-03-01"));
        // A correction of the same day, recorded later: the move was 3.5 m, not 3 m.
        await org.Http.SendAsync(With(HttpMethod.Patch, $"/api/v1/anchors/{anchor}", Point(3.5), "javítás: pontos hely", "2026-03-01"));
        var extra = await org.Http.SendAsync(With(HttpMethod.Post, $"/api/v1/nodes/{pump}/anchors", new
        {
            kind = "region", regionShape = "box", sizeX = 1, sizeY = 1, sizeZ = 1, headingDeg = 0, zRelM = 0.5,
            position = new { type = "Point", coordinates = new[] { Lon + 10 / 75470.0, Lat } },
        }, "bedobozolás", "2026-05-01"));

        async Task<JsonElement> State(string day) => await org.Http.GetFromJsonAsync<JsonElement>($"/api/v1/nodes/{pump}/state?asOf={day}");
        double East(JsonElement a) => (a.GetProperty("position")[0].GetDouble() - Lon) * 75470;
        var before = await State("2024-06-01");
        var first = await State("2025-06-01");
        var after = await State("2026-03-01");
        var later = await State("2026-06-01");

        Assert.Equal(HttpStatusCode.Created, extra.StatusCode);
        Assert.False(before.GetProperty("tracked").GetBoolean());
        Assert.Equal("P1", first.GetProperty("node").GetProperty("name").GetString());
        Assert.Equal(0, East(Assert.Single(first.GetProperty("anchors").EnumerateArray())), 2);
        Assert.Equal("P1 csere", after.GetProperty("node").GetProperty("name").GetString());
        Assert.Equal(3.5, East(Assert.Single(after.GetProperty("anchors").EnumerateArray())), 2);
        Assert.Equal(2, later.GetProperty("anchors").GetArrayLength());
    }

    [Fact]
    public async Task Deletions_OfFinalizedData_NeedAReason_HistoryOutlivesTheNode()
    {
        var (org, site, pump, anchor) = await SetupAsync();
        var level = (await org.Http.CreateNodeAsync(site, null, await org.Http.TypeIdAsync("building"), "Gépház")).GetProperty("id").GetInt64();
        await using var sql = await SqlTestData.OpenAsync(Postgis);
        var panorama = await sql.PanoramaAsync(org.OrganizationId, level, Lon, Lat, null);
        var infoPoint = (await TestApi.ReadJson(await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/anchors", new
        {
            kind = "panorama_point", panoramaId = panorama, yawDeg = 10, pitchDeg = 0, position = new { type = "Point", coordinates = new[] { Lon, Lat } },
        }))).GetProperty("id").GetInt64();
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/finalize", new { });

        var anchorNoReason = await org.Http.DeleteAsync($"/api/v1/anchors/{anchor}");
        var anchorDeleted = await org.Http.SendAsync(With(HttpMethod.Delete, $"/api/v1/anchors/{anchor}", null, "téves pont"));
        var panoramaNoReason = await org.Http.DeleteAsync($"/api/v1/panoramas/{panorama}"); // would take the info point with it
        var panoramaDeleted = await org.Http.SendAsync(With(HttpMethod.Delete, $"/api/v1/panoramas/{panorama}", null, "új felvétel készül"));
        var nodeNoReason = await org.Http.DeleteAsync($"/api/v1/nodes/{pump}");
        var nodeDeleted = await org.Http.SendAsync(With(HttpMethod.Delete, $"/api/v1/nodes/{pump}", null, "leszerelve"));

        Assert.Equal(HttpStatusCode.PreconditionRequired, anchorNoReason.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, anchorDeleted.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, panoramaNoReason.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, panoramaDeleted.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionRequired, nodeNoReason.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, nodeDeleted.StatusCode);
        var actions = await sql.ScalarAsync<string>("""
            SELECT string_agg(entity || ':' || action || ':' || coalesce(reason, ''), ',' ORDER BY id) FROM change_entry WHERE node_id = @id
            """, ("id", pump));
        Assert.Equal($"node:finalize:,anchor:finalize:,anchor:finalize:,anchor:delete:téves pont,anchor:delete:új felvétel készül,node:delete:leszerelve", actions);
        Assert.Equal(infoPoint, await sql.ScalarAsync<long>("SELECT entity_id FROM change_entry WHERE node_id = @id AND reason = 'új felvétel készül'", ("id", pump)));
    }

    [Fact]
    public async Task OtherOrganization_SeesNoHistory()
    {
        var (org, _, pump, _) = await SetupAsync();
        await org.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/finalize", new { });
        var other = await Api.NewOrganizationAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.GetAsync($"/api/v1/nodes/{pump}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Http.PostAsJsonAsync($"/api/v1/nodes/{pump}/finalize", new { })).StatusCode);
    }
}
