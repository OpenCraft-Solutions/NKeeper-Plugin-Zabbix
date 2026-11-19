using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NKeeper.Plugins.Zabbix;

public sealed record ZabbixTag(string Name, string? Value);

/// <summary>A host in Zabbix, as searches and the comparison list it.</summary>
/// <param name="HostId">Zabbix's ID for it.</param>
/// <param name="Host">Its technical name.</param>
/// <param name="Name">Its visible name.</param>
/// <param name="MacAddresses">From its inventory (MAC address A and B).</param>
public sealed record ZabbixHostSummary(
    string HostId,
    string Host,
    string Name,
    bool Monitored,
    IReadOnlyList<string> IpAddresses,
    IReadOnlyList<string> DnsNames,
    IReadOnlyList<string> MacAddresses,
    string? OperatingSystem,
    IReadOnlyList<ZabbixTag> Tags);

/// <summary>A Zabbix host with what adding it to NKeeper takes, as well as listing it.</summary>
public sealed record ZabbixHostRecord(ZabbixHostSummary Summary, string? Description, IReadOnlyList<string> Groups, string? SerialNumber);

/// <param name="Type">Agent, SNMP, IPMI or JMX.</param>
/// <param name="Availability">Available, Unavailable or Unknown.</param>
public sealed record ZabbixInterface(string Type, string? IpAddress, string? Dns, string Port, bool UseIp, bool Main, string Availability, string? Error);

/// <summary>An item's latest value.</summary>
/// <param name="Units">As Zabbix keeps them, e.g. "%", "B", "uptime".</param>
public sealed record ZabbixItem(string Key, string Name, string? Value, string? Units, DateTimeOffset? CheckedAtUtc);

/// <param name="Severity">0 not classified, 1 information, 2 warning, 3 average, 4 high, 5 disaster.</param>
public sealed record ZabbixProblem(string EventId, string Name, int Severity, DateTimeOffset StartedAtUtc, bool Acknowledged, bool Suppressed);

/// <summary>What Zabbix knows about one host.</summary>
/// <param name="Availability">From its interfaces: Available, Unavailable, Unknown or Mixed.</param>
/// <param name="Inventory">Its inventory fields with a value, by Zabbix's field name; empty when inventory is off.</param>
/// <param name="Problems">Unresolved, newest first.</param>
public sealed record ZabbixHostOverview(
    ZabbixHostSummary Host,
    string? Description,
    bool InMaintenance,
    string Availability,
    IReadOnlyList<ZabbixInterface> Interfaces,
    IReadOnlyList<string> Groups,
    IReadOnlyDictionary<string, string> Inventory,
    IReadOnlyList<ZabbixItem> Items,
    IReadOnlyList<ZabbixProblem> Problems);

/// <summary>The Zabbix API calls the plugin makes (host.get, hostinterface.get, item.get, problem.get), ported from 1.x's <c>ZabbixQueries</c>.</summary>
internal static partial class ZabbixQueries
{
    /// <summary>Items worth showing when a host has them, from Zabbix's own templates (Linux, Windows, generic agent and ICMP).</summary>
    public static readonly string[] WellKnownItemKeys =
    [
        "system.hostname",
        "system.uname",
        "system.sw.os",
        "system.sw.arch",
        "system.uptime",
        "system.cpu.num",
        "system.cpu.util",
        "system.cpu.load[all,avg1]",
        "vm.memory.size[total]",
        "vm.memory.size[available]",
        "vm.memory.size[pavailable]",
        "vm.memory.utilization",
        "vm.memory.util",
        "system.swap.size[,pfree]",
        "vfs.fs.dependent.size[/,pused]",
        "vfs.fs.size[/,pused]",
        "vfs.fs.dependent.size[C:,pused]",
        "vfs.fs.size[C:,pused]",
        "agent.version",
        "agent.ping",
        "icmpping",
        "icmppingsec",
        "icmppingloss",
    ];

    private static readonly string[] HostOutput = ["hostid", "host", "name", "status", "description", "maintenance_status"];
    private static readonly string[] InterfaceOutput = ["type", "ip", "dns", "port", "useip", "main", "available", "error"];
    private static readonly string[] SummaryInventory = ["os", "macaddress_a", "macaddress_b", "serialno_a"];
    private static readonly string[] TagOutput = ["tag", "value"];
    private static readonly string[] GroupOutput = ["name"];
    private static readonly string[] IdOutput = ["hostid"];
    private static readonly string[] ItemOutput = ["key_", "name", "lastvalue", "units", "lastclock"];
    private static readonly string[] ProblemOutput = ["eventid", "name", "severity", "clock", "acknowledged", "suppressed"];
    private static readonly string[] ProblemSort = ["eventid"];

    /// <summary>How many hosts the token can see, to test the binding.</summary>
    public static async Task<int> CountHostsAsync(ZabbixApi api, CancellationToken cancellationToken)
    {
        var result = await api.CallAsync("host.get", new { countOutput = true }, cancellationToken);
        return int.TryParse(ZabbixApi.Text(result), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    /// <summary>Every host the token can see (or those with <paramref name="hostIds"/>), with interfaces, groups, tags and a few inventory fields.</summary>
    public static async Task<List<ZabbixHostRecord>> ListHostsAsync(ZabbixApi api, CancellationToken cancellationToken, IReadOnlyCollection<string>? hostIds = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["output"] = HostOutput,
            ["selectInterfaces"] = InterfaceOutput,
            ["selectInventory"] = SummaryInventory,
            ["selectTags"] = TagOutput,
            ["selectHostGroups"] = GroupOutput,
            ["sortfield"] = "name",
        };
        if (hostIds is not null)
        {
            parameters["hostids"] = hostIds;
        }

        return [.. Array(await api.CallAsync("host.get", parameters, cancellationToken)).Select(ToRecord)];
    }

    /// <summary>Hosts whose visible or technical name, IP address or DNS name contains the text, at most <paramref name="limit"/>.</summary>
    public static async Task<List<ZabbixHostSummary>> SearchAsync(ZabbixApi api, string text, int limit, CancellationToken cancellationToken)
    {
        var byName = await api.CallAsync("host.get", new { output = IdOutput, search = new { name = text, host = text }, searchByAny = true, limit }, cancellationToken);
        var byInterface = await api.CallAsync("hostinterface.get", new { output = IdOutput, search = new { ip = text, dns = text }, searchByAny = true, limit }, cancellationToken);

        var ids = Array(byName).Concat(Array(byInterface)).Select(h => String(h, "hostid")).OfType<string>().Distinct().Take(limit).ToList();
        return ids.Count == 0 ? [] : [.. (await ListHostsAsync(api, cancellationToken, ids)).Select(r => r.Summary)];
    }

    /// <summary>One host's details, latest well-known values and unresolved problems.</summary>
    /// <exception cref="ZabbixException">With <see cref="ZabbixFailure.HostNotFound"/> when the token can't see a host with that ID.</exception>
    public static async Task<ZabbixHostOverview> GetOverviewAsync(ZabbixApi api, string hostId, CancellationToken cancellationToken)
    {
        var hosts = await api.CallAsync("host.get", new
        {
            hostids = new[] { hostId },
            output = HostOutput,
            selectInterfaces = InterfaceOutput,
            selectInventory = "extend",
            selectTags = TagOutput,
            selectHostGroups = GroupOutput,
        }, cancellationToken);
        var host = Array(hosts).FirstOrDefault();
        if (host.ValueKind != JsonValueKind.Object)
        {
            throw new ZabbixException(ZabbixFailure.HostNotFound, $"Zabbix has no host with ID {hostId}, or the API token can't see it.");
        }

        var items = await api.CallAsync("item.get", new { hostids = new[] { hostId }, output = ItemOutput, filter = new { key_ = WellKnownItemKeys }, webitems = true }, cancellationToken);
        var problems = await api.CallAsync("problem.get", new { hostids = new[] { hostId }, output = ProblemOutput, sortfield = ProblemSort, sortorder = "DESC", limit = 100 }, cancellationToken);

        var record = ToRecord(host);
        var interfaces = Array(host, "interfaces").Select(ToInterface).ToList();
        return new ZabbixHostOverview(
            record.Summary,
            record.Description,
            String(host, "maintenance_status") == "1",
            OverallAvailability(interfaces),
            interfaces,
            record.Groups,
            Inventory(host),
            [.. Array(items)
                .Select(i => new ZabbixItem(String(i, "key_") ?? string.Empty, String(i, "name") ?? string.Empty, String(i, "lastvalue"), String(i, "units"), UnixTime(String(i, "lastclock"))))
                // Never collected: lastclock is 0.
                .Where(i => i.CheckedAtUtc is not null)
                .OrderBy(i => System.Array.IndexOf(WellKnownItemKeys, i.Key))],
            [.. Array(problems).Select(p => new ZabbixProblem(
                String(p, "eventid") ?? string.Empty,
                String(p, "name") ?? string.Empty,
                int.TryParse(String(p, "severity"), CultureInfo.InvariantCulture, out var severity) ? severity : 0,
                UnixTime(String(p, "clock")) ?? DateTimeOffset.UnixEpoch,
                String(p, "acknowledged") == "1",
                String(p, "suppressed") == "1"))]);
    }

    internal static ZabbixHostRecord ToRecord(JsonElement host)
    {
        var interfaces = Array(host, "interfaces").ToList();
        var inventory = host.TryGetProperty("inventory", out var inv) && inv.ValueKind == JsonValueKind.Object ? inv : default;
        var macs = new List<string>();
        foreach (var field in new[] { "macaddress_a", "macaddress_b" })
        {
            // Inventory fields are free text; keep what reads as a MAC address.
            if (inventory.ValueKind == JsonValueKind.Object && NormalizeMac(String(inventory, field)) is { } mac && !macs.Contains(mac))
            {
                macs.Add(mac);
            }
        }

        // The main interface first.
        var ordered = interfaces.OrderByDescending(i => String(i, "main") == "1").ToList();
        var summary = new ZabbixHostSummary(
            String(host, "hostid") ?? string.Empty,
            String(host, "host") ?? string.Empty,
            String(host, "name") ?? String(host, "host") ?? string.Empty,
            String(host, "status") == "0",
            [.. ordered.Select(i => NormalizeIp(String(i, "ip"))).OfType<string>().Where(ip => ip is not "127.0.0.1" and not "::1").Distinct()],
            [.. ordered.Select(i => TrimToNull(String(i, "dns"))).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)],
            macs,
            inventory.ValueKind == JsonValueKind.Object ? TrimToNull(String(inventory, "os")) : null,
            [.. Array(host, "tags").Select(t => new ZabbixTag(String(t, "tag") ?? string.Empty, TrimToNull(String(t, "value"))))]);
        return new ZabbixHostRecord(
            summary,
            TrimToNull(String(host, "description")),
            [.. Array(host, "hostgroups").Select(g => String(g, "name")).OfType<string>()],
            inventory.ValueKind == JsonValueKind.Object ? TrimToNull(String(inventory, "serialno_a")) : null);
    }

    private static ZabbixInterface ToInterface(JsonElement item) => new(
        String(item, "type") switch { "1" => "Agent", "2" => "SNMP", "3" => "IPMI", "4" => "JMX", var other => other ?? "Unknown" },
        TrimToNull(String(item, "ip")),
        TrimToNull(String(item, "dns")),
        String(item, "port") ?? string.Empty,
        String(item, "useip") == "1",
        String(item, "main") == "1",
        String(item, "available") switch { "1" => "Available", "2" => "Unavailable", _ => "Unknown" },
        TrimToNull(String(item, "error")));

    /// <summary>Available when every interface with a known state is, Unavailable when none is, Mixed in between.</summary>
    internal static string OverallAvailability(IReadOnlyList<ZabbixInterface> interfaces)
    {
        var known = interfaces.Where(i => i.Availability != "Unknown").Select(i => i.Availability).Distinct().ToList();
        return known.Count switch
        {
            0 => "Unknown",
            1 => known[0],
            _ => "Mixed",
        };
    }

    /// <summary>The inventory fields with a value; Zabbix answers [] instead of an object when inventory is off.</summary>
    private static Dictionary<string, string> Inventory(JsonElement host)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (host.TryGetProperty("inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in inventory.EnumerateObject())
            {
                if (property.Name is not ("hostid" or "inventory_mode") && property.Value.ValueKind == JsonValueKind.String && TrimToNull(property.Value.GetString()) is { } value)
                {
                    fields[property.Name] = value;
                }
            }
        }

        return fields;
    }

    /// <summary>An IPv4 address in full dotted form (not shorthand such as "10.1"), or an IPv6 address, normalized.</summary>
    internal static string? NormalizeIp(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (!candidate.Contains(':', StringComparison.Ordinal) && !Ipv4().IsMatch(candidate))
        {
            return null;
        }

        return IPAddress.TryParse(candidate, out var address) && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            ? address.ToString()
            : null;
    }

    /// <summary>A MAC address with any of the usual separators, as six upper-case pairs joined by colons.</summary>
    internal static string? NormalizeMac(string? value)
    {
        var hex = MacSeparators().Replace(value?.Trim() ?? string.Empty, string.Empty);
        return MacHex().IsMatch(hex) ? string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2))).ToUpperInvariant() : null;
    }

    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? UnixTime(string? seconds) =>
        long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? DateTimeOffset.FromUnixTimeSeconds(value) : null;

    private static IEnumerable<JsonElement> Array(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static IEnumerable<JsonElement> Array(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? Array(value) : [];

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? ZabbixApi.Text(value) : null;

    [GeneratedRegex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$")]
    private static partial Regex Ipv4();

    [GeneratedRegex("[:\\-. ]")]
    private static partial Regex MacSeparators();

    [GeneratedRegex("^[0-9A-Fa-f]{12}$")]
    private static partial Regex MacHex();
}
