/**
 * Small spherical-earth helpers on WGS84 lon/lat (degrees). Directions and short distances on site scale (a few
 * hundred metres): the error of the sphere is far below the placement accuracy. Never compute metric values from
 * EPSG:3857 coordinates (spec 7.3).
 */
const R = 6371008.8;
const rad = (d: number) => (d * Math.PI) / 180;
const deg = (r: number) => (r * 180) / Math.PI;

/** Initial bearing from a to b, degrees from geographic north, clockwise, [0, 360). */
export function bearing(a: [number, number], b: [number, number]): number {
  const [l1, p1] = [rad(a[0]), rad(a[1])];
  const [l2, p2] = [rad(b[0]), rad(b[1])];
  const y = Math.sin(l2 - l1) * Math.cos(p2);
  const x = Math.cos(p1) * Math.sin(p2) - Math.sin(p1) * Math.cos(p2) * Math.cos(l2 - l1);
  return (deg(Math.atan2(y, x)) + 360) % 360;
}

/** The point at a distance (m) in a direction (degrees from north) from a start point. */
export function destination(start: [number, number], bearingDeg: number, distanceM: number): [number, number] {
  const d = distanceM / R;
  const b = rad(bearingDeg);
  const p1 = rad(start[1]);
  const l1 = rad(start[0]);
  const p2 = Math.asin(Math.sin(p1) * Math.cos(d) + Math.cos(p1) * Math.sin(d) * Math.cos(b));
  const l2 = l1 + Math.atan2(Math.sin(b) * Math.sin(d) * Math.cos(p1), Math.cos(d) - Math.sin(p1) * Math.sin(p2));
  return [deg(l2), deg(p2)];
}

/** Degrees into [0, 360). */
export const normalizeHeading = (h: number) => ((h % 360) + 360) % 360;

/** Degrees into [-180, 180). */
export const normalizeYaw = (y: number) => {
  const n = normalizeHeading(y);
  return n >= 180 ? n - 360 : n;
};
