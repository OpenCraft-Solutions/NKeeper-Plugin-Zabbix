using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Zabbix;

public sealed record ZabbixLinkRequest(string? ZabbixHostId);

/// <summary>An NKeeper host to link to a Zabbix host, or with no Zabbix host ID, to unlink.</summary>
public sealed record ZabbixHostLink(Guid HostId, string? ZabbixHostId);

public sealed record ZabbixLinksRequest(IReadOnlyList<ZabbixHostLink>? Links);

/// <summary>
/// The plugin's endpoints, under <c>/api/p/zabbix/</c> (docs/architecture.md §7.40), ported from 1.x's
/// <c>ZabbixController</c>. Every one that reads Zabbix resolves the integration first, so a user who may not use the
/// Zabbix server's host gets nothing from it (§7.39).
/// </summary>
internal static class ZabbixEndpoints
{
    public const int SearchLimit = 50;
    public const int MaxLinks = 5000;
    public const int MaxZabbixHostIdLength = 20;
    private const int LinkVersion = 1;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // Administrators: test the binding, and Zabbix's hosts with the links, for the comparison and adding hosts.
        var admin = endpoints.MapAdministratorGroup();
        admin.MapGet("/test", (ZabbixServer zabbix, CancellationToken ct) => AnswerAsync(async () =>
        {
            var (version, count) = await zabbix.TestAsync(ct);
            return Results.Json(new { version, hostCount = count });
        }));

        admin.MapGet("/hosts", (ZabbixServer zabbix, IHostData data, CancellationToken ct) => AnswerAsync(async () =>
        {
            var (hosts, frontend) = await zabbix.WithApiAsync(async (api, integration) => (await ZabbixQueries.ListHostsAsync(api, ct), ZabbixServer.FrontendUrl(integration)), ct);
            var links = (await data.AllAsync(ct)).Select(l => new ZabbixHostLink(l.Key, LinkedId(l.Value))).Where(l => l.ZabbixHostId is not null);
            return Results.Json(new { frontendUrl = frontend, hosts, links });
        }));

        admin.MapPost("/links", async (ZabbixLinksRequest request, IHostData data, IPluginAudit audit, CancellationToken ct) =>
        {
            if (request.Links is not { Count: > 0 and <= MaxLinks } links)
            {
                return Invalid($"Link between 1 and {MaxLinks} hosts at a time.");
            }

            if (links.FirstOrDefault(l => l.ZabbixHostId is not null && !IsZabbixHostId(l.ZabbixHostId)) is { } bad)
            {
                return Invalid($"“{bad.ZabbixHostId}” isn't a Zabbix host ID: it's a number.");
            }

            foreach (var link in links)
            {
                var problem = await LinkAsync(link.HostId, link.ZabbixHostId, data, audit, ct);
                if (problem is not null)
                {
                    return problem;
                }
            }

            return Results.NoContent();
        });

        // A host's panel: anyone who may use the host, and the Zabbix server's host.
        var use = endpoints.MapHostGroup(HostPermission.Use);
        use.MapGet("/overview", (HostContext host, ZabbixServer zabbix, IHostData data, CancellationToken ct) => AnswerAsync(async () =>
        {
            var linked = LinkedId(await data.GetAsync(host.Id, ct));
            return await zabbix.WithApiAsync(async (api, integration) => linked is null
                ? Results.Json(new { linked = false, canLink = host.Permission == HostPermission.Manage })
                : Results.Json(new
                {
                    linked = true,
                    canLink = host.Permission == HostPermission.Manage,
                    zabbixHostId = linked,
                    frontendUrl = ZabbixServer.FrontendUrl(integration),
                    overview = await ZabbixQueries.GetOverviewAsync(api, linked, ct),
                }), ct);
        }));

        // Linking: someone who can manage the host.
        var manage = endpoints.MapHostGroup(HostPermission.Manage);
        manage.MapGet("/search", (string? text, ZabbixServer zabbix, CancellationToken ct) => AnswerAsync(async () =>
        {
            var search = text?.Trim() ?? string.Empty;
            return search.Length is 0 or > 255
                ? Results.Json(Array.Empty<ZabbixHostSummary>())
                : Results.Json(await zabbix.WithApiAsync((api, _) => ZabbixQueries.SearchAsync(api, search, SearchLimit, ct), ct));
        }));

        manage.MapPut("/link", async (HostContext host, ZabbixLinkRequest request, IHostData data, IPluginAudit audit, CancellationToken ct) =>
            request.ZabbixHostId is { } id && IsZabbixHostId(id)
                ? await LinkAsync(host.Id, id, data, audit, ct) ?? Results.NoContent()
                : Invalid("A Zabbix host ID is a number."));

        manage.MapDelete("/link", async (HostContext host, IHostData data, IPluginAudit audit, CancellationToken ct) =>
            await LinkAsync(host.Id, null, data, audit, ct) ?? Results.NoContent());
    }

    internal static bool IsZabbixHostId(string id) => id.Length is > 0 and <= MaxZabbixHostIdLength && id.All(char.IsAsciiDigit);

    /// <summary>The Zabbix host a host's data links it to; null when it isn't linked.</summary>
    internal static string? LinkedId(PluginDocument? document) =>
        document is { Data.ValueKind: JsonValueKind.Object } && document.Data.TryGetProperty("zabbixHostId", out var id) && id.ValueKind == JsonValueKind.String
            && id.GetString() is { } text && IsZabbixHostId(text)
                ? text
                : null;

    /// <summary>Links (or with null, unlinks) a host, audited as <c>zabbix.link</c> or <c>zabbix.unlink</c>; a problem for a host that's gone.</summary>
    private static async Task<IResult?> LinkAsync(Guid hostId, string? zabbixHostId, IHostData data, IPluginAudit audit, CancellationToken ct)
    {
        if (zabbixHostId is null)
        {
            var before = LinkedId(await data.GetAsync(hostId, ct));
            if (before is not null)
            {
                await data.ClearAsync(hostId, ct);
                await audit.RecordAsync("zabbix.unlink", hostId, $"Zabbix host {before}", ct);
            }

            return null;
        }

        try
        {
            await data.SetAsync(hostId, LinkVersion, new { zabbixHostId }, ct);
        }
        catch (ArgumentException)
        {
            return Results.Problem("There's no such host.", statusCode: StatusCodes.Status404NotFound);
        }

        await audit.RecordAsync("zabbix.link", hostId, $"Zabbix host {zabbixHostId}", ct);
        return null;
    }

    private static IResult Invalid(string detail) => Results.Problem(detail, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>What went wrong, as the panels show it, with a code they can tell apart.</summary>
    private static async Task<IResult> AnswerAsync(Func<Task<IResult>> run)
    {
        try
        {
            return await run();
        }
        catch (IntegrationUnavailableException ex)
        {
            return Problem(StatusCodes.Status409Conflict, ex.Message, ex.Reason switch
            {
                IntegrationUnavailableReason.NotBound => "zabbix_not_bound",
                IntegrationUnavailableReason.Gone => "zabbix_gone",
                _ => "zabbix_no_access",
            });
        }
        catch (CredentialDeniedException ex)
        {
            return Problem(StatusCodes.Status409Conflict, ex.Message, "zabbix_no_access");
        }
        catch (ConnectException ex) when (ex.Presented is { } presented)
        {
            return Problem(StatusCodes.Status502BadGateway, ex.Message, "host_key_unknown", presented);
        }
        catch (ConnectException ex)
        {
            return Problem(StatusCodes.Status502BadGateway, ex.Message, "zabbix_unreachable");
        }
        catch (ZabbixException ex)
        {
            return ex.Failure switch
            {
                ZabbixFailure.HostNotFound => Problem(StatusCodes.Status404NotFound, ex.Message, "zabbix_host_not_found"),
                ZabbixFailure.TokenRefused => Problem(StatusCodes.Status502BadGateway, ex.Message, "zabbix_token_refused"),
                ZabbixFailure.Timeout => Problem(StatusCodes.Status504GatewayTimeout, ex.Message, "zabbix_timeout"),
                ZabbixFailure.Unreachable => Problem(StatusCodes.Status502BadGateway, ex.Message, "zabbix_unreachable"),
                _ => Problem(StatusCodes.Status502BadGateway, ex.Message, "zabbix_error"),
            };
        }
    }

    private static IResult Problem(int status, string detail, string code, PresentedIdentity? presented = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (presented is not null)
        {
            extensions["presented"] = presented;
        }

        return Results.Problem(detail, statusCode: status, extensions: extensions);
    }
}
