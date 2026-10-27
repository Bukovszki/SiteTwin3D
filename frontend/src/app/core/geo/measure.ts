import { LocalPlane } from './snap';

/** WGS84 lon/lat. */
type LonLat = [number, number];

/**
 * Measuring on the 2D map (user request): lengths and areas in REAL metres, never from EPSG:3857 coordinates
 * (spec 7.3). Every segment is measured in a local east-north-up plane at its midpoint (the ellipsoid's radii of
 * curvature), an area in one plane at its centre – exact to far below a millimetre per metre over a site, and still
 * well within a per mille over several kilometres.
 */
export function lengthM(points: LonLat[]): number {
  let total = 0;
  for (let i = 1; i < points.length; i++) {
    const [a, b] = [points[i - 1], points[i]];
    const plane = new LocalPlane((a[0] + b[0]) / 2, (a[1] + b[1]) / 2);
    const [x1, y1] = plane.toLocal(a[0], a[1]);
    const [x2, y2] = plane.toLocal(b[0], b[1]);
    total += Math.hypot(x2 - x1, y2 - y1);
  }
  return total;
}

/** Area of a ring (closed or not), shoelace formula in the local plane at its centre. */
export function areaM2(ring: LonLat[]): number {
  const points = ring.length > 1 && ring[0][0] === ring.at(-1)![0] && ring[0][1] === ring.at(-1)![1] ? ring.slice(0, -1) : ring;
  if (points.length < 3) return 0;
  const lon = points.reduce((s, p) => s + p[0], 0) / points.length;
  const lat = points.reduce((s, p) => s + p[1], 0) / points.length;
  const plane = new LocalPlane(lon, lat);
  const local = points.map(([x, y]) => plane.toLocal(x, y));
  let twice = 0;
  for (let i = 0; i < local.length; i++) {
    const [x1, y1] = local[i];
    const [x2, y2] = local[(i + 1) % local.length];
    twice += x1 * y2 - x2 * y1;
  }
  return Math.abs(twice) / 2;
}
