import { Component, computed, effect, inject, input, model, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { InputText } from 'primeng/inputtext';
import { Select } from 'primeng/select';
import { ApiService } from '../../core/api/api.service';
import { Node } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { SchemaForm } from '../datasheet/schema-form';

/** New node under a parent (or as a root) of the current tree; required technical data asked right away. */
@Component({
  selector: 'app-node-create-dialog',
  imports: [FormsModule, Dialog, Button, InputText, Select, SchemaForm],
  template: `
    <p-dialog [header]="m.node.createTitle" [(visible)]="visible" [modal]="true" [style]="{ width: '30rem' }">
      <form class="form-grid" (ngSubmit)="create()">
        <div class="field">
          <label>{{ m.node.parent }}</label>
          <span>{{ parent()?.name ?? m.node.root }}</span>
        </div>
        <div class="field">
          <label for="new-type">{{ m.node.type }}</label>
          @if (placeHere()) {
            <!-- Under a place: its own children, or equipment that gets this place (one record, both trees). -->
            <p-select inputId="new-type" name="type" [options]="typeGroups()" [group]="true" optionGroupLabel="label" optionGroupChildren="items"
              optionLabel="name" optionValue="id" [ngModel]="typeId()" (ngModelChange)="typeId.set($event); attrs = {}" fluid />
          } @else {
            <p-select inputId="new-type" name="type" [options]="types()" optionLabel="name" optionValue="id" [ngModel]="typeId()" (ngModelChange)="typeId.set($event); attrs = {}" fluid />
          }
        </div>
        @if (placing()) {
          <div class="field">
            <label for="new-function-parent">{{ m.node.functionParent }}</label>
            <p-select inputId="new-function-parent" name="functionParent" [options]="functionParents()" optionLabel="label" optionValue="value"
              [(ngModel)]="functionParentId" [filter]="true" filterBy="label" appendTo="body" fluid />
            <small class="muted">{{ m.node.placedHint(parent()!.name) }}</small>
          </div>
        }
        <div class="row">
          <div class="field">
            <label for="new-name">{{ m.node.name }}</label>
            <input pInputText id="new-name" name="name" [(ngModel)]="name" />
          </div>
          <div class="field">
            <label for="new-code">{{ m.node.code }}</label>
            <input pInputText id="new-code" name="code" [(ngModel)]="code" />
          </div>
        </div>
        @if (schema(); as s) {
          <app-schema-form [schema]="s" [(value)]="attrs" idPrefix="new-attr-" />
        }
        <div class="actions">
          <p-button [label]="m.common.cancel" severity="secondary" [text]="true" (onClick)="visible.set(false)" />
          <p-button type="submit" [label]="m.node.create" icon="pi pi-check" [loading]="saving()" [disabled]="!typeId() || !name.trim() || missingRequired()" />
        </div>
      </form>
    </p-dialog>
  `,
  styles: `
    .actions {
      display: flex;
      justify-content: flex-end;
      gap: 0.5rem;
    }
  `,
})
export class NodeCreateDialog {
  protected readonly m = inject(MESSAGES);
  private readonly api = inject(ApiService);
  private readonly selection = inject(SelectionService);

  readonly visible = model(false);

  /** Reset when opened (not on the dialog's onShow, which fires only after the opening animation). */
  private readonly resetOnOpen = effect(() => {
    if (this.visible()) untracked(() => this.reset());
  });
  readonly parent = input<Node | null>(null);
  readonly created = output<Node>();

  /** A signal: the schema (form fields) follows the chosen type. */
  protected readonly typeId = signal<number | null>(null);
  protected name = '';
  protected code = '';
  protected attrs: Record<string, unknown> = {};
  protected readonly saving = signal(false);

  /** Types of the current tree (aspect). */
  protected readonly types = computed(() => this.selection.nodeTypes().filter((t) => t.aspect === this.selection.aspect()));
  /** A place selected in the location tree: equipment (functional types) can be created right under it. */
  protected readonly placeHere = computed(() => this.selection.aspect() === 'location' && this.parent()?.aspect === 'location');
  protected readonly typeGroups = computed(() => [
    { label: this.m.node.locationTypes, items: this.types() },
    { label: this.m.node.equipmentHere(this.parent()?.name ?? ''), items: this.selection.nodeTypes().filter((t) => t.aspect === 'function') },
  ]);
  /** A functional type under a place: created in the functional tree, its place (located_in) is the selected one. */
  protected readonly placing = computed(() => this.placeHere() && this.selection.typeOf(this.typeId())?.aspect === 'function');
  private readonly functionNodes = signal<Node[]>([]);
  protected readonly functionParents = computed(() => {
    const byId = new Map(this.functionNodes().map((n) => [n.id, n]));
    const path = (n: Node): string => (n.parentId != null && byId.has(n.parentId) ? `${path(byId.get(n.parentId)!)} › ` : '') + n.name;
    return [
      { label: this.m.node.root, value: null as number | null },
      ...this.functionNodes().map((n) => ({ label: path(n), value: n.id as number | null })).sort((a, b) => a.label.localeCompare(b.label, 'hu')),
    ];
  });
  protected functionParentId: number | null = null;
  protected readonly schema = computed(() => this.selection.typeOf(this.typeId())?.attrSchema ?? null);

  /** Required attributes of the type not filled in (e.g. a level's floor elevation): the server would refuse it. */
  protected missingRequired(): boolean {
    const required = (this.schema() as { required?: string[] } | null)?.required ?? [];
    return required.some((name) => this.attrs[name] == null || this.attrs[name] === '');
  }

  protected reset(): void {
    this.typeId.set(null);
    this.functionParentId = null;
    const siteId = this.selection.siteId();
    if (this.placeHere() && siteId != null) this.api.tree(siteId, 'function').subscribe((nodes) => this.functionNodes.set(nodes));
    this.name = '';
    this.code = '';
    this.attrs = {};
  }

  protected create(): void {
    const siteId = this.selection.siteId();
    const typeId = this.typeId();
    if (siteId == null || typeId == null) return;
    this.saving.set(true);
    const place = this.placing() ? this.parent() : null;
    const done = (node: Node) => {
      this.saving.set(false);
      this.visible.set(false);
      this.created.emit(node);
    };
    const parentId = place ? this.functionParentId : (this.parent()?.id ?? null);
    this.api
      .createNode({ siteId, parentId, typeId, name: this.name.trim(), code: this.code.trim() || null, attrs: this.attrs })
      .subscribe({
        // Equipment created under a place: that place is its location (located_in).
        next: (node) =>
          place ? this.api.setLocation(node.id, place.id).subscribe({ next: () => done(node), error: () => done(node) }) : done(node),
        error: () => this.saving.set(false),
      });
  }
}
