// getRandomValues works over ordinary LAN HTTP even when randomUUID() does not.
export function clientId(prefix: string): string {
  if (typeof crypto === 'undefined' || typeof crypto.getRandomValues !== 'function')
    throw new Error('当前浏览器不支持生成安全编号')
  const bytes = crypto.getRandomValues(new Uint8Array(12))
  return prefix + Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('')
}
