import { bearing, destination, normalizeYaw } from './geodesy';

describe('geodesy', () => {
  it('bearing: north, east, and a diagonal at 47.5°', () => {
    expect(bearing([19, 47.5], [19, 47.6])).toBeCloseTo(0, 6);
    expect(bearing([19, 47.5], [19.1, 47.5])).toBeCloseTo(90, 1);
    expect(bearing([19, 47.5], [19.001, 47.501])).toBeCloseTo(34.0, 0); // a degree of longitude is shorter here
  });

  it('destination and bearing are consistent', () => {
    const end = destination([19.08, 47.43], 123, 50);
    expect(bearing([19.08, 47.43], end)).toBeCloseTo(123, 4);
  });

  it('normalizes yaw into [-180, 180)', () => {
    expect(normalizeYaw(190)).toBe(-170);
    expect(normalizeYaw(-190)).toBe(170);
    expect(normalizeYaw(180)).toBe(-180);
  });
});
