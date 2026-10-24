import { LocalPlane, SnapIndex } from './snap';

describe('snap', () => {
  const plane = new LocalPlane(19.08, 47.43);
  /** A 10 m × 6 m room with its south-west corner at the reference point (in lon/lat). */
  const room = {
    type: 'Polygon',
    coordinates: [
      [
        [0, 0],
        [10, 0],
        [10, 6],
        [0, 6],
        [0, 0],
      ].map(([e, n]) => plane.toLonLat(e, n)),
    ],
  };
  const index = new SnapIndex(plane, [room, { type: 'Point', coordinates: plane.toLonLat(5, 3) }]);

  it('projects in real metres around the reference point', () => {
    // 1e-5° of longitude at 47.43° ≈ 0.7547 m, of latitude ≈ 1.1119 m (WGS84).
    const [e, n] = plane.toLocal(19.08001, 47.43001);
    expect(e).toBeCloseTo(0.7547, 3);
    expect(n).toBeCloseTo(1.1119, 3);
    const [lon, lat] = plane.toLonLat(e, n);
    expect(lon).toBeCloseTo(19.08001, 10);
    expect(lat).toBeCloseTo(47.43001, 10);
  });

  it('prefers the nearest vertex within the tolerance', () => {
    const hit = index.snap(9.85, 5.9, 0.3)!;
    expect(hit.kind).toBe('vertex');
    expect(hit.east).toBeCloseTo(10, 6);
    expect(hit.north).toBeCloseTo(6, 6);
    expect(index.snap(4.9, 3.1, 0.3)?.kind).toBe('vertex'); // the free point
  });

  it('falls back to the nearest point of an edge', () => {
    const hit = index.snap(3, 0.2, 0.3)!;
    expect(hit.kind).toBe('edge');
    expect(hit.east).toBeCloseTo(3, 6);
    expect(hit.north).toBeCloseTo(0, 6);
    expect(hit.distance).toBeCloseTo(0.2, 6);
  });

  it('leaves points outside the tolerance (or with snapping off) alone', () => {
    expect(index.snap(3, 0.5, 0.3)).toBeNull();
    expect(index.snap(10, 6, 0)).toBeNull();
    expect(index.size).toBe(5); // 4 corners (closing vertex once) + the point
  });
});
