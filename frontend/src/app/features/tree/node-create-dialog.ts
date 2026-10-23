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
          <p-select inputId="new-type" name="type" [options]="types()" optionLabel="name" optionValue="id" [(ngModel)]="typeId" fluid />
        </div>
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
          <p-button type="submit" [label]="m.node.create" icon="pi pi-check" [loading]="saving()" [disabled]="!typeId || !name.trim()" />
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

  protected typeId: number | null = null;
  protected name = '';
  protected code = '';
  protected attrs: Record<string, unknown> = {};
  protected readonly saving = signal(false);

  /** Types of the current tree (aspect). */
  protected readonly types = computed(() => this.selection.nodeTypes().filter((t) => t.aspect === this.selection.aspect()));
  protected readonly schema = computed(() => this.selection.typeOf(this.typeId)?.attrSchema ?? null);

  protected reset(): void {
    this.typeId = null;
    this.name = '';
    this.code = '';
    this.attrs = {};
  }

  protected create(): void {
    const siteId = this.selection.siteId();
    if (siteId == null || this.typeId == null) return;
    this.saving.set(true);
    this.api
      .createNode({ siteId, parentId: this.parent()?.id ?? null, typeId: this.typeId, name: this.name.trim(), code: this.code.trim() || null, attrs: this.attrs })
      .subscribe({
        next: (node) => {
          this.saving.set(false);
          this.visible.set(false);
          this.created.emit(node);
        },
        error: () => this.saving.set(false),
      });
  }
}
