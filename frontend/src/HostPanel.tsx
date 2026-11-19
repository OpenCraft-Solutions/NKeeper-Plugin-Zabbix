import { Fragment, useCallback, useEffect, useState } from 'react'
import { api, type HostPanelProps } from '@nkeeper/sdk'
import { formatItemValue, INVENTORY_LABELS } from './format'
import { codeOf, messageOf, SEVERITIES, zabbixLinks, type OverviewResponse, type ZabbixAvailability, type ZabbixHostSummary } from './zabbix'

const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

type State =
  | { kind: 'loading' }
  | { kind: 'hidden' }
  | { kind: 'ready'; data: OverviewResponse }
  | { kind: 'failed'; code: string | null; message: string }

/**
 * A host's Zabbix panel (docs/architecture.md §7.40), ported from 1.x's ZabbixSection: what Zabbix knows about the
 * linked host. It draws nothing when Zabbix isn't set up, or for a host that isn't linked and that the user can't link.
 */
export function HostPanel({ host }: HostPanelProps) {
  const [state, setState] = useState<State>({ kind: 'loading' })
  const [attempt, setAttempt] = useState(0)
  const base = `/api/p/zabbix/hosts/${host.id}`

  useEffect(() => {
    const controller = new AbortController()
    api.get<OverviewResponse>(`${base}/overview`, { signal: controller.signal }).then(
      (data) => setState(!data.linked && !data.canLink ? { kind: 'hidden' } : { kind: 'ready', data }),
      (error: unknown) => {
        if (controller.signal.aborted) {
          return
        }
        const code = codeOf(error)
        // Not set up: nothing to say about any host.
        setState(code === 'zabbix_not_bound' ? { kind: 'hidden' } : { kind: 'failed', code, message: messageOf(error, 'Zabbix couldn’t be reached.') })
      },
    )
    return () => controller.abort()
  }, [base, attempt])

  const reload = useCallback(() => setAttempt((a) => a + 1), [])

  async function unlink() {
    try {
      await api.delete(`${base}/link`)
      reload()
    } catch (error) {
      setState({ kind: 'failed', code: codeOf(error), message: messageOf(error, 'The host couldn’t be unlinked.') })
    }
  }

  switch (state.kind) {
    case 'hidden':
      return null
    case 'loading':
      return <p className="nk-zbx-muted">Loading from Zabbix…</p>
    case 'failed':
      return (
        <div className="nk-zbx">
          <p className="nk-zbx-notice" data-error={state.code === 'zabbix_no_access' ? undefined : ''}>
            {state.code === 'zabbix_no_access' ? 'You have no access to the Zabbix server, so Zabbix’s information isn’t shown.' : state.message}
          </p>
          <div className="nk-zbx-bar">
            <button type="button" className="nk-zbx-button" onClick={reload}>
              Try again
            </button>
            {state.code === 'zabbix_host_not_found' ? (
              <button type="button" className="nk-zbx-button" onClick={() => void unlink()}>
                Unlink
              </button>
            ) : null}
          </div>
        </div>
      )
    case 'ready':
      return state.data.linked ? (
        <Overview data={state.data} onRefresh={reload} onUnlink={state.data.canLink ? () => void unlink() : null} />
      ) : (
        <LinkSearch base={base} hostName={host.name} onLinked={reload} />
      )
  }
}

function Overview({ data, onRefresh, onUnlink }: { data: Extract<OverviewResponse, { linked: true }>; onRefresh: () => void; onUnlink: (() => void) | null }) {
  const { overview, frontendUrl } = data
  const zabbix = overview.host
  const inventory = Object.entries(overview.inventory)
  return (
    <div className="nk-zbx">
      <div className="nk-zbx-bar">
        <span className="nk-zbx-grow">
          {zabbix.name}
          {zabbix.name !== zabbix.host ? <span className="nk-zbx-muted"> ({zabbix.host})</span> : null}
          <span className="nk-zbx-muted nk-zbx-mono"> · ID {zabbix.hostId}</span>
        </span>
        <a className="nk-zbx-link" href={zabbixLinks.latestData(frontendUrl, zabbix.hostId)} target="_blank" rel="noreferrer noopener">
          Open in Zabbix
        </a>
        <button type="button" className="nk-zbx-button" onClick={onRefresh}>
          Refresh
        </button>
        {onUnlink ? (
          <button type="button" className="nk-zbx-button" onClick={onUnlink}>
            Unlink
          </button>
        ) : null}
      </div>
      <dl className="nk-zbx-props">
        <dt>Status</dt>
        <dd>
          <Availability value={overview.availability} />
          {!zabbix.monitored ? <span className="nk-zbx-muted"> · Not monitored</span> : null}
          {overview.inMaintenance ? <span className="nk-zbx-muted"> · In maintenance</span> : null}
        </dd>
        {overview.groups.length > 0 ? (
          <>
            <dt>Host groups</dt>
            <dd>{overview.groups.join(', ')}</dd>
          </>
        ) : null}
        {zabbix.tags.length > 0 ? (
          <>
            <dt>Zabbix tags</dt>
            <dd>{zabbix.tags.map((t) => (t.value ? `${t.name}: ${t.value}` : t.name)).join(', ')}</dd>
          </>
        ) : null}
        {overview.description ? (
          <>
            <dt>Description</dt>
            <dd>{overview.description}</dd>
          </>
        ) : null}
      </dl>

      <h3>
        Problems ({overview.problems.length})
        {overview.problems.length > 0 ? (
          <>
            {' '}
            <a className="nk-zbx-link" href={zabbixLinks.problems(frontendUrl, zabbix.hostId)} target="_blank" rel="noreferrer noopener">
              View in Zabbix
            </a>
          </>
        ) : null}
      </h3>
      {overview.problems.length === 0 ? (
        <p className="nk-zbx-muted">No problems.</p>
      ) : (
        <ul className="nk-zbx-rows" aria-label="Problems">
          {overview.problems.map((problem) => (
            <li key={problem.eventId} className="nk-zbx-row">
              <span className="nk-zbx-severity" data-severity={problem.severity}>
                {SEVERITIES[problem.severity] ?? 'Unknown'}
              </span>
              <span className="nk-zbx-grow">{problem.name}</span>
              <span className="nk-zbx-muted">
                since <time dateTime={problem.startedAtUtc}>{dateTimeFormat.format(new Date(problem.startedAtUtc))}</time>
                {problem.acknowledged ? ' · acknowledged' : ''}
                {problem.suppressed ? ' · suppressed' : ''}
              </span>
            </li>
          ))}
        </ul>
      )}

      {overview.items.length > 0 ? (
        <>
          <h3>Latest values</h3>
          <dl className="nk-zbx-props">
            {overview.items.map((item) => (
              <Fragment key={item.key}>
                <dt title={item.key}>{item.name}</dt>
                <dd title={item.checkedAtUtc ? `Checked ${dateTimeFormat.format(new Date(item.checkedAtUtc))}` : undefined}>{formatItemValue(item)}</dd>
              </Fragment>
            ))}
          </dl>
        </>
      ) : null}

      <h3>Inventory</h3>
      {inventory.length === 0 ? (
        <p className="nk-zbx-muted">No inventory. Turn on host inventory for this host in Zabbix (manual or automatic) to see it here.</p>
      ) : (
        <dl className="nk-zbx-props">
          {inventory.map(([field, value]) => (
            <Fragment key={field}>
              <dt title={field}>{INVENTORY_LABELS[field] ?? field}</dt>
              <dd>{value}</dd>
            </Fragment>
          ))}
        </dl>
      )}

      {overview.interfaces.length > 0 ? (
        <>
          <h3>Interfaces</h3>
          <ul className="nk-zbx-rows" aria-label="Interfaces">
            {overview.interfaces.map((item, index) => (
              <li key={index} className="nk-zbx-row">
                <span className="nk-zbx-badge">{item.type}</span>
                <span className="nk-zbx-grow nk-zbx-mono">
                  {item.useIp ? item.ipAddress : item.dns}:{item.port}
                  {item.useIp && item.dns ? <span className="nk-zbx-muted"> ({item.dns})</span> : null}
                  {!item.useIp && item.ipAddress ? <span className="nk-zbx-muted"> ({item.ipAddress})</span> : null}
                </span>
                <Availability value={item.availability} />
                {item.error ? <span className="nk-zbx-muted">{item.error}</span> : null}
              </li>
            ))}
          </ul>
        </>
      ) : null}
    </div>
  )
}

const AVAILABILITY: Record<ZabbixAvailability, string> = {
  Available: 'Available',
  Unavailable: 'Unavailable',
  Mixed: 'Partly available',
  Unknown: 'Availability unknown',
}

function Availability({ value }: { value: ZabbixAvailability }) {
  return (
    <span>
      <span className="nk-zbx-dot" data-state={value} aria-hidden="true" />
      {AVAILABILITY[value]}
    </span>
  )
}

/** Linking a host that isn't linked yet: search Zabbix by name, technical name, IP address or DNS name, then pick one. */
function LinkSearch({ base, hostName, onLinked }: { base: string; hostName: string; onLinked: () => void }) {
  const [text, setText] = useState(hostName)
  const [results, setResults] = useState<ZabbixHostSummary[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function search() {
    setBusy(true)
    setError(null)
    try {
      setResults(await api.get<ZabbixHostSummary[]>(`${base}/search?text=${encodeURIComponent(text.trim())}`))
    } catch (e) {
      setError(messageOf(e, 'Zabbix couldn’t be searched.'))
    } finally {
      setBusy(false)
    }
  }

  async function link(zabbixHostId: string) {
    setBusy(true)
    setError(null)
    try {
      await api.put(`${base}/link`, { zabbixHostId })
      onLinked()
    } catch (e) {
      setError(messageOf(e, 'The host couldn’t be linked.'))
      setBusy(false)
    }
  }

  return (
    <form
      className="nk-zbx"
      aria-label="Link to a Zabbix host"
      onSubmit={(e) => {
        e.preventDefault()
        void search()
      }}
    >
      <p className="nk-zbx-muted">This host isn’t linked to a Zabbix host. Find it in Zabbix by name, technical name, IP address or DNS name.</p>
      <div className="nk-zbx-bar">
        <input className="nk-zbx-input nk-zbx-grow" aria-label="Search Zabbix" value={text} maxLength={255} onChange={(e) => setText(e.target.value)} />
        <button type="submit" className="nk-zbx-button" disabled={busy || text.trim() === ''}>
          Search
        </button>
      </div>
      {error ? (
        <p className="nk-zbx-notice" data-error="">
          {error}
        </p>
      ) : null}
      {results?.length === 0 ? <p className="nk-zbx-muted">No Zabbix host matches.</p> : null}
      {results && results.length > 0 ? (
        <ul className="nk-zbx-rows" aria-label="Zabbix hosts">
          {results.map((z) => (
            <li key={z.hostId} className="nk-zbx-row">
              <span className="nk-zbx-grow">
                {z.name}
                {z.name !== z.host ? <span className="nk-zbx-muted"> ({z.host})</span> : null}
                <span className="nk-zbx-muted nk-zbx-mono"> {[...z.ipAddresses, ...z.dnsNames].join(', ')}</span>
              </span>
              <button type="button" className="nk-zbx-button" disabled={busy} onClick={() => void link(z.hostId)} aria-label={`Link to ${z.name}`}>
                Link
              </button>
            </li>
          ))}
        </ul>
      ) : null}
    </form>
  )
}
