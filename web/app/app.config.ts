export default defineAppConfig({
  ui: {
    colors: { primary: 'brand', neutral: 'zinc' },
    button: { slots: { base: 'min-h-[var(--control-h)] rounded-md cursor-pointer text-sm font-medium', leadingIcon: 'size-4', trailingIcon: 'size-4' } },
    input: { slots: { base: 'min-h-[var(--control-h)] rounded-md text-sm ring-[var(--border-strong)]' } },
    select: { slots: { base: 'min-h-[var(--control-h)] rounded-md text-sm ring-[var(--border-strong)]' } },
    selectMenu: { slots: { base: 'min-h-[var(--control-h)] rounded-md text-sm ring-[var(--border-strong)]' } },
    textarea: { slots: { base: 'min-h-[var(--control-h)] rounded-md text-sm leading-[22px] ring-[var(--border-strong)]' } },
    badge: { slots: { base: 'h-5 rounded text-xs font-normal leading-[18px]' } },
    card: { slots: { root: 'rounded-lg shadow-none ring-1 ring-[var(--border)] bg-[var(--surface)]', header: 'px-3 py-2.5 sm:px-4 sm:py-2.5', body: 'p-3 sm:p-4', footer: 'px-3 py-2.5 sm:px-4 sm:py-2.5' } },
    table: { slots: { th: 'px-3 py-1.5 text-[13px] font-medium text-[var(--text-2)]', td: 'px-3 py-px' } },
    modal: { slots: { content: 'mobile-dialog rounded-lg max-w-[560px]', title: 'text-base font-semibold', header: 'p-3 sm:p-4 min-h-[48px]', body: 'p-3 sm:p-4', footer: 'justify-end p-3 sm:p-4' } },
    slideover: { slots: { content: 'w-full max-w-[480px] max-md:max-w-none', title: 'text-base font-semibold', body: 'p-3 sm:p-4', footer: 'justify-end' } },
    checkbox: { slots: { base: 'check-control', container: 'check-container', label: 'check-label', wrapper: 'check-wrapper' } },
    toast: { slots: { root: 'rounded-lg' } }
  },
  icon: { mode: 'css', cssLayer: 'base' }
})
