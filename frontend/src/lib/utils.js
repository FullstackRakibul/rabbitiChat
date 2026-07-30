import { clsx } from 'clsx'
import { twMerge } from 'tailwind-merge'

// shadcn's class helper: merges conditional classes and lets a caller's
// utility win over a component's default (e.g. `class="bg-red-500"`).
export function cn(...inputs) {
  return twMerge(clsx(inputs))
}
