using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace NKeeper.Plugins.Zabbix.Tests;

/// <summary>
/// A Zabbix 7 frontend's JSON-RPC API over HTTPS with a self-signed certificate, at <c>/zabbix/api_jsonrpc.php</c>.
/// Like Zabbix, it answers <c>apiinfo.version</c> only without a token, and everything else only with
/// <c>Authorization: Bearer</c> and <see cref="Token"/>. It has two hosts, web01 and db01.
/// </summary>
internal sealed class FakeZabbix : IAsyncDisposable
{
    public const string Token = "zbx-api-token";
    public const string Web01 = "10084";
    public const string Db01 = "10085";

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _certificate;
    private readonly Task _serving;

    public FakeZabbix()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=zabbix.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        _listener.Start();
        _serving = ServeAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The methods called, in order, with whether they carried the token.</summary>
    public List<(string Method, bool Signed)> Calls { get; } = [];

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(client));
        }
    }

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(_certificate);
                using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
                var first = (await reader.ReadLineAsync())?.Split(' ') ?? [];
                string? authorization = null;
                var length = 0;
                for (var line = await reader.ReadLineAsync(); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync())
                {
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    var (name, value) = (line[..colon].Trim(), line[(colon + 1)..].Trim());
                    if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        authorization = value;
                    }
                    else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        length = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                var body = new char[length];
                if (length > 0)
                {
                    await reader.ReadBlockAsync(body);
                }

                var (status, json) = first.ElementAtOrDefault(0) == "POST" && first.ElementAtOrDefault(1) == "/zabbix/api_jsonrpc.php"
                    ? (200, Answer(new string(body), authorization))
                    : (404, "<html>Not found</html>");
                var bytes = Encoding.UTF8.GetBytes(json);
                await tls.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"));
                await tls.WriteAsync(bytes);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or ObjectDisposedException)
            {
                // A handshake that only reads the certificate, or a client that went away.
            }
        }
    }

    private string Answer(string body, string? authorization)
    {
        using var request = JsonDocument.Parse(body);
        var method = request.RootElement.GetProperty("method").GetString()!;
        var parameters = request.RootElement.GetProperty("params");
        var id = request.RootElement.GetProperty("id").GetInt32();
        lock (Calls)
        {
            Calls.Add((method, authorization is not null));
        }

        if (method == "apiinfo.version")
        {
            return authorization is null
                ? Result(id, "\"7.0.5\"")
                : Error(id, "Invalid params.", "The \"apiinfo.version\" method must be called without the \"auth\" parameter.");
        }

        if (authorization != $"Bearer {Token}")
        {
            return Error(id, "Invalid params.", "Not authorized.");
        }

        return method switch
        {
            "host.get" when parameters.TryGetProperty("countOutput", out _) => Result(id, "\"2\""),
            "host.get" when parameters.TryGetProperty("search", out var search) => Result(id, Ids(Hosts().Where(h => h.Name.Contains(search.GetProperty("name").GetString()!, StringComparison.OrdinalIgnoreCase)))),
            "host.get" => Result(id, $"[{string.Join(',', Hosts().Where(h => Wanted(parameters, h.Id)).Select(h => h.Json))}]"),
            "hostinterface.get" => Result(id, Ids(Hosts().Where(h => h.Ip.Contains(parameters.GetProperty("search").GetProperty("ip").GetString()!, StringComparison.Ordinal)))),
            "item.get" => Result(id, """
                [{ "key_": "system.uptime", "name": "System uptime", "lastvalue": "3600", "units": "uptime", "lastclock": "1763500000" },
                 { "key_": "system.cpu.num", "name": "Number of CPUs", "lastvalue": "4", "units": "", "lastclock": "1763500000" },
                 { "key_": "agent.ping", "name": "Agent ping", "lastvalue": "0", "units": "", "lastclock": "0" }]
                """),
            "problem.get" => Result(id, """[{ "eventid": "501", "name": "High CPU utilization", "severity": "4", "clock": "1763400000", "acknowledged": "0", "suppressed": "0" }]"""),
            _ => Error(id, "Invalid params.", $"Unknown method {method}."),
        };
    }

    private static bool Wanted(JsonElement parameters, string id) =>
        !parameters.TryGetProperty("hostids", out var ids) || ids.EnumerateArray().Any(i => i.GetString() == id);

    private static string Ids(IEnumerable<(string Id, string Name, string Ip, string Json)> hosts) =>
        $"[{string.Join(',', hosts.Select(h => $$"""{ "hostid": "{{h.Id}}" }"""))}]";

    private static (string Id, string Name, string Ip, string Json)[] Hosts() =>
    [
        (Web01, "web01", "192.0.2.10", $$"""
            { "hostid": "{{Web01}}", "host": "web01", "name": "web01", "status": "0", "description": "Front end", "maintenance_status": "0",
              "interfaces": [{ "type": "1", "ip": "192.0.2.10", "dns": "web01.example.com", "port": "10050", "useip": "1", "main": "1", "available": "1", "error": "" }],
              "inventory": { "os": "Debian 12", "macaddress_a": "aa-bb-cc-dd-ee-01", "serialno_a": "SN-WEB" },
              "tags": [{ "tag": "env", "value": "prod" }], "hostgroups": [{ "name": "Linux servers" }] }
            """),
        (Db01, "db01", "192.0.2.20", $$"""
            { "hostid": "{{Db01}}", "host": "db01", "name": "db01", "status": "0", "description": "", "maintenance_status": "1",
              "interfaces": [{ "type": "1", "ip": "192.0.2.20", "dns": "", "port": "10050", "useip": "1", "main": "1", "available": "2", "error": "Timed out" }],
              "inventory": [], "tags": [], "hostgroups": [{ "name": "Databases" }] }
            """),
    ];

    private static string Result(int id, string result) => $$"""{ "jsonrpc": "2.0", "result": {{result}}, "id": {{id}} }""";

    private static string Error(int id, string message, string data) =>
        $$"""{ "jsonrpc": "2.0", "error": { "code": -32602, "message": {{JsonSerializer.Serialize(message)}}, "data": {{JsonSerializer.Serialize(data)}} }, "id": {{id}} }""";

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serving;
        _stop.Dispose();
        _certificate.Dispose();
    }
}
