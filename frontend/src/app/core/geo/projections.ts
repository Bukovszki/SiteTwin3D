import { register } from 'ol/proj/proj4';
import { get as getProjection } from 'ol/proj';
import proj4 from 'proj4';

/**
 * Coordinate systems a WMS layer may be requested in (spec 4.3, FR-AR1): Web Mercator (the map's own), WGS84 and EOV.
 * A layer served only in EOV is requested in EOV and reprojected by OpenLayers (raster reprojection). EOV with the
 * EPSG:23700 parameters (HD72 → WGS84 shift as in the EPSG database).
 */
export const WMS_PROJECTIONS = ['EPSG:3857', 'EPSG:23700', 'EPSG:4326'] as const;
export type WmsProjection = (typeof WMS_PROJECTIONS)[number];

let registered = false;

/** Registers EOV with OpenLayers (once). */
export function registerProjections(): void {
  if (registered) return;
  proj4.defs(
    'EPSG:23700',
    '+proj=somerc +lat_0=47.14439372222222 +lon_0=19.04857177777778 +k_0=0.99993 +x_0=650000 +y_0=200000 ' +
      '+ellps=GRS67 +towgs84=52.17,-71.82,-14.9,0,0,0,0 +units=m +no_defs +type=crs',
  );
  register(proj4);
  registered = true;
}

/** The coordinate system a layer is requested in (its wmsParams.CRS), if it is a known one other than the map's. */
export function layerProjection(
  wmsParams: Record<string, unknown> | null | undefined,
): string | undefined {
  const crs = String(wmsParams?.['CRS'] ?? wmsParams?.['SRS'] ?? '').toUpperCase();
  if (!crs || crs === 'EPSG:3857') return undefined;
  registerProjections();
  return getProjection(crs) ? crs : undefined;
}
