import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  BoundingSphere,
  BoxGeometry,
  CallbackProperty,
  Cartesian2,
  Cartesian3,
  Cartographic,
  Cesium3DTileset,
  ClassificationPrimitive,
  ClassificationType,
  ClippingPlane,
  ClippingPlaneCollection,
  Color,
  ColorGeometryInstanceAttribute,
  DirectionalLight,
  Entity,
  GeometryInstance,
  HeadingPitchRange,
  HeadingPitchRoll,
  ImageryLayer,
  Math as CesiumMath,
  Matrix4,
  PerInstanceColorAppearance,
  PrimitiveCollection,
  ScreenSpaceEventHandler,
  ScreenSpaceEventType,
  TileMapServiceImageryProvider,
  Transforms,
  Viewer,
  buildModuleUrl,
} from 'cesium';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { InputNumber } from 'primeng/inputnumber';
import { Message } from 'primeng/message';
import { Select } from 'primeng/select';
import { Tooltip } from 'primeng/tooltip';
import { ApiService } from '../core/api/api.service';
import { Anchor, LevelInfo, ModelAsset } from '../core/api/models';
import { Permissions } from '../core/auth/permissions';
import { MESSAGES } from '../core/i18n/messages';
import { SelectionService } from '../core/selection/selection.service';
import {
  MARGIN,
  BoxShape,
  Handle,
  dragHandle,
  dragPlanePoint,
  handlePosition,
  smallestContaining,
} from './box-math';
import { ModelsDialog } from './models-dialog';

/** A box being shown or edited: the anchor plus its centre in ECEF. */
interface BoxDraft extends BoxShape {
  anchor: Anchor;
  center: Cartesian3;
}

const HANDLES: Handle[] = ['+x', '-x', '+y', '-y', '+z', 'rotate', 'move'];
/** Default box when placing: 1 m cube standing on the clicked surface. */
const NEW_BOX = 1;
/** The cut under the ceiling of a level: the slab above is removed, the walls stay. */
const CUT_BELOW_CEILING = 0.5;
const DEFAULT_LEVEL_HEIGHT = 3;

/**
 * 3D view of the site (spec 9, M6): the site's models as 3D Tiles (a GLB is served as a one-tile tileset), cut
 * under the ceiling of the selected level; boxes (region anchors) highlighted on the mesh with classification
 * volumes; a click selects the record of the smallest box containing the picked point (FR-A3b), editors place,
 * resize, turn and move boxes with handles (FR-A3). The render loop runs outside Angular (zoneless); only the
 * selection and saved edits come back.
 */
@Component({
  selector: 'app-model-viewer',
  imports: [FormsModule, Button, InputNumber, Message, Select, Tooltip, ModelsDialog],
  template: `
    <div #container class="scene" [class.placing]="placing()"></div>
    @if (error()) {
      <p class="error" role="alert">{{ error() }}</p>
    } @else {
      <div class="toolbar">
        <p-select
          [options]="levelOptions()"
          optionLabel="label"
          optionValue="value"
          [ngModel]="selection.levelId()"
          (ngModelChange)="selection.levelId.set($event)"
          size="small"
          styleClass="level-select"
          appendTo="body"
          [pTooltip]="m.viewer3d.levelHint"
        />
        <p-button
          icon="pi pi-home"
          [text]="true"
          size="small"
          [pTooltip]="m.viewer3d.flyTo"
          (onClick)="flyHome()"
        />
        @if (can.anchors()) {
          <p-button
            icon="pi pi-box"
            [text]="true"
            size="small"
            [pTooltip]="m.box.place"
            [disabled]="models().length === 0"
            (onClick)="startPlacing()"
          />
        }
        @if (can.prepare()) {
          <p-button
            icon="pi pi-upload"
            [text]="true"
            size="small"
            [pTooltip]="m.models.manage"
            (onClick)="modelsOpen.set(true)"
          />
        }
      </div>
      @if (hint(); as text) {
        <p-message severity="info" styleClass="hint">{{ text }}</p-message>
      } @else if (loaded() && models().length === 0) {
        <p-message severity="secondary" styleClass="hint">{{ m.viewer3d.noModel }}</p-message>
      }
      @if (editing(); as box) {
        <div class="box-panel">
          <div class="head">
            <b>{{ m.box.title }}: {{ box.anchor.nodeName }}</b>
            <p-button
              icon="pi pi-times"
              [text]="true"
              size="small"
              severity="secondary"
              (onClick)="stopEditing()"
            />
          </div>
          <div class="grid">
            <label for="b-x">{{ m.box.sizeX }}</label
            ><p-inputnumber
              inputId="b-x"
              [ngModel]="box.sizeX"
              (ngModelChange)="setField('sizeX', $event)"
              [min]="0.05"
              [max]="500"
              [maxFractionDigits]="2"
              size="small"
            />
            <label for="b-y">{{ m.box.sizeY }}</label
            ><p-inputnumber
              inputId="b-y"
              [ngModel]="box.sizeY"
              (ngModelChange)="setField('sizeY', $event)"
              [min]="0.05"
              [max]="500"
              [maxFractionDigits]="2"
              size="small"
            />
            <label for="b-z">{{ m.box.sizeZ }}</label
            ><p-inputnumber
              inputId="b-z"
              [ngModel]="box.sizeZ"
              (ngModelChange)="setField('sizeZ', $event)"
              [min]="0.05"
              [max]="500"
              [maxFractionDigits]="2"
              size="small"
            />
            <label for="b-h">{{ m.box.heading }}</label
            ><p-inputnumber
              inputId="b-h"
              [ngModel]="box.headingDeg"
              (ngModelChange)="setField('headingDeg', $event)"
              [min]="0"
              [max]="360"
              [maxFractionDigits]="1"
              size="small"
            />
          </div>
          <small class="muted">{{ m.box.handles }}</small>
          <div class="actions">
            <p-button
              icon="pi pi-trash"
              [label]="m.box.delete"
              severity="danger"
              [text]="true"
              size="small"
              (onClick)="removeBox(box)"
            />
          </div>
        </div>
      }
    }
    @if (can.prepare()) {
      @defer (when modelsOpen()) {
        <app-models-dialog [(visible)]="modelsOpen" (changed)="loadModels()" />
      }
    }
  `,
  styles: `
    :host {
      position: absolute;
      inset: 0;
    }
    .scene {
      position: absolute;
      inset: 0;
      &.placing {
        cursor: crosshair;
      }
    }
    .toolbar {
      position: absolute;
      top: 0.5rem;
      left: 0.5rem;
      display: flex;
      align-items: center;
      gap: 0.25rem;
      padding: 0.2rem 0.35rem;
      background: var(--p-content-background);
      border: 1px solid var(--p-content-border-color);
      border-radius: 8px;
      box-shadow: 0 2px 8px rgb(0 0 0 / 0.1);
    }
    :host ::ng-deep .level-select {
      min-width: 11rem;
    }
    :host ::ng-deep .hint {
      position: absolute;
      left: 50%;
      bottom: 1rem;
      transform: translateX(-50%);
    }
    .box-panel {
      position: absolute;
      top: 0.5rem;
      right: 0.5rem;
      width: 17rem;
      display: flex;
      flex-direction: column;
      gap: 0.4rem;
      padding: 0.5rem 0.65rem;
      background: var(--p-content-background);
      border: 1px solid var(--p-content-border-color);
      border-radius: 8px;
      box-shadow: 0 2px 8px rgb(0 0 0 / 0.15);
      .head,
      .actions {
        display: flex;
        align-items: center;
        justify-content: space-between;
      }
      .grid {
        display: grid;
        grid-template-columns: 1fr 6.5rem;
        gap: 0.3rem 0.5rem;
        align-items: center;
        font-size: 0.85rem;
      }
      :host ::ng-deep .p-inputnumber input {
        width: 6.5rem;
      }
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
export class ModelViewer {
  protected readonly m = inject(MESSAGES);
  protected readonly selection = inject(SelectionService);
  protected readonly can = inject(Permissions);
  private readonly api = inject(ApiService);
  private readonly toasts = inject(MessageService);
  private readonly confirm = inject(ConfirmationService);
  private readonly container = viewChild.required<ElementRef<HTMLDivElement>>('container');

  protected readonly error = signal<string | null>(null);
  protected readonly hint = signal<string | null>(null);
  protected readonly loaded = signal(false);
  protected readonly models = signal<ModelAsset[]>([]);
  protected readonly levels = signal<LevelInfo[]>([]);
  protected readonly boxes = signal<Anchor[]>([]);
  protected readonly placing = signal(false);
  protected readonly modelsOpen = signal(false);
  /** The box whose handles are shown (editors). */
  protected readonly editing = signal<BoxDraft | null>(null);

  protected readonly levelOptions = computed(() => [
    { label: this.m.viewer3d.wholeModel, value: null },
    ...this.levels().map((l) => ({ label: l.name, value: l.id })),
  ]);

  private viewer?: Viewer;
  private handler?: ScreenSpaceEventHandler;
  private readonly tilesets = new Map<number, Cesium3DTileset>();
  private readonly volumes = new PrimitiveCollection();
  private drag: {
    handle: Handle;
    start: BoxDraft;
    frame: Matrix4;
    from: [number, number, number];
  } | null = null;
  private dragged = false;

  constructor() {
    afterNextRender(() => this.create());
    inject(DestroyRef).onDestroy(() => {
      this.handler?.destroy();
      this.viewer?.destroy();
    });
    effect(() => {
      const siteId = this.selection.siteId();
      this.selection.version();
      untracked(() => this.loadSite(siteId));
    });
    // Level: cut and the boxes of that level only.
    effect(() => {
      this.selection.levelId();
      this.levels();
      untracked(() => {
        this.applyClipping();
        this.drawBoxes();
      });
    });
    effect(() => {
      this.selection.nodeId();
      this.boxes();
      untracked(() => this.drawBoxes());
    });
    // Leaving the editing modes ends the editing.
    effect(() => {
      if (!this.can.anchors()) untracked(() => this.stopEditing());
    });
  }

  private create(): void {
    try {
      this.viewer = new Viewer(this.container().nativeElement, {
        baseLayer: ImageryLayer.fromProviderAsync(
          TileMapServiceImageryProvider.fromUrl(buildModuleUrl('Assets/Textures/NaturalEarthII')),
        ),
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
      this.error.set(this.m.viewer3d.webglUnavailable);
      return;
    }
    // For end-to-end tests: the viewer on its element.
    (this.container().nativeElement as unknown as { cesiumViewer: Viewer }).cesiumViewer =
      this.viewer;
    const scene = this.viewer.scene;
    scene.primitives.add(this.volumes);
    // Without terrain the globe lies at the ellipsoid, far below the site's base height: hidden while models are shown.
    scene.backgroundColor = Color.fromCssColorString('#dde5ee');
    this.viewer.cesiumWidget.creditContainer.setAttribute('style', 'display:none');

    this.handler = new ScreenSpaceEventHandler(scene.canvas);
    this.handler.setInputAction(
      (e: { position: Cartesian2 }) => this.pointerDown(e.position),
      ScreenSpaceEventType.LEFT_DOWN,
    );
    this.handler.setInputAction(
      (e: { endPosition: Cartesian2 }) => this.pointerMove(e.endPosition),
      ScreenSpaceEventType.MOUSE_MOVE,
    );
    this.handler.setInputAction(() => this.pointerUp(), ScreenSpaceEventType.LEFT_UP);
    this.handler.setInputAction(
      (e: { position: Cartesian2 }) => this.click(e.position),
      ScreenSpaceEventType.LEFT_CLICK,
    );

    this.showModels(this.models());
  }

  // ── Data ────────────────────────────────────────────────────────────────

  private loadSite(siteId: number | null): void {
    if (siteId == null) {
      this.models.set([]);
      this.levels.set([]);
      this.boxes.set([]);
      this.showModels([]);
      return;
    }
    this.loadModels();
    this.api.levels(siteId).subscribe((levels) => this.levels.set(levels));
    this.api.regionAnchors(siteId).subscribe((boxes) => {
      this.boxes.set(boxes);
      const editing = this.editing();
      if (editing) {
        const fresh = boxes.find((b) => b.id === editing.anchor.id);
        if (fresh) this.edit(fresh);
        else this.stopEditing();
      }
    });
  }

  loadModels(): void {
    const siteId = this.selection.siteId();
    if (siteId == null) return;
    this.api.models(siteId).subscribe((models) => {
      const changed =
        models.map((x) => `${x.id}:${JSON.stringify(x.placement)}`).join() !==
        this.models()
          .map((x) => `${x.id}:${JSON.stringify(x.placement)}`)
          .join();
      this.models.set(models);
      this.loaded.set(true);
      if (changed) this.showModels(models);
    });
  }

  private async showModels(models: ModelAsset[]): Promise<void> {
    if (!this.viewer) return;
    const scene = this.viewer.scene;
    for (const tileset of this.tilesets.values()) scene.primitives.remove(tileset);
    this.tilesets.clear();
    scene.globe.show = models.length === 0;
    if (scene.skyBox) scene.skyBox.show = models.length === 0;
    if (scene.skyAtmosphere) scene.skyAtmosphere.show = models.length === 0;
    if (models.length === 0) return;
    // A fixed light from the south-west above: the model looks the same at any time of day.
    const site = this.selection.site();
    if (site) {
      const enu = Transforms.eastNorthUpToFixedFrame(
        Cartesian3.fromDegrees(
          site.refPoint.coordinates[0],
          site.refPoint.coordinates[1],
          site.baseHeightM,
        ),
      );
      const direction = Matrix4.multiplyByPointAsVector(
        enu,
        new Cartesian3(0.45, 0.6, -0.65),
        new Cartesian3(),
      );
      scene.light = new DirectionalLight({
        direction: Cartesian3.normalize(direction, direction),
        intensity: 2.2,
      });
    }
    for (const model of models) {
      try {
        const tileset = await Cesium3DTileset.fromUrl(model.tilesetUrl, {
          maximumScreenSpaceError: 8,
        });
        if (this.selection.siteId() !== model.siteId || !this.viewer) return;
        this.tilesets.set(model.id, scene.primitives.add(tileset));
      } catch (e) {
        console.error(e);
        this.toasts.add({
          severity: 'error',
          summary: this.m.viewer3d.loadFailed(model.name),
          life: 6000,
        });
      }
    }
    // Classification volumes are drawn after the tilesets they colour.
    scene.primitives.raiseToTop(this.volumes);
    this.applyClipping();
    this.flyHome(0);
  }

  protected flyHome(duration = 1.2): void {
    const site = this.selection.site();
    if (!this.viewer || !site) return;
    const [lon, lat] = site.refPoint.coordinates;
    const level = this.levels().find((l) => l.id === this.selection.levelId());
    const center = Cartesian3.fromDegrees(lon, lat, site.baseHeightM + (level?.elevationM ?? 0));
    this.viewer.camera.flyToBoundingSphere(new BoundingSphere(center, 30), {
      offset: new HeadingPitchRange(CesiumMath.toRadians(20), CesiumMath.toRadians(-50), 70),
      duration,
    });
  }

  // ── Level cut (spec M6: per-level hiding / cutting) ─────────────────────

  private cutHeight(): number | null {
    const site = this.selection.site();
    const levels = [...this.levels()].sort((a, b) => a.elevationM - b.elevationM);
    const index = levels.findIndex((l) => l.id === this.selection.levelId());
    if (!site || index < 0) return null;
    const level = levels[index];
    const height =
      level.heightM ??
      (levels[index + 1] ? levels[index + 1].elevationM - level.elevationM : DEFAULT_LEVEL_HEIGHT);
    return site.baseHeightM + level.elevationM + height - CUT_BELOW_CEILING;
  }

  private applyClipping(): void {
    const site = this.selection.site();
    const cut = this.cutHeight();
    for (const tileset of this.tilesets.values()) {
      if (cut == null || !site) {
        tileset.clippingPlanes = undefined as unknown as ClippingPlaneCollection;
        continue;
      }
      // The plane's frame is relative to the tileset's clipping origin (root transform, or ENU at the bounding sphere
      // centre); Cesium keeps that getter out of its typings. Wanted: ENU at the site point, at the cut height.
      const enu = Transforms.eastNorthUpToFixedFrame(
        Cartesian3.fromDegrees(site.refPoint.coordinates[0], site.refPoint.coordinates[1], cut),
      );
      const modelMatrix = Matrix4.multiply(
        Matrix4.inverse(
          (tileset as unknown as { clippingPlanesOriginMatrix: Matrix4 })
            .clippingPlanesOriginMatrix,
          new Matrix4(),
        ),
        enu,
        new Matrix4(),
      );
      tileset.clippingPlanes = new ClippingPlaneCollection({
        planes: [new ClippingPlane(new Cartesian3(0, 0, -1), 0)],
        modelMatrix,
        edgeWidth: 1.5,
        edgeColor: Color.fromCssColorString('#475569'),
      });
    }
  }

  // ── Boxes ───────────────────────────────────────────────────────────────

  private levelElevation(levelId: number | null): number {
    return this.levels().find((l) => l.id === levelId)?.elevationM ?? 0;
  }

  private draftOf(anchor: Anchor): BoxDraft {
    const site = this.selection.site();
    const [lon, lat] = anchor.position.coordinates;
    const height = (site?.baseHeightM ?? 0) + this.levelElevation(anchor.levelId) + anchor.zRelM;
    return {
      anchor,
      center: Cartesian3.fromDegrees(lon, lat, height),
      sizeX: anchor.sizeX ?? 1,
      sizeY: anchor.sizeY ?? 1,
      sizeZ: anchor.sizeZ ?? 1,
      headingDeg: anchor.headingDeg ?? 0,
    };
  }

  private frameOf(box: { center: Cartesian3; headingDeg: number }): Matrix4 {
    return Transforms.headingPitchRollToFixedFrame(
      box.center,
      new HeadingPitchRoll(CesiumMath.toRadians(box.headingDeg), 0, 0),
    );
  }

  /** Boxes of the selected level (all of them for the whole model). */
  private visibleBoxes(): Anchor[] {
    const level = this.selection.levelId();
    return this.boxes().filter(
      (b) => b.regionShape === 'box' && (level == null || b.levelId === level),
    );
  }

  private drawBoxes(): void {
    if (!this.viewer) return;
    this.volumes.removeAll();
    this.viewer.entities.removeAll();
    const selected = this.selection.nodeId();
    const editing = this.editing();
    const visible = this.visibleBoxes();
    if (visible.length > 0) {
      // Classification (spec 9.1): the part of the mesh inside the box is coloured.
      // The edited box keeps its saved volume until the edit is saved (rebuilt then). One primitive per colour:
      // a ClassificationPrimitive takes instances of a single colour only.
      for (const highlighted of [false, true]) {
        const group = visible.filter((b) => (b.nodeId === selected) === highlighted);
        if (group.length === 0) continue;
        this.volumes.add(
          new ClassificationPrimitive({
            geometryInstances: group.map((b) => this.volumeInstance(this.draftOf(b), highlighted)),
            appearance: new PerInstanceColorAppearance({ flat: true, translucent: true }),
            classificationType: ClassificationType.CESIUM_3D_TILE,
            asynchronous: false,
          }),
        );
      }
      for (const b of visible) {
        if (b.id === editing?.anchor.id) continue;
        const draft = this.draftOf(b);
        this.viewer.entities.add(
          new Entity({
            id: `box:${b.id}`,
            position: draft.center,
            orientation: Transforms.headingPitchRollQuaternion(
              draft.center,
              new HeadingPitchRoll(CesiumMath.toRadians(draft.headingDeg), 0, 0),
            ),
            box: {
              dimensions: new Cartesian3(draft.sizeX, draft.sizeY, draft.sizeZ),
              fill: false,
              outline: true,
              outlineColor:
                b.nodeId === selected
                  ? Color.fromCssColorString('#facc15')
                  : Color.WHITE.withAlpha(0.45),
            },
          }),
        );
      }
    }
    if (editing) this.drawEditor();
  }

  private volumeInstance(box: BoxDraft, selected: boolean): GeometryInstance {
    return new GeometryInstance({
      id: `vol:${box.anchor.id}`,
      geometry: BoxGeometry.fromDimensions({
        vertexFormat: PerInstanceColorAppearance.VERTEX_FORMAT,
        dimensions: new Cartesian3(
          box.sizeX + 2 * MARGIN,
          box.sizeY + 2 * MARGIN,
          box.sizeZ + 2 * MARGIN,
        ),
      }),
      modelMatrix: this.frameOf(box),
      attributes: {
        color: ColorGeometryInstanceAttribute.fromColor(
          Color.fromCssColorString(selected ? '#facc15' : '#22d3ee').withAlpha(
            selected ? 0.65 : 0.45,
          ),
        ),
      },
    });
  }

  /** The edited box: outline and handles follow the draft while dragging (callback properties). */
  private drawEditor(): void {
    if (!this.viewer) return;
    const draft = () => this.editing();
    const orientation = new CallbackProperty(() => {
      const d = draft();
      return d
        ? Transforms.headingPitchRollQuaternion(
            d.center,
            new HeadingPitchRoll(CesiumMath.toRadians(d.headingDeg), 0, 0),
          )
        : undefined;
    }, false);
    this.viewer.entities.add(
      new Entity({
        id: 'edit:box',
        position: new CallbackProperty(() => draft()?.center, false) as never,
        orientation: orientation as never,
        box: {
          dimensions: new CallbackProperty(() => {
            const d = draft();
            return d ? new Cartesian3(d.sizeX, d.sizeY, d.sizeZ) : undefined;
          }, false),
          material: Color.fromCssColorString('#facc15').withAlpha(0.25),
          outline: true,
          outlineColor: Color.fromCssColorString('#facc15'),
        },
      }),
    );
    for (const handle of HANDLES) {
      this.viewer.entities.add(
        new Entity({
          id: `handle:${handle}`,
          position: new CallbackProperty(() => {
            const d = draft();
            if (!d) return undefined;
            const [x, y, z] = handlePosition(d, handle);
            return Matrix4.multiplyByPoint(
              this.frameOf(d),
              new Cartesian3(x, y, z),
              new Cartesian3(),
            );
          }, false) as never,
          point: {
            pixelSize: handle === 'move' ? 16 : 12,
            color:
              handle === 'rotate'
                ? Color.fromCssColorString('#a855f7')
                : handle === 'move'
                  ? Color.WHITE
                  : Color.fromCssColorString('#2563eb'),
            outlineColor: handle === 'move' ? Color.fromCssColorString('#2563eb') : Color.WHITE,
            outlineWidth: 2,
            disableDepthTestDistance: Number.POSITIVE_INFINITY,
          },
        }),
      );
    }
  }

  private edit(anchor: Anchor): void {
    this.editing.set(this.draftOf(anchor));
    this.drawBoxes();
  }

  protected stopEditing(): void {
    if (!this.editing()) return;
    this.editing.set(null);
    this.drawBoxes();
  }

  // ── Pointer ─────────────────────────────────────────────────────────────

  private pickedHandle(position: Cartesian2): Handle | null {
    const picked = this.viewer?.scene.pick(position) as { id?: Entity } | undefined;
    const id = picked?.id instanceof Entity ? picked.id.id : '';
    return id.startsWith('handle:') ? (id.slice(7) as Handle) : null;
  }

  /** The pointer ray in the box frame of the drag start. */
  private localRay(
    position: Cartesian2,
    frame: Matrix4,
  ): { o: [number, number, number]; d: [number, number, number] } | null {
    const ray = this.viewer?.camera.getPickRay(position);
    if (!ray) return null;
    const inverse = Matrix4.inverseTransformation(frame, new Matrix4());
    const o = Matrix4.multiplyByPoint(inverse, ray.origin, new Cartesian3());
    const d = Matrix4.multiplyByPointAsVector(inverse, ray.direction, new Cartesian3());
    return { o: [o.x, o.y, o.z], d: [d.x, d.y, d.z] };
  }

  private pointerDown(position: Cartesian2): void {
    this.dragged = false;
    const editing = this.editing();
    if (!editing || !this.can.anchors()) return;
    const handle = this.pickedHandle(position);
    if (!handle) return;
    const frame = this.frameOf(editing);
    const ray = this.localRay(position, frame);
    const from = ray ? dragPlanePoint(handle, ray.o, ray.d) : null;
    this.drag = { handle, start: { ...editing }, frame, from: from ?? [0, 0, 0] };
    this.viewer!.scene.screenSpaceCameraController.enableInputs = false;
  }

  private pointerMove(position: Cartesian2): void {
    if (!this.drag) return;
    const ray = this.localRay(position, this.drag.frame);
    const q = ray ? dragPlanePoint(this.drag.handle, ray.o, ray.d) : null;
    if (!q) return;
    this.dragged = true;
    const { shape, shift } = dragHandle(this.drag.start, this.drag.handle, q, this.drag.from);
    const center = Matrix4.multiplyByPoint(
      this.drag.frame,
      new Cartesian3(...shift),
      new Cartesian3(),
    );
    this.editing.set({ ...this.drag.start, ...shape, center });
  }

  private pointerUp(): void {
    if (!this.drag) return;
    this.drag = null;
    this.viewer!.scene.screenSpaceCameraController.enableInputs = true;
    if (this.dragged) this.saveEditing();
  }

  private click(position: Cartesian2): void {
    if (!this.viewer || this.dragged || this.pickedHandle(position)) return;
    const point = this.viewer.scene.pickPosition(position);
    if (this.placing()) {
      if (point) this.placeBox(point);
      else this.toasts.add({ severity: 'warn', summary: this.m.box.missedModel, life: 3000 });
      return;
    }
    if (!point) return;
    // FR-A3b: the smallest box containing the picked point, on the current level.
    const drafts = this.visibleBoxes().map((b) => this.draftOf(b));
    const hit = smallestContaining(drafts, (d) => {
      const local = Matrix4.multiplyByPoint(
        Matrix4.inverseTransformation(Transforms.eastNorthUpToFixedFrame(d.center), new Matrix4()),
        point,
        new Cartesian3(),
      );
      return [local.x, local.y, local.z];
    });
    if (!hit) {
      this.stopEditing();
      return;
    }
    this.selection.selectNode(hit.anchor.nodeId);
    this.openInfoPointPanorama(hit.anchor.nodeId);
    if (this.can.anchors()) this.edit(hit.anchor);
  }

  /** The selection also shows on the panorama: if the record has an info point, its panorama opens. */
  private openInfoPointPanorama(nodeId: number): void {
    this.api.nodeAnchors(nodeId).subscribe((anchors) => {
      const panoramas = anchors
        .filter((a) => a.kind === 'panorama_point' && a.panoramaId != null)
        .map((a) => a.panoramaId!);
      if (panoramas.length > 0 && !panoramas.includes(this.selection.panoramaId() ?? -1))
        this.selection.openPanorama(panoramas[0]);
    });
  }

  // ── Editing ─────────────────────────────────────────────────────────────

  protected startPlacing(): void {
    const node = this.selection.node();
    if (!node) {
      this.toasts.add({ severity: 'warn', summary: this.m.box.needNode, life: 4000 });
      return;
    }
    this.stopEditing();
    this.placing.set(true);
    this.hint.set(this.m.box.step(node.name));
  }

  /** A 1 m box standing on the clicked surface; level: the selected one, or the one whose storey contains the point. */
  private placeBox(surface: Cartesian3): void {
    const node = this.selection.node();
    const site = this.selection.site();
    this.placing.set(false);
    this.hint.set(null);
    if (!node || !site) return;
    const up = Matrix4.multiplyByPointAsVector(
      Transforms.eastNorthUpToFixedFrame(surface),
      new Cartesian3(0, 0, NEW_BOX / 2),
      new Cartesian3(),
    );
    const center = Cartesian3.add(surface, up, new Cartesian3());
    const carto = Cartographic.fromCartesian(center);
    const above = carto.height - site.baseHeightM;
    const levels = [...this.levels()].sort((a, b) => a.elevationM - b.elevationM);
    const levelId =
      this.selection.levelId() ??
      [...levels].reverse().find((l) => l.elevationM <= above + 0.01 - NEW_BOX / 2)?.id ??
      null;
    this.api
      .createAnchor(node.id, {
        kind: 'region',
        regionShape: 'box',
        sizeX: NEW_BOX,
        sizeY: NEW_BOX,
        sizeZ: NEW_BOX,
        headingDeg: 0,
        levelId,
        zRelM: Math.round((above - this.levelElevation(levelId)) * 1000) / 1000,
        position: {
          type: 'Point',
          coordinates: [
            CesiumMath.toDegrees(carto.longitude),
            CesiumMath.toDegrees(carto.latitude),
          ],
        },
      })
      .subscribe((anchor) => {
        this.toasts.add({ severity: 'success', summary: this.m.box.created, life: 2500 });
        this.boxes.update((b) => [...b, anchor]);
        this.edit(anchor);
        this.selection.changed();
      });
  }

  protected setField(
    field: 'sizeX' | 'sizeY' | 'sizeZ' | 'headingDeg',
    value: number | null,
  ): void {
    const editing = this.editing();
    if (!editing || value == null || editing[field] === value) return;
    let center = editing.center;
    if (field === 'sizeZ') {
      // A new height keeps the box standing on its base.
      const lift = new Cartesian3(0, 0, (value - editing.sizeZ) / 2);
      center = Matrix4.multiplyByPoint(this.frameOf(editing), lift, new Cartesian3());
    }
    this.editing.set({ ...editing, [field]: value, center });
    this.saveEditing();
  }

  private saveEditing(): void {
    const box = this.editing();
    const site = this.selection.site();
    if (!box || !site) return;
    const carto = Cartographic.fromCartesian(box.center);
    const round = (v: number, d = 3) => Math.round(v * 10 ** d) / 10 ** d;
    this.api
      .updateAnchor(box.anchor.id, {
        position: {
          type: 'Point',
          coordinates: [
            CesiumMath.toDegrees(carto.longitude),
            CesiumMath.toDegrees(carto.latitude),
          ],
        },
        zRelM: round(carto.height - site.baseHeightM - this.levelElevation(box.anchor.levelId)),
        sizeX: round(box.sizeX),
        sizeY: round(box.sizeY),
        sizeZ: round(box.sizeZ),
        headingDeg: round(box.headingDeg, 1),
      })
      .subscribe((anchor) => {
        this.boxes.update((list) => list.map((b) => (b.id === anchor.id ? anchor : b)));
        if (this.editing()?.anchor.id === anchor.id)
          this.editing.set({ ...this.editing()!, anchor });
        this.toasts.add({ severity: 'success', summary: this.m.box.saved, life: 1500 });
      });
  }

  protected removeBox(box: BoxDraft): void {
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.box.deleteConfirm,
      acceptButtonProps: { label: this.m.common.delete, severity: 'danger' },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () =>
        this.api.deleteAnchor(box.anchor.id).subscribe(() => {
          this.editing.set(null);
          this.boxes.update((list) => list.filter((b) => b.id !== box.anchor.id));
          this.selection.changed();
        }),
    });
  }
}
