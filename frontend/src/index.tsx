import { adoptStyles, definePlugin } from '@nkeeper/sdk'
import { HostPanel } from './HostPanel'
import { SettingsPanel } from './SettingsPanel'
import { zabbixStyles } from './styles'
import { ZabbixReport } from './ZabbixReport'

const plugin = definePlugin({
  id: 'zabbix',
  activate() {
    // Constructable stylesheets: the shell's policy blocks <style> elements (docs/architecture.md §7.14).
    adoptStyles(zabbixStyles)
  },
  // SDK 1.13 (§7.39): a panel in every host's details, and a section of the Settings page.
  hostPanels: { Zabbix: HostPanel },
  settingsPanel: SettingsPanel,
  // SDK 1.14 (§7.41): 1.x's Zabbix report, for administrators.
  reports: { zabbix: { label: 'Zabbix', description: 'Hosts in Zabbix and NKeeper: link them, or add Zabbix’s to NKeeper', administrators: true, view: ZabbixReport } },
})

export default plugin
