import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { ApiService } from '../api/api.service';
import { Node, NodeMembers, NodeType, Site } from '../api/models';

/**
 * The central selection (spec 1.5, 11): current site, aspect, selected node. Every view (tree, grid, map, data
 * sheet, later panorama and 3D) reads and writes it, so a selection in one view shows in all the others.
 * `version` is bumped after data changes so that views reload.
 */
@Injectable({ providedIn: 'root' })
export class SelectionService {
  private readonly api = inject(ApiService);

  readonly sites = signal<Site[]>([]);
  readonly siteId = signal<number | null>(null);
  readonly site = computed(() => this.sites().find((s) => s.id === this.siteId()) ?? null);

  readonly nodeTypes = signal<NodeType[]>([]);
  readonly aspect = signal<'location' | 'function'>('location');

  readonly nodeId = signal<number | null>(null);
  /** The selected node's data, once loaded (by the data sheet / whoever has it). */
  readonly node = signal<Node | null>(null);

  /** Current level of the map (floorplans, panoramas); null: outdoor. */
  readonly levelId = signal<number | null>(null);

  /** The open panorama, and where its camera looks (absolute heading from north, horizontal FOV) – for the view cone. */
  readonly panoramaId = signal<number | null>(null);
  readonly panoramaView = signal<{
    position: [number, number];
    headingDeg: number;
    fovDeg: number;
  } | null>(null);

  /** A panorama shown on the data sheet (picked in the data tree); selecting a node takes the sheet back. */
  readonly sheetPanoramaId = signal<number | null>(null);

  /** The middle pane: 2D map or 3D view (a model focused from the data tree switches to 3D). */
  readonly centerView = signal<'map' | 'globe'>('map');
  /** A model to bring into view in 3D (data tree); the tick repeats a focus on the same model. */
  readonly modelFocus = signal<{ id: number; tick: number } | null>(null);

  /** Bumped after edits (create, update, move, delete, import): views reload their data. */
  readonly version = signal(0);

  /** The selected node's subtree with places (e.g. every element of a process line, M8). */
  readonly members = signal<NodeMembers | null>(null);
  /** The selected node and its whole subtree: highlighted together in the views (FR-3D5). */
  readonly highlightIds = computed(() => {
    const ids = new Set<number>(this.members()?.members.map((m) => m.nodeId) ?? []);
    const id = this.nodeId();
    if (id != null) ids.add(id);
    return ids;
  });

  constructor() {
    effect(() => {
      const id = this.nodeId();
      this.version();
      untracked(() => {
        if (this.members()?.node.nodeId !== id) this.members.set(null);
        if (id != null)
          this.api.members(id).subscribe((members) => {
            if (this.nodeId() === id) this.members.set(members);
          });
      });
    });
  }

  loadSites(selectId?: number): void {
    this.api.sites().subscribe((sites) => {
      this.sites.set(sites);
      const keep = selectId ?? this.siteId();
      this.selectSite(sites.some((s) => s.id === keep) ? keep : (sites[0]?.id ?? null));
    });
  }

  loadNodeTypes(): void {
    this.api.nodeTypes().subscribe((types) => this.nodeTypes.set(types));
  }

  selectSite(id: number | null): void {
    if (id === this.siteId()) return;
    this.siteId.set(id);
    this.levelId.set(null);
    this.openPanorama(null);
    this.sheetPanoramaId.set(null);
    this.modelFocus.set(null);
    this.selectNode(null);
  }

  openPanorama(id: number | null): void {
    this.panoramaId.set(id);
    if (id == null) this.panoramaView.set(null);
  }

  selectNode(id: number | null): void {
    if (id != null) this.sheetPanoramaId.set(null);
    if (id === this.nodeId()) return;
    this.nodeId.set(id);
    if (id == null) this.node.set(null);
  }

  /** Bumped by {@link focusNode}: views bring the selected node into view even if it was selected already. */
  readonly focusTick = signal(0);

  /**
   * Select a node and ask every view to show it, even if it was selected already. The panorama follows too: if the
   * node has an info point, its panorama opens (<paramref name="openPanorama"/>: a click in a graphical view) or – if
   * a panorama is open already – the view switches to one showing the node.
   */
  focusNode(id: number, openPanorama = false): void {
    this.sheetPanoramaId.set(null);
    this.selectNode(id);
    this.focusTick.update((v) => v + 1);
    if (!openPanorama && this.panoramaId() == null) return;
    this.api.nodeAnchors(id).subscribe((anchors) => {
      if (this.nodeId() !== id) return;
      const panoramas = anchors
        .filter((a) => a.kind === 'panorama_point' && a.panoramaId != null)
        .map((a) => a.panoramaId!);
      if (panoramas.length > 0 && !panoramas.includes(this.panoramaId() ?? -1))
        this.openPanorama(panoramas[0]);
    });
  }

  /** Show an uploaded model: the 3D view opens and flies to it. */
  focusModel(id: number): void {
    this.centerView.set('globe');
    this.modelFocus.set({ id, tick: (this.modelFocus()?.tick ?? 0) + 1 });
  }

  /** Show a panorama: it opens, and the map goes to its level and position. */
  focusPanorama(id: number): void {
    this.centerView.set('map');
    this.openPanorama(id);
    this.sheetPanoramaId.set(id);
  }

  /** After a change: reload the views; optionally select a node (e.g. a newly created one). */
  changed(selectId?: number | null): void {
    this.version.update((v) => v + 1);
    if (selectId !== undefined) {
      this.nodeId.set(null);
      this.selectNode(selectId);
    }
  }

  typeOf(typeId: number | null | undefined): NodeType | undefined {
    return this.nodeTypes().find((t) => t.id === typeId);
  }
}
