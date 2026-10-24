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
  Cesium3DTileFeature,
  Cesium3DTileStyle,
  Cesium3DTileset,
  ClassificationPrimitive,
  ClassificationType,
  ClippingPlane,
  ClippingPlaneCollection,
  Color,
  ColorGeometryInstanceAttribute,
  CustomDataSource,
  DirectionalLight,
  Entity,
  GeometryInstance,
  HeadingPitchRange,
  HeadingPitchRoll,
  ImageryLayer,
  Math as CesiumMath,
  Matrix4,
  PerInstanceColorAppearance,
  PolylineDashMaterialProperty,
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
import { Anchor, GeoJsonGeometry, LevelInfo, ModelAsset } from '../core/api/models';
import { LocalPlane, SnapIndex, SnapResult } from '../core/geo/snap';
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
const DEFAULT_SNAP_M = 0.25;

type Tool = 'box' | 'point' | 'element';

/**
 * 3D view of the site (spec 9, M6): the site's models as 3D Tiles (a GLB is served as a one-tile tileset), cut
 * under the ceiling of the selected level; boxes (region anchors) highlighted on the mesh with classification
 * volumes; a click selects the record of the smallest box containing the picked point (FR-A3b), editors place,
 * resize, turn and move boxes with handles (FR-A3). Segmented (BIM) models (M7): an element is picked by its STABLE
 * id property (never the per-tile feature index), linked to a record as a model_feature anchor and highlighted with a
 * tileset style in every tile and level of detail; a linked element wins over boxes. Placing snaps to the level's
 * vector geometry within a tolerance in real metres (FR-S2). The render loop runs outside Angular (zoneless); only
 * the selection and saved edits come back.
 */
@Component({
  selector: 'app-model-viewer',
  imports: [FormsModule, Button, InputNumber, Message, Select, Tooltip, ModelsDialog],
  template: `
    <div #container class="scene" [class.placing]="tool() != null"></div>
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
            (onClick)="startTool('box')"
          />
          <p-button
            icon="pi pi-map-marker"
            [text]="true"
            size="small"
            [pTooltip]="m.viewer3d.point"
            [disabled]="models().length === 0"
            (onClick)="startTool('point')"
          />
          @if (hasSegmented()) {
            <p-button
              icon="pi pi-link"
              [text]="true"
              size="small"
              [pTooltip]="m.viewer3d.element"
              (onClick)="startTool('element')"
            />
          }
          <span class="snap" [pTooltip]="m.viewer3d.snapHint" tooltipPosition="bottom">
            <label for="snap-m"><i class="pi pi-th-large"></i></label>
            <p-inputnumber
              inputId="snap-m"
              [ngModel]="snapTolerance()"
              (ngModelChange)="snapTolerance.set($event ?? 0)"
              [min]="0"
              [max]="5"
              [maxFractionDigits]="2"
              suffix=" m"
              size="small"
              [ariaLabel]="m.viewer3d.snap"
            />
          </span>
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
        <p-message severity="info" styleClass="hint">
          {{ text }}
          @if (floorPartial() && tool() !== 'element') {
            <br /><small>{{ m.viewer3d.snapPartial }}</small>
          }
        </p-message>
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
    .snap {
      display: flex;
      align-items: center;
      gap: 0.25rem;
      margin-left: 0.25rem;
      color: var(--p-text-muted-color);
      :host ::ng-deep input {
        width: 4.5rem;
      }
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
  protected readonly tool = signal<Tool | null>(null);
  /** Model elements linked to records (model_feature anchors). */
  protected readonly elements = signal<Anchor[]>([]);
  protected readonly snapTolerance = signal(DEFAULT_SNAP_M);
  protected readonly floorPartial = signal(false);
  protected readonly hasSegmented = computed(() => this.models().some((x) => x.segmented));
  /**
   * X-ray (M8): a whole subtree (e.g. a process line) is selected and the whole model is shown – the buildings turn
   * translucent and the highlighted elements show through floors and walls, across buildings and levels.
   */
  private readonly xray = computed(
    () => (this.selection.members()?.members.length ?? 0) > 0 && this.selection.levelId() == null,
  );
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
  /** Snap targets and the snap indicator (kept apart from the box entities, which are redrawn often). */
  private readonly snapLayer = new CustomDataSource('snap');
  private snapIndex: SnapIndex | null = null;
  private floorGeometries: GeoJsonGeometry[] = [];
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
      this.selection.highlightIds();
      this.boxes();
      this.xray();
      untracked(() => this.drawBoxes());
    });
    // Leaving the editing modes ends the editing.
    effect(() => {
      if (!this.can.anchors())
        untracked(() => {
          this.stopEditing();
          this.endTool();
        });
    });
    // Element highlight (spec 9.1): a style on the element ids, the same in every tile and level of detail.
    effect(() => {
      this.elements();
      this.selection.highlightIds();
      this.can.assign();
      this.xray();
      untracked(() => this.applyStyles());
    });
    // Snap targets: the vector geometry of the selected level.
    effect(() => {
      const siteId = this.selection.siteId();
      const levelId = this.selection.levelId();
      this.selection.version();
      untracked(() => this.loadFloor(siteId, levelId));
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
    void this.viewer.dataSources.add(this.snapLayer);
    // Without terrain the globe lies at the ellipsoid, far below the site's base height: hidden while models are shown.
    scene.backgroundColor = Color.fromCssColorString('#dde5ee');
    this.viewer.cesiumWidget.creditContainer.setAttribute('style', 'display:none');

    this.handler = new ScreenSpaceEventHandler(scene.canvas);
    this.handler.setInputAction(
      (e: { position: Cartesian2 }) => this.pointerDown(e.position),
      ScreenSpaceEventType.LEFT_DOWN,
    );
    this.handler.setInputAction((e: { endPosition: Cartesian2 }) => {
      this.pointerMove(e.endPosition);
      this.preview(e.endPosition);
    }, ScreenSpaceEventType.MOUSE_MOVE);
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
      this.elements.set([]);
      this.showModels([]);
      return;
    }
    this.loadModels();
    this.api.levels(siteId).subscribe((levels) => this.levels.set(levels));
    this.api
      .siteAnchors(siteId, 'model_feature')
      .subscribe((elements) => this.elements.set(elements));
    this.api.siteAnchors(siteId, 'region').subscribe((boxes) => {
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
      const key = (list: ModelAsset[]) =>
        list.map((x) => `${x.id}:${JSON.stringify(x.placement)}:${x.elementIdProperty}`).join();
      const changed = key(models) !== key(this.models());
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
    this.applyStyles();
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

  // ── Model elements (M7) ─────────────────────────────────────────────────

  private applyStyles(): void {
    const highlight = this.selection.highlightIds();
    const rest = this.xray() ? "color('white', 0.22)" : "color('white')";
    for (const [modelId, tileset] of this.tilesets) {
      const property = this.models().find((x) => x.id === modelId)?.elementIdProperty;
      if (!property) {
        tileset.style = this.xray()
          ? new Cesium3DTileStyle({ color: rest })
          : (undefined as unknown as Cesium3DTileStyle);
        continue;
      }
      const linked = this.elements().filter((e) => e.modelAssetId === modelId && e.featureRef);
      const value = `\${feature[${JSON.stringify(property)}]}`;
      const any = (refs: string[]) =>
        refs.map((r) => `${value} === ${JSON.stringify(r)}`).join(' || ');
      const conditions: [string, string][] = [];
      const mine = linked.filter((e) => highlight.has(e.nodeId)).map((e) => e.featureRef!);
      if (mine.length) conditions.push([any(mine), "color('#facc15')"]);
      // Assignment: the elements already linked are tinted, so the rest is easy to see.
      const others = linked.filter((e) => !highlight.has(e.nodeId)).map((e) => e.featureRef!);
      if (this.can.assign() && others.length) conditions.push([any(others), "color('#7dd3fc')"]);
      conditions.push(['true', rest]);
      tileset.style = new Cesium3DTileStyle({ color: { conditions } });
    }
  }

  /** The element under the pointer: its model and STABLE id (the property named by the model). */
  private pickElement(position: Cartesian2): { model: ModelAsset; ref: string } | null {
    // Box volumes and markers lie over the mesh: the first tile feature below them.
    const picked = (this.viewer?.scene.drillPick(position, 6) ?? []).find(
      (p) => p instanceof Cesium3DTileFeature,
    ) as Cesium3DTileFeature | undefined;
    if (!picked) return null;
    const entry = [...this.tilesets].find(([, t]) => t === picked.tileset);
    const model = entry && this.models().find((x) => x.id === entry[0]);
    if (!model?.elementIdProperty) return null;
    const ref = picked.getProperty(model.elementIdProperty);
    return ref == null || ref === '' ? null : { model, ref: String(ref) };
  }

  // ── Snapping (FR-S2) ────────────────────────────────────────────────────

  private loadFloor(siteId: number | null, levelId: number | null): void {
    this.snapIndex = null;
    this.floorGeometries = [];
    this.floorPartial.set(false);
    const site = this.selection.site();
    if (siteId == null || levelId == null || !site) return;
    this.api.floorGeometry(siteId, levelId).subscribe((geometry) => {
      if (this.selection.levelId() !== levelId) return;
      this.floorGeometries = geometry.features.map((f) => f.geometry);
      this.snapIndex = new SnapIndex(
        new LocalPlane(...site.refPoint.coordinates),
        this.floorGeometries,
      );
      this.floorPartial.set(geometry.partial);
      this.drawSnapTargets();
    });
  }

  /** A surface point pulled to the nearest vertex / edge of the level geometry; the height stays. */
  private snapped(surface: Cartesian3): { point: Cartesian3; snap: SnapResult | null } {
    const carto = Cartographic.fromCartesian(surface);
    const index = this.snapIndex;
    if (!index) return { point: surface, snap: null };
    const [east, north] = index.plane.toLocal(
      CesiumMath.toDegrees(carto.longitude),
      CesiumMath.toDegrees(carto.latitude),
    );
    const snap = index.snap(east, north, this.snapTolerance());
    if (!snap) return { point: surface, snap: null };
    const [lon, lat] = index.plane.toLonLat(snap.east, snap.north);
    return { point: Cartesian3.fromDegrees(lon, lat, carto.height), snap };
  }

  /** While placing: the level geometry as dashed lines on the floor. */
  private drawSnapTargets(): void {
    this.snapLayer.entities.removeAll();
    const site = this.selection.site();
    const placing = this.tool() === 'box' || this.tool() === 'point' || this.editing() != null;
    if (!site || !placing || this.snapTolerance() <= 0) return;
    const height = site.baseHeightM + this.levelElevation(this.selection.levelId()) + 0.03;
    const lines: [number, number][][] = [];
    const collect = (g: GeoJsonGeometry) => {
      const c = g.coordinates as never;
      if (g.type === 'LineString') lines.push(c);
      else if (g.type === 'MultiLineString' || g.type === 'Polygon')
        lines.push(...(c as [number, number][][]));
      else if (g.type === 'MultiPolygon')
        for (const p of c as [number, number][][][]) lines.push(...p);
    };
    this.floorGeometries.forEach(collect);
    for (const line of lines)
      this.snapLayer.entities.add({
        polyline: {
          positions: line.map(([lon, lat]) => Cartesian3.fromDegrees(lon, lat, height)),
          width: 1.5,
          material: new PolylineDashMaterialProperty({
            color: Color.fromCssColorString('#2563eb').withAlpha(0.8),
            dashLength: 8,
          }),
        },
      });
    this.snapLayer.entities.add({
      id: 'snap:indicator',
      show: false,
      position: Cartesian3.ZERO,
      point: { pixelSize: 11 },
    });
  }

  /** Where a click would land: the snap indicator under the pointer. */
  private preview(position: Cartesian2): void {
    const indicator = this.snapLayer.entities.getById('snap:indicator');
    if (!indicator || !this.viewer) return;
    const tool = this.tool();
    const surface =
      tool === 'box' || tool === 'point' ? this.viewer.scene.pickPosition(position) : undefined;
    if (!surface) {
      indicator.show = false;
      return;
    }
    const { point, snap } = this.snapped(surface);
    indicator.position = point as never;
    indicator.point!.color = (
      snap?.kind === 'vertex'
        ? Color.fromCssColorString('#16a34a')
        : snap
          ? Color.ORANGE
          : Color.WHITE
    ) as never;
    indicator.point!.outlineColor = Color.BLACK as never;
    indicator.point!.outlineWidth = 1 as never;
    indicator.point!.disableDepthTestDistance = Number.POSITIVE_INFINITY as never;
    indicator.show = true;
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
    // The selected node with its subtree, e.g. every element of a process line (FR-3D5, M8).
    const highlight = this.selection.highlightIds();
    const editing = this.editing();
    const visible = this.visibleBoxes();
    if (visible.length > 0) {
      // Classification (spec 9.1): the part of the mesh inside the box is coloured.
      // The edited box keeps its saved volume until the edit is saved (rebuilt then). One primitive per colour:
      // a ClassificationPrimitive takes instances of a single colour only.
      for (const highlighted of [false, true]) {
        // X-ray: the other boxes would tint the highlighted elements.
        if (!highlighted && this.xray()) continue;
        const group = visible.filter((b) => highlight.has(b.nodeId) === highlighted);
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
      const xray = this.xray();
      for (const b of visible) {
        if (b.id === editing?.anchor.id) continue;
        const lit = highlight.has(b.nodeId);
        // X-ray: only the highlighted boxes, filled, so that they show through the translucent model.
        if (xray && !lit) continue;
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
              fill: xray,
              material: Color.fromCssColorString('#facc15').withAlpha(0.75),
              outline: true,
              outlineColor: lit ? Color.fromCssColorString('#facc15') : Color.WHITE.withAlpha(0.45),
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
            selected ? 0.65 : 0.22,
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
    this.drawSnapTargets();
  }

  protected stopEditing(): void {
    if (!this.editing()) return;
    this.editing.set(null);
    this.drawBoxes();
    this.drawSnapTargets();
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
    // Moving: the centre snaps to the level geometry (e.g. onto an axis or a corner).
    const snappedCenter = this.drag.handle === 'move' ? this.snapped(center).point : center;
    this.editing.set({ ...this.drag.start, ...shape, center: snappedCenter });
  }

  private pointerUp(): void {
    if (!this.drag) return;
    this.drag = null;
    this.viewer!.scene.screenSpaceCameraController.enableInputs = true;
    if (this.dragged) this.saveEditing();
  }

  private click(position: Cartesian2): void {
    if (!this.viewer || this.dragged || this.pickedHandle(position)) return;
    const tool = this.tool();
    if (tool === 'element') {
      this.linkElement(position);
      return;
    }
    const point = this.viewer.scene.pickPosition(position);
    if (tool) {
      if (!point) {
        this.toasts.add({ severity: 'warn', summary: this.m.box.missedModel, life: 3000 });
        return;
      }
      const snappedPoint = this.snapped(point).point;
      this.endTool();
      if (tool === 'box') this.placeBox(snappedPoint);
      else this.placePoint(snappedPoint);
      return;
    }
    // A linked model element wins over boxes (spec 9.3, assumption).
    const element = this.pickElement(position);
    const linked =
      element &&
      this.elements().find(
        (e) => e.modelAssetId === element.model.id && e.featureRef === element.ref,
      );
    if (linked) {
      this.stopEditing();
      this.selection.selectNode(linked.nodeId);
      this.openInfoPointPanorama(linked.nodeId);
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
      if (element && this.can.assign())
        this.toasts.add({
          severity: 'info',
          summary: this.m.viewer3d.unassigned(element.ref),
          life: 3000,
        });
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

  protected startTool(tool: Tool): void {
    const node = this.selection.node();
    if (!node) {
      this.toasts.add({ severity: 'warn', summary: this.m.box.needNode, life: 4000 });
      return;
    }
    this.stopEditing();
    this.tool.set(tool);
    this.hint.set(
      tool === 'box'
        ? this.m.box.step(node.name)
        : tool === 'point'
          ? this.m.viewer3d.pointStep(node.name)
          : this.m.viewer3d.elementStep(node.name),
    );
    this.drawSnapTargets();
  }

  private endTool(): void {
    this.tool.set(null);
    this.hint.set(null);
    this.drawSnapTargets();
  }

  /** The level of a point: the selected one, or the storey whose floor is the highest below it. */
  private levelAt(heightAboveBase: number): number | null {
    const levels = [...this.levels()].sort((a, b) => b.elevationM - a.elevationM);
    return (
      this.selection.levelId() ??
      levels.find((l) => l.elevationM <= heightAboveBase + 0.01)?.id ??
      null
    );
  }

  /** Position, level and height above its floor of a point (spec 7.1). */
  private placement(point: Cartesian3): {
    position: object;
    levelId: number | null;
    zRelM: number;
  } {
    const carto = Cartographic.fromCartesian(point);
    const above = carto.height - (this.selection.site()?.baseHeightM ?? 0);
    const levelId = this.levelAt(above);
    return {
      position: {
        type: 'Point',
        coordinates: [CesiumMath.toDegrees(carto.longitude), CesiumMath.toDegrees(carto.latitude)],
      },
      levelId,
      zRelM: Math.round((above - this.levelElevation(levelId)) * 1000) / 1000,
    };
  }

  /** A spatial point of the selected record (FR-A2), snapped to the level geometry. */
  private placePoint(point: Cartesian3): void {
    const node = this.selection.node();
    if (!node) return;
    this.api.createAnchor(node.id, { kind: 'point', ...this.placement(point) }).subscribe(() => {
      this.toasts.add({ severity: 'success', summary: this.m.viewer3d.pointCreated, life: 2500 });
      this.selection.changed();
    });
  }

  /** Links the clicked element of a segmented model to the selected record by its stable id. */
  private linkElement(position: Cartesian2): void {
    const node = this.selection.node();
    // The surface point first: a drill pick re-renders the pick buffers and spoils the depth read after it.
    const surface = this.viewer?.scene.pickPosition(position);
    const element = this.pickElement(position);
    if (!element || !surface) {
      this.toasts.add({ severity: 'warn', summary: this.m.viewer3d.noElement, life: 4000 });
      return;
    }
    this.endTool();
    if (!node) return;
    this.api
      .createAnchor(node.id, {
        kind: 'model_feature',
        modelAssetId: element.model.id,
        featureRef: element.ref,
        ...this.placement(surface),
      })
      .subscribe((anchor) => {
        this.toasts.add({
          severity: 'success',
          summary: this.m.viewer3d.elementCreated(element.ref),
          life: 3000,
        });
        this.elements.update((list) => [...list, anchor]);
        this.selection.changed();
      });
  }

  /** A 1 m box standing on the clicked surface; level: the selected one, or the one whose storey contains the point. */
  private placeBox(surface: Cartesian3): void {
    const node = this.selection.node();
    const site = this.selection.site();
    if (!node || !site) return;
    const up = Matrix4.multiplyByPointAsVector(
      Transforms.eastNorthUpToFixedFrame(surface),
      new Cartesian3(0, 0, NEW_BOX / 2),
      new Cartesian3(),
    );
    const center = Cartesian3.add(surface, up, new Cartesian3());
    // The level of the surface the box stands on, the height of its centre above that floor.
    const base = this.placement(surface);
    this.api
      .createAnchor(node.id, {
        kind: 'region',
        regionShape: 'box',
        sizeX: NEW_BOX,
        sizeY: NEW_BOX,
        sizeZ: NEW_BOX,
        headingDeg: 0,
        levelId: base.levelId,
        zRelM: base.zRelM + NEW_BOX / 2,
        position: this.placement(center).position,
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
