import Feature from 'ol/Feature';
import OlMap from 'ol/Map';
import { Geometry, LineString, Point, Polygon } from 'ol/geom';
import { Draw } from 'ol/interaction';
import VectorLayer from 'ol/layer/Vector';
import { toLonLat } from 'ol/proj';
import VectorSource from 'ol/source/Vector';
import { Circle, Fill, Stroke, Style, Text } from 'ol/style';
import { areaM2, lengthM } from '../../core/geo/measure';
import { Messages } from '../../core/i18n/messages.hu';

export type MeasureKind = 'distance' | 'area';

type LonLat = [number, number];

const COLOR = '#ea580c';

/**
 * Distances and areas on the 2D map (user request). Drawn in the map's EPSG:3857 view, measured in real metres
 * from the WGS84 coordinates (core/geo/measure). The finished measurements stay on the map until cleared.
 */
export class MapMeasure {
  private readonly source = new VectorSource();
  private draw?: Draw;

  constructor(
    private readonly map: OlMap,
    private readonly m: Messages,
    /** A measurement finished or the list cleared: the number of measurements on the map. */
    private readonly changed: (count: number) => void,
  ) {
    this.map.addLayer(
      new VectorLayer({ source: this.source, zIndex: 1000, style: (f) => this.style(f as Feature<Geometry>) }),
    );
  }

  get count(): number {
    return this.source.getFeatures().length;
  }

  start(kind: MeasureKind): void {
    this.stop();
    this.draw = new Draw({
      source: this.source,
      type: kind === 'distance' ? 'LineString' : 'Polygon',
      style: (f) => this.style(f as Feature<Geometry>, true),
      // The clicks of measuring do not select markers underneath.
      stopClick: true,
    });
    this.draw.on('drawstart', (event) => {
      const feature = event.feature;
      feature.getGeometry()!.on('change', () => feature.set('label', this.label(feature.getGeometry()!)));
    });
    this.draw.on('drawend', (event) => {
      event.feature.set('label', this.label(event.feature.getGeometry()!));
      // Added to the source right after this event.
      setTimeout(() => this.changed(this.count));
    });
    this.map.addInteraction(this.draw);
  }

  /** Measuring ends; an unfinished measurement is dropped, the finished ones stay. */
  stop(): void {
    if (this.draw) {
      this.draw.abortDrawing();
      this.map.removeInteraction(this.draw);
    }
    this.draw = undefined;
  }

  clear(): void {
    this.source.clear();
    this.changed(0);
  }

  private label(geometry: Geometry): string {
    if (geometry instanceof LineString) {
      return this.m.measure.length(lengthM(geometry.getCoordinates().map((c) => toLonLat(c) as LonLat)));
    }
    if (geometry instanceof Polygon) {
      const ring = geometry.getCoordinates()[0].map((c) => toLonLat(c) as LonLat);
      return this.m.measure.areaValue(areaM2(ring));
    }
    return '';
  }

  private style(feature: Feature<Geometry>, sketch = false): Style[] {
    const geometry = feature.getGeometry();
    const styles = [
      new Style({
        stroke: new Stroke({ color: COLOR, width: 2.5, lineDash: sketch ? [6, 4] : undefined }),
        fill: new Fill({ color: 'rgba(234, 88, 12, 0.12)' }),
        image: new Circle({ radius: 4, fill: new Fill({ color: COLOR }), stroke: new Stroke({ color: '#fff', width: 1.5 }) }),
      }),
    ];
    const label = feature.get('label') as string | undefined;
    if (!label || !geometry) return styles;
    // The value at the end of a line, in the middle of an area.
    const at =
      geometry instanceof LineString
        ? new Point(geometry.getLastCoordinate())
        : geometry instanceof Polygon
          ? geometry.getInteriorPoint()
          : undefined;
    if (at)
      styles.push(
        new Style({
          geometry: at,
          text: new Text({
            text: label,
            font: '600 13px system-ui, sans-serif',
            offsetY: geometry instanceof LineString ? -14 : 0,
            fill: new Fill({ color: '#1f2937' }),
            backgroundFill: new Fill({ color: 'rgba(255, 255, 255, 0.9)' }),
            backgroundStroke: new Stroke({ color: COLOR, width: 1 }),
            padding: [2, 5, 2, 5],
          }),
        }),
      );
    return styles;
  }
}
