export default defineAppConfig({
  ui: {
    colors: { primary: 'emerald', neutral: 'zinc' },
    button: { slots: { base: 'min-h-12 cursor-pointer font-medium' } },
    input: { slots: { base: 'min-h-12 text-base' } },
    select: { slots: { base: 'min-h-12 text-base' } },
    selectMenu: { slots: { base: 'min-h-12 text-base' } },
    textarea: { slots: { base: 'text-base leading-7' } },
    card: { slots: { root: 'rounded-2xl shadow-none ring-1 ring-default' } },
    table: { slots: { th: 'py-4 text-xs font-medium text-muted', td: 'py-4' } }
  },
  icon: { mode: 'css', cssLayer: 'base' }
})
