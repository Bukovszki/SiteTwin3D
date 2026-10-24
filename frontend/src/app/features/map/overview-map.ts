import { Component, DestroyRef, computed, ElementRef, afterNextRender, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import OlMap from 'ol/Map';
import View from 'ol/View';
import Feature from 'ol/Feature';
import GeoJSON from 'ol/format/GeoJSON';
import { Geometry, LineString, Point, Polygon } from 'ol/geom';
import { Draw, Translate } from 'ol/interaction';
import ImageLayer from 'ol/layer/Image';
import VectorLayer from 'ol/layer/Vector';
import { fromLonLat, toLonLat } from 'ol/proj';
import ImageWMS from 'ol/source/ImageWMS';
import VectorSource from 'ol/source/Vector';
import { Circle, Fill, RegularShape, Stroke, Style, Text } from 'ol/style';
import { MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Checkbox } from 'primeng/checkbox';
import { Message } from 'primeng/message';
import { Select } from 'primeng/select';
import { Tooltip } from 'primeng/tooltip';
import { AnchorEditService } from '../../core/anchors/anchor-edit.service';
import { ApiService, floorplanWmsUrl, wmsProxyUrl } from '../../core/api/api.service';
import { Anchor, LevelInfo, MapLayer, Panorama, Place, Site } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { destination } from '../../core/geo/geodesy';
import { LocalPlane } from '../../core/geo/snap';
import { fromBoxFrame } from '../../viewer3d/box-math';
import { DrawPanel } from '../drawing/draw-panel';
import { PanoramaUploadDialog } from '../panorama/panorama-upload-dialog';
import { FloorplansDialog } from './floorplans-dialog';
import { Permissions } from '../../core/auth/permissions';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { LonLat, MapToolsService } from './map-tools.service';

const geoJson = new GeoJSON({ dataProjection: 'EPSG:4326', featureProjection: 'EPSG:3857' });

/** Length of the view cone on the floorplan (m, real distance – computed on the sphere, not in EPSG:3857). */
const CONE_LENGTH_M = 8;

/**
 * Map from the region down to the level floorplan (FR-AR5) in OpenLayers, Web Mercator (EPSG:3857); data is WGS84
 * and projected by OpenLayers.
 * <ul>
 * <li>Sites (reference point, extent) over the background WMS layers; the floorplans of the chosen level (FR-AR1);
 * every WMS request goes through the backend proxy.</li>
 * <li>Panoramas of the level as markers (FR-AR3): a click opens one; editors can drag them; the open panorama's view
 * cone follows where its camera looks (FR-P7).</li>
 * <li>The selected node's own geometry is highlighted; answers point / polygon requests of forms (MapToolsService).</li>
 * </ul>
 */
@Component({
  selector: 'app-overview-map',
  imports: [FormsModule, Button, Checkbox, Message, Select, Tooltip, PanoramaUploadDialog, FloorplansDialog, DrawPanel],
  template: `
    <div #map class="map"></div>
    @if (selection.siteId() != null) {
      <div class="level-bar">
        <p-select [options]="levelOptions()" optionLabel="label" optionValue="value" [ngModel]="selection.levelId()"
          (ngModelChange)="selection.levelId.set($event)" size="small" styleClass="level-select" appendTo="body" />
        @if (can.prepare()) {
          @if (selection.levelId() != null) {
            <p-button icon="pi pi-images" [text]="true" size="small" [pTooltip]="m.levels.manage" (onClick)="floorplansOpen.set(true)" />
          }
          <p-button icon="pi pi-camera" [text]="true" size="small" [pTooltip]="m.panorama.upload" (onClick)="uploadOpen.set(true)" />
        }
        @if (can.anchors()) {
          <p-button icon="pi pi-arrows-alt" [text]="!edits.active()" [outlined]="edits.active()" size="small"
            [pTooltip]="edits.active() ? m.anchorEdit.stop : m.anchorEdit.start" (onClick)="edits.toggle()" />
        }
        <p-button icon="pi pi-pencil" [text]="true" size="small" [pTooltip]="m.drawing.layers" (onClick)="drawingOpen.set(!drawingOpen())" />
      </div>
      @if (drawingOpen()) {
        <div class="draw-dock"><app-draw-panel [map]="olMap()" /></div>
      }
    }
    @if (layers().length > 0) {
      <div class="layer-switcher">
        @for (layer of layers(); track layer.id) {
          <label>
            <p-checkbox [binary]="true" [ngModel]="visible()[layer.id]" (ngModelChange)="toggle(layer.id, $event)" />
            {{ layer.name }}
          </label>
        }
      </div>
    }
    @if (tools.tool(); as tool) {
      <p-message severity="info" styleClass="tool-hint">{{ tool.hint ?? (tool.kind === 'polygon' ? m.sites.drawHint : m.sites.pickHint) }}</p-message>
    }
    @if (can.prepare()) {
      @defer {
        <app-panorama-upload-dialog [(visible)]="uploadOpen" [levels]="levels()" />
        <app-floorplans-dialog [(visible)]="floorplansOpen" [levelName]="levelName()" />
      }
    }
  `,
  styles: `
    :host {
      position: absolute;
      inset: 0;
    }
    .map {
      position: absolute;
      inset: 0;
      background: var(--p-surface-100);
    }
    .layer-switcher {
      position: absolute;
      top: 0.5rem;
      right: 0.5rem;
      display: flex;
      flex-direction: column;
      gap: 0.35rem;
      padding: 0.5rem 0.75rem;
      background: var(--p-content-background);
      border: 1px solid var(--p-content-border-color);
      border-radius: 8px;
      box-shadow: 0 2px 8px rgb(0 0 0 / 0.1);
      label {
        display: flex;
        align-items: center;
        gap: 0.4rem;
        font-size: 0.85rem;
      }
    }
    .level-bar {
      position: absolute;
      top: 0.5rem;
      left: 2.75rem;
      display: flex;
      align-items: center;
      gap: 0.25rem;
      padding: 0.2rem 0.35rem;
      background: var(--p-content-background);
      border: 1px solid var(--p-content-border-color);
      border-radius: 8px;
      box-shadow: 0 2px 8px rgb(0 0 0 / 0.1);
    }
    .draw-dock {
      position: absolute;
      left: 0.5rem;
      bottom: 0.5rem;
    }
    :host ::ng-deep .level-select {
      min-width: 11rem;
    }
    :host ::ng-deep .tool-hint {
      position: absolute;
      left: 50%;
      bottom: 1rem;
      transform: translateX(-50%);
    }
  `,
})
export class OverviewMap {
  protected readonly m = inject(MESSAGES);
  protected readonly tools = inject(MapToolsService);
  protected readonly auth = inject(AuthService);
  protected readonly can = inject(Permissions);
  protected readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  protected readonly edits = inject(AnchorEditService);
  private readonly toasts = inject(MessageService);
  private readonly element = viewChild.required<ElementRef<HTMLDivElement>>('map');

  protected readonly layers = signal<MapLayer[]>([]);
  protected readonly visible = signal<Record<number, boolean>>({});
  protected readonly levels = signal<LevelInfo[]>([]);
  protected readonly uploadOpen = signal(false);
  protected readonly floorplansOpen = signal(false);
  protected readonly drawingOpen = signal(false);
  protected readonly olMap = signal<OlMap | null>(null);
  protected readonly levelOptions = computed(() => [
    { label: this.m.levels.outdoor, value: null },
    ...this.levels().map((l) => ({ label: l.name, value: l.id })),
  ]);
  protected readonly levelName = computed(() => this.levels().find((l) => l.id === this.selection.levelId())?.name ?? '');

  private map?: OlMap;
  private readonly sitesSource = new VectorSource<Feature<Geometry>>();
  private readonly highlightSource = new VectorSource<Feature<Geometry>>();
  private readonly backgroundLayers = new Map<number, ImageLayer<ImageWMS>>();
  private readonly floorplanLayers: ImageLayer<ImageWMS>[] = [];
  private readonly panoramaSource = new VectorSource<Feature<Geometry>>();
  private readonly coneSource = new VectorSource<Feature<Geometry>>();
  /** One marker per node: its place (user decision: one element, one place). */
  private readonly anchorSource = new VectorSource<Feature<Geometry>>();
  /** The boxes of the level as areas (rooms, equipment outlines). */
  private readonly footprintSource = new VectorSource<Feature<Geometry>>();
  private focusPending = false;
  private readonly linkSource = new VectorSource<Feature<Geometry>>();
  private translate?: Translate;
  private shownLevel: number | null | undefined;
  private draw?: Draw;
  private resizeObserver?: ResizeObserver;

  constructor() {
    afterNextRender(() => this.create());
    inject(DestroyRef).onDestroy(() => {
      this.resizeObserver?.disconnect();
      this.map?.setTarget(undefined);
    });

    effect(() => {
      const sites = this.selection.sites();
      const selected = this.selection.siteId();
      untracked(() => this.showSites(sites, selected));
    });
    effect(() => {
      const site = this.selection.site();
      untracked(() => site && this.fitSite(site));
    });
    effect(() => {
      const siteId = this.selection.siteId();
      this.selection.version();
      untracked(() => this.loadLayers(siteId));
    });
    effect(() => {
      const node = this.selection.node();
      untracked(() => this.highlight(node?.geom ?? null));
    });
    effect(() => {
      const tool = this.tools.tool();
      untracked(() => this.useTool(tool));
    });
    effect(() => {
      const siteId = this.selection.siteId();
      this.selection.version();
      untracked(() => siteId != null ? this.api.levels(siteId).subscribe((l) => this.levels.set(l)) : this.levels.set([]));
    });
    effect(() => {
      const siteId = this.selection.siteId();
      const levelId = this.selection.levelId();
      this.selection.version();
      untracked(() => this.loadLevel(siteId, levelId));
    });
    effect(() => {
      const open = this.selection.panoramaId();
      this.selection.version();
      untracked(() => {
        this.panoramaSource.getFeatures().forEach((f) => f.set('open', f.get('panoramaId') === open));
        this.loadLinks(open);
      });
    });
    // The selected node with its subtree (e.g. a process line) is highlighted: markers and box areas.
    effect(() => {
      const ids = this.selection.highlightIds();
      untracked(() => {
        for (const source of [this.anchorSource, this.footprintSource])
          source.getFeatures().forEach((f) => f.set('selected', ids.has(f.get('nodeId') as number)));
      });
    });
    // Selected or focused anywhere: the map goes to the node's place (its level too).
    effect(() => {
      const nodeId = this.selection.nodeId();
      this.selection.focusTick();
      untracked(() => {
        if (nodeId != null) this.showNodeAnchors(nodeId);
      });
    });
    effect(() => {
      const view = this.selection.panoramaView();
      untracked(() => this.drawCone(view));
    });
    // Opening a panorama: the map goes to its level and position (FR-P5).
    effect(() => {
      const id = this.selection.panoramaId();
      if (id == null) return;
      untracked(() =>
        this.api.panorama(id).subscribe((p) => {
          if (p.levelId !== this.selection.levelId()) this.selection.levelId.set(p.levelId);
          this.map?.getView().animate({ center: fromLonLat(p.position.coordinates), duration: 300 });
        }),
      );
    });
  }

  /** Floorplans and panoramas of the level (null: outdoor panoramas, no floorplan). */
  private loadLevel(siteId: number | null, levelId: number | null): void {
    this.floorplanLayers.forEach((l) => this.map?.removeLayer(l));
    this.floorplanLayers.length = 0;
    this.panoramaSource.clear();
    if (siteId == null) return;
    if (levelId != null)
      this.api.floorplans(levelId).subscribe((floorplans) => {
        if (this.selection.levelId() !== levelId) return;
        floorplans.forEach((f, i) => {
          const layer = new ImageLayer({
            source: new ImageWMS({ url: floorplanWmsUrl(f.id), params: { LAYERS: f.layer }, ratio: 1 }),
            opacity: f.opacity,
            zIndex: 50 + i,
          });
          this.floorplanLayers.push(layer);
          this.map?.addLayer(layer);
        });
      });
    const changedLevel = levelId !== this.shownLevel;
    this.shownLevel = levelId;
    this.anchorSource.clear();
    this.footprintSource.clear();
    this.api.places(siteId, levelId).subscribe((places) => {
      if (this.selection.levelId() !== levelId) return;
      this.anchorSource.clear();
      this.anchorSource.addFeatures(places.map((p) => this.placeFeature(p)));
    });
    this.api.levelAnchors(siteId, levelId).subscribe((anchors) => {
      if (this.selection.levelId() !== levelId) return;
      this.footprintSource.clear();
      this.footprintSource.addFeatures(anchors.filter((a) => a.regionShape === 'box').map((a) => this.footprintFeature(a)));
    });
    this.api.panoramas(siteId, levelId).subscribe((panoramas) => {
      if (this.selection.levelId() !== levelId) return;
      this.panoramaSource.clear();
      this.panoramaSource.addFeatures(panoramas.map((p) => this.panoramaFeature(p)));
      // Choosing a level zooms to its panoramas (the floorplan is around them).
      const focusing = this.focusPending;
      this.focusPending = false;
      if (changedLevel && !focusing && levelId != null && panoramas.length > 0 && this.selection.panoramaId() == null)
        this.map?.getView().fit(this.panoramaSource.getExtent(), { padding: [80, 80, 80, 80], maxZoom: 21, duration: 300 });
    });
  }

  private placeFeature(p: Place): Feature<Geometry> {
    const feature = new Feature<Geometry>(new Point(fromLonLat(p.position.coordinates)));
    feature.setProperties({ anchorId: p.anchorId, nodeId: p.nodeId, name: p.nodeName, kind: p.source, selected: this.selection.highlightIds().has(p.nodeId) });
    return feature;
  }

  /** A box as its (turned) rectangle on the floorplan, in real metres around its centre. */
  private footprintFeature(a: Anchor): Feature<Geometry> {
    const [lon, lat] = a.position.coordinates;
    const plane = new LocalPlane(lon, lat);
    const [hx, hy] = [(a.sizeX ?? 1) / 2, (a.sizeY ?? 1) / 2];
    const ring = [[-hx, -hy], [hx, -hy], [hx, hy], [-hx, hy], [-hx, -hy]].map(([x, y]) => {
      const [e, n] = fromBoxFrame(a.headingDeg ?? 0, x, y, 0);
      return fromLonLat(plane.toLonLat(e, n));
    });
    const feature = new Feature<Geometry>(new Polygon([ring]));
    feature.setProperties({ nodeId: a.nodeId, name: a.nodeName, area: (a.sizeX ?? 1) * (a.sizeY ?? 1), selected: this.selection.highlightIds().has(a.nodeId) });
    return feature;
  }

  private footprintStyle(feature: Feature<Geometry>): Style {
    const selected = feature.get('selected') as boolean;
    return new Style({
      fill: new Fill({ color: selected ? 'rgba(250, 204, 21, 0.28)' : 'rgba(37, 99, 235, 0.04)' }),
      stroke: new Stroke({ color: selected ? '#ca8a04' : 'rgba(71, 85, 105, 0.7)', width: selected ? 2.5 : 1, lineDash: selected ? undefined : [4, 3] }),
      zIndex: selected ? 2 : 1,
    });
  }

  /** One marker per node (FR-AR1b): diamond if its place is only an info point's; the selected node (subtree) highlighted. */
  private anchorStyle(feature: Feature<Geometry>): Style {
    const selected = feature.get('selected') as boolean;
    return new Style({
      image: new RegularShape({
        points: 4, radius: selected ? 10 : 7, angle: feature.get('kind') === 'panorama_point' ? 0 : Math.PI / 4,
        fill: new Fill({ color: selected ? '#facc15' : '#f97316' }), stroke: new Stroke({ color: selected ? '#1e293b' : '#fff', width: 2 }),
      }),
      text: new Text({
        text: feature.get('name') as string, offsetY: -16, font: `${selected ? 700 : 600} 11px system-ui, sans-serif`,
        fill: new Fill({ color: '#1e293b' }), stroke: new Stroke({ color: '#fff', width: 3 }),
      }),
      zIndex: selected ? 3 : 1,
    });
  }

  /**
   * A node selected anywhere: if it has an anchor but none on the current level, the map goes to the level and place
   * of its first anchor (spec 1.5: "if the element has a floorplan appearance, it shows on the floorplan").
   */
  private showNodeAnchors(nodeId: number): void {
    this.api.place(nodeId).subscribe((place) => {
      if (this.selection.nodeId() !== nodeId || !place) return;
      if (place.levelId !== this.selection.levelId()) {
        // The level change must not zoom to the level's panoramas: the map goes to the node.
        this.focusPending = true;
        this.selection.levelId.set(place.levelId);
      }
      this.map?.getView().animate({ center: fromLonLat(place.position.coordinates), duration: 300 });
    });
  }

  /** The open panorama's walk-through links as lines (FR-P4). */
  private loadLinks(panoramaId: number | null): void {
    this.linkSource.clear();
    if (panoramaId == null) return;
    const from = this.panoramaSource.getFeatures().find((f) => f.get('panoramaId') === panoramaId);
    this.api.neighbours(panoramaId).subscribe((neighbours) => {
      if (this.selection.panoramaId() !== panoramaId) return;
      const start = (from?.getGeometry() as Point | undefined)?.getCoordinates();
      if (!start) return;
      this.linkSource.addFeatures(neighbours.map((n) => new Feature(new LineString([start, fromLonLat(n.position.coordinates)]))));
    });
  }

  private panoramaFeature(p: Panorama): Feature<Geometry> {
    const feature = new Feature<Geometry>(new Point(fromLonLat(p.position.coordinates)));
    feature.setProperties({ panoramaId: p.id, status: p.privacyStatus, open: p.id === this.selection.panoramaId() });
    return feature;
  }

  private pickedPanorama(id: number): void {
    const tool = this.tools.tool();
    if (tool?.kind !== 'panorama') return;
    this.tools.cancel();
    tool.done(id);
  }

  private panoramaStyle(feature: Feature<Geometry>): Style {
    const status = feature.get('status') as string;
    const open = feature.get('open') as boolean;
    const color = status === 'approved' ? '#2563eb' : status === 'failed' ? '#dc2626' : '#f59e0b';
    return new Style({
      image: new Circle({ radius: open ? 9 : 7, fill: new Fill({ color }), stroke: new Stroke({ color: '#fff', width: open ? 3 : 2 }) }),
      zIndex: open ? 2 : 1,
    });
  }

  /** View cone of the open panorama: apex at the camera, opening = horizontal FOV, CONE_LENGTH_M long (FR-P7). */
  private drawCone(view: { position: [number, number]; headingDeg: number; fovDeg: number } | null): void {
    this.coneSource.clear();
    if (!view) return;
    const points: [number, number][] = [view.position];
    for (let i = 0; i <= 12; i++) {
      const direction = view.headingDeg - view.fovDeg / 2 + (view.fovDeg * i) / 12;
      points.push(destination(view.position, direction, CONE_LENGTH_M));
    }
    points.push(view.position);
    this.coneSource.addFeature(new Feature(new Polygon([points.map((p) => fromLonLat(p))])));
  }

  private create(): void {
    this.map = new OlMap({
      target: this.element().nativeElement,
      layers: [
        new VectorLayer({ source: this.sitesSource, style: (f) => this.siteStyle(f as Feature<Geometry>), zIndex: 100 }),
        new VectorLayer({
          source: this.coneSource,
          zIndex: 115,
          style: new Style({ fill: new Fill({ color: 'rgba(37, 99, 235, 0.25)' }), stroke: new Stroke({ color: '#2563eb', width: 1.5 }) }),
        }),
        new VectorLayer({
          source: this.linkSource,
          zIndex: 112,
          style: new Style({ stroke: new Stroke({ color: '#2563eb', width: 2, lineDash: [6, 4] }) }),
        }),
        new VectorLayer({ source: this.panoramaSource, style: (f) => this.panoramaStyle(f as Feature<Geometry>), zIndex: 120 }),
        new VectorLayer({ source: this.footprintSource, style: (f) => this.footprintStyle(f as Feature<Geometry>), zIndex: 110 }),
        new VectorLayer({ source: this.anchorSource, style: (f) => this.anchorStyle(f as Feature<Geometry>), zIndex: 125 }),
        new VectorLayer({
          source: this.highlightSource,
          zIndex: 110,
          style: new Style({
            stroke: new Stroke({ color: '#f59e0b', width: 3 }),
            fill: new Fill({ color: 'rgba(245, 158, 11, 0.15)' }),
            image: new Circle({ radius: 7, fill: new Fill({ color: '#f59e0b' }), stroke: new Stroke({ color: '#fff', width: 2 }) }),
          }),
        }),
      ],
      // Hungary as the start view; a selected site takes over.
      view: new View({ center: fromLonLat([19.5, 47.2]), zoom: 7, maxZoom: 22 }),
    });
    // For end-to-end tests: the map instance on its element (screen position of features).
    (this.element().nativeElement as unknown as { olMap: OlMap }).olMap = this.map;
    this.olMap.set(this.map);
    this.map.on('singleclick', (event) => {
      const tool = this.tools.tool();
      if (tool?.kind === 'point') {
        const [lon, lat] = toLonLat(event.coordinate);
        this.tools.cancel();
        tool.done([lon, lat]);
        return;
      }
      const panorama = this.map!.forEachFeatureAtPixel(event.pixel, (f) => f, { layerFilter: (l) => l.getSource() === this.panoramaSource });
      if (tool?.kind === 'panorama') {
        if (panorama) this.pickedPanorama(panorama.get('panoramaId') as number);
        return;
      }
      if (tool) return;
      const anchor = this.map!.forEachFeatureAtPixel(event.pixel, (f) => f, { layerFilter: (l) => l.getSource() === this.anchorSource });
      if (anchor) {
        this.selection.focusNode(anchor.get('nodeId') as number, true);
        return;
      }
      if (panorama) {
        this.selection.openPanorama(panorama.get('panoramaId') as number);
        return;
      }
      // Inside box areas: the smallest one (as in 3D, FR-A3b).
      const area = this.footprintSource.getFeaturesAtCoordinate(event.coordinate).sort((a, b) => a.get('area') - b.get('area'))[0];
      if (area) {
        this.selection.focusNode(area.get('nodeId') as number, true);
        return;
      }
      const feature = this.map!.forEachFeatureAtPixel(event.pixel, (f) => f, { layerFilter: (l) => l.getSource() === this.sitesSource });
      const siteId = feature?.get('siteId') as number | undefined;
      if (siteId != null) this.selection.selectSite(siteId);
    });
    // Editors move panoramas by dragging their marker (position stored in WGS84).
    {
      // Dragging follows the state at the moment of the drag: panoramas in preparation, anchors only in an editing
      // session (AnchorEditService) – a stray drag never moves an anchor.
      this.translate = new Translate({
        layers: (l) => (l.getSource() === this.panoramaSource && this.can.prepare()) || (l.getSource() === this.anchorSource && this.edits.active()),
        // A marker moves its point or info-point place; a place from a box or model element is moved in 3D.
        filter: (f) => f.get('anchorId') == null || f.get('kind') === 'point' || f.get('kind') === 'panorama_point',
        hitTolerance: 4,
      });
      let start: number[] = [];
      this.translate.on('translatestart', (event) => (start = event.coordinate));
      this.translate.on('translateend', (event) => {
        const feature = event.features.item(0);
        // The interaction takes the clicks on markers too: no movement = a click, which opens the panorama.
        const pixelsMoved = Math.hypot(event.coordinate[0] - start[0], event.coordinate[1] - start[1]) / this.map!.getView().getResolution()!;
        const anchorId = feature.get('anchorId') as number | undefined;
        if (pixelsMoved < 3) {
          if (anchorId != null) this.selection.focusNode(feature.get('nodeId') as number, true);
          else if (this.tools.tool()?.kind === 'panorama') this.pickedPanorama(feature.get('panoramaId') as number);
          else this.selection.openPanorama(feature.get('panoramaId') as number);
          return;
        }
        const [lon, lat] = toLonLat((feature.getGeometry() as Point).getCoordinates());
        if (anchorId != null) {
          // Staged: written only when the session is accepted.
          this.edits.stage(anchorId, { position: { type: 'Point', coordinates: [lon, lat] } });
          return;
        }
        this.api.updatePanorama(feature.get('panoramaId') as number, { position: { type: 'Point', coordinates: [lon, lat] } }).subscribe({
          next: () => {
            this.toasts.add({ severity: 'success', summary: this.m.panorama.moved, life: 2000 });
            this.selection.changed();
          },
          error: () => this.selection.changed(),
        });
      });
      this.map.addInteraction(this.translate);
    }
    // The splitter changes the size without a window resize.
    this.resizeObserver = new ResizeObserver(() => this.map?.updateSize());
    this.resizeObserver.observe(this.element().nativeElement);
    this.showSites(this.selection.sites(), this.selection.siteId());
    const site = this.selection.site();
    if (site) this.fitSite(site);
    this.layers().forEach((l) => this.addBackground(l));
  }

  private showSites(sites: Site[], selected: number | null): void {
    this.sitesSource.clear();
    for (const site of sites) {
      const point = new Feature(new Point(fromLonLat(site.refPoint.coordinates)));
      point.setProperties({ siteId: site.id, name: site.name, selected: site.id === selected });
      this.sitesSource.addFeature(point);
      if (site.extent) {
        const extent = geoJson.readFeature({ type: 'Feature', geometry: site.extent, properties: {} }) as Feature<Geometry>;
        extent.setProperties({ siteId: site.id, selected: site.id === selected, isExtent: true });
        this.sitesSource.addFeature(extent);
      }
    }
  }

  private fitSite(site: Site): void {
    const view = this.map?.getView();
    if (!view) return;
    if (site.extent) {
      const geometry = geoJson.readGeometry(site.extent);
      if (geometry) view.fit(geometry.getExtent(), { padding: [40, 40, 40, 40], duration: 400, maxZoom: 19 });
    } else view.animate({ center: fromLonLat(site.refPoint.coordinates), zoom: 17, duration: 400 });
  }

  private siteStyle(feature: Feature<Geometry>): Style {
    const selected = feature.get('selected') as boolean;
    const color = selected ? '#2563eb' : '#64748b';
    if (feature.get('isExtent'))
      return new Style({ stroke: new Stroke({ color, width: selected ? 2.5 : 1.5, lineDash: [6, 4] }), fill: new Fill({ color: 'rgba(37, 99, 235, 0.04)' }) });
    return new Style({
      image: new Circle({ radius: selected ? 8 : 6, fill: new Fill({ color }), stroke: new Stroke({ color: '#fff', width: 2 }) }),
      text: new Text({
        text: feature.get('name') as string,
        offsetY: -16,
        font: '600 12px system-ui, sans-serif',
        fill: new Fill({ color: '#1e293b' }),
        stroke: new Stroke({ color: '#fff', width: 3 }),
      }),
    });
  }

  private loadLayers(siteId: number | null): void {
    this.api.mapLayers(siteId).subscribe((layers) => {
      this.backgroundLayers.forEach((l) => this.map?.removeLayer(l));
      this.backgroundLayers.clear();
      this.layers.set(layers);
      this.visible.set(Object.fromEntries(layers.map((l) => [l.id, l.visibleDefault])));
      if (this.map) layers.forEach((l) => this.addBackground(l));
    });
  }

  private addBackground(layer: MapLayer): void {
    const index = this.layers().indexOf(layer);
    const image = new ImageLayer({
      source: new ImageWMS({ url: wmsProxyUrl(layer.id), params: { LAYERS: layer.layer }, ratio: 1 }),
      visible: this.visible()[layer.id] ?? layer.visibleDefault,
      zIndex: index,
    });
    this.backgroundLayers.set(layer.id, image);
    this.map!.addLayer(image);
  }

  protected toggle(id: number, on: boolean): void {
    this.visible.update((v) => ({ ...v, [id]: on }));
    this.backgroundLayers.get(id)?.setVisible(on);
  }

  private highlight(geometry: { type: string; coordinates: unknown } | null): void {
    this.highlightSource.clear();
    if (!geometry) return;
    const feature = geoJson.readFeature({ type: 'Feature', geometry, properties: {} }) as Feature<Geometry>;
    this.highlightSource.addFeature(feature);
    const extent = feature.getGeometry()!.getExtent();
    this.map?.getView().fit(extent, { padding: [60, 60, 60, 60], duration: 400, maxZoom: 20 });
  }

  private useTool(tool: ReturnType<MapToolsService['tool']>): void {
    if (this.draw) {
      this.map?.removeInteraction(this.draw);
      this.draw = undefined;
    }
    if (tool?.kind !== 'polygon' || !this.map) return;
    this.draw = new Draw({ type: 'Polygon' });
    this.draw.on('drawend', (event) => {
      const ring = (event.feature.getGeometry() as Polygon).getCoordinates()[0].map((c) => toLonLat(c) as LonLat);
      this.tools.cancel();
      tool.done(ring);
    });
    this.map.addInteraction(this.draw);
  }
}
