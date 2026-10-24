import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  effect,
  inject,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import {
  Cartesian2,
  Cartesian3,
  Color,
  Entity,
  LabelStyle,
  VerticalOrigin,
  EquirectangularPanorama,
  HeadingPitchRoll,
  Math as CesiumMath,
  Matrix4,
  PerspectiveFrustum,
  ScreenSpaceEventHandler,
  ScreenSpaceEventType,
  Transforms,
  Viewer,
} from 'cesium';
import { Anchor, Panorama, PanoramaNeighbour } from '../../core/api/models';
import { normalizeHeading, normalizeYaw } from '../../core/geo/geodesy';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';

/**
 * Offset between the panorama's heading (direction of the image centre) and the Cesium heading of its transform:
 * the image is mapped onto Cesium's sphere geometry so that its centre faces this far from the transform's heading.
 * Measured with a labelled test panorama (frontend/src/app/features/panorama/README.md).
 */
export const IMAGE_HEADING_OFFSET = -90;

/** Radius of the panorama sphere (m): small, so that later the 3D model and markers can be placed around it. */
const RADIUS = 20;
const MIN_FOV = 30;
const MAX_FOV = 100;

/** Info points float inside the sphere; navigation markers lie towards the floor (spec 4.4: own markers). */
const INFO_DISTANCE = 15;
const NAV_DISTANCE = 6;
const NAV_PITCH = -25;

/** A click on the panorama in image angles (yaw 0 = image centre, + right; pitch + up). */
export interface PanoramaClick {
  yaw: number;
  pitch: number;
}

/**
 * Panorama viewer on CesiumJS's built-in EquirectangularPanorama (spec 4.4, decided). The transform comes from the
 * panorama's WGS84 position, absolute height (spec 7.1) and heading; the camera stands in the centre and only turns
 * (drag: direction, wheel: field of view). Where the camera looks goes to the central state (view cone on the
 * floorplan, FR-P7). The render loop runs outside Angular (zoneless); only clicks and the view leave Cesium.
 */
@Component({
  selector: 'app-panorama-viewer',
  template: `
    <div #container class="viewer"></div>
    @if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    }
  `,
  styles: `
    :host {
      position: absolute;
      inset: 0;
    }
    .viewer {
      position: absolute;
      inset: 0;
      cursor: grab;
    }
    .error {
      position: absolute;
      inset: auto 1rem 1rem;
      margin: 0;
      padding: 0.75rem 1rem;
      background: #fff;
      color: #b00020;
      border-radius: 4px;
    }
  `,
})
export class PanoramaViewer {
  private readonly container = viewChild.required<ElementRef<HTMLDivElement>>('container');
  private readonly messages = inject(MESSAGES);
  private readonly selection = inject(SelectionService);

  /** Image-angle click on the picture (calibration, placing info points). */
  readonly clicked = output<PanoramaClick>();
  /** A navigation marker: go to the neighbouring panorama (FR-P4). */
  readonly navigate = output<number>();
  /** An info point: select its node (FR-A6). */
  readonly infoPointClicked = output<number>();
  /** An info point dragged to a new place on the picture (editors): its new image angles. */
  readonly infoPointMoved = output<{ anchorId: number } & PanoramaClick>();

  protected readonly error = signal<string | null>(null);

  private viewer?: Viewer;
  private primitive?: EquirectangularPanorama;
  private handler?: ScreenSpaceEventHandler;
  private panorama: Panorama | null = null;
  private center = new Cartesian3();
  private heading = 0;
  private pitch = 0;

  /** The panorama to show (with imageUrl and absoluteHeightM). */
  readonly show = signal<Panorama | null>(null);
  readonly neighbours = signal<PanoramaNeighbour[]>([]);
  readonly infoPoints = signal<Anchor[]>([]);
  /** The selected node: its info points are highlighted. */
  readonly highlightNodeId = signal<number | null>(null);
  /** Boxes of the selected node (region anchors): drawn where they stand, if on the panorama's level (M6). */
  readonly boxes = signal<Anchor[]>([]);
  /** Info points may be dragged (editing modes). */
  readonly editable = signal(false);

  constructor() {
    afterNextRender(() => this.create());
    inject(DestroyRef).onDestroy(() => {
      this.handler?.destroy();
      this.viewer?.destroy();
      this.selection.panoramaView.set(null);
    });
    effect(() => {
      const panorama = this.show();
      untracked(() => this.load(panorama));
    });
    effect(() => {
      const neighbours = this.neighbours();
      const infoPoints = this.infoPoints();
      const highlight = this.highlightNodeId();
      this.boxes();
      untracked(() => this.drawMarkers(neighbours, infoPoints, highlight));
    });
  }

  /** A point inside the sphere in an absolute direction (degrees from north, elevation) at a distance. */
  private direction(azimuthDeg: number, elevationDeg: number, distance: number): Cartesian3 {
    const az = CesiumMath.toRadians(azimuthDeg);
    const el = CesiumMath.toRadians(elevationDeg);
    const local = new Cartesian3(
      Math.sin(az) * Math.cos(el) * distance,
      Math.cos(az) * Math.cos(el) * distance,
      Math.sin(el) * distance,
    );
    return Matrix4.multiplyByPoint(
      Transforms.eastNorthUpToFixedFrame(this.center),
      local,
      new Cartesian3(),
    );
  }

  private drawMarkers(
    neighbours: PanoramaNeighbour[],
    infoPoints: Anchor[],
    highlight: number | null,
  ): void {
    if (!this.viewer) return;
    this.viewer.entities.removeAll();
    if (!this.panorama?.imageUrl) return;
    const heading = this.panorama.headingDeg ?? 0;
    for (const n of neighbours) {
      this.viewer.entities.add(
        new Entity({
          id: `nav:${n.id}`,
          position: this.direction(n.bearingDeg, NAV_PITCH, NAV_DISTANCE),
          point: {
            pixelSize: 22,
            color: Color.WHITE.withAlpha(0.85),
            outlineColor: Color.fromCssColorString('#2563eb'),
            outlineWidth: 4,
            disableDepthTestDistance: Number.POSITIVE_INFINITY,
          },
          label: {
            text: `↑ ${n.distanceM.toFixed(1)} m`,
            font: '600 14px system-ui, sans-serif',
            fillColor: Color.WHITE,
            outlineColor: Color.BLACK,
            outlineWidth: 3,
            style: LabelStyle.FILL_AND_OUTLINE,
            verticalOrigin: VerticalOrigin.TOP,
            pixelOffset: new Cartesian2(0, 16),
            disableDepthTestDistance: Number.POSITIVE_INFINITY,
          },
        }),
      );
    }
    for (const a of infoPoints) {
      if (a.yawDeg == null || a.pitchDeg == null) continue;
      const selected = a.nodeId === highlight;
      this.viewer.entities.add(
        new Entity({
          id: `info:${a.nodeId}:${a.id}`,
          position: this.direction(heading + a.yawDeg, a.pitchDeg, INFO_DISTANCE),
          point: {
            pixelSize: selected ? 20 : 14,
            color: Color.fromCssColorString(selected ? '#facc15' : '#f97316'),
            outlineColor: Color.WHITE,
            outlineWidth: selected ? 4 : 2,
            disableDepthTestDistance: Number.POSITIVE_INFINITY,
          },
          label: {
            text: a.nodeName,
            font: `${selected ? 700 : 600} 14px system-ui, sans-serif`,
            fillColor: Color.WHITE,
            outlineColor: Color.BLACK,
            outlineWidth: 3,
            style: LabelStyle.FILL_AND_OUTLINE,
            verticalOrigin: VerticalOrigin.BOTTOM,
            pixelOffset: new Cartesian2(0, -14),
            showBackground: selected,
            backgroundColor: Color.fromCssColorString('#1e293b').withAlpha(0.7),
            disableDepthTestDistance: Number.POSITIVE_INFINITY,
          },
        }),
      );
    }
    this.drawBoxes();
  }

  /**
   * The selected record's boxes in the panorama's space: same level, so the height is the panorama's floor
   * (absolute height − its height above the floor) plus the box centre's height above the floor. Only boxes inside the
   * sphere show in front of the picture.
   */
  private drawBoxes(): void {
    const p = this.panorama;
    if (!this.viewer || !p || p.absoluteHeightM == null) return;
    const floor = p.absoluteHeightM - p.zRelM;
    for (const b of this.boxes()) {
      if (
        b.regionShape !== 'box' ||
        b.levelId !== p.levelId ||
        b.sizeX == null ||
        b.sizeY == null ||
        b.sizeZ == null
      )
        continue;
      const center = Cartesian3.fromDegrees(
        b.position.coordinates[0],
        b.position.coordinates[1],
        floor + b.zRelM,
      );
      if (Cartesian3.distance(center, this.center) > RADIUS - 1) continue;
      this.viewer.entities.add(
        new Entity({
          id: `box:${b.nodeId}:${b.id}`,
          position: center,
          orientation: Transforms.headingPitchRollQuaternion(
            center,
            new HeadingPitchRoll(CesiumMath.toRadians(b.headingDeg ?? 0), 0, 0),
          ),
          box: {
            dimensions: new Cartesian3(b.sizeX, b.sizeY, b.sizeZ),
            material: Color.fromCssColorString('#facc15').withAlpha(0.3),
            outline: true,
            outlineColor: Color.fromCssColorString('#facc15'),
          },
        }),
      );
    }
  }

  /** Turns towards an info point (e.g. its node was selected in another view). */
  lookAtInfoPoint(anchor: Anchor): void {
    if (anchor.yawDeg == null || !this.panorama) return;
    this.lookAt((this.panorama.headingDeg ?? 0) + anchor.yawDeg, anchor.pitchDeg ?? 0);
  }

  private create(): void {
    try {
      this.viewer = new Viewer(this.container().nativeElement, {
        baseLayer: false,
        baseLayerPicker: false,
        geocoder: false,
        animation: false,
        timeline: false,
        sceneModePicker: false,
        navigationHelpButton: false,
        fullscreenButton: false,
        homeButton: false,
        infoBox: false,
        selectionIndicator: false,
      });
    } catch (e) {
      console.error(e);
      this.error.set(this.messages.viewer3d.webglUnavailable);
      return;
    }
    // For end-to-end tests: the viewer on its element (screen position of markers).
    (this.container().nativeElement as unknown as { cesiumViewer: Viewer }).cesiumViewer =
      this.viewer;
    const scene = this.viewer.scene;
    scene.globe.show = false;
    if (scene.skyBox) scene.skyBox.show = false;
    if (scene.skyAtmosphere) scene.skyAtmosphere.show = false;
    if (scene.sun) scene.sun.show = false;
    if (scene.moon) scene.moon.show = false;
    // The camera only turns in place: Cesium's own navigation is off.
    const controller = scene.screenSpaceCameraController;
    controller.enableRotate =
      controller.enableTranslate =
      controller.enableZoom =
      controller.enableTilt =
      controller.enableLook =
        false;
    (this.viewer.camera.frustum as PerspectiveFrustum).fov = CesiumMath.toRadians(75);

    this.handler = new ScreenSpaceEventHandler(scene.canvas);
    let last: Cartesian2 | null = null;
    let moved = false;
    // Dragging an info point (editors) instead of turning the view.
    let dragged: { entity: Entity; anchorId: number; angles: PanoramaClick | null } | null = null;
    this.handler.setInputAction((e: { position: Cartesian2 }) => {
      last = Cartesian2.clone(e.position);
      moved = false;
      const entity = this.editable() ? this.pickedEntity(e.position) : null;
      dragged = entity?.id.startsWith('info:')
        ? { entity, anchorId: Number(entity.id.split(':')[2]), angles: null }
        : null;
    }, ScreenSpaceEventType.LEFT_DOWN);
    this.handler.setInputAction((e: { endPosition: Cartesian2 }) => {
      if (!last) return;
      if (dragged) {
        if (Math.abs(e.endPosition.x - last.x) + Math.abs(e.endPosition.y - last.y) > 2)
          moved = true;
        if (!moved) return;
        const angles = this.anglesAt(e.endPosition);
        if (!angles || !this.panorama) return;
        dragged.angles = angles;
        dragged.entity.position = this.direction(
          (this.panorama.headingDeg ?? 0) + angles.yaw,
          angles.pitch,
          INFO_DISTANCE,
        ) as never;
        return;
      }
      const fov = CesiumMath.toDegrees((this.viewer!.camera.frustum as PerspectiveFrustum).fov);
      const width = scene.canvas.clientWidth || 1;
      const dx = e.endPosition.x - last.x;
      const dy = e.endPosition.y - last.y;
      if (Math.abs(dx) + Math.abs(dy) > 2) moved = true;
      // Dragging moves the picture with the pointer.
      this.heading = normalizeHeading(this.heading - (dx * fov) / width);
      this.pitch = Math.max(-85, Math.min(85, this.pitch + (dy * fov) / width));
      last = Cartesian2.clone(e.endPosition);
      this.applyCamera();
    }, ScreenSpaceEventType.MOUSE_MOVE);
    this.handler.setInputAction((e: { position: Cartesian2 }) => {
      last = null;
      const drop = dragged;
      dragged = null;
      if (drop?.angles && moved)
        this.infoPointMoved.emit({ anchorId: drop.anchorId, ...drop.angles });
      else if (!moved) this.click(e.position);
    }, ScreenSpaceEventType.LEFT_UP);
    this.handler.setInputAction((delta: number) => {
      const frustum = this.viewer!.camera.frustum as PerspectiveFrustum;
      const fov = CesiumMath.toDegrees(frustum.fov) * (delta > 0 ? 0.9 : 1.1);
      frustum.fov = CesiumMath.toRadians(Math.max(MIN_FOV, Math.min(MAX_FOV, fov)));
      this.applyCamera();
    }, ScreenSpaceEventType.WHEEL);

    this.load(this.show());
  }

  private load(panorama: Panorama | null): void {
    if (!this.viewer) return;
    if (this.primitive) {
      this.viewer.scene.primitives.remove(this.primitive);
      this.primitive = undefined;
    }
    this.panorama = panorama;
    if (!panorama?.imageUrl) {
      this.selection.panoramaView.set(null);
      return;
    }
    const [lon, lat] = panorama.position.coordinates;
    this.center = Cartesian3.fromDegrees(lon, lat, panorama.absoluteHeightM ?? 0);
    const heading = panorama.headingDeg ?? 0;
    const transform = Transforms.headingPitchRollToFixedFrame(
      this.center,
      new HeadingPitchRoll(CesiumMath.toRadians(heading - IMAGE_HEADING_OFFSET), 0, 0),
    );
    this.primitive = new EquirectangularPanorama({
      transform,
      image: panorama.imageUrl,
      radius: RADIUS,
    });
    this.viewer.scene.primitives.add(this.primitive);
    this.drawMarkers(this.neighbours(), this.infoPoints(), this.highlightNodeId());
    // Start looking at the image centre.
    this.heading = heading;
    this.pitch = 0;
    this.applyCamera();
  }

  private applyCamera(): void {
    if (!this.viewer || !this.panorama) return;
    this.viewer.camera.setView({
      destination: this.center,
      orientation: {
        heading: CesiumMath.toRadians(this.heading),
        pitch: CesiumMath.toRadians(this.pitch),
        roll: 0,
      },
    });
    const fov = CesiumMath.toDegrees((this.viewer.camera.frustum as PerspectiveFrustum).fov);
    // PerspectiveFrustum.fov is the larger of the two angles: horizontal on a landscape canvas.
    const canvas = this.viewer.scene.canvas;
    const horizontal =
      canvas.clientWidth >= canvas.clientHeight
        ? fov
        : CesiumMath.toDegrees(
            2 *
              Math.atan(
                Math.tan(CesiumMath.toRadians(fov) / 2) *
                  (canvas.clientWidth / canvas.clientHeight),
              ),
          );
    this.selection.panoramaView.set({
      position: this.panorama.position.coordinates,
      headingDeg: this.heading,
      fovDeg: horizontal,
    });
  }

  private pickedEntity(position: Cartesian2): Entity | null {
    const picked = this.viewer?.scene.pick(position) as { id?: Entity } | undefined;
    return picked?.id instanceof Entity ? picked.id : null;
  }

  private click(position: Cartesian2): void {
    if (!this.viewer || !this.panorama) return;
    const id = this.pickedEntity(position)?.id ?? null;
    if (id?.startsWith('nav:')) {
      this.navigate.emit(Number(id.slice(4)));
      return;
    }
    if (id?.startsWith('info:')) {
      this.infoPointClicked.emit(Number(id.split(':')[1]));
      return;
    }
    const angles = this.anglesAt(position);
    if (angles) this.clicked.emit(angles);
  }

  /** Screen point → direction in the local east-north-up frame → image angles. */
  private anglesAt(position: Cartesian2): PanoramaClick | null {
    if (!this.viewer || !this.panorama) return null;
    const ray = this.viewer.camera.getPickRay(position);
    if (!ray) return null;
    const toLocal = Matrix4.inverseTransformation(
      Transforms.eastNorthUpToFixedFrame(this.center),
      new Matrix4(),
    );
    const local = Matrix4.multiplyByPointAsVector(toLocal, ray.direction, new Cartesian3());
    Cartesian3.normalize(local, local);
    const azimuth = normalizeHeading(CesiumMath.toDegrees(Math.atan2(local.x, local.y)));
    const elevation = CesiumMath.toDegrees(Math.asin(Math.max(-1, Math.min(1, local.z))));
    return { yaw: normalizeYaw(azimuth - (this.panorama.headingDeg ?? 0)), pitch: elevation };
  }

  /** Turns the camera to an absolute heading (e.g. after calibration). */
  lookAt(headingDeg: number, pitchDeg = 0): void {
    this.heading = normalizeHeading(headingDeg);
    this.pitch = pitchDeg;
    this.applyCamera();
  }
}
