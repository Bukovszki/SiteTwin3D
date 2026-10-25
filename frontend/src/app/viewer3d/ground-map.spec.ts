import { MapLayer, Site } from '../core/api/models';
import { groundable, groundBox, groundImageUrl } from './ground-map';

const layer = (wmsParams: Record<string, unknown>): MapLayer => ({
  id: 7, siteId: null, source: 'external_wms', name: 'OSM', layer: 'OSM-WMS', wmsParams, externalUrl: null, sortOrder: 0, visibleDefault: true,
});
const site = { refPoint: { type: 'Point', coordinates: [19.08, 47.43] }, extent: null, baseHeightM: 120 } as unknown as Site;

describe('ground map', () => {
  it('lays flat only layers served in Web Mercator or WGS84', () => {
    expect(groundable(layer({}))).toBe(true);
    expect(groundable(layer({ CRS: 'EPSG:3857' }))).toBe(true);
    expect(groundable(layer({ CRS: 'EPSG:23700' }))).toBe(false);
  });

  it('covers 800 m around a site without extent (Web Mercator stretched by 1/cos(lat))', () => {
    const [minX, minY, maxX, maxY] = groundBox(site);
    const metres = (maxX - minX) * Math.cos((47.43 * Math.PI) / 180);
    expect(metres).toBeCloseTo(800, 0);
    expect(maxY - minY).toBeCloseTo(maxX - minX, 6);
  });

  it('asks the image through the proxy in Web Mercator', () => {
    const url = groundImageUrl(layer({}), groundBox(site));
    expect(url.startsWith('/api/v1/maps/wms?mapLayerId=7&')).toBe(true);
    expect(url).toContain('CRS=EPSG%3A3857');
    expect(url).toContain('LAYERS=OSM-WMS');
  });
});
