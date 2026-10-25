import { Component, computed, inject, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Checkbox } from 'primeng/checkbox';
import { Dialog } from 'primeng/dialog';
import { InputText } from 'primeng/inputtext';
import { Select } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { ApiService } from '../../core/api/api.service';
import { MapLayer } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';

/**
 * Background layers (org admin): references to MapManager layers or external WMS layers, for every site or only the
 * current one (spec 4.3). Nothing is uploaded; the layers load through the backend WMS proxy.
 */
@Component({
  selector: 'app-map-layers-dialog',
  imports: [FormsModule, Dialog, Button, InputText, Select, Checkbox, TableModule],
  template: `
    <p-dialog
      [header]="m.mapLayers.title"
      [(visible)]="visible"
      [modal]="true"
      [style]="{ width: '46rem' }"
      (onShow)="load()"
    >
      <small class="muted">{{ m.mapLayers.hint }}</small>
      <p-table [value]="layers()" size="small" styleClass="list">
        <ng-template #header>
          <tr>
            <th>{{ m.mapLayers.name }}</th>
            <th>{{ m.mapLayers.source }}</th>
            <th>{{ m.mapLayers.layer }}</th>
            <th>{{ m.mapLayers.scope }}</th>
            <th></th>
          </tr>
        </ng-template>
        <ng-template #body let-l>
          <tr>
            <td>{{ l.name }}</td>
            <td>{{ m.mapLayers.sources[l.source] }}</td>
            <td>{{ l.layer }}</td>
            <td>{{ l.siteId ? m.mapLayers.thisSite : m.mapLayers.allSites }}</td>
            <td class="row-actions">
              <p-button
                icon="pi pi-pencil"
                [text]="true"
                size="small"
                [ariaLabel]="m.mapLayers.edit"
                (onClick)="edit(l)"
              />
              <p-button
                icon="pi pi-trash"
                severity="danger"
                [text]="true"
                size="small"
                [ariaLabel]="m.common.delete"
                (onClick)="remove(l)"
              />
            </td>
          </tr>
        </ng-template>
      </p-table>
      <h4>{{ editing() ? m.mapLayers.edit + ': ' + editing()!.name : m.mapLayers.newLayer }}</h4>
      <form class="form-grid" (ngSubmit)="save()">
        <div class="row">
          <div class="field">
            <label for="ml-name">{{ m.mapLayers.name }}</label>
            <input pInputText id="ml-name" name="name" [(ngModel)]="name" />
          </div>
          <div class="field">
            <label for="ml-source">{{ m.mapLayers.source }}</label>
            <p-select
              inputId="ml-source"
              name="source"
              [options]="sources()"
              optionLabel="label"
              optionValue="value"
              [(ngModel)]="source"
              appendTo="body"
              fluid
              [disabled]="!!editing()"
            />
          </div>
        </div>
        <div class="row">
          <div class="field">
            <label for="ml-layer">{{ m.mapLayers.layer }}</label>
            <input pInputText id="ml-layer" name="layer" [(ngModel)]="layer" />
          </div>
          <div class="field">
            <label for="ml-scope">{{ m.mapLayers.scope }}</label>
            <p-select
              inputId="ml-scope"
              name="scope"
              [options]="scopes()"
              optionLabel="label"
              optionValue="value"
              [(ngModel)]="siteOnly"
              appendTo="body"
              fluid
              [disabled]="!!editing()"
            />
          </div>
        </div>
        @if (source === 'external_wms') {
          <div class="field">
            <label for="ml-url">{{ m.mapLayers.url }}</label>
            <input
              pInputText
              id="ml-url"
              name="url"
              [(ngModel)]="url"
              placeholder="https://…/wms"
            />
          </div>
        }
        <div class="field">
          <label for="ml-crs">{{ m.mapLayers.crs }}</label>
          <p-select
            inputId="ml-crs"
            name="crs"
            [options]="m.mapLayers.crsOptions"
            optionLabel="label"
            optionValue="value"
            [(ngModel)]="crs"
            appendTo="body"
            fluid
          />
          <small class="muted">{{ m.mapLayers.crsHint }}</small>
        </div>
        <label class="check"
          ><p-checkbox [binary]="true" name="visible" [(ngModel)]="visibleDefault" />
          {{ m.mapLayers.visible }}</label
        >
        @if (editing()) {
          <small class="muted">{{ m.mapLayers.sourceFixed }}</small>
        }
        <div class="actions">
          @if (editing()) {
            <p-button
              [label]="m.mapLayers.cancelEdit"
              severity="secondary"
              [text]="true"
              (onClick)="reset()"
            />
          }
          <p-button
            type="submit"
            [label]="editing() ? m.common.save : m.common.add"
            [icon]="editing() ? 'pi pi-save' : 'pi pi-plus'"
            [loading]="saving()"
            [disabled]="!name.trim() || !layer.trim() || (source === 'external_wms' && !url.trim())"
          />
        </div>
      </form>
    </p-dialog>
  `,
  styles: `
    .row-actions {
      white-space: nowrap;
      text-align: right;
    }
    h4 {
      margin: 0 0 0.5rem;
    }
    :host ::ng-deep .list {
      margin: 0.75rem 0 1rem;
    }
    .check {
      display: flex;
      align-items: center;
      gap: 0.4rem;
    }
    .actions {
      display: flex;
      justify-content: flex-end;
    }
  `,
})
export class MapLayersDialog {
  protected readonly m = inject(MESSAGES);
  private readonly api = inject(ApiService);
  private readonly selection = inject(SelectionService);
  private readonly toasts = inject(MessageService);

  readonly visible = model(false);

  protected readonly layers = signal<MapLayer[]>([]);
  protected readonly saving = signal(false);
  protected name = '';
  protected source: 'mapmanager' | 'external_wms' = 'external_wms';
  protected layer = '';
  protected url = '';
  protected siteOnly = false;
  protected visibleDefault = true;
  /** The coordinate system the layer is requested in (wmsParams.CRS; the map's own when EPSG:3857). */
  protected crs = 'EPSG:3857';
  /** The layer being edited (null: a new one). */
  protected readonly editing = signal<MapLayer | null>(null);

  protected readonly sources = computed(() =>
    (['external_wms', 'mapmanager'] as const).map((value) => ({
      value,
      label: this.m.mapLayers.sources[value],
    })),
  );
  protected readonly scopes = computed(() => [
    { value: false, label: this.m.mapLayers.allSites },
    { value: true, label: this.m.mapLayers.thisSite },
  ]);

  protected load(): void {
    this.api.mapLayers(this.selection.siteId()).subscribe((layers) => this.layers.set(layers));
  }

  protected edit(layer: MapLayer): void {
    this.editing.set(layer);
    this.name = layer.name;
    this.source = layer.source;
    this.layer = layer.layer;
    this.url = layer.externalUrl ?? '';
    this.siteOnly = layer.siteId != null;
    this.visibleDefault = layer.visibleDefault;
    this.crs = String(layer.wmsParams?.['CRS'] ?? 'EPSG:3857').toUpperCase();
  }

  protected reset(): void {
    this.editing.set(null);
    this.name = this.layer = this.url = '';
    this.source = 'external_wms';
    this.siteOnly = false;
    this.visibleDefault = true;
    this.crs = 'EPSG:3857';
  }

  /** The layer's WMS parameters with the chosen coordinate system (none: the map's own). */
  private params(previous: Record<string, unknown> = {}): Record<string, unknown> {
    const { CRS: _crs, SRS: _srs, ...rest } = previous;
    return this.crs === 'EPSG:3857' ? rest : { ...rest, CRS: this.crs };
  }

  protected save(): void {
    const editing = this.editing();
    this.saving.set(true);
    const request = editing
      ? this.api.updateMapLayer(editing.id, {
          name: this.name.trim(),
          layer: this.layer.trim(),
          ...(editing.source === 'external_wms' ? { externalUrl: this.url.trim() } : {}),
          visibleDefault: this.visibleDefault,
          wmsParams: this.params(editing.wmsParams),
        })
      : this.api.createMapLayer({
          name: this.name.trim(),
          source: this.source,
          layer: this.layer.trim(),
          externalUrl: this.source === 'external_wms' ? this.url.trim() : null,
          siteId: this.siteOnly ? this.selection.siteId() : null,
          visibleDefault: this.visibleDefault,
          sortOrder: this.layers().length,
          wmsParams: this.params(),
        });
    request.subscribe({
      next: () => {
        this.saving.set(false);
        if (editing)
          this.toasts.add({ severity: 'success', summary: this.m.mapLayers.saved, life: 2500 });
        this.reset();
        this.load();
        this.selection.changed();
      },
      error: () => this.saving.set(false),
    });
  }

  protected remove(layer: MapLayer): void {
    this.api.deleteMapLayer(layer.id).subscribe(() => {
      if (this.editing()?.id === layer.id) this.reset();
      this.load();
      this.selection.changed();
    });
  }
}
