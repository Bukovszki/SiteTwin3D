/**
 * Geometry of the boxes (spec 9.1, 9.3) in the box's own frame: x to the right, y forward (the heading, clockwise
 * from north), z up, origin in the centre, metres. Kept free of Cesium so that it can be unit tested; the viewer
 * converts between ECEF and the east-north-up offsets used here.
 */

export interface BoxShape {
  sizeX: number;
  sizeY: number;
  sizeZ: number;
  headingDeg: number;
}

/** Smallest side, so that a handle can never turn a box inside out. */
export const MIN_SIZE = 0.05;
/**
 * A picked surface point is a little noisy (depth buffer), and a box usually stands exactly on a floor or wall: this
 * much outside still counts as inside. The classification volume is drawn with the same margin (a surface lying
 * exactly on a face of the volume would be coloured only partly).
 */
export const MARGIN = 0.05;

const rad = (deg: number) => (deg * Math.PI) / 180;

/** East-north-up offset from the centre → box frame. */
export function toBoxFrame(
  heading: number,
  east: number,
  north: number,
  up: number,
): [number, number, number] {
  const h = rad(heading);
  return [east * Math.cos(h) - north * Math.sin(h), east * Math.sin(h) + north * Math.cos(h), up];
}

/** Box frame → east-north-up offset from the centre. */
export function fromBoxFrame(
  heading: number,
  x: number,
  y: number,
  z: number,
): [number, number, number] {
  const h = rad(heading);
  return [x * Math.cos(h) + y * Math.sin(h), -x * Math.sin(h) + y * Math.cos(h), z];
}

/** Containment test of a point given as east-north-up offset from the box centre (spec 9.3). */
export function contains(box: BoxShape, east: number, north: number, up: number): boolean {
  const [x, y, z] = toBoxFrame(box.headingDeg, east, north, up);
  return (
    Math.abs(x) <= box.sizeX / 2 + MARGIN &&
    Math.abs(y) <= box.sizeY / 2 + MARGIN &&
    Math.abs(z) <= box.sizeZ / 2 + MARGIN
  );
}

export const volume = (box: BoxShape) => box.sizeX * box.sizeY * box.sizeZ;

/**
 * FR-A3b: of overlapping or nested boxes the one with the smallest volume wins. `offset` gives the clicked point
 * relative to each candidate's centre (east, north, up).
 */
export function smallestContaining<T extends BoxShape>(
  boxes: T[],
  offset: (box: T) => [number, number, number],
): T | null {
  let best: T | null = null;
  for (const box of boxes) {
    const [e, n, u] = offset(box);
    if (contains(box, e, n, u) && (best == null || volume(box) < volume(best))) best = box;
  }
  return best;
}

export type Handle = '+x' | '-x' | '+y' | '-y' | '+z' | 'rotate' | 'move';

/** A handle's place in the box frame (the rotation handle stands out in front of the box). */
export function handlePosition(box: BoxShape, handle: Handle): [number, number, number] {
  switch (handle) {
    case '+x':
      return [box.sizeX / 2, 0, 0];
    case '-x':
      return [-box.sizeX / 2, 0, 0];
    case '+y':
      return [0, box.sizeY / 2, 0];
    case '-y':
      return [0, -box.sizeY / 2, 0];
    case '+z':
      return [0, 0, box.sizeZ / 2];
    case 'rotate':
      return [0, box.sizeY / 2 + Math.max(0.4, box.sizeY * 0.3), 0];
    case 'move':
      return [0, 0, 0];
  }
}

/** Result of dragging a handle: new shape and how far the centre moved (box frame of the drag start). */
export interface BoxEdit {
  shape: BoxShape;
  shift: [number, number, number];
}

/**
 * Dragging `handle` to the point `q` (box frame at the start of the drag; for side, rotation and move handles on the
 * horizontal plane through the centre, for the top handle on a vertical plane through the axis). Sides move one face
 * and keep the opposite one; `start` is where a move drag began.
 */
export function dragHandle(
  box: BoxShape,
  handle: Handle,
  q: [number, number, number],
  start: [number, number, number] = [0, 0, 0],
): BoxEdit {
  const shape = { ...box };
  const shift: [number, number, number] = [0, 0, 0];
  const side = (size: number, value: number, sign: 1 | -1): [number, number] => {
    // Fixed face at −sign·size/2; the dragged one follows the pointer, never closer than MIN_SIZE.
    const fixed = (-sign * size) / 2;
    const moving = sign > 0 ? Math.max(value, fixed + MIN_SIZE) : Math.min(value, fixed - MIN_SIZE);
    return [Math.abs(moving - fixed), (moving + fixed) / 2];
  };
  switch (handle) {
    case '+x':
    case '-x':
      [shape.sizeX, shift[0]] = side(box.sizeX, q[0], handle === '+x' ? 1 : -1);
      break;
    case '+y':
    case '-y':
      [shape.sizeY, shift[1]] = side(box.sizeY, q[1], handle === '+y' ? 1 : -1);
      break;
    case '+z':
      [shape.sizeZ, shift[2]] = side(box.sizeZ, q[2], 1);
      break;
    case 'rotate':
      shape.headingDeg =
        (((box.headingDeg + (Math.atan2(q[0], q[1]) * 180) / Math.PI) % 360) + 360) % 360;
      break;
    case 'move':
      shift[0] = q[0] - start[0];
      shift[1] = q[1] - start[1];
      break;
  }
  return { shape, shift };
}

/** Ray (origin o, direction d, box frame) ∩ the plane through the centre that a handle moves on. */
export function dragPlanePoint(
  handle: Handle,
  o: [number, number, number],
  d: [number, number, number],
): [number, number, number] | null {
  let t: number;
  if (handle === '+z') {
    // Vertical plane through the z axis, facing the viewer.
    const len = Math.hypot(d[0], d[1]);
    if (len < 1e-9) return null;
    const n = [d[0] / len, d[1] / len];
    const denom = d[0] * n[0] + d[1] * n[1];
    t = -(o[0] * n[0] + o[1] * n[1]) / denom;
  } else {
    if (Math.abs(d[2]) < 1e-9) return null;
    t = -o[2] / d[2];
  }
  if (!Number.isFinite(t) || t < 0) return null;
  return [o[0] + t * d[0], o[1] + t * d[1], o[2] + t * d[2]];
}
