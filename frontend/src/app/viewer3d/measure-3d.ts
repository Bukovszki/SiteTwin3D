import {
  Cartesian2,
  Cartesian3,
  Cartographic,
  Color,
  ColorMaterialProperty,
  CustomDataSource,
  LabelStyle,
  Viewer,
} from 'cesium';
import { Messages } from '../core/i18n/messages.hu';

const COLOR = Color.fromCssColorString('#ea580c');
/** Two clicks closer than this are one point (a double click ends a line and clicks twice). */
const SAME_POINT_M = 0.01;

/** The last segment of a measurement: its length on the ground and the height difference (m). */
export interface MeasureSummary {
  total: number;
  horizontal: number;
  dz: number;
}

/**
 * Distances on the 3D model (user request: relative distances between points, not coordinates). A line of clicked
 * points, every segment labelled with its real 3D length (ECEF, metres); a double click ends a line, the next click
 * starts another. Its own data source: the viewer's entities are cleared when the boxes and markers are redrawn.
 */
export class Measure3d {
  private readonly source = new CustomDataSource('measure-3d');
  private lines: Cartesian3[][] = [];
  private current: Cartesian3[] = [];

  constructor(
    viewer: Viewer,
    private readonly m: Messages,
  ) {
    void viewer.dataSources.add(this.source);
  }

  /** Finished lines and the one being drawn (if it has a segment). */
  get count(): number {
    return this.lines.length + (this.current.length > 1 ? 1 : 0);
  }

  /** The last point of the line being drawn. */
  get last(): Cartesian3 | undefined {
    return this.current.at(-1);
  }

  add(point: Cartesian3): void {
    const last = this.current.at(-1);
    if (last && Cartesian3.distance(last, point) < SAME_POINT_M) return;
    this.current.push(point);
    this.redraw();
  }

  /** Ends the line being drawn (a single point is dropped). */
  finish(): void {
    if (this.current.length > 1) this.lines.push(this.current);
    this.current = [];
    this.redraw();
  }

  clear(): void {
    this.lines = [];
    this.current = [];
    this.redraw();
  }

  /** The line being drawn, else the last finished one. */
  summary(): MeasureSummary | null {
    const line = this.current.length > 1 ? this.current : this.lines.at(-1);
    if (!line || line.length < 2) return null;
    let total = 0;
    for (let i = 1; i < line.length; i++) total += Cartesian3.distance(line[i - 1], line[i]);
    const [a, b] = [Cartographic.fromCartesian(line.at(-2)!), Cartographic.fromCartesian(line.at(-1)!)];
    // On the ground: both ends at the same height (over a site the chord equals the arc to well below a millimetre).
    const horizontal = Cartesian3.distance(
      Cartesian3.fromRadians(a.longitude, a.latitude, a.height),
      Cartesian3.fromRadians(b.longitude, b.latitude, a.height),
    );
    return { total, horizontal, dz: b.height - a.height };
  }

  private redraw(): void {
    this.source.entities.removeAll();
    for (const line of [...this.lines, this.current]) {
      if (line.length > 1)
        this.source.entities.add({
          polyline: {
            positions: line,
            width: 3,
            material: new ColorMaterialProperty(COLOR),
            // Seen through walls too, fainter.
            depthFailMaterial: new ColorMaterialProperty(COLOR.withAlpha(0.45)),
          },
        });
      for (const point of line)
        this.source.entities.add({
          position: point,
          point: {
            pixelSize: 7,
            color: COLOR,
            outlineColor: Color.WHITE,
            outlineWidth: 1.5,
            disableDepthTestDistance: Number.POSITIVE_INFINITY,
          },
        });
      for (let i = 1; i < line.length; i++)
        this.source.entities.add({
          position: Cartesian3.midpoint(line[i - 1], line[i], new Cartesian3()),
          label: {
            text: this.m.measure.length(Cartesian3.distance(line[i - 1], line[i])),
            font: '600 13px system-ui, sans-serif',
            fillColor: Color.fromCssColorString('#1f2937'),
            showBackground: true,
            backgroundColor: Color.WHITE.withAlpha(0.9),
            backgroundPadding: new Cartesian2(6, 3),
            style: LabelStyle.FILL,
            pixelOffset: new Cartesian2(0, -12),
            disableDepthTestDistance: Number.POSITIVE_INFINITY,
          },
        });
    }
  }
}
