import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  ConfirmationService,
  MessageService,
  PrimeTemplate,
  TreeDragDropService,
  TreeNode,
} from 'primeng/api';
import { Button } from 'primeng/button';
import { SelectButton } from 'primeng/selectbutton';
import { Tooltip } from 'primeng/tooltip';
import { Tree, TreeNodeDropEvent } from 'primeng/tree';
import { Observable, forkJoin } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import { ModelAsset, Node, Panorama, Placement } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { NodeCreateDialog } from './node-create-dialog';

/** The tree view: one of the two aspects, or the uploaded data along the location structure. */
type TreeView = 'location' | 'function' | 'data';

/**
 * Tree view per aspect (FR-H7): search by name and code, create, delete with subtree, move by drag and drop (FR-H1).
 * Selecting a node selects it everywhere; a selection made elsewhere expands the tree down to the node.
 */
@Component({
  selector: 'app-tree-panel',
  // Drag and drop of the PrimeNG tree needs its service.
  providers: [TreeDragDropService],
  imports: [
    NgTemplateOutlet,
    FormsModule,
    Tree,
    PrimeTemplate,
    Button,
    SelectButton,
    Tooltip,
    NodeCreateDialog,
  ],
  template: `
    <div class="panel-toolbar">
      <p-selectbutton
        [options]="aspects()"
        optionLabel="label"
        optionValue="value"
        [ngModel]="view()"
        (ngModelChange)="setView($event)"
        [allowEmpty]="false"
        size="small"
      />
      <span class="spacer"></span>
      @if (can.prepare()) {
        <p-button
          icon="pi pi-plus"
          [text]="true"
          size="small"
          [pTooltip]="selected() ? m.tree.newChild : m.tree.newRoot"
          (onClick)="creating.set(true)"
        />
        <p-button
          icon="pi pi-trash"
          [text]="true"
          size="small"
          severity="danger"
          [pTooltip]="m.common.delete"
          [disabled]="!selected() || selectedTreeNode()?.type === 'placed'"
          (onClick)="remove()"
        />
      }
      <p-button
        icon="pi pi-refresh"
        [text]="true"
        size="small"
        [pTooltip]="m.common.refresh"
        (onClick)="load()"
      />
    </div>
    <p-tree
      [value]="roots()"
      selectionMode="single"
      [selection]="selectedTreeNode()"
      (onNodeSelect)="select($event.node)"
      (onNodeUnselect)="select($event.node)"
      (onNodeExpand)="expanded.add($event.node.key!)"
      (onNodeCollapse)="expanded.delete($event.node.key!)"
      [filter]="true"
      filterBy="label,data.code"
      [filterPlaceholder]="m.tree.filter"
      [emptyMessage]="loading() ? m.common.loading : m.tree.empty"
      [draggableNodes]="can.prepare()"
      [droppableNodes]="true"
      [validateDrop]="true"
      (onNodeDrop)="drop($event)"
      [loading]="loading()"
      styleClass="tree"
    >
      <!-- The tree picks the template by node type: the same label for nodes, placed equipment and the group. -->
      <ng-template let-node pTemplate="default">
        <ng-container *ngTemplateOutlet="label; context: { $implicit: node }" />
      </ng-template>
      <ng-template let-node pTemplate="placed">
        <ng-container *ngTemplateOutlet="label; context: { $implicit: node }" />
      </ng-template>
      <ng-template let-node pTemplate="folder">
        <ng-container *ngTemplateOutlet="label; context: { $implicit: node }" />
      </ng-template>
      <ng-template let-node pTemplate="panorama">
        <ng-container *ngTemplateOutlet="label; context: { $implicit: node }" />
      </ng-template>
      <ng-template let-node pTemplate="model">
        <ng-container *ngTemplateOutlet="label; context: { $implicit: node }" />
      </ng-template>
    </p-tree>
    <ng-template #label let-node>
      <span class="label" [class.muted]="node.type === 'folder'">{{ node.label }}</span>
      @if (isNode(node) && node.data?.code) {
        <small class="code">{{ node.data.code }}</small>
      }
      @if (view() === 'data' && dataCounts().get(node.key); as count) {
        <small class="code">{{ count }}</small>
      }
      @if (node.type === 'panorama' || node.type === 'model') {
        @if (dataStatus(node); as status) {
          <small class="status" [class.failed]="status.failed">{{ status.text }}</small>
        }
      }
      @if (node.type === 'placed') {
        <i
          class="pi pi-link placed"
          [title]="
            m.tree.placedHint(m.location.sources[placementOf(node.data.id)?.source ?? 'relation'])
          "
          [class.derived]="placementOf(node.data.id)?.source === 'box'"
        ></i>
      }
    </ng-template>
    @if (can.prepare()) {
      @defer {
        <app-node-create-dialog
          [(visible)]="creating"
          [parent]="selected()"
          (created)="selection.changed($event.id)"
        />
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    :host ::ng-deep .tree {
      flex: 1;
      min-height: 0;
      overflow: auto;
      border: 0;
    }
    .code {
      margin-left: 0.5rem;
      color: var(--p-text-muted-color);
    }
    .status {
      margin-left: 0.5rem;
      color: var(--p-orange-500);
      &.failed {
        color: var(--p-red-500);
      }
    }
    .placed {
      margin-left: 0.4rem;
      font-size: 0.75rem;
      color: var(--p-primary-color);
      &.derived {
        color: var(--p-text-muted-color);
      }
    }
  `,
})
export class TreePanel {
  protected readonly m = inject(MESSAGES);
  protected readonly auth = inject(AuthService);
  protected readonly can = inject(Permissions);
  protected readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmationService);
  private readonly toasts = inject(MessageService);

  protected readonly nodes = signal<Node[]>([]);
  /** Location tree: the functional nodes and where they are – the same records show under their place. */
  private readonly functionNodes = signal<Node[]>([]);
  private readonly placements = signal<Placement[]>([]);
  /** Data tree: the uploaded panoramas and models of the site. */
  private readonly panoramas = signal<Panorama[]>([]);
  private readonly models = signal<ModelAsset[]>([]);
  /** The data leaf last clicked (a panorama or model is not a node: the node selection cannot hold it). */
  private readonly dataKey = signal<string | null>(null);
  private readonly dataMode = signal(false);
  protected readonly view = computed<TreeView>(() =>
    this.selection.aspect() === 'function' ? 'function' : this.dataMode() ? 'data' : 'location',
  );
  protected readonly loading = signal(false);
  protected readonly creating = signal(false);
  /** Keys of expanded nodes, kept across reloads. */
  protected readonly expanded = new Set<string>();
  /** An equipment to open the location tree to once it shows (after a reload). */
  private reveal: number | null = null;

  protected readonly aspects = computed(() => [
    { label: this.m.aspects['location'], value: 'location' },
    { label: this.m.aspects['function'], value: 'function' },
    { label: this.m.aspects['data'], value: 'data' },
  ]);

  /** Tree nodes built from the flat list (parentId), siblings in sort order. */
  private readonly index = computed(() => {
    const byId = new Map<number, TreeNode<Node>>();
    for (const node of this.nodes()) {
      byId.set(node.id, {
        key: String(node.id),
        label: node.name,
        data: node,
        children: [],
        icon: this.icon(node),
        expanded: this.expanded.has(String(node.id)),
      });
    }
    const roots: TreeNode<Node>[] = [];
    for (const node of this.nodes()) {
      const treeNode = byId.get(node.id)!;
      const parent = node.parentId != null ? byId.get(node.parentId) : undefined;
      if (parent) {
        treeNode.parent = parent;
        parent.children!.push(treeNode);
      } else roots.push(treeNode);
    }
    const sort = (list: TreeNode<Node>[]) => {
      list.sort(
        (a, b) =>
          a.data!.sortOrder - b.data!.sortOrder || a.data!.name.localeCompare(b.data!.name, 'hu'),
      );
      list.forEach((n) => {
        n.leaf = n.children!.length === 0;
        sort(n.children!);
      });
    };
    sort(roots);
    const data = new Map<string, TreeNode>();
    if (this.view() === 'location') this.addPlaced(byId, roots);
    else if (this.view() === 'data') this.addData(byId, roots, data);
    return { byId, roots, data };
  });

  /** Where a record is in the location tree: a location node itself, or the place of an equipment. */
  private locationOf(
    nodeId: number | null,
    byId: Map<number, TreeNode<Node>>,
  ): TreeNode<Node> | undefined {
    if (nodeId == null) return undefined;
    const own = byId.get(nodeId);
    if (own) return own;
    const place = this.placementOf(nodeId)?.locationId;
    return place != null ? byId.get(place) : undefined;
  }

  /**
   * The data tree (user decision): the location structure, with the uploaded panoramas and 3D models as leaves under
   * the node they belong to (a functional node: under its place); the rest under "the whole site".
   */
  private addData(
    byId: Map<number, TreeNode<Node>>,
    roots: TreeNode<Node>[],
    data: Map<string, TreeNode>,
  ): void {
    const rest: TreeNode[] = [];
    const add = (leaf: TreeNode, nodeId: number | null) => {
      const parent = this.locationOf(nodeId, byId);
      leaf.leaf = true;
      leaf.droppable = false;
      if (parent) {
        leaf.parent = parent;
        parent.children!.push(leaf as TreeNode<Node>);
        parent.leaf = false;
      } else rest.push(leaf);
      data.set(leaf.key!, leaf);
    };
    for (const p of this.panoramas())
      add(
        {
          key: `pano:${p.id}`,
          label: this.m.tree.panorama(p.id, p.capturedAt),
          type: 'panorama',
          icon: 'pi pi-camera',
          data: p,
        },
        p.nodeId,
      );
    for (const x of [...this.models()].sort((a, b) => a.name.localeCompare(b.name, 'hu')))
      add(
        { key: `model:${x.id}`, label: x.name, type: 'model', icon: 'pi pi-box', data: x },
        x.nodeId,
      );
    if (rest.length) {
      const folder: TreeNode = {
        key: 'site-data',
        label: this.m.tree.siteData,
        type: 'folder',
        icon: 'pi pi-globe',
        selectable: false,
        droppable: false,
        children: rest,
        expanded: this.expanded.has('site-data'),
      };
      rest.forEach((leaf) => (leaf.parent = folder));
      roots.push(folder as TreeNode<Node>);
    }
  }

  /** Data tree: how many panoramas and models are under each node (to find where the data is). */
  protected readonly dataCounts = computed(() => {
    const counts = new Map<string, [number, number]>();
    for (const leaf of this.index().data.values())
      for (let p = leaf.parent; p; p = p.parent) {
        const c = counts.get(p.key!) ?? [0, 0];
        c[leaf.type === 'panorama' ? 0 : 1]++;
        counts.set(p.key!, c);
      }
    return new Map([...counts].map(([key, [p, x]]) => [key, this.m.tree.dataCounts(p, x)]));
  });

  protected dataStatus(node: TreeNode): { text: string; failed: boolean } | null {
    const text =
      node.type === 'panorama'
        ? this.m.tree.panoramaStatus[(node.data as Panorama).privacyStatus]
        : this.m.tree.modelStatus[(node.data as ModelAsset).status];
    if (!text) return null;
    const status =
      node.type === 'panorama'
        ? (node.data as Panorama).privacyStatus
        : (node.data as ModelAsset).status;
    return { text, failed: status === 'failed' };
  }

  /** A row of a record (not a data leaf or a group). */
  protected isNode(node: TreeNode): boolean {
    return !node.type || node.type === 'default' || node.type === 'placed';
  }

  protected setView(view: TreeView): void {
    this.dataMode.set(view === 'data');
    this.dataKey.set(null);
    this.selection.aspect.set(view === 'function' ? 'function' : 'location');
  }

  /**
   * The equipment is one record in both trees (user decision): in the location tree it shows as a leaf under its
   * place (located_in, else the smallest box containing it); those without a place in a "without place" group.
   */
  private addPlaced(byId: Map<number, TreeNode<Node>>, roots: TreeNode<Node>[]): void {
    const functional = this.functionNodes();
    const hasChildren = new Set(functional.map((n) => n.parentId).filter((id) => id != null));
    const where = new Map(this.placements().map((p) => [p.nodeId, p]));
    const unplaced: TreeNode<Node>[] = [];
    const folder: TreeNode<Node> = {
      key: 'unplaced',
      label: this.m.tree.unplaced,
      type: 'folder',
      icon: 'pi pi-inbox',
      selectable: false,
      children: unplaced,
      expanded: this.expanded.has('unplaced'),
    };
    for (const node of [...functional].sort((a, b) => a.name.localeCompare(b.name, 'hu'))) {
      const place = where.get(node.id)?.locationId;
      const parent = place != null ? byId.get(place) : undefined;
      // Only the leaves of the functional tree (equipment) wait for a place; a line has none of its own.
      if (!parent && hasChildren.has(node.id)) continue;
      const leaf: TreeNode<Node> = {
        key: `p:${node.id}`,
        label: node.name,
        data: node,
        type: 'placed',
        icon: this.icon(node),
        leaf: true,
        droppable: false,
      };
      if (parent) {
        leaf.parent = parent;
        parent.children!.push(leaf);
        parent.leaf = false;
      } else {
        leaf.parent = folder;
        unplaced.push(leaf);
      }
      byId.set(node.id, leaf);
    }
    folder.leaf = unplaced.length === 0;
    if (unplaced.length) roots.push(folder);
  }

  protected placementOf(nodeId: number): Placement | undefined {
    return this.placements().find((p) => p.nodeId === nodeId);
  }

  protected readonly roots = computed(() => this.index().roots);
  protected readonly selectedTreeNode = computed<TreeNode | null>(() => {
    const key = this.dataKey();
    if (key != null && this.index().data.has(key)) return this.index().data.get(key)!;
    return this.index().byId.get(this.selection.nodeId() ?? -1) ?? null;
  });
  protected readonly selected = computed(() => {
    const node = this.selectedTreeNode();
    return node && this.isNode(node) ? (node.data as Node) : null;
  });

  constructor() {
    effect(() => {
      this.selection.siteId();
      this.selection.aspect();
      this.dataMode();
      this.selection.version();
      untracked(() => this.load());
    });
    // A node selected or focused (here or elsewhere): the data leaf is no longer the selection.
    effect(() => {
      this.selection.nodeId();
      this.selection.focusTick();
      untracked(() => this.dataKey.set(null));
    });
    // A selection made elsewhere: switch to its tree and expand down to it.
    effect(() => {
      const node = this.selection.node();
      if (!node) return;
      untracked(() => {
        // Expanded first: switching the tree reloads it, and the reload builds with these.
        // An equipment shown in the location tree (under its place): stay there and open its place.
        const shown =
          this.selection.aspect() === 'location' && node.aspect === 'function'
            ? this.index().byId.get(node.id)
            : undefined;
        // Equipment not shown yet (e.g. just created under a place): it will be after the reload – stay and open it then.
        const equipmentComing =
          !shown &&
          this.selection.aspect() === 'location' &&
          node.aspect === 'function' &&
          !this.functionNodes().some((n) => n.parentId === node.id);
        if (equipmentComing) {
          this.reveal = node.id;
          return;
        }
        const ancestors = shown ? this.keysAbove(shown) : node.path.split('.').slice(0, -1);
        const closed = ancestors.some((key) => !this.expanded.has(key));
        ancestors.forEach((key) => this.expanded.add(key));
        if (!shown && node.aspect !== this.selection.aspect())
          this.selection.aspect.set(node.aspect);
        else if (closed) this.nodes.set([...this.nodes()]);
      });
    });
  }

  protected load(): void {
    const siteId = this.selection.siteId();
    if (siteId == null) {
      this.nodes.set([]);
      return;
    }
    this.loading.set(true);
    const aspect = this.selection.aspect();
    if (aspect === 'location')
      forkJoin([this.api.tree(siteId, 'function'), this.api.placements(siteId)]).subscribe(
        ([functional, placements]) => {
          if (this.selection.aspect() !== 'location') return;
          this.functionNodes.set(functional);
          this.placements.set(placements);
          // An equipment waiting to be shown: open the tree down to it under its place.
          const shown = this.reveal != null ? this.index().byId.get(this.reveal) : undefined;
          if (shown) {
            this.reveal = null;
            this.keysAbove(shown).forEach((key) => this.expanded.add(key));
            this.nodes.set([...this.nodes()]);
          }
        },
      );
    if (this.view() === 'data')
      forkJoin([this.api.sitePanoramas(siteId), this.api.models(siteId)]).subscribe(
        ([panoramas, models]) => {
          if (this.view() !== 'data' || this.selection.siteId() !== siteId) return;
          this.panoramas.set(panoramas);
          this.models.set(models);
          // A conversion is still running: its status is looked at again shortly.
          if (models.some((x) => x.status === 'converting'))
            setTimeout(
              () => this.view() === 'data' && this.selection.siteId() === siteId && this.load(),
              10000,
            );
        },
      );
    this.api.tree(siteId, aspect).subscribe({
      next: (nodes) => {
        this.nodes.set(nodes);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  /** Keys of the tree nodes above one (to expand down to it). */
  private keysAbove(node: TreeNode<Node>): string[] {
    const keys: string[] = [];
    for (let p = node.parent; p; p = p.parent) keys.unshift(p.key!);
    return keys;
  }

  protected select(node: TreeNode): void {
    if (node.type === 'panorama' || node.type === 'model') {
      // Not a node: the node selection stays, the view goes to the data (a node selection would fly to its room).
      this.dataKey.set(node.key!);
      const id = (node.data as Panorama | ModelAsset).id;
      if (node.type === 'panorama') this.selection.focusPanorama(id);
      else this.selection.focusModel(id);
      return;
    }
    this.selection.focusNode((node.data as Node).id);
  }

  protected remove(): void {
    const node = this.selected();
    if (!node) return;
    if (this.selectedTreeNode()?.type === 'placed') {
      this.toasts.add({ severity: 'info', summary: this.m.tree.placedNoDelete, life: 4000 });
      return;
    }
    const treeNode = this.selectedTreeNode()!;
    const hasChildren = (treeNode.children?.length ?? 0) > 0;
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: hasChildren
        ? this.m.tree.deleteSubtreeConfirm(node.name)
        : this.m.tree.deleteConfirm(node.name),
      acceptButtonProps: { label: this.m.common.delete, severity: 'danger' },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () =>
        this.api.deleteNode(node.id, hasChildren).subscribe(() => {
          this.toasts.add({ severity: 'success', summary: this.m.tree.deleted, life: 3000 });
          this.selection.changed(null);
        }),
    });
  }

  /**
   * Drop on a node: becomes its child; between nodes: sibling (same parent). The drop is applied to the tree first
   * (accept), the new parent read from it, then confirmed and saved; on cancel or error the tree reloads. The
   * database refuses cycles (node.cycle).
   */
  protected drop(event: TreeNodeDropEvent): void {
    const dragNode = event.dragNode;
    const dropNode = event.dropNode;
    const dragged = dragNode?.data as Node | undefined;
    // The tree subscribes to drags only if dropping is on when it starts, so dropping is always on and the mode is
    // checked here (dragging is off outside preparation anyway).
    if (!dragNode || !dropNode || !this.can.prepare()) return;
    event.accept?.();
    const parentNode = dropNode.children?.includes(dragNode) ? dropNode : (dropNode.parent ?? null);
    // Uploaded data in the data tree: the drop sets the node it belongs to.
    if (dragNode.type === 'panorama' || dragNode.type === 'model') {
      this.moveData(dragNode, parentNode);
      return;
    }
    // An equipment in the location tree: the drop sets its place (located_in), the functional tree stays as it is.
    if (dragNode.type === 'placed' && dragged) {
      this.place(
        dragged,
        parentNode?.type === 'folder' ? null : ((parentNode?.data as Node | undefined) ?? null),
      );
      return;
    }
    if (!dragged || parentNode?.type === 'placed' || parentNode?.type === 'folder') {
      this.toasts.add({ severity: 'warn', summary: this.m.tree.notAPlace, life: 3000 });
      this.load();
      return;
    }
    const parent = (
      dropNode.children?.includes(dragNode) ? dropNode.data : (dropNode.parent?.data ?? null)
    ) as Node | null;
    if ((parent?.id ?? null) === dragged.parentId) return;
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: parent
        ? this.m.tree.moveConfirm(dragged.name, parent.name)
        : this.m.tree.moveToRootConfirm(dragged.name),
      acceptButtonProps: { label: this.m.common.yes },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      reject: () => this.load(),
      accept: () =>
        this.api.updateNode(dragged.id, { parentId: parent?.id ?? null }).subscribe({
          next: () => {
            if (parent) this.expanded.add(String(parent.id));
            this.toasts.add({ severity: 'success', summary: this.m.tree.moved, life: 3000 });
            this.selection.changed();
          },
          error: () => this.load(),
        }),
    });
  }

  /** A panorama or model moved in the data tree: it belongs to the new location node (a model also to the site). */
  private moveData(leaf: TreeNode, target: TreeNode | null): void {
    const location = target && this.isNode(target) ? (target.data as Node) : null;
    const toSite = !target || target.key === 'site-data';
    const isPanorama = leaf.type === 'panorama';
    if ((!location && !(toSite && !isPanorama)) || (location && location.aspect !== 'location')) {
      this.toasts.add({
        severity: 'warn',
        summary: isPanorama ? this.m.tree.panoramaNeedsNode : this.m.tree.notAPlace,
        life: 3000,
      });
      this.load();
      return;
    }
    const data = leaf.data as Panorama | ModelAsset;
    if ((location?.id ?? null) === data.nodeId) return;
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.tree.dataMoveConfirm(leaf.label!, location?.name ?? this.m.tree.siteData),
      acceptButtonProps: { label: this.m.common.yes },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      reject: () => this.load(),
      accept: () =>
        (
          (isPanorama
            ? this.api.updatePanorama(data.id, { nodeId: location!.id })
            : this.api.updateModel(data.id, {
                nodeId: location?.id ?? null,
              })) as Observable<unknown>
        ).subscribe({
          next: () => {
            if (location) this.expanded.add(String(location.id));
            this.toasts.add({ severity: 'success', summary: this.m.tree.placeSaved, life: 2500 });
            this.selection.changed();
          },
          error: () => this.load(),
        }),
    });
  }

  private place(node: Node, location: Node | null): void {
    if (location && location.aspect !== 'location') {
      this.toasts.add({ severity: 'warn', summary: this.m.tree.notAPlace, life: 3000 });
      this.load();
      return;
    }
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: location
        ? this.m.tree.placeConfirm(node.name, location.name)
        : this.m.tree.unplaceConfirm(node.name),
      acceptButtonProps: { label: this.m.common.yes },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      reject: () => this.load(),
      accept: () =>
        this.api.setLocation(node.id, location?.id ?? null).subscribe({
          next: () => {
            if (location) this.expanded.add(String(location.id));
            this.toasts.add({ severity: 'success', summary: this.m.tree.placeSaved, life: 2500 });
            this.selection.changed();
          },
          error: () => this.load(),
        }),
    });
  }

  private icon(node: Node): string {
    const type = this.selection.typeOf(node.typeId);
    if (type?.isLevel) return 'pi pi-bars';
    switch (node.typeCode) {
      case 'building':
        return 'pi pi-building';
      case 'room':
      case 'outdoor_area':
        return 'pi pi-stop';
      case 'process_line':
        return 'pi pi-share-alt';
      case 'equipment':
        return 'pi pi-cog';
      default:
        return node.attrs.mapmanager_ref ? 'pi pi-link' : 'pi pi-circle';
    }
  }
}
