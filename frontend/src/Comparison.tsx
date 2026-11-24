import { useEffect, useMemo, useState } from 'react'
import { api, type SettingsPanelProps } from '@nkeeper/sdk'
import { compare, newHost, type ComparedHost, type Comparison, type MatchedBy } from './matcher'
import { messageOf, presented, type HostsResponse, type ZabbixHostRecord } from './zabbix'

const MATCHED_BY: Record<MatchedBy, string> = { mac: 'MAC address', ip: 'IP address', hostname: 'hostname', name: 'name' }

interface Loaded {
  comparison: Comparison
  records: Map<string, ZabbixHostRecord>
}

type ShowHost = ((hostId: string) => void) | undefined

/**
 * Zabbix next to NKeeper (1.x's comparison): on the Settings page, loaded on demand, since it reads every host on both
 * sides; as the Zabbix report (`load`), at once, with its NKeeper hosts linking to the workspace.
 */
export function ComparisonSection({
  version,
  confirmTrust,
  showHost,
  loadAtOnce = false,
}: {
  version: number
  /** The Settings page's: trusting the Zabbix server's certificate. The report has none, and sends the user there. */
  confirmTrust?: SettingsPanelProps['confirmTrust']
  showHost?: ShowHost
  loadAtOnce?: boolean
}) {
  const [loaded, setLoaded] = useState<Loaded | null>(null)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<{ text: string; error?: boolean } | null>(null)

  useEffect(() => {
    queueMicrotask(() => (loadAtOnce ? void load() : setLoaded(null)))
    // The report loads once; the settings panel again for each new binding.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [version])

  async function load(retried = false): Promise<void> {
    setBusy(true)
    setNotice(null)
    try {
      const [zabbix, hosts] = await Promise.all([api.get<HostsResponse>('/api/p/zabbix/admin/hosts'), api.get<ComparedHost[]>('/api/hosts')])
      const links = new Map(zabbix.links.map((l) => [l.hostId, l.zabbixHostId]))
      setLoaded({ comparison: compare(hosts, links, zabbix.hosts.map((r) => r.summary)), records: new Map(zabbix.hosts.map((r) => [r.summary.hostId, r])) })
    } catch (error) {
      const prompt = presented(error)
      if (prompt && !confirmTrust) {
        setNotice({ text: 'NKeeper doesn’t trust the Zabbix server’s certificate yet. Test the connection under Settings ▸ Zabbix, and trust it there.', error: true })
        return
      }
      if (prompt && confirmTrust && !retried && (await confirmTrust(prompt))) {
        return load(true)
      }
      setNotice({ text: messageOf(error, 'Zabbix couldn’t be read.'), error: true })
    } finally {
      setBusy(false)
    }
  }

  async function act(action: () => Promise<string>) {
    setBusy(true)
    setNotice(null)
    try {
      const text = await action()
      await load()
      setNotice({ text })
    } catch (error) {
      setNotice({ text: messageOf(error, 'That didn’t work.'), error: true })
      setBusy(false)
    }
  }

  const link = (links: { hostId: string; zabbixHostId: string | null }[], done: string) =>
    act(async () => {
      await api.post('/api/p/zabbix/admin/links', { links })
      return done
    })

  return (
    <section className="nk-zbx" aria-label="Zabbix and NKeeper">
      <h3>Zabbix and NKeeper</h3>
      <p className="nk-zbx-muted">Zabbix’s hosts next to NKeeper’s: linked, probably the same machine, and only on one side. NKeeper never changes anything in Zabbix.</p>
      <div className="nk-zbx-bar">
        <button type="button" className="nk-zbx-button" disabled={busy} onClick={() => void load()}>
          {loaded ? 'Compare again' : 'Compare'}
        </button>
      </div>
      {notice ? (
        <p className="nk-zbx-notice" role={notice.error ? 'alert' : 'status'} data-error={notice.error ? '' : undefined}>
          {notice.text}
        </p>
      ) : null}
      {loaded ? (
        <ComparisonView loaded={loaded} busy={busy} showHost={showHost} onLink={link} onImport={(ids, groups) => act(() => importHosts(ids, groups, loaded.records))} />
      ) : null}
    </section>
  )
}

function ComparisonView({
  loaded,
  busy,
  showHost,
  onLink,
  onImport,
}: {
  loaded: Loaded
  busy: boolean
  showHost: ShowHost
  onLink: (links: { hostId: string; zabbixHostId: string | null }[], done: string) => void
  onImport: (zabbixHostIds: string[], groupsAsTags: boolean) => void
}) {
  const { comparison } = loaded
  const [pairs, setPairs] = useState<Set<string>>(() => new Set(comparison.probableMatches.map((p) => p.host.id)))
  const [adding, setAdding] = useState<Set<string>>(() => new Set())
  const [groupsAsTags, setGroupsAsTags] = useState(false)
  const chosenPairs = useMemo(() => comparison.probableMatches.filter((p) => pairs.has(p.host.id)), [comparison, pairs])

  const toggle = (set: Set<string>, id: string, on: boolean) => {
    const next = new Set(set)
    if (on) {
      next.add(id)
    } else {
      next.delete(id)
    }
    return next
  }

  return (
    <>
      <p>
        {comparison.linked.length} linked · {comparison.probableMatches.length} probably the same · {comparison.zabbixOnly.length} only in Zabbix ·{' '}
        {comparison.nkeeperOnly.length} only in NKeeper
        {comparison.brokenLinks.length > 0 ? ` · ${comparison.brokenLinks.length} linked to a Zabbix host that’s gone` : ''}
      </p>

      {comparison.probableMatches.length > 0 ? (
        <details open>
          <summary>Probably the same machine ({comparison.probableMatches.length})</summary>
          <table className="nk-zbx-table" aria-label="Probably the same machine">
            <thead>
              <tr>
                <th scope="col">Link</th>
                <th scope="col">NKeeper</th>
                <th scope="col">Zabbix</th>
                <th scope="col">Matched by</th>
              </tr>
            </thead>
            <tbody>
              {comparison.probableMatches.map((p) => (
                <tr key={p.host.id}>
                  <td>
                    <input type="checkbox" aria-label={`Link ${p.host.name} to ${p.zabbix.name}`} checked={pairs.has(p.host.id)} onChange={(e) => setPairs(toggle(pairs, p.host.id, e.target.checked))} />
                  </td>
                  <td>
                    <HostName host={p.host} showHost={showHost} />
                  </td>
                  <td>{p.zabbix.name}</td>
                  <td>{p.matchedBy ? MATCHED_BY[p.matchedBy] : ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <button
            type="button"
            className="nk-zbx-button"
            data-primary=""
            disabled={busy || chosenPairs.length === 0}
            onClick={() => onLink(chosenPairs.map((p) => ({ hostId: p.host.id, zabbixHostId: p.zabbix.hostId })), `Linked ${chosenPairs.length} ${chosenPairs.length === 1 ? 'host' : 'hosts'}.`)}
          >
            Link {chosenPairs.length} selected
          </button>
        </details>
      ) : null}

      {comparison.zabbixOnly.length > 0 ? (
        <details>
          <summary>Only in Zabbix ({comparison.zabbixOnly.length})</summary>
          <table className="nk-zbx-table" aria-label="Only in Zabbix">
            <thead>
              <tr>
                <th scope="col">Add</th>
                <th scope="col">Zabbix host</th>
                <th scope="col">Addresses</th>
              </tr>
            </thead>
            <tbody>
              {comparison.zabbixOnly.map((z) => (
                <tr key={z.hostId}>
                  <td>
                    <input type="checkbox" aria-label={`Add ${z.name}`} checked={adding.has(z.hostId)} onChange={(e) => setAdding(toggle(adding, z.hostId, e.target.checked))} />
                  </td>
                  <td>
                    {z.name}
                    {z.name !== z.host ? <span className="nk-zbx-muted"> ({z.host})</span> : null}
                  </td>
                  <td className="nk-zbx-mono">{[...z.ipAddresses, ...z.dnsNames].join(', ') || '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="nk-zbx-bar">
            <label>
              <input type="checkbox" checked={groupsAsTags} onChange={(e) => setGroupsAsTags(e.target.checked)} /> Add host groups as group tags
            </label>
            <button type="button" className="nk-zbx-button" data-primary="" disabled={busy || adding.size === 0} onClick={() => onImport([...adding], groupsAsTags)}>
              Add {adding.size} to NKeeper
            </button>
          </div>
        </details>
      ) : null}

      {comparison.brokenLinks.length > 0 ? (
        <details>
          <summary>Linked to a Zabbix host that’s gone ({comparison.brokenLinks.length})</summary>
          <ul className="nk-zbx-rows">
            {comparison.brokenLinks.map((h) => (
              <li key={h.id} className="nk-zbx-row">
                <HostName host={h} showHost={showHost} />
              </li>
            ))}
          </ul>
          <button
            type="button"
            className="nk-zbx-button"
            disabled={busy}
            onClick={() => onLink(comparison.brokenLinks.map((h) => ({ hostId: h.id, zabbixHostId: null })), `Unlinked ${comparison.brokenLinks.length}.`)}
          >
            Unlink them
          </button>
        </details>
      ) : null}

      {comparison.linked.length > 0 ? (
        <details>
          <summary>Linked ({comparison.linked.length})</summary>
          <ul className="nk-zbx-rows">
            {comparison.linked.map((p) => (
              <li key={p.host.id} className="nk-zbx-row">
                <span className="nk-zbx-grow">
                  <HostName host={p.host} showHost={showHost} /> <span className="nk-zbx-muted">↔ {p.zabbix.name}</span>
                </span>
              </li>
            ))}
          </ul>
        </details>
      ) : null}

      {comparison.nkeeperOnly.length > 0 ? (
        <details>
          <summary>Only in NKeeper ({comparison.nkeeperOnly.length})</summary>
          <ul className="nk-zbx-rows">
            {comparison.nkeeperOnly.map((h) => (
              <li key={h.id} className="nk-zbx-row">
                <HostName host={h} showHost={showHost} />
              </li>
            ))}
          </ul>
        </details>
      ) : null}
    </>
  )
}

/** An NKeeper host's name: on the report, a link that shows it in the workspace. */
function HostName({ host, showHost }: { host: ComparedHost; showHost: ShowHost }) {
  return showHost ? (
    <button type="button" className="nk-zbx-link nk-zbx-plain" onClick={() => showHost(host.id)}>
      {host.name}
    </button>
  ) : (
    <>{host.name}</>
  )
}

/**
 * Adds NKeeper hosts for Zabbix hosts (1.x's import): through core's own `POST /api/hosts`, as the administrator, each
 * linked once it's created. A Zabbix host with no address is skipped, and so is one core refuses, with why.
 */
async function importHosts(zabbixHostIds: string[], groupsAsTags: boolean, records: Map<string, ZabbixHostRecord>): Promise<string> {
  const links: { hostId: string; zabbixHostId: string }[] = []
  const skipped: string[] = []
  for (const id of zabbixHostIds) {
    const record = records.get(id)
    const request = record ? newHost(record, groupsAsTags) : null
    if (!record || !request) {
      skipped.push(`${record?.summary.name ?? id}: Zabbix has no IP address or DNS name for it`)
      continue
    }
    try {
      const created = await api.post<{ id: string }>('/api/hosts', request)
      links.push({ hostId: created.id, zabbixHostId: id })
    } catch (error) {
      skipped.push(`${record.summary.name}: ${messageOf(error, 'NKeeper refused it')}`)
    }
  }
  if (links.length > 0) {
    await api.post('/api/p/zabbix/admin/links', { links })
  }
  return `Added ${links.length} ${links.length === 1 ? 'host' : 'hosts'}.${skipped.length > 0 ? ` Skipped: ${skipped.join('; ')}.` : ''}`
}
