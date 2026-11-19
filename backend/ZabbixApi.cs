using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Zabbix;

/// <summary>Why a Zabbix call failed, as the plugin's endpoints answer it.</summary>
internal enum ZabbixFailure
{
    /// <summary>Zabbix refused the API token.</summary>
    TokenRefused,

    /// <summary>The address doesn't answer like a Zabbix API, or Zabbix answered with an error.</summary>
    Error,

    /// <summary>Zabbix didn't answer in time.</summary>
    Timeout,

    /// <summary>Zabbix couldn't be reached, or its certificate isn't the trusted one.</summary>
    Unreachable,

    /// <summary>Zabbix has no host with that ID, or the token can't see it.</summary>
    HostNotFound,
}

internal sealed class ZabbixException(ZabbixFailure failure, string message) : Exception(message)
{
    public ZabbixFailure Failure { get; } = failure;
}

/// <summary>
/// The Zabbix JSON-RPC API (<c>api_jsonrpc.php</c>) over an HTTPS connection core opened (docs/architecture.md §7.40),
/// ported from 1.x's <c>ZabbixClient</c>. Core checks the certificate and, on a signed-in connection, sends the API
/// token as <c>Authorization: Bearer</c>; this plugin never sees it.
/// </summary>
internal sealed class ZabbixApi(IHttpConnection http, string path, string display)
{
    public const string ApiPage = "api_jsonrpc.php";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly MediaTypeHeaderValue JsonRpc = new("application/json-rpc");
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web);
    private int _nextId;

    /// <summary>The Zabbix frontend's path on the service, as the <c>path</c> setting says: <c>/zabbix/</c> by default, always with slashes round it.</summary>
    public static string FrontendPath(HostContext host, Guid serviceId)
    {
        var setting = host.Services.FirstOrDefault(s => s.Id == serviceId)?.Settings is { ValueKind: JsonValueKind.Object } settings
            && settings.TryGetProperty(ZabbixPlugin.PathSetting, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        return NormalizePath(setting);
    }

    /// <summary>A path of plain URL characters, with a slash at each end; <c>/zabbix/</c> when there's none, or it isn't one.</summary>
    public static string NormalizePath(string? setting)
    {
        var path = setting?.Trim() ?? string.Empty;
        if (path.Length == 0)
        {
            return ZabbixPlugin.DefaultPath + "/";
        }

        if (path.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~')) || path.Contains("..", StringComparison.Ordinal))
        {
            return ZabbixPlugin.DefaultPath + "/";
        }

        // 1.x accepted the frontend's address with a page in it, such as .../zabbix.php: keep the directory.
        var lastSlash = path.LastIndexOf('/');
        if (path[(lastSlash + 1)..].EndsWith(".php", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..(lastSlash + 1)];
        }

        path = "/" + path.Trim('/');
        return path == "/" ? "/" : path + "/";
    }

    /// <summary>The server's API version, e.g. "7.0.5". Zabbix refuses this call with a token, so it goes on a connection without one.</summary>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken) =>
        Text(await CallAsync("apiinfo.version", new { }, cancellationToken)) ?? string.Empty;

    /// <summary>Calls an API method and returns its <c>result</c>.</summary>
    /// <exception cref="ZabbixException">Zabbix couldn't be reached, refused the token, or answered with an error.</exception>
    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(http.BaseAddress, path + ApiPage))
        {
            // Sent whole, with its length, rather than chunked.
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters, id = Interlocked.Increment(ref _nextId) }, RequestJson), JsonRpc),
        };

        using var response = await SendAsync(request, cancellationToken);
        JsonElement body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        }
        catch (JsonException)
        {
            throw new ZabbixException(
                ZabbixFailure.Error,
                response.IsSuccessStatusCode
                    ? $"{display} didn't answer like a Zabbix API. Check the service's path."
                    : $"{display} refused the request ({(int)response.StatusCode}). Check the service's path.");
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ZabbixException(ZabbixFailure.Error, $"{display} didn't answer like a Zabbix API. Check the service's path.");
        }

        if (body.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw ToException(error);
        }

        return body.TryGetProperty("result", out var result)
            ? result.Clone()
            : throw new ZabbixException(ZabbixFailure.Error, $"{display} didn't answer like a Zabbix API. Check the service's path.");
    }

    /// <summary>Zabbix sends numbers as strings, but take either.</summary>
    public static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await http.Client.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ZabbixException(ZabbixFailure.Timeout, $"Zabbix at {display} didn't answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new ZabbixException(
                ZabbixFailure.Unreachable,
                ex.InnerException is System.Security.Authentication.AuthenticationException
                    ? $"The certificate of {display} isn't the one trusted for it, so the connection was refused."
                    : $"Couldn't connect to Zabbix at {display}.");
        }
    }

    /// <summary>Zabbix errors are <c>{code, message, data}</c>; <c>data</c> says what was wrong.</summary>
    private ZabbixException ToException(JsonElement error)
    {
        var data = error.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var detail = data ?? message ?? "unknown error";
        return detail.Contains("Not authorized", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Not authorised", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("re-login", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("API token expired", StringComparison.OrdinalIgnoreCase)
                ? new ZabbixException(ZabbixFailure.TokenRefused, $"Zabbix at {display} refused the API token: {detail}")
                : new ZabbixException(ZabbixFailure.Error, $"Zabbix at {display} answered with an error: {detail}");
    }
}
