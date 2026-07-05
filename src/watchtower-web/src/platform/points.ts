// Extension points — the app-owned contracts that modules contribute to without importing each other's
// components. A point is a typed token (data); a contributor imports the token, not the owner's chunk.
//
// These live in the platform layer ("what you own"): the kernel provides `defineExtensionPoint`, the app
// declares which slots exist and what payload each accepts.
import type { ComponentType } from 'react'
import type { LucideIcon } from 'lucide-react'
import { defineExtensionPoint } from './contributions'
import type { Container, Stack } from '@/lib/types'

/** Primary navigation. Rendered in the desktop sidebar and (unless `mobile === false`) the mobile tab bar. */
export interface SidebarItem {
  readonly label: string
  readonly icon: LucideIcon
  readonly to: string
  /** Exact path match for active state (index route); otherwise prefix match. */
  readonly exact?: boolean
  /** Show in the mobile bottom tab bar. Defaults to true; set false for desktop-only destinations. */
  readonly mobile?: boolean
  /** Optional dynamic indicator (e.g. an "update available" dot) rendered by the owning module. */
  readonly badge?: ComponentType<{ placement: 'sidebar' | 'tab' }>
}
export const sidebarItems = defineExtensionPoint<SidebarItem>('platform.sidebar')

/** A tab on the stack-detail page. The slot supplies the current stack to each tab's component. */
export interface StackDetailTab {
  readonly label: string
  /** Stable `?tab=` value; also the active-tab key. */
  readonly value: string
  readonly component: ComponentType<{ stack: Stack }>
}
export const stackDetailTabs = defineExtensionPoint<StackDetailTab, { stack: Stack }>('stacks.detailTabs')

/** A section on the dashboard. Sections are self-contained (they fetch their own data). */
export interface DashboardSection {
  readonly component: ComponentType
}
export const dashboardSections = defineExtensionPoint<DashboardSection>('dashboard.sections')

/** A section on the fleet-wide Infrastructure page. */
export interface InfraSection {
  readonly component: ComponentType
}
export const infraSections = defineExtensionPoint<InfraSection>('infrastructure.sections')

/** Extra content rendered inside each container card on the stack Overview (e.g. live CPU/RAM). */
export interface ContainerCardExtra {
  readonly component: ComponentType<{ container: Container }>
}
export const containerCardExtras = defineExtensionPoint<ContainerCardExtra, { container: Container }>(
  'stacks.containerCardExtras',
)
