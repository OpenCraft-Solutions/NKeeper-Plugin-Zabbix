using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NKeeper.Testing;
using Npgsql;

namespace NKeeper.Plugins.Zabbix.Tests;

/// <summary>
/// docs/architecture.md §7.40: the real <c>zabbix</c> plugin, packaged by its build and loaded into NKeeper's test server,
/// against a fake Zabbix API over HTTPS that core signs in to with a bearer token.
/// </summary>
public sealed class ZabbixPluginTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string BobPassword = "Bob-Passw0rd-123";

    private TestServer _server = null!;
    private string _connectionString = string.Empty;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        var options = new TestServerOptions { ConnectionString = _connectionString };
        foreach (var package in DevelopmentKeys.BuiltPackages())
        {
            options.PluginPackages.Add(package);
        }

        _server = await TestServer.StartAsync(options, Token);
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task The_plugin_contributes_the_zabbix_service_type_and_its_server_integration()
    {
        using var admin = await _server.SignedInClientAsync(Token);
        var plugins = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri("/api/plugins", UriKind.Relative), Token);
        var plugin = plugins.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "zabbix");
        Assert.Equal("loaded", plugin.GetProperty("state").GetString());
        var type = Assert.Single(plugin.GetProperty("serviceTypes").EnumerateArray());
        Assert.Equal(("zabbix", 443, "tls"), (type.GetProperty("id").GetString(), type.GetProperty("defaultPort").GetInt32(), type.GetProperty("health").GetString()));

        var integration = Assert.Single((await admin.Http.GetFromJsonAsync<JsonElement>(new Uri("/api/plugins/integrations", UriKind.Relative), Token)).EnumerateArray());
        Assert.Equal(("zabbix", "server", JsonValueKind.Null), (integration.GetProperty("pluginId").GetString(), integration.GetProperty("name").GetString(), integration.GetProperty("binding").ValueKind));

        // Not bound: nothing to say about any host.
        var web = await CreateHostAsync(admin, "web01", "192.0.2.10");
        Assert.Equal("zabbix_not_bound", await CodeAsync(admin, HttpMethod.Get, $"/api/p/zabbix/hosts/{Id(web)}/overview", HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task An_administrator_binds_and_tests_the_server_then_a_host_is_linked_and_shows_what_zabbix_knows()
    {
        await using var zabbix = new FakeZabbix();
        using var admin = await _server.SignedInClientAsync(Token);
        await BindAsync(admin, zabbix, FakeZabbix.Token);

        // The version goes without the token, as Zabbix wants; the count with it.
        var test = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri("/api/p/zabbix/admin/test", UriKind.Relative), Token);
        Assert.Equal(("7.0.5", 2), (test.GetProperty("version").GetString(), test.GetProperty("hostCount").GetInt32()));
        Assert.Contains(("apiinfo.version", false), zabbix.Calls);
        Assert.DoesNotContain(("apiinfo.version", true), zabbix.Calls);

        var web = await CreateHostAsync(admin, "web01", "192.0.2.10");
        var overview = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri($"/api/p/zabbix/hosts/{Id(web)}/overview", UriKind.Relative), Token);
        Assert.Equal((false, true), (overview.GetProperty("linked").GetBoolean(), overview.GetProperty("canLink").GetBoolean()));

        var found = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri($"/api/p/zabbix/hosts/{Id(web)}/search?text=web", UriKind.Relative), Token);
        Assert.Equal([FakeZabbix.Web01], found.EnumerateArray().Select(h => h.GetProperty("hostId").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.SendAsync(HttpMethod.Put, $"/api/p/zabbix/hosts/{Id(web)}/link", new { zabbixHostId = "web01" }, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Put, $"/api/p/zabbix/hosts/{Id(web)}/link", new { zabbixHostId = FakeZabbix.Web01 }, Token)).StatusCode);

        overview = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri($"/api/p/zabbix/hosts/{Id(web)}/overview", UriKind.Relative), Token);
        var details = overview.GetProperty("overview");
        Assert.Equal(("web01", "Available", "Debian 12"), (details.GetProperty("host").GetProperty("name").GetString(), details.GetProperty("availability").GetString(), details.GetProperty("inventory").GetProperty("os").GetString()));
        Assert.Equal(["AA:BB:CC:DD:EE:01"], details.GetProperty("host").GetProperty("macAddresses").EnumerateArray().Select(m => m.GetString()));
        // Never collected (lastclock 0) isn't shown; the rest in the well-known order.
        Assert.Equal(["system.uptime", "system.cpu.num"], details.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString()));
        Assert.Equal(4, Assert.Single(details.GetProperty("problems").EnumerateArray()).GetProperty("severity").GetInt32());
        Assert.Equal($"https://127.0.0.1:{zabbix.Port}/zabbix/", overview.GetProperty("frontendUrl").GetString());

        // The comparison's data: Zabbix's hosts and the links.
        var hosts = await admin.Http.GetFromJsonAsync<JsonElement>(new Uri("/api/p/zabbix/admin/hosts", UriKind.Relative), Token);
        Assert.Equal(2, hosts.GetProperty("hosts").GetArrayLength());
        var link = Assert.Single(hosts.GetProperty("links").EnumerateArray());
        Assert.Equal((Id(web), FakeZabbix.Web01), (link.GetProperty("hostId").GetGuid(), link.GetProperty("zabbixHostId").GetString()));

        // Links in bulk, and unlinking.
        var db = await CreateHostAsync(admin, "db01", "192.0.2.20");
        using (var linked = await admin.SendAsync(HttpMethod.Post, "/api/p/zabbix/admin/links", new { links = new object[] { new { hostId = Id(db), zabbixHostId = FakeZabbix.Db01 }, new { hostId = Id(web), zabbixHostId = (string?)null } } }, Token))
        {
            Assert.True(linked.StatusCode == HttpStatusCode.NoContent, await linked.Content.ReadAsStringAsync(Token));
        }

        Assert.False((await admin.Http.GetFromJsonAsync<JsonElement>(new Uri($"/api/p/zabbix/hosts/{Id(web)}/overview", UriKind.Relative), Token)).GetProperty("linked").GetBoolean());
        Assert.Equal([$"Zabbix host {FakeZabbix.Web01}", $"Zabbix host {FakeZabbix.Db01}"], await AuditAsync("zabbix.link"));
        Assert.Equal([$"Zabbix host {FakeZabbix.Web01}"], await AuditAsync("zabbix.unlink"));
    }

    [Fact]
    public async Task Only_users_of_the_zabbix_servers_host_see_anything_from_it_and_only_managers_link()
    {
        await using var zabbix = new FakeZabbix();
        using var admin = await _server.SignedInClientAsync(Token);
        var server = await BindAsync(admin, zabbix, FakeZabbix.Token);
        var web = await CreateHostAsync(admin, "web01", "192.0.2.10");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Put, $"/api/p/zabbix/hosts/{Id(web)}/link", new { zabbixHostId = FakeZabbix.Web01 }, Token)).StatusCode);

        var bobId = await CreateUserAsync(admin, "bob");
        await GrantAsync(admin, bobId, (Id(web), "use"));
        using var bob = _server.CreateClient();
        (await bob.SendAsync(HttpMethod.Post, "/api/auth/login", new { userNameOrEmail = "bob", password = BobPassword, rememberMe = false }, Token)).EnsureSuccessStatusCode();

        Assert.Equal("zabbix_no_access", await CodeAsync(bob, HttpMethod.Get, $"/api/p/zabbix/hosts/{Id(web)}/overview", HttpStatusCode.Conflict));

        await GrantAsync(admin, bobId, (Id(web), "use"), (Id(server), "use"));
        var overview = await bob.Http.GetFromJsonAsync<JsonElement>(new Uri($"/api/p/zabbix/hosts/{Id(web)}/overview", UriKind.Relative), Token);
        Assert.Equal((true, false), (overview.GetProperty("linked").GetBoolean(), overview.GetProperty("canLink").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.SendAsync(HttpMethod.Delete, $"/api/p/zabbix/hosts/{Id(web)}/link", null, Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.Http.GetAsync(new Uri("/api/p/zabbix/admin/hosts", UriKind.Relative), Token)).StatusCode);
    }

    [Fact]
    public async Task A_refused_token_and_a_zabbix_host_that_is_gone_are_reported()
    {
        await using var zabbix = new FakeZabbix();
        using var admin = await _server.SignedInClientAsync(Token);
        await BindAsync(admin, zabbix, "wrong-token");
        Assert.Equal("zabbix_token_refused", await CodeAsync(admin, HttpMethod.Get, "/api/p/zabbix/admin/test", HttpStatusCode.BadGateway));

        var other = await CreateHostAsync(admin, "other", "192.0.2.30");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(HttpMethod.Put, $"/api/p/zabbix/hosts/{Id(other)}/link", new { zabbixHostId = "999" }, Token)).StatusCode);
        Assert.Equal("zabbix_token_refused", await CodeAsync(admin, HttpMethod.Get, $"/api/p/zabbix/hosts/{Id(other)}/overview", HttpStatusCode.BadGateway));

        await BindAsync(admin, zabbix, FakeZabbix.Token);
        Assert.Equal("zabbix_host_not_found", await CodeAsync(admin, HttpMethod.Get, $"/api/p/zabbix/hosts/{Id(other)}/overview", HttpStatusCode.NotFound));
    }

    [Theory]
    [InlineData(null, "/zabbix/")]
    [InlineData("", "/zabbix/")]
    [InlineData("/", "/")]
    [InlineData("monitoring", "/monitoring/")]
    [InlineData("/zabbix/zabbix.php", "/zabbix/")]
    [InlineData("/a/../b", "/zabbix/")]
    [InlineData("/z?x=1", "/zabbix/")]
    public void The_path_setting_is_normalized(string? setting, string expected) => Assert.Equal(expected, ZabbixApi.NormalizePath(setting));

    [Theory]
    [InlineData(" 192.0.2.1 ", "192.0.2.1")]
    [InlineData("10.1", null)]
    [InlineData("::1", "::1")]
    [InlineData("example", null)]
    public void Addresses_are_read_as_1x_read_them(string value, string? expected) => Assert.Equal(expected, ZabbixQueries.NormalizeIp(value));

    [Fact]
    public void Mac_addresses_take_the_usual_separators() =>
        Assert.Equal(["AA:BB:CC:DD:EE:FF", "AA:BB:CC:DD:EE:FF", null], new[] { "aa-bb-cc-dd-ee-ff", "aabb.ccdd.eeff", "not a mac" }.Select(ZabbixQueries.NormalizeMac));

    // ---- Helpers ----

    /// <summary>A Zabbix server host with the token as its credential's password, its certificate trusted, bound to the integration.</summary>
    private static async Task<JsonElement> BindAsync(TestServerClient admin, FakeZabbix zabbix, string token)
    {
        using var created = await admin.SendAsync(HttpMethod.Post, "/api/hosts", new
        {
            name = $"zabbix-{Guid.NewGuid():N}"[..20],
            ipAddress = "127.0.0.1",
            services = new[] { new { type = "zabbix", port = zabbix.Port, settings = new { path = "/zabbix" } } },
            credentials = new[] { new { username = "api", password = token } },
        }, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        var host = await created.Content.ReadFromJsonAsync<JsonElement>(Token);
        using (var bound = await admin.SendAsync(HttpMethod.Put, "/api/plugins/zabbix/integrations/server", new { hostId = Id(host), serviceId = ServiceId(host), credentialId = CredentialId(host) }, Token))
        {
            Assert.True(bound.StatusCode == HttpStatusCode.NoContent, await bound.Content.ReadAsStringAsync(Token));
        }

        // Zabbix's self-signed certificate: core reports it, and the administrator trusts it, as the settings panel does.
        using var unknown = await admin.Http.GetAsync(new Uri("/api/p/zabbix/admin/test", UriKind.Relative), Token);
        var problem = await unknown.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.True(unknown.StatusCode == HttpStatusCode.BadGateway && problem.GetProperty("code").GetString() == "host_key_unknown", problem.GetRawText());
        var presented = problem.GetProperty("presented");
        using var trusted = await admin.SendAsync(
            HttpMethod.Post,
            $"/api/hosts/{Id(host)}/services/{ServiceId(host)}/trusted-keys",
            new { algorithm = presented.GetProperty("algorithm").GetString(), fingerprint = presented.GetProperty("fingerprint").GetString() },
            Token);
        Assert.True(trusted.IsSuccessStatusCode, await trusted.Content.ReadAsStringAsync(Token));
        return host;
    }

    private static async Task<JsonElement> CreateHostAsync(TestServerClient admin, string name, string ipAddress)
    {
        using var created = await admin.SendAsync(HttpMethod.Post, "/api/hosts", new { name, ipAddress }, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        return await created.Content.ReadFromJsonAsync<JsonElement>(Token);
    }

    private static async Task<Guid> CreateUserAsync(TestServerClient admin, string userName)
    {
        using var created = await admin.SendAsync(HttpMethod.Post, "/api/users", new { userName, email = $"{userName}@nkeeper.test", password = BobPassword, role = "user", mustChangePassword = false }, Token);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Token));
        return (await created.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("id").GetGuid();
    }

    private static async Task GrantAsync(TestServerClient admin, Guid userId, params (Guid HostId, string Level)[] grants)
    {
        using var granted = await admin.SendAsync(HttpMethod.Put, $"/api/users/{userId}/grants", grants.Select(g => new { hostId = g.HostId, level = g.Level }).ToArray(), Token);
        Assert.True(granted.IsSuccessStatusCode, await granted.Content.ReadAsStringAsync(Token));
    }

    private static async Task<string?> CodeAsync(TestServerClient client, HttpMethod method, string path, HttpStatusCode status)
    {
        using var response = await client.SendAsync(method, path, null, Token);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("code").GetString();
    }

    private static Guid Id(JsonElement host) => host.GetProperty("id").GetGuid();

    private static Guid ServiceId(JsonElement host) => host.GetProperty("services")[0].GetProperty("id").GetGuid();

    private static Guid CredentialId(JsonElement host) => host.GetProperty("credentials")[0].GetProperty("id").GetGuid();

    private async Task<List<string>> AuditAsync(string action)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand("SELECT detail FROM core.audit_events WHERE action = @action ORDER BY id", connection);
        command.Parameters.AddWithValue("action", action);
        await using var reader = await command.ExecuteReaderAsync(Token);
        var details = new List<string>();
        while (await reader.ReadAsync(Token))
        {
            details.Add(reader.GetString(0));
        }

        return details;
    }
}
