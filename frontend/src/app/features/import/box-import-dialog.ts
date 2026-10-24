import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, inject, model, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { InputText } from 'primeng/inputtext';
import { Message } from 'primeng/message';
import { Select } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { ApiService } from '../../core/api/api.service';
import { errorMessage, silent } from '../../core/api/errors';
import { BoxImportResult, BoxImportRow, ImportError } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { Table, normalize, readTable } from './table-reader';

/** What a column holds in the box import. */
export type BoxTarget = '' | keyof BoxImportRow;

const TARGETS: (keyof BoxImportRow)[] = [
  'code',
  'level',
  'east',
  'north',
  'bottom',
  'sizeX',
  'sizeY',
  'sizeZ',
  'heading',
];
const REQUIRED: (keyof BoxImportRow)[] = ['code', 'east', 'north', 'sizeX', 'sizeY', 'sizeZ'];

/** Usual headers (normalized) of each column; "szélesség" is the width, a latitude column is "lat" or "földrajzi szélesség". */
const HEADERS: Record<keyof BoxImportRow, string[]> = {
  code: ['kod', 'code', 'azonosito', 'jeloles', 'rekordkodja', 'kks'],
  level: ['szint', 'level', 'emelet'],
  east: ['kelet', 'east', 'easting', 'y', 'eovy', 'hosszusag', 'lon', 'lng', 'longitude'],
  north: ['eszak', 'north', 'northing', 'x', 'eovx', 'lat', 'latitude', 'foldrajziszelesseg'],
  bottom: ['alsomagassag', 'also', 'alja', 'bottom', 'zmin', 'padlotol'],
  sizeX: ['szelesseg', 'width', 'sizex', 'dx'],
  sizeY: ['melyseg', 'depth', 'sizey', 'dy', 'hossz', 'length'],
  sizeZ: ['magassag', 'height', 'sizez', 'dz'],
  heading: ['elforgatas', 'forgatas', 'heading', 'irany', 'azimut', 'rotation'],
};

/** Suggested target of each column by its header (a known word, or a header starting with one). */
export function autoMapBoxes(headers: string[]): BoxTarget[] {
  const used = new Set<BoxTarget>();
  return headers.map((header) => {
    const h = normalize(header);
    const target =
      TARGETS.find(
        (t) =>
          !used.has(t) && HEADERS[t].some((k) => h === k || (k.length >= 4 && h.startsWith(k))),
      ) ?? '';
    if (target) used.add(target);
    return target;
  });
}

/** API rows by the column targets; cells go as read (the API converts decimal commas and XLSX numbers). */
export function toBoxRows(table: Table, targets: BoxTarget[]): BoxImportRow[] {
  return table.rows.map((cells) => {
    const row: BoxImportRow = {};
    targets.forEach((target, i) => {
      const value = cells[i];
      if (!target || value === undefined || value === null || String(value).trim() === '') return;
      if (target === 'code' || target === 'level') row[target] = String(value).trim();
      else row[target] = value;
    });
    return row;
  });
}

/**
 * Bounding boxes from a table (user decision): file → column mapping (suggested from the headers) → import. A record's
 * rows replace its boxes; the coordinates are in the chosen system (EOV by default). All or nothing: errors listed per
 * row. Finalized records ask for one reason (the change reason interceptor).
 */
@Component({
  selector: 'app-box-import-dialog',
  imports: [FormsModule, Dialog, Button, Select, InputText, Message, TableModule],
  template: `
    <p-dialog
      [header]="m.boxImport.title"
      [(visible)]="visible"
      [modal]="true"
      [style]="{ width: '48rem' }"
      [maximizable]="true"
    >
      <div class="form-grid">
        <small class="muted">{{ m.boxImport.hint }}</small>
        <div class="row">
          <div class="field">
            <label for="bi-srs">{{ m.boxImport.srs }}</label>
            <p-select
              inputId="bi-srs"
              [options]="m.boxImport.srsOptions"
              optionLabel="label"
              optionValue="value"
              [(ngModel)]="srsChoice"
              appendTo="body"
              fluid
            />
          </div>
          @if (srsChoice === 'other') {
            <div class="field">
              <label for="bi-srs-other">{{ m.boxImport.srsOther }}</label>
              <input pInputText id="bi-srs-other" [(ngModel)]="srsOther" placeholder="EPSG:3857" />
            </div>
          }
        </div>
        <div class="field">
          <label for="bi-file">{{ m.import.file }}</label>
          <div class="inline">
            <input id="bi-file" type="file" accept=".csv,.txt,.xlsx" (change)="onFile($event)" />
            <p-button
              [label]="m.boxImport.template"
              icon="pi pi-download"
              [text]="true"
              size="small"
              (onClick)="downloadTemplate()"
            />
          </div>
        </div>

        @if (table(); as t) {
          <div class="field">
            <label>{{ m.import.mapping }} · {{ m.import.rows(t.rows.length) }}</label>
            <p-table [value]="mappingRows()" size="small" [scrollable]="true" scrollHeight="16rem">
              <ng-template #header>
                <tr>
                  <th>{{ m.import.column }}</th>
                  <th>{{ m.import.target }}</th>
                  <th>{{ m.import.sample }}</th>
                </tr>
              </ng-template>
              <ng-template #body let-r>
                <tr>
                  <td>{{ r.header }}</td>
                  <td>
                    <p-select
                      [options]="targetOptions"
                      optionLabel="label"
                      optionValue="value"
                      [ngModel]="targets()[r.index]"
                      (ngModelChange)="setTarget(r.index, $event)"
                      size="small"
                      appendTo="body"
                      fluid
                    />
                  </td>
                  <td class="muted">{{ r.sample }}</td>
                </tr>
              </ng-template>
            </p-table>
          </div>
          @if (!mapped()) {
            <p-message severity="warn" size="small">{{ m.boxImport.needs }}</p-message>
          }
        }

        @if (readError(); as error) {
          <p-message severity="error">{{ error }}</p-message>
        }
        @if (result(); as r) {
          <p-message severity="success">{{
            m.boxImport.done(r.created, r.updated, r.removed, r.records)
          }}</p-message>
        }
        @if (errors().length > 0) {
          <div class="field">
            <label class="error-title">{{ m.import.errors }}</label>
            <p-table [value]="errors()" size="small" [scrollable]="true" scrollHeight="14rem">
              <ng-template #header>
                <tr>
                  <th>{{ m.import.row }}</th>
                  <th>{{ m.import.field }}</th>
                  <th>{{ m.import.message }}</th>
                </tr>
              </ng-template>
              <ng-template #body let-e>
                <tr>
                  <td>{{ e.row + 1 }}</td>
                  <td>{{ m.boxImport.targets[e.field] ?? e.field }}</td>
                  <td>{{ m.errors[e.code] ?? e.message }}</td>
                </tr>
              </ng-template>
            </p-table>
          </div>
        }
      </div>
      <ng-template #footer>
        <p-button
          [label]="m.common.close"
          severity="secondary"
          [text]="true"
          (onClick)="visible.set(false)"
        />
        <p-button
          [label]="m.import.run"
          icon="pi pi-file-import"
          [loading]="running()"
          [disabled]="!canRun()"
          (onClick)="run()"
        />
      </ng-template>
    </p-dialog>
  `,
  styles: `
    .error-title {
      color: var(--p-red-600);
    }
    .inline {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
  `,
})
export class BoxImportDialog {
  protected readonly m = inject(MESSAGES);
  private readonly api = inject(ApiService);
  private readonly selection = inject(SelectionService);

  readonly visible = model(false);

  /** Reset when opened (not on the dialog's onShow, which fires only after the opening animation). */
  private readonly resetOnOpen = effect(() => {
    if (this.visible()) untracked(() => this.reset());
  });

  protected readonly table = signal<Table | null>(null);
  protected readonly targets = signal<BoxTarget[]>([]);
  protected readonly errors = signal<ImportError[]>([]);
  protected readonly result = signal<BoxImportResult | null>(null);
  protected readonly readError = signal<string | null>(null);
  protected readonly running = signal(false);
  protected srsChoice = 'EPSG:23700';
  protected srsOther = '';

  protected readonly targetOptions = [
    { label: this.m.import.skip, value: '' },
    ...TARGETS.map((t) => ({ label: this.m.boxImport.targets[t], value: t })),
  ];

  protected readonly mappingRows = computed(() => {
    const t = this.table();
    return (t?.headers ?? []).map((header, index) => ({
      header,
      index,
      sample: t!.rows[0]?.[index] ?? '',
    }));
  });

  protected readonly mapped = computed(() => REQUIRED.every((r) => this.targets().includes(r)));
  protected readonly canRun = computed(() => !!this.table() && this.mapped() && !this.running());

  protected reset(): void {
    this.table.set(null);
    this.targets.set([]);
    this.errors.set([]);
    this.result.set(null);
    this.readError.set(null);
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
      this.targets.set(autoMapBoxes(table.headers));
    } catch {
      this.table.set(null);
      this.readError.set(this.m.import.readError);
    }
  }

  protected setTarget(index: number, target: BoxTarget): void {
    this.targets.update((t) => t.map((v, i) => (i === index ? target : v)));
  }

  /** A CSV with the expected headers and one example row (semicolon, decimal comma: opens in Hungarian Excel). */
  protected downloadTemplate(): void {
    const headers = TARGETS.map((t) => this.m.boxImport.targets[t]);
    const example = ['=P01', 'F0', '650123,45', '239876,54', '0', '1,2', '0,8', '1,5', '0'];
    const csv = '﻿' + [headers, example].map((r) => r.join(';')).join('\r\n') + '\r\n';
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = this.m.boxImport.templateName;
    a.click();
    URL.revokeObjectURL(url);
  }

  protected run(): void {
    const siteId = this.selection.siteId();
    const table = this.table();
    if (siteId == null || !table) return;
    const srs = this.srsChoice === 'other' ? this.srsOther.trim() : this.srsChoice;
    this.running.set(true);
    this.errors.set([]);
    this.result.set(null);
    this.readError.set(null);
    this.api.importBoxes(siteId, srs, toBoxRows(table, this.targets()), silent()).subscribe({
      next: (result) => {
        this.running.set(false);
        this.result.set(result);
        this.selection.changed();
      },
      error: (err: unknown) => {
        this.running.set(false);
        const errors =
          err instanceof HttpErrorResponse
            ? (err.error?.errors as ImportError[] | undefined)
            : undefined;
        if (errors?.length) this.errors.set(errors);
        else this.readError.set(errorMessage(err, this.m));
      },
    });
  }
}
