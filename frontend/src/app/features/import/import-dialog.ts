import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, input, model, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { Message } from 'primeng/message';
import { Select } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { ApiService } from '../../core/api/api.service';
import { errorMessage, silent } from '../../core/api/errors';
import { ImportError } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { schemaFields } from '../datasheet/schema-form';
import { Table, Target, autoMap, readTable, toImportRows } from './table-reader';

/**
 * Bulk import of technical data from CSV/XLSX (FR-I3): file → column mapping (suggested from the headers) → import.
 * Existing records are updated by code, the rest created under the chosen parent. All or nothing: errors are listed
 * per row and nothing is saved.
 */
@Component({
  selector: 'app-import-dialog',
  imports: [FormsModule, Dialog, Button, Select, Message, TableModule],
  template: `
    <p-dialog [header]="m.import.title" [(visible)]="visible" [modal]="true" [style]="{ width: '46rem' }" [maximizable]="true">
      <div class="form-grid">
        <small class="muted">{{ m.import.hint }}</small>
        <div class="row">
          <div class="field">
            <label for="imp-type">{{ m.import.type }}</label>
            <p-select inputId="imp-type" [options]="types()" optionLabel="name" optionValue="id" [ngModel]="chosenTypeId()"
              (ngModelChange)="chooseType($event)" [filter]="true" appendTo="body" fluid />
          </div>
          <div class="field">
            <label for="imp-parent">{{ m.import.parent }}</label>
            <p-select inputId="imp-parent" [options]="parents()" optionLabel="label" optionValue="value" [(ngModel)]="parentId" appendTo="body" fluid />
          </div>
        </div>
        <div class="field">
          <label for="imp-file">{{ m.import.file }}</label>
          <input id="imp-file" type="file" accept=".csv,.txt,.xlsx" (change)="onFile($event)" />
        </div>

        @if (table(); as t) {
          <div class="field">
            <label>{{ m.import.mapping }} · {{ m.import.rows(t.rows.length) }}</label>
            <p-table [value]="mappingRows()" size="small" [scrollable]="true" scrollHeight="16rem">
              <ng-template #header>
                <tr><th>{{ m.import.column }}</th><th>{{ m.import.target }}</th><th>{{ m.import.sample }}</th></tr>
              </ng-template>
              <ng-template #body let-r>
                <tr>
                  <td>{{ r.header }}</td>
                  <td>
                    <p-select [options]="targetOptions()" optionLabel="label" optionValue="value" [ngModel]="targets()[r.index]"
                      (ngModelChange)="setTarget(r.index, $event)" size="small" appendTo="body" fluid />
                  </td>
                  <td class="muted">{{ r.sample }}</td>
                </tr>
              </ng-template>
            </p-table>
          </div>
        }

        @if (readError(); as error) {
          <p-message severity="error">{{ error }}</p-message>
        }
        @if (result(); as r) {
          <p-message severity="success">{{ m.import.done(r.created, r.updated) }}</p-message>
        }
        @if (errors().length > 0) {
          <div class="field">
            <label class="error-title">{{ m.import.errors }}</label>
            <p-table [value]="errors()" size="small" [scrollable]="true" scrollHeight="14rem">
              <ng-template #header>
                <tr><th>{{ m.import.row }}</th><th>{{ m.import.field }}</th><th>{{ m.import.message }}</th></tr>
              </ng-template>
              <ng-template #body let-e>
                <tr><td>{{ e.row + 1 }}</td><td>{{ fieldLabel(e.field) }}</td><td>{{ m.errors[e.code] ?? e.message }}</td></tr>
              </ng-template>
            </p-table>
          </div>
        }
      </div>
      <ng-template #footer>
        <p-button [label]="m.common.close" severity="secondary" [text]="true" (onClick)="visible.set(false)" />
        <p-button [label]="m.import.run" icon="pi pi-file-import" [loading]="running()" [disabled]="!canRun()" (onClick)="run()" />
      </ng-template>
    </p-dialog>
  `,
  styles: `
    .error-title {
      color: var(--p-red-600);
    }
  `,
})
export class ImportDialog {
  protected readonly m = inject(MESSAGES);
  private readonly api = inject(ApiService);
  private readonly selection = inject(SelectionService);

  readonly visible = model(false);

  /** Reset when opened (not on the dialog's onShow, which fires only after the opening animation). */
  private readonly resetOnOpen = effect(() => {
    if (this.visible()) untracked(() => this.reset());
  });
  /** Type preselected by the grid. */
  readonly typeId = input<number | null>(null);

  protected readonly chosenTypeId = signal<number | null>(null);
  protected readonly table = signal<Table | null>(null);
  protected readonly targets = signal<Target[]>([]);
  protected readonly errors = signal<ImportError[]>([]);
  protected readonly result = signal<{ created: number; updated: number } | null>(null);
  protected readonly readError = signal<string | null>(null);
  protected readonly running = signal(false);
  protected parentId: number | null = null;

  protected readonly types = computed(() => this.selection.nodeTypes());
  private readonly type = computed(() => this.selection.typeOf(this.chosenTypeId()));
  private readonly fields = computed(() => schemaFields(this.type()?.attrSchema));

  protected readonly targetOptions = computed(() => [
    { label: this.m.import.skip, value: '' },
    { label: this.m.node.code, value: 'code' },
    { label: this.m.node.name, value: 'name' },
    ...this.fields().map((f) => ({ label: f.label, value: `attrs.${f.name}` })),
  ]);

  /** Root, or the selected node if it is in the type's tree. */
  protected readonly parents = computed(() => {
    const options: { label: string; value: number | null }[] = [{ label: this.m.node.root, value: null }];
    const node = this.selection.node();
    if (node && node.aspect === this.type()?.aspect) options.push({ label: node.name, value: node.id });
    return options;
  });

  protected readonly mappingRows = computed(() => {
    const t = this.table();
    return (t?.headers ?? []).map((header, index) => ({ header, index, sample: t!.rows[0]?.[index] ?? '' }));
  });

  protected readonly canRun = computed(
    () => !!this.table() && this.chosenTypeId() != null && this.targets().some((t) => t === 'code' || t === 'name') && !this.running(),
  );

  protected reset(): void {
    this.chosenTypeId.set(this.typeId());
    this.table.set(null);
    this.targets.set([]);
    this.errors.set([]);
    this.result.set(null);
    this.readError.set(null);
    this.parentId = this.parents().at(-1)?.value ?? null;
  }

  protected chooseType(id: number): void {
    this.chosenTypeId.set(id);
    const t = this.table();
    if (t) this.targets.set(autoMap(t.headers, this.fields()));
  }

  protected async onFile(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0];
    this.errors.set([]);
    this.result.set(null);
    this.readError.set(null);
    if (!file) return;
    try {
      const table = await readTable(file);
      this.table.set(table);
      this.targets.set(autoMap(table.headers, this.fields()));
    } catch {
      this.table.set(null);
      this.readError.set(this.m.import.readError);
    }
  }

  protected setTarget(index: number, target: Target): void {
    this.targets.update((t) => t.map((v, i) => (i === index ? target : v)));
  }

  protected fieldLabel(field: string | null): string {
    if (!field) return '';
    if (field === 'code' || field === 'name') return this.m.node[field];
    return this.fields().find((f) => f.name === field)?.label ?? field;
  }

  protected run(): void {
    const siteId = this.selection.siteId();
    const table = this.table();
    const typeId = this.chosenTypeId();
    if (siteId == null || !table || typeId == null) return;
    this.running.set(true);
    this.errors.set([]);
    this.result.set(null);
    this.readError.set(null);
    this.api.importAttributes(siteId, typeId, this.parentId, toImportRows(table, this.targets()), silent()).subscribe({
      next: (result) => {
        this.running.set(false);
        this.result.set(result);
        this.selection.changed();
      },
      error: (err: unknown) => {
        this.running.set(false);
        const errors = err instanceof HttpErrorResponse ? (err.error?.errors as ImportError[] | undefined) : undefined;
        // Row numbers from the API count data rows from 1; shown as file lines (header = line 1).
        if (errors?.length) this.errors.set(errors);
        else this.readError.set(errorMessage(err, this.m));
      },
    });
  }
}
