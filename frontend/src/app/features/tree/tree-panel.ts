import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConfirmationService, MessageService, PrimeTemplate, TreeDragDropService, TreeNode } from 'primeng/api';
import { Button } from 'primeng/button';
import { SelectButton } from 'primeng/selectbutton';
import { Tooltip } from 'primeng/tooltip';
import { Tree, TreeNodeDropEvent } from 'primeng/tree';
import { ApiService } from '../../core/api/api.service';
import { Node } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { NodeCreateDialog } from './node-create-dialog';

/**
 * Tree view per aspect (FR-H7): search by name and code, create, delete with subtree, move by drag and drop (FR-H1).
 * Selecting a node selects it everywhere; a selection made elsewhere expands the tree down to the node.
 */
@Component({
  selector: 'app-tree-panel',
  // Drag and drop of the PrimeNG tree needs its service.
  providers: [TreeDragDropService],
  imports: [FormsModule, Tree, PrimeTemplate, Button, SelectButton, Tooltip, NodeCreateDialog],
  template: `
    <div class="panel-toolbar">
      <p-selectbutton [options]="aspects()" optionLabel="label" optionValue="value" [ngModel]="selection.aspect()"
        (ngModelChange)="selection.aspect.set($event)" [allowEmpty]="false" size="small" />
      <span class="spacer"></span>
      @if (can.prepare()) {
        <p-button icon="pi pi-plus" [text]="true" size="small" [pTooltip]="selected() ? m.tree.newChild : m.tree.newRoot" (onClick)="creating.set(true)" />
        <p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" [pTooltip]="m.common.delete" [disabled]="!selected()" (onClick)="remove()" />
      }
      <p-button icon="pi pi-refresh" [text]="true" size="small" [pTooltip]="m.common.refresh" (onClick)="load()" />
    </div>
    <p-tree
      [value]="roots()"
      selectionMode="single"
      [selection]="selectedTreeNode()"
      (onNodeSelect)="select($event.node)"
      (onNodeUnselect)="selection.selectNode(null)"
      (onNodeExpand)="expanded.add($event.node.key!)"
      (onNodeCollapse)="expanded.delete($event.node.key!)"
      [filter]="true"
      filterBy="label,data.code"
      [filterPlaceholder]="m.tree.filter"
      [emptyMessage]="loading() ? m.common.loading : m.tree.empty"
      [draggableNodes]="can.prepare()"
      [droppableNodes]="can.prepare()"
      [validateDrop]="true"
      (onNodeDrop)="drop($event)"
      [loading]="loading()"
      styleClass="tree"
    >
      <ng-template let-node pTemplate="default">
        <span class="label">{{ node.label }}</span>
        @if (node.data.code) {
          <small class="code">{{ node.data.code }}</small>
        }
      </ng-template>
    </p-tree>
    @if (can.prepare()) {
      @defer {
        <app-node-create-dialog [(visible)]="creating" [parent]="selected()" (created)="selection.changed($event.id)" />
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
  protected readonly loading = signal(false);
  protected readonly creating = signal(false);
  /** Keys of expanded nodes, kept across reloads. */
  protected readonly expanded = new Set<string>();

  protected readonly aspects = computed(() => [
    { label: this.m.aspects['location'], value: 'location' },
    { label: this.m.aspects['function'], value: 'function' },
  ]);

  /** Tree nodes built from the flat list (parentId), siblings in sort order. */
  private readonly index = computed(() => {
    const byId = new Map<number, TreeNode<Node>>();
    for (const node of this.nodes()) {
      byId.set(node.id, { key: String(node.id), label: node.name, data: node, children: [], icon: this.icon(node), expanded: this.expanded.has(String(node.id)) });
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
      list.sort((a, b) => a.data!.sortOrder - b.data!.sortOrder || a.data!.name.localeCompare(b.data!.name, 'hu'));
      list.forEach((n) => {
        n.leaf = n.children!.length === 0;
        sort(n.children!);
      });
    };
    sort(roots);
    return { byId, roots };
  });

  protected readonly roots = computed(() => this.index().roots);
  protected readonly selectedTreeNode = computed(() => this.index().byId.get(this.selection.nodeId() ?? -1) ?? null);
  protected readonly selected = computed(() => this.selectedTreeNode()?.data ?? null);

  constructor() {
    effect(() => {
      this.selection.siteId();
      this.selection.aspect();
      this.selection.version();
      untracked(() => this.load());
    });
    // A selection made elsewhere: switch to its tree and expand down to it.
    effect(() => {
      const node = this.selection.node();
      if (!node) return;
      untracked(() => {
        if (node.aspect !== this.selection.aspect()) {
          this.selection.aspect.set(node.aspect);
          return;
        }
        const ancestors = node.path.split('.').slice(0, -1);
        if (ancestors.some((key) => !this.expanded.has(key))) {
          ancestors.forEach((key) => this.expanded.add(key));
          this.nodes.set([...this.nodes()]);
        }
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
    this.api.tree(siteId, this.selection.aspect()).subscribe({
      next: (nodes) => {
        this.nodes.set(nodes);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  protected select(node: TreeNode<Node>): void {
    this.selection.selectNode(node.data!.id);
  }

  protected remove(): void {
    const node = this.selected();
    if (!node) return;
    const treeNode = this.selectedTreeNode()!;
    const hasChildren = (treeNode.children?.length ?? 0) > 0;
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: hasChildren ? this.m.tree.deleteSubtreeConfirm(node.name) : this.m.tree.deleteConfirm(node.name),
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
    if (!dragNode || !dropNode || !dragged) return;
    event.accept?.();
    const parent = (dropNode.children?.includes(dragNode) ? dropNode.data : (dropNode.parent?.data ?? null)) as Node | null;
    if ((parent?.id ?? null) === dragged.parentId) return;
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: parent ? this.m.tree.moveConfirm(dragged.name, parent.name) : this.m.tree.moveToRootConfirm(dragged.name),
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
