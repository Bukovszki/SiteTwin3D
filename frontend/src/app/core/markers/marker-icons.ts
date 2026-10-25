/**
 * The point symbols of every view (map, panorama, 3D) as SVG data URIs, so that a symbol means the same everywhere:
 * - a record's place: a pin with the record type's glyph (gear: equipment, square: room …); filled where the place is
 *   exact (point, box, model element), hollow where it only comes from an info point; yellow when selected;
 * - a panorama's camera position: a disc with a camera, its colour the publication status; larger when open;
 * - a neighbouring panorama (walk-through): a disc with an arrow;
 * - a site: a green disc with a building (as the app icon).
 * Own drawings. Sizes in CSS pixels; pins are anchored at their tip (bottom centre).
 */

export const MARKER_COLORS = {
  place: '#f97316',
  selected: '#facc15',
  selectedEdge: '#1e293b',
  approved: '#2563eb',
  pending: '#f59e0b',
  failed: '#dc2626',
  site: '#10b981',
} as const;

/** Glyphs on a 20 × 20 box centred at (0, 0); `C` is the stroke/fill colour. */
const GLYPHS: Record<string, (c: string) => string> = {
  equipment: (c) =>
    `<circle r="3.4" fill="none" stroke="${c}" stroke-width="2"/>` +
    [0, 45, 90, 135, 180, 225, 270, 315]
      .map(
        (a) =>
          `<line x1="0" y1="-5.2" x2="0" y2="-7.4" stroke="${c}" stroke-width="2.4" stroke-linecap="round" transform="rotate(${a})"/>`,
      )
      .join(''),
  room: (c) =>
    `<rect x="-6" y="-6" width="12" height="12" rx="1.5" fill="none" stroke="${c}" stroke-width="2"/>`,
  outdoor_area: (c) =>
    `<rect x="-6.5" y="-6" width="13" height="12" rx="2" fill="none" stroke="${c}" stroke-width="2" stroke-dasharray="3 2"/>` +
    `<path d="M-4 1.5q2-2 4 0t4 0" fill="none" stroke="${c}" stroke-width="1.8" stroke-linecap="round"/>`,
  building: (c) =>
    `<path d="M-4.5 7V-7h9V7M-7 7h14" fill="none" stroke="${c}" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>` +
    `<rect x="-2.6" y="-4.6" width="2" height="2" fill="${c}"/><rect x="0.6" y="-4.6" width="2" height="2" fill="${c}"/>` +
    `<rect x="-2.6" y="-0.8" width="2" height="2" fill="${c}"/><rect x="0.6" y="-0.8" width="2" height="2" fill="${c}"/>`,
  level: (c) =>
    `<path d="M-6-4h12M-6 0h12M-6 4h12" stroke="${c}" stroke-width="2" stroke-linecap="round"/>`,
  process_line: (c) =>
    `<path d="M-4 0L4-4.5M-4 0L4 4.5" stroke="${c}" stroke-width="1.8"/>` +
    `<circle cx="-4.5" cy="0" r="2.4" fill="${c}"/><circle cx="4.5" cy="-4.8" r="2.4" fill="${c}"/><circle cx="4.5" cy="4.8" r="2.4" fill="${c}"/>`,
  default: (c) => `<circle r="3.2" fill="${c}"/>`,
  camera: (c) =>
    `<path d="M-7-3.5h3l1.5-2.5h5l1.5 2.5h3v9h-14z" fill="none" stroke="${c}" stroke-width="1.9" stroke-linejoin="round"/>` +
    `<circle cx="0" cy="1" r="2.6" fill="none" stroke="${c}" stroke-width="1.9"/>`,
  arrow: (c) =>
    `<path d="M0 6V-5M-5-0.5L0-6l5 5.5" fill="none" stroke="${c}" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"/>`,
};

const cache = new Map<string, string>();

function uri(key: string, svg: () => string): string {
  let value = cache.get(key);
  if (!value) {
    value = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg());
    cache.set(key, value);
  }
  return value;
}

/** Glyph name of a node type (unknown types: a dot). */
export function glyphOf(typeCode: string | null | undefined): string {
  return typeCode && GLYPHS[typeCode] ? typeCode : 'default';
}

export interface PlaceMarker {
  typeCode: string | null | undefined;
  selected?: boolean;
  /** Only an info point gives the place: an approximate position (hollow pin). */
  approximate?: boolean;
}

/** Pin of a record's place: 28 × 36 px, anchored at the tip. */
export const PIN = { width: 28, height: 36 } as const;

export function placeIcon({
  typeCode,
  selected = false,
  approximate = false,
}: PlaceMarker): string {
  const glyph = glyphOf(typeCode);
  return uri(`pin:${glyph}:${selected}:${approximate}`, () => {
    const fill = selected ? MARKER_COLORS.selected : approximate ? '#fff' : MARKER_COLORS.place;
    const edge = selected ? MARKER_COLORS.selectedEdge : approximate ? MARKER_COLORS.place : '#fff';
    const ink = selected ? MARKER_COLORS.selectedEdge : approximate ? MARKER_COLORS.place : '#fff';
    return (
      `<svg xmlns="http://www.w3.org/2000/svg" width="${PIN.width}" height="${PIN.height}" viewBox="0 0 28 36">` +
      `<path d="M14 34.5C14 34.5 3 22.2 3 14a11 11 0 0 1 22 0c0 8.2-11 20.5-11 20.5z" fill="${fill}" stroke="${edge}" ` +
      `stroke-width="2"${approximate && !selected ? ' stroke-dasharray="3.5 2.2"' : ''}/>` +
      `<g transform="translate(14 14)">${GLYPHS[glyph](ink)}</g></svg>`
    );
  });
}

export type PanoramaState = 'approved' | 'pending' | 'failed';

/** Publication state of a panorama for its symbol colour. */
export function panoramaState(privacyStatus: string): PanoramaState {
  return privacyStatus === 'approved'
    ? 'approved'
    : privacyStatus === 'failed'
      ? 'failed'
      : 'pending';
}

/** Camera position of a panorama: 26 px disc (34 px with a ring when open), centred. */
export function panoramaIcon(state: PanoramaState, open = false): string {
  return uri(`pano:${state}:${open}`, () => {
    const size = open ? 34 : 26;
    const r = open ? 13 : 12;
    const color = MARKER_COLORS[state];
    return (
      `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="${-size / 2} ${-size / 2} ${size} ${size}">` +
      (open
        ? `<circle r="16" fill="none" stroke="${color}" stroke-width="2" opacity="0.55"/>`
        : '') +
      `<circle r="${r - 1}" fill="${color}" stroke="#fff" stroke-width="2"/>` +
      `<g transform="scale(0.85)">${GLYPHS['camera']('#fff')}</g></svg>`
    );
  });
}

export function panoramaIconSize(open = false): number {
  return open ? 34 : 26;
}

/** A neighbouring panorama in the walk-through: 30 px white disc with a blue arrow, centred. */
export function neighbourIcon(): string {
  return uri(
    'nav',
    () =>
      `<svg xmlns="http://www.w3.org/2000/svg" width="30" height="30" viewBox="-15 -15 30 30">` +
      `<circle r="13" fill="#fff" fill-opacity="0.9" stroke="${MARKER_COLORS.approved}" stroke-width="3"/>` +
      `<g>${GLYPHS['arrow'](MARKER_COLORS.approved)}</g></svg>`,
  );
}

/** A site on the overview map: 24 px green disc with a building (26 px with a dark edge when selected). */
export function siteIcon(selected = false): string {
  return uri(`site:${selected}`, () => {
    const size = selected ? 28 : 24;
    return (
      `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="-14 -14 28 28">` +
      `<circle r="12.5" fill="${MARKER_COLORS.site}" stroke="${selected ? MARKER_COLORS.selectedEdge : '#fff'}" stroke-width="2"/>` +
      `<g transform="scale(0.85)">${GLYPHS['building']('#fff')}</g></svg>`
    );
  });
}
