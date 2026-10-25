import { transform } from 'ol/proj';
import { layerProjection } from './projections';

describe('WMS layer projections', () => {
  it('requests EOV layers in EOV, Web Mercator ones as the map', () => {
    expect(layerProjection({ CRS: 'EPSG:23700' })).toBe('EPSG:23700');
    expect(layerProjection({ srs: 'epsg:23700' })).toBeUndefined(); // only the upper-case WMS keys count
    expect(layerProjection({ SRS: 'epsg:23700' })).toBe('EPSG:23700');
    expect(layerProjection({ CRS: 'EPSG:3857' })).toBeUndefined();
    expect(layerProjection({})).toBeUndefined();
    expect(layerProjection({ CRS: 'EPSG:999999' })).toBeUndefined();
  });

  it('places the EOV origin of the projection where EPSG:23700 has it', () => {
    layerProjection({ CRS: 'EPSG:23700' });
    // The projection centre (Gellérthegy) is at false easting/northing 650000 / 200000 (within a few metres: datum shift).
    const [e, n] = transform([19.04857177777778, 47.14439372222222], 'EPSG:4326', 'EPSG:23700');
    expect(Math.abs(e - 650000)).toBeLessThan(150);
    expect(Math.abs(n - 200000)).toBeLessThan(150);
    // Budapest centre (site of the API tests): the same EOV coordinates PostGIS gives.
    const [be, bn] = transform([19.04, 47.5], 'EPSG:4326', 'EPSG:23700');
    expect(Math.abs(be - 649439.11)).toBeLessThan(2);
    expect(Math.abs(bn - 239562.88)).toBeLessThan(2);
  });
});
