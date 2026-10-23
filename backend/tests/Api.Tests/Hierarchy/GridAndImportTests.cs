using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Model;
using Api.Tests.Infrastructure;

namespace Api.Tests.Hierarchy;

/// <summary>
/// M2 acceptance (spec 12): after importing a sample table the equipment can be filtered and edited in the grid
/// (FR-G1, G2, G4, G5; FR-I3).
/// </summary>
public sealed class GridAndImportTests(PostgisFixture postgis) : ApiTestBase(postgis)
{
    /// <summary>The sample table (docs/samples/berendezesek.csv) as the client sends it after column mapping.</summary>
    private static object SampleRows() => new object[]
    {
        new { code = "=P01", name = "Befolyó szivattyú 1", attrs = new Dictionary<string, object> { ["manufacturer"] = "Grundfos", ["model"] = "SE1.80", ["power_kw"] = "7,5", ["install_year"] = "2015" } },
        new { code = "=P02", name = "Befolyó szivattyú 2", attrs = new Dictionary<string, object> { ["manufacturer"] = "Grundfos", ["model"] = "SE1.80", ["power_kw"] = "7,5", ["install_year"] = "2015" } },
        new { code = "=B01", name = "Légfúvó 1", attrs = new Dictionary<string, object> { ["manufacturer"] = "Aerzen", ["model"] = "Delta Hybrid", ["power_kw"] = "55", ["install_year"] = "2019" } },
        new { code = "=K01", name = "Keverő 1", attrs = new Dictionary<string, object> { ["manufacturer"] = "Flygt", ["power_kw"] = 3.1, ["install_year"] = 2012 } },
        new { code = "=M01", name = "Iszapszivattyú", attrs = new Dictionary<string, object> { ["manufacturer"] = "Flygt", ["power_kw"] = "1 200,5", ["install_year"] = "" } },
    };

    private async Task<(OrgClient Org, long Site, long TypeId)> ImportedAsync()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var typeId = await org.Http.TypeIdAsync("equipment");
        var response = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/import/attributes", new { typeId, rows = SampleRows() });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = await TestApi.ReadJson(response);
        Assert.Equal(5, result.GetProperty("created").GetInt32());
        return (org, site, typeId);
    }

    private static async Task<JsonElement> GridAsync(HttpClient http, long site, long typeId, string query = "")
    {
        var response = await http.GetAsync($"/api/v1/nodes?siteId={site}&typeId={typeId}{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await TestApi.ReadJson(response);
    }

    private static string Filter(params object[] filters) => "&filter=" + Uri.EscapeDataString(JsonSerializer.Serialize(filters));

    private static List<string> Codes(JsonElement page) =>
        page.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("node").GetProperty("code").GetString()!).ToList();

    [Fact]
    public async Task Import_ConvertsHungarianNumbers_AndValidates()
    {
        var (org, site, typeId) = await ImportedAsync();

        var page = await GridAsync(org.Http, site, typeId, "&sort=code");
        var mixer = page.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("node").GetProperty("code").GetString() == "=M01");
        var attrs = mixer.GetProperty("node").GetProperty("attrs");

        Assert.Equal(5, page.GetProperty("total").GetInt32());
        Assert.Equal(1200.5, attrs.GetProperty("power_kw").GetDouble());
        Assert.False(attrs.TryGetProperty("install_year", out _)); // empty cell: left out
    }

    [Fact]
    public async Task Grid_FiltersOnTechnicalData()
    {
        var (org, site, typeId) = await ImportedAsync();

        var grundfos = await GridAsync(org.Http, site, typeId, Filter(new { field = "attrs.manufacturer", op = "contains", value = "grund" }) + "&sort=code");
        var strong = await GridAsync(org.Http, site, typeId, Filter(new { field = "attrs.power_kw", op = "gte", value = 7.5 }) + "&sort=code");
        var combined = await GridAsync(org.Http, site, typeId, Filter(
            new { field = "attrs.manufacturer", op = "eq", value = "Flygt" },
            new { field = "attrs.power_kw", op = "lte", value = 5 }));
        var noYear = await GridAsync(org.Http, site, typeId, Filter(new { field = "attrs.install_year", op = "empty" }));
        var search = await GridAsync(org.Http, site, typeId, "&search=szivattyú&sort=code");

        Assert.Equal(["=P01", "=P02"], Codes(grundfos));
        Assert.Equal(["=B01", "=M01", "=P01", "=P02"], Codes(strong));
        Assert.Equal(["=K01"], Codes(combined));
        Assert.Equal(["=M01"], Codes(noYear));
        Assert.Equal(["=M01", "=P01", "=P02"], Codes(search));
    }

    [Fact]
    public async Task Grid_SortsNumericallyAndPages()
    {
        var (org, site, typeId) = await ImportedAsync();

        var byPower = await GridAsync(org.Http, site, typeId, "&sort=attrs.power_kw&dir=desc");
        var page2 = await GridAsync(org.Http, site, typeId, "&sort=code&first=2&rows=2");

        // Numeric, not text order (text order would put "55" before "7.5" and "1200.5" first).
        Assert.Equal(["=M01", "=B01"], Codes(byPower).Take(2));
        Assert.Equal("=K01", Codes(byPower).Last());
        Assert.Equal(5, page2.GetProperty("total").GetInt32());
        Assert.Equal(["=M01", "=P01"], Codes(page2));
    }

    [Fact]
    public async Task Grid_UnknownField_IsBadRequest()
    {
        var (org, site, typeId) = await ImportedAsync();

        var response = await org.Http.GetAsync($"/api/v1/nodes?siteId={site}&typeId={typeId}" + Filter(new { field = "attrs.drop_table", op = "eq", value = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("grid.unknown_field", await TestApi.ErrorCode(response));
    }

    [Fact]
    public async Task Grid_ShowsAnchorKinds()
    {
        var (org, site, typeId) = await ImportedAsync();
        var first = (await GridAsync(org.Http, site, typeId, "&sort=code")).GetProperty("rows")[0].GetProperty("node");
        await using (var sql = await Database.SqlTestData.OpenAsync(Postgis))
            await sql.ExecuteAsync(
                "INSERT INTO anchor (organization_id, node_id, position, kind) VALUES (@org, @node, ST_SetSRID(ST_MakePoint(19, 47), 4326), 'point')",
                ("org", org.OrganizationId), ("node", first.GetProperty("id").GetInt64()));

        var rows = (await GridAsync(org.Http, site, typeId, "&sort=code")).GetProperty("rows");

        Assert.Equal(["point"], rows[0].GetProperty("anchorKinds").EnumerateArray().Select(k => k.GetString()));
        Assert.Empty(rows[1].GetProperty("anchorKinds").EnumerateArray());
    }

    [Fact]
    public async Task EditedInGrid_IsValidatedAndSaved()
    {
        var (org, site, typeId) = await ImportedAsync();
        var row = (await GridAsync(org.Http, site, typeId, "&sort=code")).GetProperty("rows")[0].GetProperty("node");
        var id = row.GetProperty("id").GetInt64();

        var invalid = await org.Http.PatchJsonAsync($"/api/v1/nodes/{id}", new { attrs = new { manufacturer = "Aerzen", power_kw = -1 } });
        var valid = await org.Http.PatchJsonAsync($"/api/v1/nodes/{id}", new { attrs = new { manufacturer = "Aerzen", model = "GM 10S", power_kw = 60 } });
        var filtered = await GridAsync(org.Http, site, typeId, Filter(new { field = "attrs.power_kw", op = "gte", value = 60 }));

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(["=M01", "=B01"], Codes(filtered)); // =M01: 1200.5 kW; =B01 edited to 60 kW
    }

    [Fact]
    public async Task Import_UpdatesByCode_AndCreatesNew()
    {
        var (org, site, typeId) = await ImportedAsync();

        var response = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/import/attributes", new
        {
            typeId,
            rows = new object[]
            {
                new { code = "=P01", attrs = new Dictionary<string, object> { ["power_kw"] = "9,2" } },
                new { code = "=P03", name = "Befolyó szivattyú 3", attrs = new Dictionary<string, object> { ["manufacturer"] = "KSB" } },
            },
        });
        var p01 = (await GridAsync(org.Http, site, typeId, Filter(new { field = "code", op = "eq", value = "=P01" }))).GetProperty("rows")[0].GetProperty("node");

        var result = await TestApi.ReadJson(response);
        Assert.Equal(1, result.GetProperty("created").GetInt32());
        Assert.Equal(1, result.GetProperty("updated").GetInt32());
        Assert.Equal(9.2, p01.GetProperty("attrs").GetProperty("power_kw").GetDouble());
        Assert.Equal("Grundfos", p01.GetProperty("attrs").GetProperty("manufacturer").GetString()); // merged, not replaced
        Assert.Equal("Befolyó szivattyú 1", p01.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Import_WithErrors_SavesNothing_AndReportsEveryRow()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var typeId = await org.Http.TypeIdAsync("equipment");

        var response = await org.Http.PostAsJsonAsync($"/api/v1/sites/{site}/import/attributes", new
        {
            typeId,
            rows = new object[]
            {
                new { code = "=A", name = "Jó sor", attrs = new Dictionary<string, object> { ["power_kw"] = "1" } },
                new { code = "=B", name = "Rossz szám", attrs = new Dictionary<string, object> { ["power_kw"] = "sok" } },
                new { code = "=C", name = "Ismeretlen mező", attrs = new Dictionary<string, object> { ["color"] = "piros" } },
                new { code = "=D", name = "Negatív", attrs = new Dictionary<string, object> { ["power_kw"] = "-3" } },
                new { code = "=A", name = "Ismétlődő kód" },
            },
        });
        var problem = await TestApi.ReadJson(response);
        var errors = problem.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("code").GetString())).Distinct().ToList();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("import.invalid", problem.GetProperty("code").GetString());
        Assert.Equal([(2, "import.invalid_value"), (3, "import.unknown_field"), (4, "node.invalid_attrs"), (5, "import.duplicate_code")], errors);
        Assert.Equal(0, (await GridAsync(org.Http, site, typeId)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Viewer_CannotImport()
    {
        var org = await Api.NewOrganizationAsync();
        var site = await org.Http.CreateSiteAsync();
        var viewer = await Api.NewUserAsync(org.OrganizationId, org.OrganizationCode, Roles.Viewer);

        var response = await viewer.Http.PostAsJsonAsync($"/api/v1/sites/{site}/import/attributes",
            new { typeId = await org.Http.TypeIdAsync("equipment"), rows = SampleRows() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
