import { describe, expect, it } from 'vitest'
import { compare, newHost, type ComparedHost } from './matcher'
import type { ZabbixHostSummary } from './zabbix'

function zabbix(overrides: Partial<ZabbixHostSummary>): ZabbixHostSummary {
  return { hostId: '1', host: 'h', name: 'h', monitored: true, ipAddresses: [], dnsNames: [], macAddresses: [], operatingSystem: null, tags: [], ...overrides }
}

function host(overrides: Partial<ComparedHost>): ComparedHost {
  return { id: 'n', name: 'n', hostname: null, ipAddress: null, macAddress: null, canManage: true, ...overrides }
}

describe('the comparison (1.x ZabbixMatcher)', () => {
  it('sorts hosts into linked, probable by MAC then IP then hostname then name, one side only, and broken links', () => {
    const zabbixHosts = [
      zabbix({ hostId: '10', host: 'web01', name: 'Web 01' }),
      zabbix({ hostId: '11', host: 'db01.example.com', name: 'Database', macAddresses: ['AA:BB:CC:DD:EE:FF'], ipAddresses: ['192.0.2.9'] }),
      zabbix({ hostId: '12', host: 'mail', name: 'mail', ipAddresses: ['192.0.2.20'] }),
      zabbix({ hostId: '13', host: 'app', name: 'App server', dnsNames: ['app.example.com'] }),
      zabbix({ hostId: '14', host: 'lonely', name: 'Lonely' }),
    ]
    const hosts = [
      host({ id: 'a', name: 'web' }),
      host({ id: 'b', name: 'db', macAddress: 'aa:bb:cc:dd:ee:ff', ipAddress: '192.0.2.20' }),
      host({ id: 'c', name: 'postbox', ipAddress: '192.0.2.20' }),
      host({ id: 'd', name: 'x', hostname: 'app' }),
      host({ id: 'e', name: 'other' }),
      host({ id: 'f', name: 'gone' }),
    ]
    const result = compare(hosts, new Map([['a', '10'], ['f', '99']]), zabbixHosts)

    expect(result.linked.map((p) => [p.host.id, p.zabbix.hostId])).toEqual([['a', '10']])
    // MAC wins over the IP address that would match mail; the hostname matches without its domain.
    expect(result.probableMatches.map((p) => [p.host.id, p.zabbix.hostId, p.matchedBy])).toEqual([
      ['b', '11', 'mac'],
      ['c', '12', 'ip'],
      ['d', '13', 'hostname'],
    ])
    expect(result.zabbixOnly.map((z) => z.hostId)).toEqual(['14'])
    expect(result.nkeeperOnly.map((h) => h.id)).toEqual(['e'])
    expect(result.brokenLinks.map((h) => h.id)).toEqual(['f'])
  })

  it('matches each Zabbix host once', () => {
    const result = compare([host({ id: 'a', name: 'web' }), host({ id: 'b', name: 'web' })], new Map(), [zabbix({ hostId: '1', host: 'web', name: 'web' })])
    expect(result.probableMatches).toHaveLength(1)
    expect(result.nkeeperOnly).toHaveLength(1)
  })
})

describe('adding a Zabbix host (1.x import)', () => {
  const record = (summary: Partial<ZabbixHostSummary>) => ({ summary: zabbix(summary), description: 'Rack 4', groups: ['Linux servers'], serialNumber: 'SN1' })

  it('takes the first address, a DNS name, the MAC, the tags and, if asked, the groups', () => {
    const request = newHost(
      record({ name: 'Web 01', host: 'web01', ipAddresses: ['192.0.2.10'], dnsNames: ['web01.example.com'], macAddresses: ['AA:BB:CC:DD:EE:FF'], tags: [{ name: 'env', value: 'prod' }, { name: 'a:b', value: null }] }),
      true,
    )
    expect(request).toMatchObject({
      name: 'Web 01',
      hostname: 'web01.example.com',
      ipAddress: '192.0.2.10',
      macAddress: 'AA:BB:CC:DD:EE:FF',
      serialNumber: 'SN1',
      description: 'Rack 4',
      tags: [{ name: 'env', value: 'prod' }, { name: 'group', value: 'Linux servers' }],
    })
  })

  it('uses the technical name only without an IP address, and skips a host with no address', () => {
    expect(newHost(record({ host: 'web01' }), false)?.hostname).toBe('web01')
    expect(newHost(record({ host: 'Web server (old)' }), false)).toBeNull()
  })
})
