using Api.Data;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Api.Tests.Infrastructure;

/// <summary>
/// Real infrastructure in containers, shared by every test in the collection: PostgreSQL + PostGIS (+ ltree) and the
/// S3-compatible object store. Same images as in deploy/docker-compose.yml (multi-arch, also on Apple Silicon).
/// </summary>
public sealed class PostgisFixture : IAsyncLifetime
{
    public const string Image = "docker.io/kartoza/postgis:16-3.4";
    private const string Database = "appdb";
    private const string User = "postgres";
    private const string Password = "postgres";

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithEnvironment("POSTGRES_USER", User)
        .WithEnvironment("POSTGRES_PASS", Password)
        .WithEnvironment("POSTGRES_DBNAME", Database)
        .WithEnvironment("ALLOW_IP_RANGE", "0.0.0.0/0")
        .WithPortBinding(5432, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5432))
        .Build();

    public const string StorageImage = "docker.io/chrislusf/seaweedfs:4.48";
    public const string StorageAccessKey = "testaccess";
    public const string StorageSecretKey = "testsecret123456";

    private readonly IContainer _storage = new ContainerBuilder(StorageImage)
        .WithCommand("server", "-dir=/data", "-s3", "-ip.bind=0.0.0.0", "-master.volumeSizeLimitMB=256", "-volume.max=20")
        .WithEnvironment("AWS_ACCESS_KEY_ID", StorageAccessKey)
        .WithEnvironment("AWS_SECRET_ACCESS_KEY", StorageSecretKey)
        .WithPortBinding(8333, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8333))
        .Build();

    /// <summary>The S3 endpoint as seen from the tests (also used as the "public" URL of signed URLs).</summary>
    public string StorageUrl => $"http://{_storage.Hostname}:{_storage.GetMappedPublicPort(8333)}";

    public string ConnectionString => new NpgsqlConnectionStringBuilder
    {
        Host = _container.Hostname,
        Port = _container.GetMappedPublicPort(5432),
        Database = Database,
        Username = User,
        Password = Password,
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_container.StartAsync(), _storage.StartAsync());
        await WaitUntilReadyAsync();
        await WaitUntilStorageReadyAsync();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>A context without a signed-in user: the organization filter lets no organization data through.</summary>
    public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(ConnectionString, o => o.UseNetTopologySuite())
        .UseSnakeCaseNamingConvention()
        .Options);

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        await _storage.DisposeAsync();
    }

    /// <summary>The S3 gateway answers (403 without credentials) only after the master and the volume server are up.</summary>
    private async Task WaitUntilStorageReadyAsync()
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            try
            {
                var response = await http.GetAsync(StorageUrl);
                if ((int)response.StatusCode is 403 or 200) return;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline) { }
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Object store did not start.");
            await Task.Delay(500);
        }
    }

    /// <summary>
    /// The image runs an init phase (and a server restart) after the port opens: wait until the application
    /// database accepts queries.
    /// </summary>
    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                await command.ExecuteScalarAsync();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgisCollection : ICollectionFixture<PostgisFixture>
{
    public const string Name = "postgis";
}
