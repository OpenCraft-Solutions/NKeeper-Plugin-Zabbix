import { adoptStyles, definePlugin } from '@nkeeper/sdk'
import { HostPanel } from './HostPanel'
import { SettingsPanel } from './SettingsPanel'
import { zabbixStyles } from './styles'

const plugin = definePlugin({
  id: 'zabbix',
  activate() {
    // Constructable stylesheets: the shell's policy blocks <style> elements (docs/architecture.md §7.14).
    adoptStyles(zabbixStyles)
  },
  // SDK 1.13 (§7.39): a panel in every host's details, and a section of the Settings page.
  hostPanels: { Zabbix: HostPanel },
  settingsPanel: SettingsPanel,
})

export default plugin
