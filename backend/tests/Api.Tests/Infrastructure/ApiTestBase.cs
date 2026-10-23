namespace Api.Tests.Infrastructure;

/// <summary>An in-memory API per test class on the shared PostGIS container.</summary>
[Collection(PostgisCollection.Name)]
public abstract class ApiTestBase(PostgisFixture postgis) : IAsyncLifetime
{
    protected PostgisFixture Postgis { get; } = postgis;
    protected ApiFactory Factory { get; private set; } = null!;
    protected TestApi Api { get; private set; } = null!;

    public virtual Task InitializeAsync()
    {
        Factory = new ApiFactory(Postgis);
        Api = new TestApi(Factory, Postgis);
        return Task.CompletedTask;
    }

    public virtual async Task DisposeAsync() => await Factory.DisposeAsync();
}
