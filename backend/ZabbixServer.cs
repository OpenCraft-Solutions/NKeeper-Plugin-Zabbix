using NKeeper.Abstractions;

namespace NKeeper.Plugins.Zabbix;

/// <summary>
/// The bound Zabbix server (docs/architecture.md §7.39, §7.40), for this request's user: core resolves the integration
/// only for users who may use its host, then opens HTTPS to it, signing in with the credential's API token.
/// </summary>
public sealed class ZabbixServer(ICredentialBroker broker, IConnectivity connectivity)
{
    /// <summary>Runs <paramref name="action"/> against the Zabbix API, signed in.</summary>
    /// <exception cref="IntegrationUnavailableException">It isn't bound, is gone, or the user may not use its host.</exception>
    /// <exception cref="ConnectException">Core couldn't connect: an untrusted certificate, say.</exception>
    /// <exception cref="ZabbixException">Zabbix refused the token, or answered with an error.</exception>
    internal async Task<T> WithApiAsync<T>(Func<ZabbixApi, IntegrationHandle, Task<T>> action, CancellationToken cancellationToken)
    {
        var integration = await broker.ResolveIntegrationAsync(ZabbixPlugin.IntegrationName, cancellationToken);
        await using var http = await connectivity.OpenHttpAsync(integration.Host, integration.ServiceId, integration.Credential, HttpSignIn.Bearer, cancellationToken);
        return await action(Api(integration, http), integration);
    }

    /// <summary>The server's API version, which Zabbix answers only without a token; then how many hosts the token can see.</summary>
    internal async Task<(string Version, int HostCount)> TestAsync(CancellationToken cancellationToken)
    {
        var integration = await broker.ResolveIntegrationAsync(ZabbixPlugin.IntegrationName, cancellationToken);
        string version;
        await using (var anonymous = await connectivity.OpenHttpAsync(integration.Host, integration.ServiceId, tls: true, cancellationToken))
        {
            version = await Api(integration, anonymous).GetVersionAsync(cancellationToken);
        }

        await using var http = await connectivity.OpenHttpAsync(integration.Host, integration.ServiceId, integration.Credential, HttpSignIn.Bearer, cancellationToken);
        return (version, await ZabbixQueries.CountHostsAsync(Api(integration, http), cancellationToken));
    }

    /// <summary>The Zabbix frontend's address, for "Open in Zabbix" links: the service's address, port and path.</summary>
    internal static Uri FrontendUrl(IntegrationHandle integration)
    {
        var service = integration.Host.Services.FirstOrDefault(s => s.Id == integration.ServiceId);
        var address = service?.Address ?? integration.Host.Hostname ?? integration.Host.IpAddress ?? integration.Host.Name;
        return new UriBuilder(Uri.UriSchemeHttps, address, service?.Port ?? 443, ZabbixApi.FrontendPath(integration.Host, integration.ServiceId)).Uri;
    }

    private static ZabbixApi Api(IntegrationHandle integration, IHttpConnection http)
    {
        var service = integration.Host.Services.FirstOrDefault(s => s.Id == integration.ServiceId);
        var display = service?.Address is { } address ? $"{address}:{service.Port ?? 443}" : integration.Host.Name;
        return new ZabbixApi(http, ZabbixApi.FrontendPath(integration.Host, integration.ServiceId), display);
    }
}
