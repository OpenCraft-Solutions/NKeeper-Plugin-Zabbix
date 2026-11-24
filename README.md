# NKeeper Zabbix plugin

The official `zabbix` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: what Zabbix knows about your hosts, read only. It lives in a repository of its own and is released on its own, so releasing it isn't a release of NKeeper (NKeeper's `docs/architecture.md` §9). Its design is §7.40 there, on the integrations of §7.39. It's ported from 1.x's Zabbix integration.

## What it does

It contributes the **`zabbix` service type** ("Zabbix", port 443, health by TLS), with one setting, `path`: where the Zabbix frontend is on the server (`/zabbix` by default). The API is `api_jsonrpc.php` under it.

**The Zabbix server is an ordinary host.** Give it a `zabbix` service and a credential whose password is a Zabbix API token (Zabbix 6.4 or later). Then, on NKeeper's Settings page, bind the plugin's **Zabbix server** integration to that host, service and credential:
- Core signs every request in with `Authorization: Bearer <token>`, and checks the certificate as for any HTTPS service (SDK 1.13). The plugin never sees the token.
- **Only users who may use the Zabbix server's host get anything from it.** Unlike 1.x, whose one token served everyone, Zabbix data is granted by granting that host.
- The settings panel tests the binding: the API version (asked without the token, as Zabbix requires), then how many hosts the token can see. A self-signed certificate is offered for trust with core's own dialog.

**A host's Zabbix panel**, in its details, for a host linked to a Zabbix host: its availability, maintenance, groups, tags, open problems by severity, latest well-known values (uptime, CPUs, memory, disk, agent and ICMP), inventory and interfaces, with links into Zabbix. Someone who can manage the host links it, searching Zabbix by name, technical name, IP address or DNS name, or unlinks it. A link is the plugin's host data (the Zabbix host's ID), audited as `zabbix.link` and `zabbix.unlink`.

**The comparison**, for administrators, on the settings panel and as the **Zabbix** report on the Reports page (SDK 1.14), where each NKeeper host shows in the workspace when chosen: Zabbix's hosts next to NKeeper's, as 1.x's matcher sorted them (linked; probably the same machine by MAC address, IP address, hostname or name; only in Zabbix; only in NKeeper; linked to a Zabbix host that's gone). From it, link the probable pairs, unlink the broken ones, or add the Zabbix-only hosts to NKeeper with their names, addresses, MAC address, serial number, description and tags (and host groups as `group` tags, if asked). Adding goes through core's own `POST /api/hosts`, as the administrator.

NKeeper never writes to Zabbix.

**Not yet:** 1.x's "copy to host" of Zabbix's hardware details.

The plugin's ID is `zabbix`, and it is built in: it is signed with NKeeper's official key and installed with NKeeper. It can be turned off (§9.2). It needs SDK 1.14 (`>=1.14.0 <2.0.0`), for its report.

## Layout

```text
backend/        the plugin: ZabbixPlugin.cs (the service type and integration), ZabbixEndpoints.cs, ZabbixServer.cs (the bound server),
                ZabbixApi.cs (JSON-RPC) and ZabbixQueries.cs (1.x's queries), nkeeper-plugin.json, the project
frontend/       the host panel, the settings panel and the comparison (matcher.ts, 1.x's matcher): a Vite library build to dist/index.js
tests/          the backend tests, which start NKeeper's test server with this plugin loaded and a fake Zabbix API
.github/        CI and the release workflow
```

It builds against NKeeper's published SDK, never core's source:

| Package | Registry | Used for |
| --- | --- | --- |
| `NKeeper.Abstractions` | GitHub Packages (NuGet) | the backend SDK |
| `NKeeper.Plugin.Build` | GitHub Packages (NuGet) | builds, packs and signs the plugin |
| `NKeeper.Testing` | GitHub Packages (NuGet) | the test server and PostgreSQL fixture |
| `@opencraft-solutions/nkeeper-sdk` | GitHub Packages (npm) | the frontend SDK, imported as `@nkeeper/sdk` |

## Building

You need the .NET 10 SDK (see `global.json`), Node.js 24 (see `.nvmrc`) and Docker for the tests.

GitHub Packages needs a token even for public packages. Make a GitHub personal access token that can read packages (`read:packages`) and give it to the build in the environment. It is never written to the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_AUTH_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
```

Then, from the repository root:

```bash
(cd frontend && npm ci && npm run lint && npm run typecheck && npm test)
dotnet restore NKeeper.Plugins.Zabbix.slnx
dotnet build NKeeper.Plugins.Zabbix.slnx --configuration Release
```

Building the plugin builds its frontend, packs `zabbix-<version>.nkp` into `artifacts/plugins/` and signs it with a development key, which the build creates under `.dev/keys/` on first use. Both are ignored by git. To load the package in a development NKeeper, point `Plugins:Directory` at `artifacts/plugins` and `Plugins:DevelopmentTrustDirectory` at `.dev/keys`.

Pinned versions: NuGet packages are locked in `packages.lock.json` (CI restores with `--locked-mode`), and npm packages in `frontend/package-lock.json`. Run `dotnet restore --force-evaluate` after changing a version.

## Testing

```bash
dotnet test NKeeper.Plugins.Zabbix.slnx --configuration Release
```

The tests need Docker, for PostgreSQL. There's no Zabbix in them:
- NKeeper's test server (from `NKeeper.Testing`) runs with this plugin's built package.
- A fake Zabbix API, in the tests, answers over HTTPS with a self-signed certificate. Like Zabbix, it answers the version only without a token, and everything else only with the right bearer token.

They cover:
- binding, trusting the certificate and testing the server;
- linking a host, its overview, the comparison's data, bulk links and the audit;
- that only users of the Zabbix server's host see anything, and only managers link;
- a refused token and a Zabbix host that's gone;
- the path setting and address parsing.

The frontend's tests (`npm test`) cover the matcher, adding a host from Zabbix's data, and the value formatting.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request: it restores from GitHub Packages with the workflow's token, lints, type-checks and tests the frontend, checks formatting, builds, runs the tests and packs the plugin signed with a development key, which it keeps as a build artifact.

## Releasing

1. Raise `version` in `backend/nkeeper-plugin.json` (and keep `frontend/package.json` in step), and merge it to `main`.
2. Tag the merge commit `v<version>`, for example `v0.2.0`, and push the tag.
3. `.github/workflows/release.yml` runs. It checks that the tag matches the manifest's version and is on `main`, builds and tests, and then, in the protected `release` environment, which needs an owner's approval, packs `zabbix-<version>.nkp` signed with the official key and publishes a GitHub release with the package and its SHA-256.

The official key's private half, `NKEEPER_RELEASE_KEY` (a PEM), is a secret of the `release` environment. It is available only to the signing job, goes to memory-backed storage for the packing step alone, and is removed when that step ends. It is never in the repository or in a log.

NKeeper's installer picks the release up when its `deploy/plugins.lock` is updated with the new URL and SHA-256.

## License

MIT, as NKeeper. See [LICENSE](LICENSE).
