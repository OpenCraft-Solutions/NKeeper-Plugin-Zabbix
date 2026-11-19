/** The plugin's styles, adopted as a constructable stylesheet (the shell's policy blocks <style> elements, §7.14). */
export const zabbixStyles = `
.nk-zbx { display: flex; flex-direction: column; gap: 0.75rem; font-size: 0.875rem; }
.nk-zbx h3 { margin: 0.25rem 0 0; font-size: 0.875rem; font-weight: 620; }
.nk-zbx-muted { color: var(--color-text-muted, #9aa8c1); }
.nk-zbx-mono { font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace; font-size: 0.8125rem; }
.nk-zbx-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; }
.nk-zbx-bar > .nk-zbx-grow { flex: 1; }
.nk-zbx-props { display: grid; grid-template-columns: minmax(8rem, max-content) 1fr; gap: 0.25rem 1rem; margin: 0; }
.nk-zbx-props dt { color: var(--color-text-muted, #9aa8c1); }
.nk-zbx-props dd { margin: 0; overflow-wrap: anywhere; white-space: pre-wrap; }
.nk-zbx-rows { display: flex; flex-direction: column; gap: 0.25rem; margin: 0; padding: 0; list-style: none; }
.nk-zbx-row { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; padding: 0.375rem 0.5rem;
  border: 1px solid var(--color-border, #233150); border-radius: var(--radius-md, 8px); }
.nk-zbx-row > .nk-zbx-grow { flex: 1; min-width: 10rem; }
.nk-zbx-badge { padding: 0.0625rem 0.375rem; border-radius: 999px; background: var(--color-surface-raised, #16213a); font-size: 0.75rem; font-weight: 600; }
.nk-zbx-severity { min-width: 6.5rem; padding: 0.0625rem 0.375rem; border-radius: 4px; text-align: center; font-size: 0.75rem; font-weight: 600; color: #0b1220; background: #97aab3; }
.nk-zbx-severity[data-severity='1'] { background: #7499ff; }
.nk-zbx-severity[data-severity='2'] { background: #ffc859; }
.nk-zbx-severity[data-severity='3'] { background: #ffa059; }
.nk-zbx-severity[data-severity='4'] { background: #e97659; }
.nk-zbx-severity[data-severity='5'] { background: #e45959; }
.nk-zbx-dot { display: inline-block; width: 0.5rem; height: 0.5rem; margin-right: 0.375rem; border-radius: 999px; background: var(--color-text-subtle, #7d8ba6); }
.nk-zbx-dot[data-state='Available'] { background: #4ade80; }
.nk-zbx-dot[data-state='Unavailable'] { background: #f87171; }
.nk-zbx-dot[data-state='Mixed'] { background: #f5b84b; }
.nk-zbx-notice { padding: 0.5rem 0.75rem; border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-md, 8px); }
.nk-zbx-notice[data-error] { border-color: var(--color-danger-border, #b91c1c); color: var(--color-danger-text, #fca5a5); }
.nk-zbx-button { display: inline-flex; align-items: center; min-height: 1.75rem; padding: 0 0.625rem; border: 1px solid var(--color-border-strong, #33456b);
  border-radius: var(--radius-md, 8px); background: transparent; color: inherit; font: inherit; font-size: 0.8125rem; font-weight: 600; cursor: pointer; }
.nk-zbx-button:hover:not(:disabled) { background: var(--color-surface-raised, #16213a); }
.nk-zbx-button:disabled { opacity: 0.5; cursor: default; }
.nk-zbx-button[data-primary] { background: var(--color-accent, #2563eb); border-color: var(--color-accent, #2563eb); color: var(--color-on-accent, #fff); }
.nk-zbx-input { min-height: 1.75rem; padding: 0 0.5rem; border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-md, 8px);
  background: var(--color-input-bg, #0b1220); color: inherit; font: inherit; }
.nk-zbx-link { color: var(--color-accent-text, #8fb4ff); }
.nk-zbx-table { width: 100%; border-collapse: collapse; }
.nk-zbx-table th, .nk-zbx-table td { padding: 0.25rem 0.5rem; border-bottom: 1px solid var(--color-border, #233150); text-align: left; vertical-align: top; }
.nk-zbx-table th { color: var(--color-text-muted, #9aa8c1); font-weight: 600; }
.nk-zbx details > summary { cursor: pointer; font-weight: 620; }
`
