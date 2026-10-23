import {
  contains,
  dragHandle,
  dragPlanePoint,
  fromBoxFrame,
  smallestContaining,
  toBoxFrame,
} from './box-math';

const box = (sizeX: number, sizeY: number, sizeZ: number, headingDeg = 0) => ({
  sizeX,
  sizeY,
  sizeZ,
  headingDeg,
});

describe('box math', () => {
  it('turns the frame clockwise from north', () => {
    // Heading 90°: forward (+y) points east.
    const [x, y] = toBoxFrame(90, 1, 0, 0);
    expect(x).toBeCloseTo(0);
    expect(y).toBeCloseTo(1);
    const [e, n, u] = fromBoxFrame(90, 0, 1, 2);
    expect(e).toBeCloseTo(1);
    expect(n).toBeCloseTo(0);
    expect(u).toBe(2);
  });

  it('tests containment after undoing the rotation', () => {
    // 4 m along its x axis, which points south after turning 90°: 1.9 m north is inside, 1.9 m east is not.
    const long = box(4, 1, 1, 90);
    expect(contains(long, 0, 1.9, 0)).toBe(true);
    expect(contains(long, 1.9, 0, 0)).toBe(false);
    expect(contains(long, 0, 0, 0.6)).toBe(false);
  });

  it('selects the smallest of nested boxes containing the point (FR-A3b)', () => {
    const room = { id: 'room', ...box(10, 8, 3) };
    const pump = { id: 'pump', ...box(1.2, 0.8, 1) };
    const far = { id: 'far', ...box(0.5, 0.5, 0.5) };
    const centres: Record<string, [number, number, number]> = {
      room: [0, 0, 0],
      pump: [0.3, 0.2, -0.5],
      far: [5, 5, 0],
    };
    const point: [number, number, number] = [0.5, 0.3, -0.4];
    const offset = (b: typeof room) =>
      centres[b.id].map((c, i) => point[i] - c) as [number, number, number];

    expect(smallestContaining([room, pump, far], offset)?.id).toBe('pump');
    expect(smallestContaining([room, far], offset)?.id).toBe('room');
    expect(smallestContaining([far], offset)).toBeNull();
  });

  it('resizes one face and keeps the opposite one', () => {
    const edit = dragHandle(box(2, 1, 1), '+x', [2, 0, 0]);
    expect(edit.shape.sizeX).toBeCloseTo(3); // −1 … 2
    expect(edit.shift[0]).toBeCloseTo(0.5);
    const shrink = dragHandle(box(2, 1, 1), '-y', [0, 5, 0]);
    expect(shrink.shape.sizeY).toBeCloseTo(0.05); // never inside out
    const top = dragHandle(box(1, 1, 1), '+z', [0, 0, 1.5]);
    expect(top.shape.sizeZ).toBeCloseTo(2);
    expect(top.shift[2]).toBeCloseTo(0.5);
  });

  it('rotates by the angle of the handle and moves by the drag', () => {
    expect(dragHandle(box(1, 1, 1, 350), 'rotate', [1, 1, 0]).shape.headingDeg).toBeCloseTo(35);
    expect(dragHandle(box(1, 1, 1), 'move', [3, 2, 0], [1, 1, 0]).shift).toEqual([2, 1, 0]);
  });

  it('intersects the pointer ray with the drag plane', () => {
    expect(dragPlanePoint('+x', [0, -10, 10], [0, 1, -1])).toEqual([0, 0, 0]);
    expect(dragPlanePoint('+z', [0, -10, 3], [0, 1, 0])).toEqual([0, 0, 3]);
    expect(dragPlanePoint('+x', [0, 0, 10], [0, 1, 0])).toBeNull(); // parallel
  });
});
