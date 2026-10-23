import { Component, computed, effect, inject, input, model, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConfirmationService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { InputText } from 'primeng/inputtext';
import { Message } from 'primeng/message';
import { Select } from 'primeng/select';
import { ApiService } from '../../core/api/api.service';
import { errorMessage, silent } from '../../core/api/errors';
import { LayerField, SyncResult } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';

/**
 * Link a MapManager layer (spec 6.3): skeleton records for its features (type, name field, parent); the attributes
 * stay in MapManager. The check lists new features and orphan records (feature deleted in MapManager), which are
 * deleted only after confirmation.
 */
@Component({
  selector: 'app-link-dialog',
  imports: [FormsModule, Dialog, Button, InputText, Select, Message],
  template: `
    <p-dialog [header]="m.link.title" [(visible)]="visible" [modal]="true" [style]="{ width: '34rem' }">
      <div class="form-grid">
        <small class="muted">{{ m.link.hint }}</small>
        <div class="field">
          <label for="link-layer">{{ m.link.layer }}</label>
          <div class="inline">
            <input pInputText id="link-layer" [(ngModel)]="layerName" placeholder="munkaterulet:reteg" class="grow" />
            <p-button [label]="m.link.loadFields" severity="secondary" [outlined]="true" [loading]="loadingFields()" [disabled]="!layerName.trim()" (onClick)="loadFields()" />
          </div>
        </div>
        @if (fields().length > 0) {
          <div class="row">
            <div class="field">
              <label for="link-name">{{ m.link.nameField }}</label>
              <p-select inputId="link-name" [options]="fields()" optionLabel="name" optionValue="name" [(ngModel)]="nameField" appendTo="body" fluid />
            </div>
            <div class="field">
              <label for="link-type">{{ m.link.type }}</label>
              <p-select inputId="link-type" [options]="selection.nodeTypes()" optionLabel="name" optionValue="id" [(ngModel)]="typeId" [filter]="true" appendTo="body" fluid />
            </div>
          </div>
          <div class="field">
            <label for="link-parent">{{ m.link.parent }}</label>
            <p-select inputId="link-parent" [options]="parents()" optionLabel="label" optionValue="value" [(ngModel)]="parentId" appendTo="body" fluid />
          </div>
        }
        @if (message(); as msg) {
          <p-message [severity]="msg.severity">{{ msg.text }}</p-message>
        }
        @if (sync(); as s) {
          <div class="field">
            <label>{{ m.link.orphans }}</label>
            @for (o of s.orphans; track o.nodeId) {
              <div class="orphan">
                <span>{{ o.name }} <small class="muted">({{ o.featureId }})</small></span>
                <p-button icon="pi pi-trash" severity="danger" [text]="true" size="small" [ariaLabel]="m.link.deleteOrphan" (onClick)="deleteOrphan(o.nodeId, o.name)" />
              </div>
            } @empty {
              <span class="muted">{{ m.link.noOrphans }}</span>
            }
          </div>
        }
      </div>
      <ng-template #footer>
        <p-button [label]="m.link.sync" icon="pi pi-sync" severity="secondary" [outlined]="true" [loading]="syncing()" [disabled]="!layerName.trim()" (onClick)="check()" />
        <p-button [label]="m.link.run" icon="pi pi-link" [loading]="linking()" [disabled]="!nameField || !typeId" (onClick)="link()" />
      </ng-template>
    </p-dialog>
  `,
  styles: `
    .inline {
      display: flex;
      gap: 0.5rem;
    }
    .grow {
      flex: 1;
    }
    .orphan {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
  `,
})
export class LinkDialog {
  protected readonly m = inject(MESSAGES);
  protected readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmationService);

  readonly visible = model(false);

  /** Reset when opened (not on the dialog's onShow, which fires only after the opening animation). */
  private readonly resetOnOpen = effect(() => {
    if (this.visible()) untracked(() => this.reset());
  });
  /** Layer preselected by the grid (when a linked layer is shown). */
  readonly layer = input('');

  protected layerName = '';
  protected nameField: string | null = null;
  protected typeId: number | null = null;
  protected parentId: number | null = null;
  protected readonly fields = signal<LayerField[]>([]);
  protected readonly sync = signal<SyncResult | null>(null);
  protected readonly message = signal<{ severity: 'success' | 'error' | 'info'; text: string } | null>(null);
  protected readonly loadingFields = signal(false);
  protected readonly linking = signal(false);
  protected readonly syncing = signal(false);

  protected readonly parents = computed(() => {
    const options: { label: string; value: number | null }[] = [{ label: this.m.node.root, value: null }];
    const node = this.selection.node();
    if (node) options.push({ label: node.name, value: node.id });
    return options;
  });

  protected reset(): void {
    this.layerName = this.layer();
    this.nameField = null;
    this.typeId = this.selection.nodeTypes().find((t) => t.code === 'equipment')?.id ?? null;
    this.parentId = null;
    this.fields.set([]);
    this.sync.set(null);
    this.message.set(null);
  }

  protected loadFields(): void {
    this.loadingFields.set(true);
    this.message.set(null);
    this.api.mapManagerFields(this.layerName.trim(), silent()).subscribe({
      next: (fields) => {
        this.loadingFields.set(false);
        this.fields.set(fields);
        this.nameField = fields.find((f) => f.kind === 'text')?.name ?? fields[0]?.name ?? null;
      },
      error: (err) => {
        this.loadingFields.set(false);
        this.message.set({ severity: 'error', text: errorMessage(err, this.m) });
      },
    });
  }

  protected link(): void {
    const siteId = this.selection.siteId();
    if (siteId == null || !this.nameField || !this.typeId) return;
    this.linking.set(true);
    this.api.linkLayer(siteId, { layer: this.layerName.trim(), typeId: this.typeId, nameField: this.nameField, parentId: this.parentId }, silent()).subscribe({
      next: (r) => {
        this.linking.set(false);
        this.message.set({ severity: 'success', text: this.m.link.done(r.created, r.alreadyLinked) });
        this.selection.changed();
      },
      error: (err) => {
        this.linking.set(false);
        this.message.set({ severity: 'error', text: errorMessage(err, this.m) });
      },
    });
  }

  protected check(): void {
    const siteId = this.selection.siteId();
    if (siteId == null) return;
    this.syncing.set(true);
    this.api.syncLinks(siteId, this.layerName.trim()).subscribe({
      next: (s) => {
        this.syncing.set(false);
        this.sync.set(s);
        this.message.set({ severity: 'info', text: this.m.link.syncResult(s.features, s.linked, s.unlinked) });
      },
      error: () => this.syncing.set(false),
    });
  }

  protected deleteOrphan(nodeId: number, name: string): void {
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.tree.deleteConfirm(name),
      acceptButtonProps: { label: this.m.common.delete, severity: 'danger' },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () =>
        this.api.deleteNode(nodeId, false).subscribe(() => {
          this.sync.update((s) => (s ? { ...s, orphans: s.orphans.filter((o) => o.nodeId !== nodeId) } : s));
          this.selection.changed();
        }),
    });
  }
}
