// Shared time / digest formatting helpers. These consolidate the copies that were
// duplicated inside the route files (timeAgo, useElapsed, formatUptime, shortDigest).
// Route authors import from here instead of re-implementing.
import { useEffect, useState } from 'react'

/** Compact relative time, e.g. "12s ago", "4m ago", "3h ago", "2d ago". */
export function timeAgo(iso: string): string {
  const seconds = Math.floor((Date.now() - new Date(iso).getTime()) / 1000)
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

/** Absolute locale string for `title` attributes on relative timestamps (A9). */
export function absoluteTitle(iso: string | null | undefined): string | undefined {
  if (!iso) return undefined
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? undefined : d.toLocaleString()
}

/** Human-readable uptime since an ISO timestamp, e.g. "3d 4h", "5h 12m", "8m", "42s". */
export function formatUptime(startedAt: string): string {
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(startedAt).getTime()) / 1000))
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  if (days > 0) return `${days}d ${hours}h`
  if (hours > 0) return `${hours}h ${minutes}m`
  if (minutes > 0) return `${minutes}m`
  return `${seconds}s`
}

/** Trims a "sha256:abc123…" digest to a short readable form. Returns "—" for null. */
export function shortDigest(digest: string | null | undefined): string {
  if (!digest) return '—'
  const prefix = 'sha256:'
  return digest.startsWith(prefix)
    ? digest.slice(0, prefix.length + 12) + '…'
    : digest.slice(0, 19) + '…'
}

/**
 * Duration between two ISO timestamps as a compact string, e.g. "12s", "1m 30s".
 * If `end` is null/undefined, measures against now.
 */
export function formatDuration(start: string, end?: string | null): string {
  const endMs = end ? new Date(end).getTime() : Date.now()
  const seconds = Math.max(0, Math.round((endMs - new Date(start).getTime()) / 1000))
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  const secs = seconds % 60
  return `${minutes}m ${secs}s`
}

/**
 * Threshold tone for a 0–100 percentage, shared by Sparkline, Meter, and the
 * host-health strip (spec §5.4): ok < 80, warn ≥ 80, danger ≥ 90.
 */
export function meterTone(pct: number | null | undefined): 'ok' | 'warn' | 'danger' {
  if (pct == null || Number.isNaN(pct)) return 'ok'
  if (pct >= 90) return 'danger'
  if (pct >= 80) return 'warn'
  return 'ok'
}

/**
 * Human-readable byte size with tabular-friendly precision, e.g. "812 MB", "1.2 GB".
 * Uses binary (1024) units, matching Docker's own reporting. Returns "0 B" for 0.
 */
export function formatBytes(n: number | null | undefined): string {
  if (n == null || Number.isNaN(n)) return '—'
  if (n === 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
  const exp = Math.min(Math.floor(Math.log(Math.abs(n)) / Math.log(1024)), units.length - 1)
  const value = n / Math.pow(1024, exp)
  // Whole numbers and B/KB get no decimals; larger units get one decimal below 100.
  const decimals = exp <= 1 || value >= 100 ? 0 : 1
  return `${value.toFixed(decimals)} ${units[exp]}`
}

/**
 * Live-updating elapsed time since `startedAt`, formatted like "42s" or "2m 14s".
 * Re-renders once per second.
 */
export function useElapsed(startedAt: string): string {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 1_000)
    return () => clearInterval(id)
  }, [])
  const seconds = Math.max(0, Math.floor((now - new Date(startedAt).getTime()) / 1_000))
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  const secs = seconds % 60
  return `${minutes}m ${secs}s`
}
