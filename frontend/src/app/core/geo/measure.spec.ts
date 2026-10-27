import { areaM2, lengthM } from './measure';
import { LocalPlane } from './snap';

describe('measure', () => {
  // Points laid out in metres on the ellipsoid's local plane at the site.
  const plane = new LocalPlane(19.08, 47.43);
  const at = (east: number, north: number) => plane.toLonLat(east, north);

  it('lengths in real metres (Web Mercator would give ~1.48× here)', () => {
    expect(lengthM([at(0, 0), at(100, 0)])).toBeCloseTo(100, 2);
    expect(lengthM([at(0, 0), at(0, 30), at(40, 30)])).toBeCloseTo(70, 2);
    expect(lengthM([at(0, 0)])).toBe(0);
  });

  it('area of a 40 m × 25 m rectangle, open or closed ring', () => {
    const ring = [at(0, 0), at(40, 0), at(40, 25), at(0, 25)];
    expect(areaM2(ring)).toBeCloseTo(1000, 1);
    expect(areaM2([...ring, ring[0]])).toBeCloseTo(1000, 1);
    expect(areaM2(ring.slice(0, 2))).toBe(0);
  });
});
