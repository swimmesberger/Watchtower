import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

/// <summary>Merges Tailwind CSS class names, resolving conflicts correctly.</summary>
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

/**
 * Generates an RFC 4122 v4 UUID. Prefers the native `crypto.randomUUID()`, which browsers
 * only expose in a secure context (HTTPS or localhost); falls back to `crypto.getRandomValues()`
 * — available over plain HTTP too — so the app also works on a LAN/NAS deployment served
 * over http://host:port.
 */
export function randomUuid(): string {
  if (typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID()
  }
  const bytes = new Uint8Array(16)
  crypto.getRandomValues(bytes)
  // RFC 4122 §4.4: set the version (4) and variant (10xx) bits.
  // getRandomValues fills every byte, so the reads are non-null.
  bytes[6] = (bytes[6]! & 0x0f) | 0x40
  bytes[8] = (bytes[8]! & 0x3f) | 0x80
  const hex = Array.from(bytes, (b) => b.toString(16).padStart(2, '0'))
  return (
    hex.slice(0, 4).join('') +
    '-' +
    hex.slice(4, 6).join('') +
    '-' +
    hex.slice(6, 8).join('') +
    '-' +
    hex.slice(8, 10).join('') +
    '-' +
    hex.slice(10, 16).join('')
  )
}
