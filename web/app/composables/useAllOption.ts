/** Keep an unfiltered API value empty while giving the select a nonempty option ID. */
export function useAllOption(value: Ref<string>) {
  return computed({
    get: () => value.value || 'all',
    set: (selected: string) => { value.value = selected === 'all' ? '' : selected }
  })
}
