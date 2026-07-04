import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

/// <summary>Merges Tailwind CSS class names, resolving conflicts correctly.</summary>
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}
