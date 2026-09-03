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

  // An arrow into a tray: the update is fetched and waiting, not being sent anywhere.
  download: svg('<path d="M8 2.5v7"/><path d="M5 6.5 8 9.75l3-3.25"/>' +
    '<path d="M2.75 11.5v1.25a.75.75 0 0 0 .75.75h9a.75.75 0 0 0 .75-.75V11.5"/>'),

  refresh: svg('<path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9"/><path d="M13.5 2v3.5H10"/>'),

  search: svg('<circle cx="7" cy="7" r="4.25"/><path d="m10.25 10.25 3.25 3.25"/>'),

  /** A clock face: the commit timeline, distinct from the refresh action beside it. */
  history: svg('<circle cx="8" cy="8" r="5.75"/><path d="M8 4.5v3.8l2.5 1.5"/>'),

  /** Line attribution: three source lines, each anchored to its commit marker. */
  blame: svg('<circle cx="3" cy="4" r="1"/><circle cx="3" cy="8" r="1"/><circle cx="3" cy="12" r="1"/>' +
    '<path d="M6 4h7M6 8h7M6 12h7"/>'),

  /** Cloud endpoint: the configured remote rather than a local branch. */
  cloud: svg('<path d="M5.2 12.5h6.1a2.7 2.7 0 0 0 .2-5.4A3.8 3.8 0 0 0 4.2 6a3.3 3.3 0 0 0 1 6.5Z"/>'),

  /** Upload: a push to the selected remote. */
  upload: svg('<path d="M8 13.5v-7"/><path d="m5 9.5 3-3.25 3 3.25"/><path d="M3 3.5h10"/>'),

  /** Upload with a marked shaft, used for the explicitly history-rewriting action. */
  uploadForce: svg('<path d="M8 13.5v-7"/><path d="m5 9.5 3-3.25 3 3.25"/><path d="M3 3.5h10"/><path d="M8 1.25v1"/>'),

  /** Pull: an arrow arriving from above. */
  pull: svg('<path d="M8 2.5v7"/><path d="m5 6.5 3 3.25 3-3.25"/><path d="M3 12.5h10"/>'),

  /** Pull request: a branch merge mark inside a cloud-shaped badge. */
  pullRequest: svg('<path d="M3 3.5h10M3 12.5h10"/><circle cx="5" cy="3.5" r="1.5"/>' +
    '<circle cx="11" cy="12.5" r="1.5"/><path d="M5 5v3a3 3 0 0 0 3 3h1"/>', 13),

  folder: svg('<path d="M1.75 3.75A.75.75 0 0 1 2.5 3h3.3a1 1 0 0 1 .78.37l.84 1.05a1 1 0 0 0 .78.38h5.3a.75.75 0 0 1 .75.75v6.7a.75.75 0 0 1-.75.75H2.5a.75.75 0 0 1-.75-.75Z"/>', 32),

  sun: svg('<circle cx="8" cy="8" r="3"/><path d="M8 1v1.5M8 13.5V15M15 8h-1.5M2.5 8H1M12.95 3.05l-1.06 1.06M4.11 11.89l-1.06 1.06M12.95 12.95l-1.06-1.06M4.11 4.11 3.05 3.05"/>'),

  moon: svg('<path d="M13.5 9.6A5.8 5.8 0 0 1 6.4 2.5a5.8 5.8 0 1 0 7.1 7.1Z"/>'),

  diff: svg('<path d="M4 2.5v11M12 2.5v11"/><path d="M1.5 6h5M9.5 10h5"/>', 13),

  /** Two panes with opposing arrows: compare one live worktree with another. */
  compare: svg('<path d="M2.5 5h8"/><path d="m8 2.5 2.5 2.5L8 7.5"/>' +
    '<path d="M13.5 11h-8"/><path d="m8 8.5-2.5 2.5L8 13.5"/>', 13),

  /** Stage: a plus, matching the mental model of adding to the commit. */
  stage: svg('<path d="M8 4v8M4 8h8"/>', 13),

  /** Unstage: a minus, the exact inverse. */
  unstage: svg('<path d="M4 8h8"/>', 13),

  /** Discard: a bin, because it destroys rather than moves. */
  discard: svg('<path d="M2.5 4h11M6 4V2.75A.75.75 0 0 1 6.75 2h2.5a.75.75 0 0 1 .75.75V4"/>' +
    '<path d="M4 4l.6 8.3a1 1 0 0 0 1 .95h4.8a1 1 0 0 0 1-.95L12 4"/>', 13),

  /** Reset: a branch arrow returning to a baseline, distinct from deleting a directory. */
  reject: svg('<path d="M13 5.5H6.25a3.25 3.25 0 1 0 0 6.5H9"/>' +
    '<path d="m6.5 3-3.25 2.5L6.5 8"/>', 13),

  commit: svg('<circle cx="8" cy="8" r="2.75"/><path d="M1.5 8h3.75M10.75 8h3.75"/>'),

  undo: svg('<path d="M3 7.5h7.25a3.25 3.25 0 1 1 0 6.5H6"/><path d="M5.75 4 2.5 7.5l3.25 3.5"/>'),

  check: svg('<path d="M3 8.5 6.5 12 13 4.5"/>', 13),

  /** Generate: the four-pointed spark this interaction is named with everywhere else. */
  spark: svg('<path d="M6.5 2 7.6 5.4 11 6.5 7.6 7.6 6.5 11 5.4 7.6 2 6.5 5.4 5.4Z"/>' +
    '<path d="M11.75 9.5 12.3 11.2 14 11.75 12.3 12.3 11.75 14 11.2 12.3 9.5 11.75 11.2 11.2Z"/>', 13),

  /** Stop: a square, because a spinner that is also the cancel button reads as neither. */
  stop: svg('<rect x="4.5" y="4.5" width="7" height="7" rx="1"/>', 13),

  /** Key: shown only where a credential is being asked for. */
  key: svg('<circle cx="5" cy="11" r="2.5"/><path d="M6.8 9.2 12.5 3.5M10.5 5.5l1.5 1.5M12.5 3.5 14 5"/>', 13),

  /** Rename: a pencil, the one edit affordance that is not destructive. */
  pencil: svg('<path d="M11.2 2.3a1.4 1.4 0 0 1 2 2L5.6 11.9l-2.7.8.8-2.7Z"/><path d="M10 3.5l2.5 2.5"/>', 13),

  /**
   * Stash: a tray with something set down into it. Deliberately not the bin used for
   * discard — a stash puts work aside, and the two must not read as the same action.
   */
  stash: svg('<path d="M1.75 9.5h3l.9 1.6h4.7l.9-1.6h3"/>' +
    '<path d="M3.3 4.2 1.9 9.1a1.5 1.5 0 0 0-.15.65v2.5a1.5 1.5 0 0 0 1.5 1.5h9.5a1.5 1.5 0 0 0 1.5-1.5v-2.5' +
    'a1.5 1.5 0 0 0-.15-.65L12.7 4.2a1 1 0 0 0-.95-.7H4.25a1 1 0 0 0-.95.7Z"/>', 13),

  /**
   * Worktree: a folder with a branch node on it. Deliberately not the plain branch icon the
   * rail rows use — inside the refs panel a branch row and a worktree row sit two sections
   * apart, and they must not read as the same kind of thing.
   */
  worktree: svg('<path d="M1.9 4.2a.7.7 0 0 1 .7-.7h2.7a1 1 0 0 1 .78.37l.6.76a1 1 0 0 0 .78.37h5.65' +
    'a.7.7 0 0 1 .7.7v6.6a.7.7 0 0 1-.7.7H2.6a.7.7 0 0 1-.7-.7Z"/><circle cx="8" cy="9.5" r="1.6"/>', 13),

  /** Lock: closed shackle, for a worktree that prune and move must leave alone. */
  lock: svg('<rect x="3.5" y="7" width="9" height="6.5" rx="1.2"/><path d="M5.5 7V5a2.5 2.5 0 0 1 5 0v2"/>', 13),

  /** Unlock: the same body with the shackle open, so the pair is legible at a glance. */
  unlock: svg('<rect x="3.5" y="7" width="9" height="6.5" rx="1.2"/><path d="M5.5 7V5a2.5 2.5 0 0 1 4.9-.6"/>', 13),

  /** Move: an arrow leaving one place for another. */
  move: svg('<path d="M2.5 8h9"/><path d="M8.5 5 11.5 8l-3 3"/><path d="M13.5 3v10"/>', 13),

  /** Question mark in a ring — the keyboard reference. */
  help: svg('<circle cx="8" cy="8" r="6.25"/>' +
    '<path d="M6.3 6.2a1.75 1.75 0 1 1 2.1 2.2c-.3.1-.4.4-.4.7v.4"/><path d="M8 12.1h.01"/>', 14),

  /** Tag: a label with its eyelet, which is what a tag is. */
  tag: svg('<path d="M7.2 1.9H2.6a.7.7 0 0 0-.7.7v4.6a1 1 0 0 0 .3.7l6.2 6.2a1 1 0 0 0 1.4 0l4.3-4.3' +
    'a1 1 0 0 0 0-1.4L7.9 2.2a1 1 0 0 0-.7-.3Z"/><circle cx="5" cy="5" r="1"/>', 13),
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
