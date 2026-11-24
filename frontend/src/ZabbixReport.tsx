import type { ReportViewProps } from '@nkeeper/sdk'
import { ComparisonSection } from './Comparison'

/**
 * 1.x's Zabbix report (docs/architecture.md §7.40), for administrators, on the Reports page (SDK 1.14): the comparison
 * the settings panel holds, loaded at once, each NKeeper host showing in the workspace when chosen.
 */
export function ZabbixReport({ showHost }: ReportViewProps) {
  return (
    <div className="nk-zbx">
      <ComparisonSection version={0} showHost={showHost} loadAtOnce />
    </div>
  )
}
