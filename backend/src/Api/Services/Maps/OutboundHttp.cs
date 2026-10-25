using System.Net;
using System.Net.Sockets;

namespace Api.Services.Maps;

public class MapsOptions
{
    public const string Section = "Maps";
    /// <summary>Allow external WMS servers on private / loopback addresses (development only).</summary>
    public bool AllowPrivateNetworks { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
}

public class MapManagerOptions
{
    public const string Section = "MapManager";
    /// <summary>
    /// Service tokens by reference (organization.mapmanager_token_ref → token). The database only holds the
    /// reference; the token comes from the environment / secret store, e.g. MapManager__Tokens__demo=mmst_…
    /// </summary>
    public Dictionary<string, string> Tokens { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Outbound HTTP clients of the map proxies.
/// <list type="bullet">
/// <item><see cref="MapManagerClientName"/>: the MapManager URL is set by the platform admin (trusted), it may be on the
/// internal network (e.g. the same compose network).</item>
/// <item><see cref="ExternalWmsClientName"/>: the URL is set by an organization admin, so the connection is refused to
/// loopback, private and link-local addresses (SSRF: the API must not become a door to the internal network). The
/// check runs on the resolved address at connect time, so DNS tricks do not bypass it – also for redirects, which are
/// followed (at most 3; e.g. http → https or a moved service): every connection goes through the same check.</item>
/// </list>
/// </summary>
public static class OutboundHttp
{
    public const string MapManagerClientName = "mapmanager";
    public const string ExternalWmsClientName = "external-wms";

    public static IServiceCollection AddMapHttpClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MapsOptions>(configuration.GetSection(MapsOptions.Section));
        services.Configure<MapManagerOptions>(configuration.GetSection(MapManagerOptions.Section));
        var maps = configuration.GetSection(MapsOptions.Section).Get<MapsOptions>() ?? new();
        var mapManager = configuration.GetSection(MapManagerOptions.Section).Get<MapManagerOptions>() ?? new();

        services.AddHttpClient(MapManagerClientName, c => c.Timeout = TimeSpan.FromSeconds(mapManager.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient(ExternalWmsClientName, c => c.Timeout = TimeSpan.FromSeconds(maps.TimeoutSeconds))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 3,
                ConnectCallback = maps.AllowPrivateNetworks ? null : ConnectPublicOnlyAsync,
            });
        return services;
    }

    private static async ValueTask<Stream> ConnectPublicOnlyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        // IPv4 first: a server's IPv6 address is often unreachable from a container without IPv6.
        var allowed = addresses.Where(IsPublic).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException($"Connection to {context.DnsEndPoint.Host} refused: not a public address.");

        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            // A silent address must not hold the map for the whole request timeout.
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(ConnectTimeout);
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw new HttpRequestException($"Cannot connect to {context.DnsEndPoint.Host}:{context.DnsEndPoint.Port}.", last);
    }

    /// <summary>False for loopback, private (RFC 1918, ULA), link-local, CGNAT, unspecified and multicast addresses.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
        }
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
    }
}
