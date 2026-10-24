import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { IconField } from 'primeng/iconfield';
import { InputIcon } from 'primeng/inputicon';
import { InputNumber } from 'primeng/inputnumber';
import { InputText } from 'primeng/inputtext';
import { Select } from 'primeng/select';
import { TableEditCompleteEvent, TableLazyLoadEvent, TableModule } from 'primeng/table';
import { Tag } from 'primeng/tag';
import { Tooltip } from 'primeng/tooltip';
import { Subscription } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import { GridFilter, LinkedLayer, Node } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { schemaFields } from '../datasheet/schema-form';
import { ImportDialog } from '../import/import-dialog';
import { LinkDialog } from '../link/link-dialog';
import { GridColumn, formatValue, parseFilter } from './grid-columns';

type Source = { kind: 'type'; typeId: number } | { kind: 'layer'; layer: string };

interface Row {
  key: string;
  nodeId: number | null;
  node: Node | null;
  linked: boolean;
  anchorKinds: string[];
  values: Record<string, unknown>;
}

const ANCHOR_ICONS: Record<string, string> = {
  model_feature: 'pi pi-box',
  panorama_point: 'pi pi-eye',
  region: 'pi pi-stop',
  point: 'pi pi-map-marker',
};

/**
 * Database grid (FR-G): own records of a node type with columns from its JSON Schema, or the rows of a linked
 * MapManager layer (live, read only; spec 6.3). Server-side paging, sorting and filtering, also on the technical
 * data. Row selection ↔ central selection (FR-G3); anchor kinds shown (FR-G4); cells editable for editors (FR-G5).
 */
@Component({
  selector: 'app-grid-panel',
  imports: [FormsModule, TableModule, Button, Select, InputText, InputNumber, IconField, InputIcon, Tag, Tooltip, ImportDialog, LinkDialog],
  template: `
    <div class="panel-toolbar">
      <p-select [options]="sourceOptions()" [group]="true" optionLabel="label" optionValue="value" optionGroupLabel="label"
        optionGroupChildren="items" [ngModel]="sourceKey()" (ngModelChange)="chooseSource($event)" [placeholder]="m.grid.chooseType"
        size="small" [filter]="true" styleClass="source" />
      <p-iconfield>
        <p-inputicon styleClass="pi pi-search" />
        <input pInputText size="small" [placeholder]="m.common.search" [ngModel]="search()" (ngModelChange)="onSearch($event)" [disabled]="source()?.kind !== 'type'" />
      </p-iconfield>
      <span class="spacer"></span>
      @if (can.prepare()) {
        <p-button icon="pi pi-file-import" [text]="true" size="small" [pTooltip]="m.grid.import" (onClick)="importing.set(true)" />
        <p-button icon="pi pi-link" [text]="true" size="small" [pTooltip]="m.grid.link" (onClick)="linking.set(true)" />
      }
      <p-button icon="pi pi-refresh" [text]="true" size="small" [pTooltip]="m.common.refresh" (onClick)="reload()" />
    </div>
    @if (source()?.kind === 'layer') {
      <small class="note muted"><i class="pi pi-link"></i> {{ m.grid.readOnlyLinked }}</small>
    }
    <p-table
      [value]="rows()"
      [columns]="columns()"
      [lazy]="true"
      (onLazyLoad)="onLazyLoad($event)"
      [paginator]="true"
      [rows]="pageSize"
      [totalRecords]="total()"
      [rowsPerPageOptions]="[25, 50, 100, 200]"
      [loading]="loading()"
      [scrollable]="true"
      scrollHeight="flex"
      size="small"
      [showGridlines]="true"
      [stripedRows]="true"
      selectionMode="single"
      [selection]="selectedRow()"
      (onRowSelect)="selectRow($any($event.data))"
      dataKey="key"
      editMode="cell"
      (onEditComplete)="editComplete($event)"
      [showCurrentPageReport]="true"
      currentPageReportTemplate="{first}–{last} / {totalRecords}"
      styleClass="grid-table"
    >
      <ng-template #header let-columns>
        <tr>
          @if (source()?.kind === 'type') {
            <th class="anchors">{{ m.grid.anchors }}</th>
          }
          @for (col of columns; track col.field) {
            <th [pSortableColumn]="col.field">{{ col.label }} <p-sortIcon [field]="col.field" /></th>
          }
        </tr>
        <tr class="filters">
          @if (source()?.kind === 'type') {
            <th></th>
          }
          @for (col of columns; track col.field) {
            <th>
              @if (col.kind === 'boolean') {
                <p-select [options]="booleanOptions()" optionLabel="label" optionValue="value" [ngModel]="filterValues()[col.field]"
                  (ngModelChange)="setFilter(col, $event)" [showClear]="true" size="small" appendTo="body" fluid />
              } @else {
                <input pInputText size="small" class="filter" [ngModel]="filterValues()[col.field] ?? ''" (ngModelChange)="setFilter(col, $event)"
                  [placeholder]="col.kind === 'text' ? '' : m.grid.numberFilterHint" />
              }
            </th>
          }
        </tr>
      </ng-template>
      <ng-template #body let-row let-columns="columns">
        <tr [pSelectableRow]="row">
          @if (source()?.kind === 'type') {
            <td class="anchors">
              @for (kind of row.anchorKinds; track kind) {
                <i [class]="anchorIcon(kind)" [pTooltip]="m.anchorKinds[kind]"></i>
              } @empty {
                <i class="pi pi-minus muted" [pTooltip]="m.grid.noAnchor"></i>
              }
              @if (row.linked) {
                <p-tag icon="pi pi-link" severity="info" [pTooltip]="m.grid.linked" />
              }
            </td>
          }
          @for (col of columns; track col.field) {
            @if (canEdit(row, col)) {
              <td [pEditableColumn]="row" [pEditableColumnField]="col.field" [class.number]="col.kind !== 'text'">
                <p-cellEditor>
                  <ng-template #input>
                    @switch (col.kind) {
                      @case ('number') {
                        <p-inputnumber [(ngModel)]="row.values[col.field]" mode="decimal" [maxFractionDigits]="6" locale="hu-HU" size="small" fluid />
                      }
                      @case ('integer') {
                        <p-inputnumber [(ngModel)]="row.values[col.field]" [useGrouping]="false" size="small" fluid />
                      }
                      @case ('boolean') {
                        <p-select [options]="booleanOptions()" optionLabel="label" optionValue="value" [(ngModel)]="row.values[col.field]"
                          [showClear]="true" size="small" appendTo="body" fluid />
                      }
                      @default {
                        <input pInputText size="small" [(ngModel)]="row.values[col.field]" class="w-full" />
                      }
                    }
                  </ng-template>
                  <ng-template #output>{{ format(row.values[col.field]) }}</ng-template>
                </p-cellEditor>
              </td>
            } @else {
              <td [class.number]="col.kind !== 'text'">{{ format(row.values[col.field]) }}</td>
            }
          }
        </tr>
      </ng-template>
      <ng-template #emptymessage>
        <tr>
          <td [attr.colspan]="columns().length + 1" class="muted">{{ loading() ? m.common.loading : m.grid.empty }}</td>
        </tr>
      </ng-template>
    </p-table>
    @if (can.prepare()) {
      @defer {
        <app-import-dialog [(visible)]="importing" [typeId]="source()?.kind === 'type' ? $any(source()).typeId : null" />
        <app-link-dialog [(visible)]="linking" [layer]="source()?.kind === 'layer' ? $any(source()).layer : ''" />
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
    :host ::ng-deep .source {
      min-width: 12rem;
      max-width: 18rem;
    }
    .note {
      padding: 0.25rem 0.75rem;
    }
    :host ::ng-deep .grid-table {
      flex: 1;
      min-height: 0;
    }
    td.number {
      text-align: right;
    }
    .anchors {
      width: 5rem;
      white-space: nowrap;
      i {
        margin-right: 0.3rem;
      }
    }
    input.filter {
      width: 100%;
    }
  `,
})
export class GridPanel {
  protected readonly m = inject(MESSAGES);
  protected readonly auth = inject(AuthService);
  protected readonly can = inject(Permissions);
  private readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  private readonly toasts = inject(MessageService);

  protected readonly source = signal<Source | null>(null);
  protected readonly linkedLayers = signal<LinkedLayer[]>([]);
  protected readonly layerColumns = signal<GridColumn[]>([]);
  protected readonly rows = signal<Row[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(false);
  protected readonly search = signal('');
  protected readonly filterValues = signal<Record<string, unknown>>({});
  protected readonly importing = signal(false);
  protected readonly linking = signal(false);
  protected pageSize = 50;
  private first = 0;
  private sort: string | null = null;
  private descending = false;
  private request?: Subscription;
  private debounce?: ReturnType<typeof setTimeout>;

  protected readonly sourceKey = computed(() => {
    const s = this.source();
    return s == null ? null : s.kind === 'type' ? `type:${s.typeId}` : `layer:${s.layer}`;
  });

  protected readonly sourceOptions = computed(() => {
    const groups = [];
    for (const aspect of ['location', 'function'] as const) {
      const items = this.selection.nodeTypes().filter((t) => t.aspect === aspect).map((t) => ({ label: t.name, value: `type:${t.id}` }));
      groups.push({ label: `${this.m.grid.ownData} – ${this.m.aspects[aspect]}`, items });
    }
    if (this.linkedLayers().length > 0)
      groups.push({ label: this.m.grid.linkedLayers, items: this.linkedLayers().map((l) => ({ label: `${l.layer} (${l.count})`, value: `layer:${l.layer}` })) });
    return groups;
  });

  protected readonly booleanOptions = computed(() => [
    { label: this.m.common.yes, value: true },
    { label: this.m.common.no, value: false },
  ]);

  protected readonly columns = computed<GridColumn[]>(() => {
    const s = this.source();
    if (s?.kind === 'layer') return this.layerColumns();
    if (s?.kind !== 'type') return [];
    const type = this.selection.typeOf(s.typeId);
    return [
      { field: 'name', label: this.m.node.name, kind: 'text', editable: true },
      { field: 'code', label: this.m.node.code, kind: 'text', editable: true },
      ...schemaFields(type?.attrSchema).map((f) => ({
        field: `attrs.${f.name}`,
        label: f.label,
        kind: (f.property.type === 'number' || f.property.type === 'integer' || f.property.type === 'boolean' ? f.property.type : 'text') as GridColumn['kind'],
        editable: !f.property.enum,
      })),
    ];
  });

  protected readonly selectedRow = computed(() => this.rows().find((r) => r.nodeId != null && r.nodeId === this.selection.nodeId()) ?? null);

  constructor() {
    // Site change: linked layers and a default source (the type of the selected node, or the first function type).
    effect(() => {
      const siteId = this.selection.siteId();
      this.selection.version();
      untracked(() => {
        if (siteId == null) return;
        this.api.linkedLayers(siteId).subscribe((layers) => this.linkedLayers.set(layers));
      });
    });
    effect(() => {
      const types = this.selection.nodeTypes();
      const node = this.selection.node();
      untracked(() => {
        if (this.source() == null && types.length > 0) {
          const type = (node && types.find((t) => t.id === node.typeId)) ?? types.find((t) => t.code === 'equipment') ?? types[0];
          this.source.set({ kind: 'type', typeId: type.id });
        }
      });
    });
    effect(() => {
      this.source();
      this.selection.siteId();
      this.selection.version();
      untracked(() => this.load());
    });
  }

  protected chooseSource(key: string | null): void {
    if (!key) return;
    const [kind, value] = [key.slice(0, key.indexOf(':')), key.slice(key.indexOf(':') + 1)];
    this.filterValues.set({});
    this.search.set('');
    this.sort = null;
    this.first = 0;
    this.layerColumns.set([]);
    this.source.set(kind === 'type' ? { kind: 'type', typeId: Number(value) } : { kind: 'layer', layer: value });
  }

  protected onLazyLoad(event: TableLazyLoadEvent): void {
    const sort = typeof event.sortField === 'string' ? event.sortField : null;
    const changed = (event.first ?? 0) !== this.first || (event.rows ?? this.pageSize) !== this.pageSize || sort !== this.sort || (event.sortOrder === -1) !== this.descending;
    if (!changed) return;
    this.first = event.first ?? 0;
    this.pageSize = event.rows ?? this.pageSize;
    this.sort = sort;
    this.descending = event.sortOrder === -1;
    this.load();
  }

  protected onSearch(text: string): void {
    this.search.set(text);
    this.debounced();
  }

  protected setFilter(column: GridColumn, value: unknown): void {
    this.filterValues.update((f) => ({ ...f, [column.field]: value }));
    this.debounced();
  }

  protected reload(): void {
    this.selection.changed();
  }

  private debounced(): void {
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => {
      this.first = 0;
      this.load();
    }, 400);
  }

  private filters(): GridFilter[] {
    const values = this.filterValues();
    return this.columns()
      .map((c) => parseFilter(c, values[c.field]))
      .filter((f): f is GridFilter => f != null);
  }

  private load(): void {
    const siteId = this.selection.siteId();
    const source = this.source();
    this.request?.unsubscribe();
    if (siteId == null || source == null) {
      this.rows.set([]);
      this.total.set(0);
      return;
    }
    this.loading.set(true);
    if (source.kind === 'type') {
      this.request = this.api
        .grid({ siteId, typeId: source.typeId, search: this.search(), filters: this.filters(), sort: this.sort, descending: this.descending, first: this.first, rows: this.pageSize })
        .subscribe({
          next: (page) => {
            this.total.set(page.total);
            this.rows.set(
              page.rows.map((r) => ({
                key: String(r.node.id),
                nodeId: r.node.id,
                node: r.node,
                linked: r.linked,
                anchorKinds: r.anchorKinds,
                values: {
                  name: r.node.name,
                  code: r.node.code,
                  ...Object.fromEntries(Object.entries(r.node.attrs).map(([k, v]) => [`attrs.${k}`, v])),
                },
              })),
            );
            this.loading.set(false);
          },
          error: () => this.loading.set(false),
        });
    } else {
      this.request = this.api.layerGrid(siteId, source.layer, this.filters(), this.sort, this.descending, this.first, this.pageSize).subscribe({
        next: (page) => {
          if (this.layerColumns().length === 0)
            this.layerColumns.set(
              page.fields.map((f) => ({ field: f.name, label: f.name, kind: f.kind === 'number' ? 'number' : f.kind === 'boolean' ? 'boolean' : 'text', editable: false })),
            );
          this.total.set(page.total ?? this.first + page.rows.length);
          this.rows.set(page.rows.map((r) => ({ key: r.featureId, nodeId: r.nodeId, node: null, linked: true, anchorKinds: [], values: r.properties })));
          this.loading.set(false);
        },
        error: () => {
          this.rows.set([]);
          this.loading.set(false);
        },
      });
    }
  }

  protected selectRow(row: Row): void {
    if (row.nodeId != null) this.selection.focusNode(row.nodeId);
  }

  /** Editors edit own data; on a MapManager-linked record only name and code (its data is read only). */
  protected canEdit(row: Row, column: GridColumn): boolean {
    return column.editable && this.can.prepare() && this.source()?.kind === 'type' && !(row.linked && column.field.startsWith('attrs.'));
  }

  /** Saves an edited cell; attributes go as the whole attrs object, the backend validates against the schema. */
  protected editComplete(event: TableEditCompleteEvent): void {
    const row = event.data as Row;
    const field = event.field as string;
    if (!row.node) return;
    const value = row.values[field] === '' ? null : row.values[field];
    let patch: Record<string, unknown>;
    if (field === 'name' || field === 'code') {
      if ((row.node as unknown as Record<string, unknown>)[field] === value) return;
      patch = { [field]: value };
    } else {
      if (row.linked) return;
      const key = field.slice('attrs.'.length);
      if (row.node.attrs[key] === value) return;
      const attrs = { ...row.node.attrs };
      if (value === null || value === undefined) delete attrs[key];
      else attrs[key] = value;
      patch = { attrs };
    }
    this.api.updateNode(row.node.id, patch).subscribe({
      next: () => {
        this.toasts.add({ severity: 'success', summary: this.m.common.saved, life: 2000 });
        this.selection.changed();
      },
      error: () => this.load(),
    });
  }

  protected anchorIcon(kind: string): string {
    return ANCHOR_ICONS[kind] ?? 'pi pi-circle';
  }

  protected format(value: unknown): string {
    return formatValue(value, this.m.common.yes, this.m.common.no);
  }
}
