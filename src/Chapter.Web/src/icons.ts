/**
 * Inline SVG icons. Bundled as strings rather than pulled from an icon font so the app
 * stays a single self-contained payload with no external requests.
 */

const svg = (path: string, size = 14): string =>
  `<svg width="${size}" height="${size}" viewBox="0 0 16 16" fill="none" stroke="currentColor" ` +
  `stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${path}</svg>`

export const icons = {
  branch: svg('<circle cx="4" cy="3.5" r="1.75"/><circle cx="4" cy="12.5" r="1.75"/>' +
    '<circle cx="12" cy="5.5" r="1.75"/><path d="M4 5.25v5.5M5.75 5.5H9a3 3 0 0 1 3 3v.5"/>'),

  repo: svg('<path d="M3 2.75A1.75 1.75 0 0 1 4.75 1h7.5A.75.75 0 0 1 13 1.75v9.5"/>' +
    '<path d="M3 2.75v10.5A1.75 1.75 0 0 0 4.75 15h8.5"/><path d="M4.75 11.25H13"/>'),

  chevron: svg('<path d="M6 4l4 4-4 4"/>', 12),

  close: svg('<path d="M4 4l8 8M12 4l-8 8"/>', 12),

  plus: svg('<path d="M8 3.5v9M3.5 8h9"/>'),

  warning: svg('<path d="M8 6v3.5M8 11.5h.01"/><path d="M6.7 2.4 1.6 11a1.5 1.5 0 0 0 1.3 2.3h10.2A1.5 1.5 0 0 0 14.4 11L9.3 2.4a1.5 1.5 0 0 0-2.6 0Z"/>'),

  external: svg('<path d="M9.5 2.5H13.5V6.5"/><path d="M13.5 2.5 7.5 8.5"/>' +
    '<path d="M12 9.5v3a1 1 0 0 1-1 1H3.5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h3"/>'),

  refresh: svg('<path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9"/><path d="M13.5 2v3.5H10"/>'),

  folder: svg('<path d="M1.75 3.75A.75.75 0 0 1 2.5 3h3.3a1 1 0 0 1 .78.37l.84 1.05a1 1 0 0 0 .78.38h5.3a.75.75 0 0 1 .75.75v6.7a.75.75 0 0 1-.75.75H2.5a.75.75 0 0 1-.75-.75Z"/>', 32),

  sun: svg('<circle cx="8" cy="8" r="3"/><path d="M8 1v1.5M8 13.5V15M15 8h-1.5M2.5 8H1M12.95 3.05l-1.06 1.06M4.11 11.89l-1.06 1.06M12.95 12.95l-1.06-1.06M4.11 4.11 3.05 3.05"/>'),

  moon: svg('<path d="M13.5 9.6A5.8 5.8 0 0 1 6.4 2.5a5.8 5.8 0 1 0 7.1 7.1Z"/>'),

  diff: svg('<path d="M4 2.5v11M12 2.5v11"/><path d="M1.5 6h5M9.5 10h5"/>', 13),
}

/** Single-letter status marker shown beside each changed file. */
export function kindLetter(kind: string): string {
  switch (kind) {
    case 'added':
      return 'A'
    case 'modified':
      return 'M'
    case 'deleted':
      return 'D'
    case 'renamed':
      return 'R'
    case 'copied':
      return 'C'
    case 'typeChanged':
      return 'T'
    case 'untracked':
      return 'U'
    default:
      return '?'
  }
}
