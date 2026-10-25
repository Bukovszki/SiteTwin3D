using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Api.Tests.Infrastructure;

/// <summary>The converter service in tests: records the jobs and answers with what the test sets (or fails / is down).</summary>
public sealed class FakeConverter : HttpMessageHandler
{
    public List<JsonElement> Jobs { get; } = [];

    /// <summary>The answer for a job (its output prefix is passed); null: 422 with <see cref="Error"/>.</summary>
    public Func<JsonElement, JsonObject?> Answer { get; set; } = _ => null;
    public string Error { get; set; } = "unreadable";
    public bool Down { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down) throw new HttpRequestException("Connection refused");
        var job = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
        Jobs.Add(job);
        var answer = Answer(job);
        return answer is null
            ? new HttpResponseMessage((HttpStatusCode)422) { Content = Json(new JsonObject { ["error"] = Error }) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(answer) };
    }

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}
