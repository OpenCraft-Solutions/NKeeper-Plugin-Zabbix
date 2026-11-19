import type { ZabbixHostSummary } from './zabbix'

/** An NKeeper host on one side of the comparison, as core's `GET /api/hosts` gives it. */
export interface ComparedHost {
  id: string
  name: string
  hostname: string | null
  ipAddress: string | null
  macAddress: string | null
  canManage: boolean
}

export type MatchedBy = 'mac' | 'ip' | 'hostname' | 'name'

export interface ZabbixPair {
  host: ComparedHost
  zabbix: ZabbixHostSummary
  /** For a probable match: what matched. */
  matchedBy: MatchedBy | null
}

/**
 * Zabbix next to NKeeper (docs/architecture.md §7.40): hosts linked to each other, hosts that look like the same machine
 * but aren't linked yet, and hosts only one side has. `brokenLinks` are NKeeper hosts linked to a Zabbix host that's gone.
 */
export interface Comparison {
  linked: ZabbixPair[]
  probableMatches: ZabbixPair[]
  zabbixOnly: ZabbixHostSummary[]
  nkeeperOnly: ComparedHost[]
  brokenLinks: ComparedHost[]
}

const byName = (a: { name: string }, b: { name: string }) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })

/**
 * Lines NKeeper's hosts up with Zabbix's, as 1.x's ZabbixMatcher did. A link joins them for certain; for the rest, a
 * host is probably the Zabbix host with its MAC address, else its IP address, else its hostname (a DNS name or
 * technical name, also without the domain), else its name. Each Zabbix host matches one NKeeper host at most.
 */
export function compare(hosts: ComparedHost[], links: ReadonlyMap<string, string>, zabbixHosts: ZabbixHostSummary[]): Comparison {
  const byId = new Map(zabbixHosts.map((z) => [z.hostId, z]))
  const claimed = new Set<string>()
  const linked: ZabbixPair[] = []
  const broken: ComparedHost[] = []
  const unlinked: ComparedHost[] = []
  for (const host of hosts) {
    const zabbixHostId = links.get(host.id)
    const zabbix = zabbixHostId === undefined ? undefined : byId.get(zabbixHostId)
    if (zabbixHostId === undefined) {
      unlinked.push(host)
    } else if (zabbix) {
      linked.push({ host, zabbix, matchedBy: null })
      claimed.add(zabbix.hostId)
    } else {
      broken.push(host)
    }
  }

  const open = zabbixHosts.filter((z) => !claimed.has(z.hostId))
  const macs = index(open, (z) => z.macAddresses)
  const ips = index(open, (z) => z.ipAddresses)
  const hostnames = index(open, (z) => [...z.dnsNames, z.host].flatMap(withShortName))
  const names = index(open, (z) => [z.name, z.host])

  const probable: ZabbixPair[] = []
  const nkeeperOnly: ComparedHost[] = []
  for (const host of unlinked) {
    const match =
      find(macs, [host.macAddress], 'mac', claimed) ??
      find(ips, [host.ipAddress], 'ip', claimed) ??
      find(hostnames, host.hostname ? withShortName(host.hostname) : [], 'hostname', claimed) ??
      find(names, [host.name], 'name', claimed)
    if (match) {
      claimed.add(match.zabbix.hostId)
      probable.push({ host, zabbix: match.zabbix, matchedBy: match.matchedBy })
    } else {
      nkeeperOnly.push(host)
    }
  }

  return {
    linked: linked.sort((a, b) => byName(a.host, b.host)),
    probableMatches: probable.sort((a, b) => byName(a.host, b.host)),
    zabbixOnly: open.filter((z) => !claimed.has(z.hostId)).sort(byName),
    nkeeperOnly: nkeeperOnly.sort(byName),
    brokenLinks: broken.sort(byName),
  }
}

/** A name, and its first label when it has a domain (server01.example.com and server01 match). */
function withShortName(name: string): string[] {
  const dot = name.indexOf('.')
  return dot > 0 ? [name, name.slice(0, dot)] : [name]
}

function index(hosts: ZabbixHostSummary[], keys: (host: ZabbixHostSummary) => string[]): Map<string, ZabbixHostSummary[]> {
  const found = new Map<string, ZabbixHostSummary[]>()
  for (const host of hosts) {
    for (const key of new Set(keys(host).filter((k) => k.trim() !== '').map((k) => k.toLowerCase()))) {
      const list = found.get(key)
      if (list) {
        list.push(host)
      } else {
        found.set(key, [host])
      }
    }
  }
  return found
}

function find(index: Map<string, ZabbixHostSummary[]>, keys: (string | null)[], matchedBy: MatchedBy, claimed: Set<string>) {
  for (const key of keys) {
    const zabbix = key ? index.get(key.toLowerCase())?.find((h) => !claimed.has(h.hostId)) : undefined
    if (zabbix) {
      return { zabbix, matchedBy }
    }
  }
  return null
}

/** A hostname NKeeper accepts: dot-separated labels of letters, digits and inner hyphens. */
export function isHostname(name: string): boolean {
  return name.length <= 253 && /^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*$/i.test(name)
}

/** Tag names can't have a colon ("name:value" is the search syntax), as 1.x's import kept them. */
function usableTag(tag: { name: string; value: string | null }): boolean {
  const name = tag.name.trim()
  return name.length > 0 && name.length <= 100 && !name.includes(':') && (tag.value?.length ?? 0) <= 200
}

/**
 * A new host for a Zabbix host, as 1.x's import made it: the first IP address, a DNS name (or the technical name when
 * there's no IP address), the first MAC address, the serial number, the description and the tags (and, if asked, the
 * host groups as `group` tags). Null when Zabbix has no address for it.
 */
export function newHost(
  record: { summary: ZabbixHostSummary; description: string | null; groups: string[]; serialNumber: string | null },
  groupsAsTags: boolean,
) {
  const zabbix = record.summary
  const ipAddress = zabbix.ipAddresses[0] ?? null
  const hostname = zabbix.dnsNames.find(isHostname) ?? (ipAddress === null && isHostname(zabbix.host) ? zabbix.host : null)
  if (ipAddress === null && hostname === null) {
    return null
  }
  const tags = [...zabbix.tags, ...(groupsAsTags ? record.groups.map((g) => ({ name: 'group', value: g })) : [])]
    .filter(usableTag)
    .slice(0, 50)
    .map((t) => ({ name: t.name.trim(), value: t.value }))
  return {
    name: zabbix.name.slice(0, 200),
    hostname,
    ipAddress,
    macAddress: zabbix.macAddresses[0] ?? null,
    serialNumber: record.serialNumber?.slice(0, 128) ?? null,
    description: record.description?.slice(0, 4000) ?? null,
    tags,
    services: [],
    credentials: [],
  }
}
