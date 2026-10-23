import { Component, computed, inject, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
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
    <p-dialog [header]="m.mapLayers.title" [(visible)]="visible" [modal]="true" [style]="{ width: '40rem' }" (onShow)="load()">
      <small class="muted">{{ m.mapLayers.hint }}</small>
      <p-table [value]="layers()" size="small" styleClass="list">
        <ng-template #header>
          <tr><th>{{ m.mapLayers.name }}</th><th>{{ m.mapLayers.source }}</th><th>{{ m.mapLayers.layer }}</th><th>{{ m.mapLayers.scope }}</th><th></th></tr>
        </ng-template>
        <ng-template #body let-l>
          <tr>
            <td>{{ l.name }}</td>
            <td>{{ m.mapLayers.sources[l.source] }}</td>
            <td>{{ l.layer }}</td>
            <td>{{ l.siteId ? m.mapLayers.thisSite : m.mapLayers.allSites }}</td>
            <td><p-button icon="pi pi-trash" severity="danger" [text]="true" size="small" [ariaLabel]="m.common.delete" (onClick)="remove(l)" /></td>
          </tr>
        </ng-template>
      </p-table>
      <form class="form-grid" (ngSubmit)="add()">
        <div class="row">
          <div class="field">
            <label for="ml-name">{{ m.mapLayers.name }}</label>
            <input pInputText id="ml-name" name="name" [(ngModel)]="name" />
          </div>
          <div class="field">
            <label for="ml-source">{{ m.mapLayers.source }}</label>
            <p-select inputId="ml-source" name="source" [options]="sources()" optionLabel="label" optionValue="value" [(ngModel)]="source" appendTo="body" fluid />
          </div>
        </div>
        <div class="row">
          <div class="field">
            <label for="ml-layer">{{ m.mapLayers.layer }}</label>
            <input pInputText id="ml-layer" name="layer" [(ngModel)]="layer" />
          </div>
          <div class="field">
            <label for="ml-scope">{{ m.mapLayers.scope }}</label>
            <p-select inputId="ml-scope" name="scope" [options]="scopes()" optionLabel="label" optionValue="value" [(ngModel)]="siteOnly" appendTo="body" fluid />
          </div>
        </div>
        @if (source === 'external_wms') {
          <div class="field">
            <label for="ml-url">{{ m.mapLayers.url }}</label>
            <input pInputText id="ml-url" name="url" [(ngModel)]="url" placeholder="https://…/wms" />
          </div>
        }
        <label class="check"><p-checkbox [binary]="true" name="visible" [(ngModel)]="visibleDefault" /> {{ m.mapLayers.visible }}</label>
        <div class="actions">
          <p-button type="submit" [label]="m.common.add" icon="pi pi-plus" [loading]="saving()"
            [disabled]="!name.trim() || !layer.trim() || (source === 'external_wms' && !url.trim())" />
        </div>
      </form>
    </p-dialog>
  `,
  styles: `
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

  readonly visible = model(false);

  protected readonly layers = signal<MapLayer[]>([]);
  protected readonly saving = signal(false);
  protected name = '';
  protected source: 'mapmanager' | 'external_wms' = 'external_wms';
  protected layer = '';
  protected url = '';
  protected siteOnly = false;
  protected visibleDefault = true;

  protected readonly sources = computed(() =>
    (['external_wms', 'mapmanager'] as const).map((value) => ({ value, label: this.m.mapLayers.sources[value] })),
  );
  protected readonly scopes = computed(() => [
    { value: false, label: this.m.mapLayers.allSites },
    { value: true, label: this.m.mapLayers.thisSite },
  ]);

  protected load(): void {
    this.api.mapLayers(this.selection.siteId()).subscribe((layers) => this.layers.set(layers));
  }

  protected add(): void {
    this.saving.set(true);
    this.api
      .createMapLayer({
        name: this.name.trim(),
        source: this.source,
        layer: this.layer.trim(),
        externalUrl: this.source === 'external_wms' ? this.url.trim() : null,
        siteId: this.siteOnly ? this.selection.siteId() : null,
        visibleDefault: this.visibleDefault,
        sortOrder: this.layers().length,
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.name = this.layer = this.url = '';
          this.load();
          this.selection.changed();
        },
        error: () => this.saving.set(false),
      });
  }

  protected remove(layer: MapLayer): void {
    this.api.deleteMapLayer(layer.id).subscribe(() => {
      this.load();
      this.selection.changed();
    });
  }
}
