import { Injectable, signal } from '@angular/core';

/** WGS84 lon/lat. */
export type LonLat = [number, number];

export type MapTool =
  | { kind: 'point'; done: (point: LonLat) => void; hint?: string }
  | { kind: 'polygon'; done: (ring: LonLat[]) => void; hint?: string }
  | { kind: 'panorama'; done: (panoramaId: number) => void; hint?: string };

/**
 * A request from a form to the map: pick a point or draw a polygon (e.g. site reference point and extent).
 * The map converts from its EPSG:3857 view to WGS84 (EPSG:4326) before answering; nothing metric is computed here.
 */
@Injectable({ providedIn: 'root' })
export class MapToolsService {
  readonly tool = signal<MapTool | null>(null);

  pickPoint(done: (point: LonLat) => void, hint?: string): void {
    this.tool.set({ kind: 'point', done, hint });
  }

  /** A panorama marker of the map (e.g. to link or unlink panoramas). */
  pickPanorama(done: (panoramaId: number) => void, hint?: string): void {
    this.tool.set({ kind: 'panorama', done, hint });
  }

  drawPolygon(done: (ring: LonLat[]) => void): void {
    this.tool.set({ kind: 'polygon', done });
  }

  cancel(): void {
    this.tool.set(null);
  }
}
