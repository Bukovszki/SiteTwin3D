import { Component, computed, effect, inject, input, model, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { InputNumber } from 'primeng/inputnumber';
import { InputText } from 'primeng/inputtext';
import { Select } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { ApiService } from '../../core/api/api.service';
import { Floorplan } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';

/**
 * Floorplans of the current level (FR-AR1): references to a MapManager publication ("workspace:layer", optionally a
 * CQL filter selecting the level on a shared layer) or an external WMS. No files are uploaded here (spec 6.3).
 */
@Component({
  selector: 'app-floorplans-dialog',
  imports: [FormsModule, Dialog, Button, InputText, InputNumber, Select, TableModule],
  template: `
    <p-dialog [header]="m.levels.manage + ' – ' + levelName()" [(visible)]="visible" [modal]="true" [style]="{ width: '40rem' }">
      <small class="muted">{{ m.levels.hint }}</small>
      <p-table [value]="floorplans()" size="small" styleClass="list">
        <ng-template #header>
          <tr><th>{{ m.levels.name }}</th><th>{{ m.levels.layer }}</th><th>CQL</th><th></th></tr>
        </ng-template>
        <ng-template #body let-f>
          <tr>
            <td>{{ f.name }}</td>
            <td>{{ f.layer }}</td>
            <td>{{ f.cqlFilter }}</td>
            <td><p-button icon="pi pi-trash" severity="danger" [text]="true" size="small" [ariaLabel]="m.common.delete" (onClick)="remove(f)" /></td>
          </tr>
        </ng-template>
        <ng-template #emptymessage>
          <tr><td colspan="4" class="muted">{{ m.levels.noFloorplan }}</td></tr>
        </ng-template>
      </p-table>
      <form class="form-grid" (ngSubmit)="add()">
        <div class="row">
          <div class="field">
            <label for="fp-name">{{ m.levels.name }}</label>
            <input pInputText id="fp-name" name="name" [(ngModel)]="name" />
          </div>
          <div class="field">
            <label for="fp-source">{{ m.levels.source }}</label>
            <p-select inputId="fp-source" name="source" [options]="sources()" optionLabel="label" optionValue="value" [(ngModel)]="source" appendTo="body" fluid />
          </div>
        </div>
        <div class="row">
          <div class="field">
            <label for="fp-layer">{{ m.levels.layer }}</label>
            <input pInputText id="fp-layer" name="layer" [(ngModel)]="layer" />
          </div>
          <div class="field">
            <label for="fp-opacity">{{ m.levels.opacity }}</label>
            <p-inputnumber inputId="fp-opacity" name="opacity" [(ngModel)]="opacity" [min]="0" [max]="1" [step]="0.1" mode="decimal" [maxFractionDigits]="2" locale="hu-HU" fluid />
          </div>
        </div>
        <div class="field">
          <label for="fp-cql">{{ m.levels.cql }}</label>
          <input pInputText id="fp-cql" name="cql" [(ngModel)]="cql" />
        </div>
        @if (source === 'external_wms') {
          <div class="field">
            <label for="fp-url">{{ m.levels.url }}</label>
            <input pInputText id="fp-url" name="url" [(ngModel)]="url" />
          </div>
        }
        <div class="actions">
          <p-button type="submit" [label]="m.common.add" icon="pi pi-plus" [loading]="saving()" [disabled]="!name.trim() || !layer.trim()" />
        </div>
      </form>
    </p-dialog>
  `,
  styles: `
    :host ::ng-deep .list {
      margin: 0.75rem 0 1rem;
    }
    .actions {
      display: flex;
      justify-content: flex-end;
    }
  `,
})
export class FloorplansDialog {
  protected readonly m = inject(MESSAGES);
  private readonly api = inject(ApiService);
  private readonly selection = inject(SelectionService);

  readonly visible = model(false);
  readonly levelName = input('');

  protected readonly floorplans = signal<Floorplan[]>([]);
  protected readonly saving = signal(false);
  protected name = '';
  protected source: 'mapmanager' | 'external_wms' = 'mapmanager';
  protected layer = '';
  protected cql = '';
  protected url = '';
  protected opacity = 0.85;

  protected readonly sources = computed(() =>
    (['mapmanager', 'external_wms'] as const).map((value) => ({ value, label: this.m.mapLayers.sources[value] })),
  );

  private readonly loadOnOpen = effect(() => {
    if (this.visible()) untracked(() => this.load());
  });

  private load(): void {
    const level = this.selection.levelId();
    if (level != null) this.api.floorplans(level).subscribe((list) => this.floorplans.set(list));
  }

  protected add(): void {
    const level = this.selection.levelId();
    if (level == null) return;
    this.saving.set(true);
    this.api
      .createFloorplan(level, {
        name: this.name.trim(),
        source: this.source,
        layer: this.layer.trim(),
        cqlFilter: this.cql.trim() || null,
        externalUrl: this.source === 'external_wms' ? this.url.trim() : null,
        opacity: this.opacity,
        sortOrder: this.floorplans().length,
      })
      .subscribe({
        next: () => {
          this.saving.set(false);
          this.name = this.layer = this.cql = this.url = '';
          this.load();
          this.selection.changed();
        },
        error: () => this.saving.set(false),
      });
  }

  protected remove(f: Floorplan): void {
    this.api.deleteFloorplan(f.id).subscribe(() => {
      this.load();
      this.selection.changed();
    });
  }
}
