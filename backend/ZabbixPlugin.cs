using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Zabbix;

/// <summary>
/// The zabbix plugin (docs/architecture.md §7.40): the <c>zabbix</c> service type, the <c>server</c> integration an
/// administrator binds to a Zabbix server's host, a host's Zabbix panel, and the comparison of Zabbix's hosts with
/// NKeeper's. Read only: it never writes to Zabbix. Core signs in with the bound credential's API token (SDK 1.13);
/// this plugin never sees it.
/// </summary>
public sealed class ZabbixPlugin : IPlugin
{
    public const string ServiceTypeId = "zabbix";
    public const string IntegrationName = "server";
    public const string PathSetting = "path";
    public const string DefaultPath = "/zabbix";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType(ServiceTypeId, "Zabbix")
        {
            DefaultPort = 443,
            Health = HealthProbe.Tls,
            Settings = [ServiceSetting.Text(PathSetting, "Path of the Zabbix frontend (default /zabbix)", maxLength: 200)],
        });
        contributions.AddIntegration(new Integration(IntegrationName, "Zabbix server") { ServiceTypes = [ServiceTypeId] });
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) => services.AddScoped<ZabbixServer>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context) => ZabbixEndpoints.Map(endpoints);
}
