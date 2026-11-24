import { useEffect, useState } from 'react'
import { api, type SettingsPanelProps } from '@nkeeper/sdk'
import { ComparisonSection } from './Comparison'
import { messageOf, presented } from './zabbix'

type Test = { kind: 'idle' } | { kind: 'testing' } | { kind: 'ready'; version: string; hostCount: number } | { kind: 'failed'; message: string }

/**
 * The plugin's section of the Settings page (docs/architecture.md §7.40), below the shell's binding: testing the
 * binding, and 1.x's comparison of Zabbix's hosts with NKeeper's, to link them or add the hosts NKeeper doesn't have.
 */
export function SettingsPanel({ integrations, version, confirmTrust }: SettingsPanelProps) {
  const bound = integrations.some((i) => i.name === 'server' && i.bound)
  const [test, setTest] = useState<Test>({ kind: 'idle' })

  // A new binding is tested again.
  useEffect(() => {
    queueMicrotask(() => setTest({ kind: 'idle' }))
  }, [version])

  async function run(retried = false): Promise<void> {
    setTest({ kind: 'testing' })
    try {
      const answer = await api.get<{ version: string; hostCount: number }>('/api/p/zabbix/admin/test')
      setTest({ kind: 'ready', ...answer })
    } catch (error) {
      const prompt = presented(error)
      if (prompt && !retried && (await confirmTrust(prompt))) {
        return run(true)
      }
      setTest({ kind: 'failed', message: messageOf(error, 'Zabbix couldn’t be reached.') })
    }
  }

  if (!bound) {
    return (
      <p className="nk-zbx-muted">
        Choose the Zabbix server above: a host with a Zabbix service, and a credential whose password is a Zabbix API token (Zabbix 6.4 or later). Only users who may use
        that host see anything from Zabbix.
      </p>
    )
  }
  return (
    <div className="nk-zbx">
      <div className="nk-zbx-bar">
        <button type="button" className="nk-zbx-button" disabled={test.kind === 'testing'} onClick={() => void run()}>
          {test.kind === 'testing' ? 'Testing…' : 'Test the connection'}
        </button>
        {test.kind === 'ready' ? (
          <span role="status">
            Zabbix {test.version}: the API token can see {test.hostCount} {test.hostCount === 1 ? 'host' : 'hosts'}.
          </span>
        ) : null}
      </div>
      {test.kind === 'failed' ? (
        <p className="nk-zbx-notice" data-error="">
          {test.message}
        </p>
      ) : null}
      <ComparisonSection version={version} confirmTrust={confirmTrust} />
    </div>
  )
}
