import { Handle } from './box-math';

/**
 * Icons of the box editor's handles (SVG data URIs): a double arrow for the sides and the height (the billboard is
 * turned along the side's axis, so it points the way the side moves), a circular arrow for rotating, a four-way arrow
 * for moving. White disc with a coloured ring, readable on any model colour.
 */
const SIZE = '#2563eb';
const ROTATE = '#a855f7';
const MOVE = '#0f172a';

const GLYPHS = {
  resize: 'M16 8v16M12 12l4-4 4 4M12 20l4 4 4-4',
  rotate: 'M22.6 13.2A7 7 0 1 0 23 17.5M23.2 8.6v4.8h-4.8',
  move: 'M16 7v18M7 16h18M13 10l3-3 3 3M13 22l3 3 3-3M10 13l-3 3 3 3M22 13l3 3-3 3',
};

function icon(glyph: string, color: string): string {
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">` +
    `<circle cx="16" cy="16" r="14" fill="white" stroke="${color}" stroke-width="2.5"/>` +
    `<path d="${glyph}" fill="none" stroke="${color}" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/>` +
    `</svg>`;
  return 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
}

const ICONS = {
  resize: icon(GLYPHS.resize, SIZE),
  rotate: icon(GLYPHS.rotate, ROTATE),
  move: icon(GLYPHS.move, MOVE),
};

export function handleIcon(handle: Handle): string {
  return handle === 'rotate' ? ICONS.rotate : handle === 'move' ? ICONS.move : ICONS.resize;
}

/** The box axis a handle's arrow lies along (x, y, z of the box frame); null: facing the screen. */
export function handleAxis(handle: Handle): 0 | 1 | 2 | null {
  switch (handle) {
    case '+x':
    case '-x':
      return 0;
    case '+y':
    case '-y':
      return 1;
    case '+z':
      return 2;
    default:
      return null;
  }
}

/** Mouse cursor over a handle. */
export function handleCursor(handle: Handle): string {
  return handle === 'move' ? 'move' : handle === 'rotate' ? 'grab' : 'pointer';
}
