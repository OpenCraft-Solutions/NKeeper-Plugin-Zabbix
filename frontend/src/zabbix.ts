import { ApiError, type TrustPrompt } from '@nkeeper/sdk'

/** What the plugin's endpoints answer (backend/ZabbixQueries.cs, ZabbixEndpoints.cs). */

export interface ZabbixTag {
  name: string
  value: string | null
}

export interface ZabbixHostSummary {
  hostId: string
  host: string
  name: string
  monitored: boolean
  ipAddresses: string[]
  dnsNames: string[]
  macAddresses: string[]
  operatingSystem: string | null
  tags: ZabbixTag[]
}

export interface ZabbixHostRecord {
  summary: ZabbixHostSummary
  description: string | null
  groups: string[]
  serialNumber: string | null
}

export type ZabbixAvailability = 'Available' | 'Unavailable' | 'Unknown' | 'Mixed'

export interface ZabbixInterface {
  type: string
  ipAddress: string | null
  dns: string | null
  port: string
  useIp: boolean
  main: boolean
  availability: ZabbixAvailability
  error: string | null
}

export interface ZabbixItem {
  key: string
  name: string
  value: string | null
  units: string | null
  checkedAtUtc: string | null
}

export interface ZabbixProblem {
  eventId: string
  name: string
  severity: number
  startedAtUtc: string
  acknowledged: boolean
  suppressed: boolean
}

export interface ZabbixHostOverview {
  host: ZabbixHostSummary
  description: string | null
  inMaintenance: boolean
  availability: ZabbixAvailability
  interfaces: ZabbixInterface[]
  groups: string[]
  inventory: Record<string, string>
  items: ZabbixItem[]
  problems: ZabbixProblem[]
}

export type OverviewResponse =
  | { linked: false; canLink: boolean }
  | { linked: true; canLink: boolean; zabbixHostId: string; frontendUrl: string; overview: ZabbixHostOverview }

export interface HostsResponse {
  frontendUrl: string
  hosts: ZabbixHostRecord[]
  links: { hostId: string; zabbixHostId: string }[]
}

export const SEVERITIES = ['Not classified', 'Information', 'Warning', 'Average', 'High', 'Disaster'] as const

/** Links into the Zabbix frontend, as 1.x made them. */
export const zabbixLinks = {
  host: (frontendUrl: string, hostId: string) => `${frontendUrl}zabbix.php?action=host.view&filter_host=&filter_hostids%5B%5D=${hostId}&filter_set=1`,
  problems: (frontendUrl: string, hostId: string) => `${frontendUrl}zabbix.php?action=problem.view&hostids%5B%5D=${hostId}&filter_set=1`,
  latestData: (frontendUrl: string, hostId: string) => `${frontendUrl}zabbix.php?action=latest.view&hostids%5B%5D=${hostId}&filter_set=1`,
}

/** The plugin's code for a failure, e.g. `zabbix_not_bound`. */
export function codeOf(error: unknown): string | null {
  return error instanceof ApiError ? (error.code ?? null) : null
}

export function messageOf(error: unknown, fallback: string): string {
  return error instanceof ApiError && error.message ? error.message : fallback
}

/** The certificate to trust, when Zabbix's isn't trusted yet. */
export function presented(error: unknown): TrustPrompt | undefined {
  return codeOf(error) === 'host_key_unknown' && error instanceof ApiError ? (error.problem as { presented?: TrustPrompt } | undefined)?.presented : undefined
}
