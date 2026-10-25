import { Color, CustomDataSource, Entity, ImageMaterialProperty, Rectangle, Viewer } from 'cesium';
import { wmsProxyUrl } from '../core/api/api.service';
import { MapLayer, Site } from '../core/api/models';

/** Half the side of the ground map around a site without an extent (m). */
const DEFAULT_HALF_SIDE_M = 400;
/** The extent is widened by this share on every side, so the surroundings show too. */
const MARGIN = 0.3;
const IMAGE_PX = 2048;
const R = 6378137;

/**
 * Layers that can lie flat under the model: served in Web Mercator or WGS84. A layer forced into another system
 * (e.g. EOV) would come turned by the meridian convergence – it stays on the 2D map.
 */
export function groundable(layer: MapLayer): boolean {
  const crs = String(layer.wmsParams?.['CRS'] ?? layer.wmsParams?.['SRS'] ?? '').toUpperCase();
  return !crs || crs === 'EPSG:3857' || crs === 'EPSG:900913' || crs === 'EPSG:4326';
}

const toX = (lon: number) => (R * lon * Math.PI) / 180;
const toY = (lat: number) => R * Math.log(Math.tan(Math.PI / 4 + (lat * Math.PI) / 360));
const toLon = (x: number) => (x / R) * (180 / Math.PI);
const toLat = (y: number) => (2 * Math.atan(Math.exp(y / R)) - Math.PI / 2) * (180 / Math.PI);

/** The square around the site (its extent widened, or a default size) in Web Mercator: [minX, minY, maxX, maxY]. */
export function groundBox(site: Site): [number, number, number, number] {
  const [lon, lat] = site.refPoint.coordinates;
  // Web Mercator stretches by 1/cos(lat): metres on the ground are converted with it (never measured in 3857).
  const scale = 1 / Math.cos((lat * Math.PI) / 180);
  let [minX, minY, maxX, maxY] = [toX(lon), toY(lat), toX(lon), toY(lat)];
  const ring = site.extent?.coordinates[0];
  if (ring?.length) {
    for (const [x, y] of ring.map(([a, b]) => [toX(a), toY(b)])) {
      [minX, minY, maxX, maxY] = [Math.min(minX, x), Math.min(minY, y), Math.max(maxX, x), Math.max(maxY, y)];
    }
  }
  const half = Math.max((maxX - minX) / 2, (maxY - minY) / 2) * (1 + 2 * MARGIN);
  const side = Math.max(half, DEFAULT_HALF_SIDE_M * scale);
  const [cx, cy] = [(minX + maxX) / 2, (minY + maxY) / 2];
  return [cx - side, cy - side, cx + side, cy + side];
}

/** GetMap of the layer for that square through the backend proxy (the client never reaches the WMS itself). */
export function groundImageUrl(layer: MapLayer, box: [number, number, number, number]): string {
  const params = new URLSearchParams({
    SERVICE: 'WMS',
    VERSION: '1.3.0',
    REQUEST: 'GetMap',
    LAYERS: layer.layer,
    STYLES: '',
    CRS: 'EPSG:3857',
    BBOX: box.join(','),
    WIDTH: String(IMAGE_PX),
    HEIGHT: String(IMAGE_PX),
    FORMAT: 'image/png',
    TRANSPARENT: 'false',
  });
  return `${wmsProxyUrl(layer.id)}&${params}`;
}

/**
 * The background map laid flat under the model, at the site's base height (user request: the 3D view as a flat,
 * spread-out map, not the globe – without terrain the globe lies at sea level, far below the model).
 */
export class GroundMap {
  /** Its own data source: the viewer's entities are cleared whenever the boxes and markers are redrawn. */
  private readonly source = new CustomDataSource('ground-map');
  private entity: Entity | null = null;

  constructor(viewer: Viewer) {
    void viewer.dataSources.add(this.source);
  }

  get shown(): boolean {
    return this.entity != null;
  }

  show(layer: MapLayer | null, site: Site | null): void {
    this.clear();
    if (!layer || !site) return;
    const box = groundBox(site);
    this.entity = this.source.entities.add({
      id: 'ground-map',
      rectangle: {
        coordinates: Rectangle.fromDegrees(toLon(box[0]), toLat(box[1]), toLon(box[2]), toLat(box[3])),
        // A little below the floor, so floor-level geometry is not hidden by it.
        height: site.baseHeightM - 0.05,
        material: new ImageMaterialProperty({ image: groundImageUrl(layer, box), color: Color.WHITE }),
      },
    });
  }

  clear(): void {
    if (this.entity) this.source.entities.remove(this.entity);
    this.entity = null;
  }
}
