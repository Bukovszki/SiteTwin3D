import { GeoJsonGeometry } from '../api/models';

/**
 * Snapping in 3D (spec FR-S2, 9.3): the vector geometry of the current level is projected into a local east-north-up
 * plane around a reference point, and a point is pulled to the nearest vertex – or, if none is near, to the nearest
 * point of an edge – within a tolerance given in REAL metres. Never in EPSG:3857 (spec 7.3).
 *
 * The local plane uses the ellipsoid's radii of curvature at the reference point; over a site (a few hundred metres)
 * the error is far below a millimetre per metre. The index is a plain list with bounding-box rejection: a storey has
 * hundreds to a few thousand segments, scanned well within a pointer event.
 */

const A = 6378137;
const E2 = (1 / 298.257223563) * (2 - 1 / 298.257223563);
const RAD = Math.PI / 180;

export class LocalPlane {
  private readonly mPerLon: number;
  private readonly mPerLat: number;

  constructor(
    readonly lon0: number,
    readonly lat0: number,
  ) {
    const s = Math.sin(lat0 * RAD);
    const w = Math.sqrt(1 - E2 * s * s);
    this.mPerLon = (A / w) * Math.cos(lat0 * RAD) * RAD; // prime vertical radius
    this.mPerLat = ((A * (1 - E2)) / (w * w * w)) * RAD; // meridional radius
  }

  toLocal(lon: number, lat: number): [number, number] {
    return [(lon - this.lon0) * this.mPerLon, (lat - this.lat0) * this.mPerLat];
  }

  toLonLat(east: number, north: number): [number, number] {
    return [this.lon0 + east / this.mPerLon, this.lat0 + north / this.mPerLat];
  }
}

export interface SnapResult {
  east: number;
  north: number;
  kind: 'vertex' | 'edge';
  distance: number;
}

type Segment = [number, number, number, number];

export class SnapIndex {
  private readonly vertices: [number, number][] = [];
  private readonly segments: Segment[] = [];

  constructor(
    readonly plane: LocalPlane,
    geometries: GeoJsonGeometry[],
  ) {
    for (const g of geometries) this.add(g);
  }

  get size(): number {
    return this.vertices.length;
  }

  private add(g: GeoJsonGeometry): void {
    const line = (coords: [number, number][], closed = false) => {
      const points = coords.map(([lon, lat]) => this.plane.toLocal(lon, lat));
      // A closing vertex repeats the first one: keep it once.
      const unique = closed && points.length > 1 ? points.slice(0, -1) : points;
      this.vertices.push(...unique);
      for (let i = 0; i + 1 < points.length; i++)
        this.segments.push([...points[i], ...points[i + 1]] as Segment);
    };
    const c = g.coordinates as never;
    switch (g.type) {
      case 'Point':
        this.vertices.push(this.plane.toLocal(...(c as [number, number])));
        break;
      case 'MultiPoint':
        for (const p of c as [number, number][]) this.vertices.push(this.plane.toLocal(...p));
        break;
      case 'LineString':
        line(c);
        break;
      case 'MultiLineString':
        for (const l of c as [number, number][][]) line(l);
        break;
      case 'Polygon':
        for (const ring of c as [number, number][][]) line(ring, true);
        break;
      case 'MultiPolygon':
        for (const polygon of c as [number, number][][][])
          for (const ring of polygon) line(ring, true);
        break;
      case 'GeometryCollection':
        for (const part of (g as unknown as { geometries: GeoJsonGeometry[] }).geometries)
          this.add(part);
        break;
    }
  }

  /** Nearest vertex within the tolerance, else nearest edge point within it; null if nothing is that near. */
  snap(east: number, north: number, tolerance: number): SnapResult | null {
    if (!(tolerance > 0)) return null;
    let best: SnapResult | null = null;
    for (const [x, y] of this.vertices) {
      if (Math.abs(x - east) > tolerance || Math.abs(y - north) > tolerance) continue;
      const d = Math.hypot(x - east, y - north);
      if (d <= tolerance && (!best || d < best.distance))
        best = { east: x, north: y, kind: 'vertex', distance: d };
    }
    if (best) return best;
    for (const [x1, y1, x2, y2] of this.segments) {
      if (east < Math.min(x1, x2) - tolerance || east > Math.max(x1, x2) + tolerance) continue;
      if (north < Math.min(y1, y2) - tolerance || north > Math.max(y1, y2) + tolerance) continue;
      const dx = x2 - x1;
      const dy = y2 - y1;
      const length2 = dx * dx + dy * dy;
      const t =
        length2 === 0
          ? 0
          : Math.max(0, Math.min(1, ((east - x1) * dx + (north - y1) * dy) / length2));
      const px = x1 + t * dx;
      const py = y1 + t * dy;
      const d = Math.hypot(px - east, py - north);
      if (d <= tolerance && (!best || d < best.distance))
        best = { east: px, north: py, kind: 'edge', distance: d };
    }
    return best;
  }
}
