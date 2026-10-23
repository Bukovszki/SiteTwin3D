import { Component, inject, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { InputNumber } from 'primeng/inputnumber';
import { InputText } from 'primeng/inputtext';
import { TableModule } from 'primeng/table';
import { ApiService } from '../../core/api/api.service';
import { GeoJsonPolygon, Site } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { MapToolsService } from '../map/map-tools.service';

/**
 * Sites of the organization (org admin): name, reference point (default view), base height (ellipsoidal metres;
 * levels are relative to it) and extent. Non-modal, so the reference point can be picked and the extent drawn on
 * the overview map; coordinates are WGS84.
 */
@Component({
  selector: 'app-sites-dialog',
  imports: [FormsModule, Dialog, Button, InputText, InputNumber, TableModule],
  template: `
    <p-dialog [header]="m.sites.title" [(visible)]="visible" [modal]="false" [style]="{ width: '30rem' }" position="right" (onHide)="tools.cancel()">
      <p-table [value]="selection.sites()" size="small" selectionMode="single" [selection]="editing()" (onRowSelect)="edit($any($event.data))" dataKey="id">
        <ng-template #body let-site>
          <tr [pSelectableRow]="site"><td>{{ site.name }}</td></tr>
        </ng-template>
      </p-table>
      <div class="list-actions">
        <p-button [label]="m.sites.new" icon="pi pi-plus" [text]="true" size="small" (onClick)="edit(null)" />
      </div>
      <form class="form-grid" (ngSubmit)="save()">
        <div class="field">
          <label for="site-name">{{ m.sites.name }}</label>
          <input pInputText id="site-name" name="name" [(ngModel)]="name" />
        </div>
        <div class="row">
          <div class="field">
            <label for="site-lon">{{ m.sites.lon }}</label>
            <p-inputnumber inputId="site-lon" name="lon" [(ngModel)]="lon" mode="decimal" [minFractionDigits]="6" [maxFractionDigits]="8" locale="hu-HU" [min]="-180" [max]="180" fluid />
          </div>
          <div class="field">
            <label for="site-lat">{{ m.sites.lat }}</label>
            <p-inputnumber inputId="site-lat" name="lat" [(ngModel)]="lat" mode="decimal" [minFractionDigits]="6" [maxFractionDigits]="8" locale="hu-HU" [min]="-90" [max]="90" fluid />
          </div>
        </div>
        <p-button [label]="m.sites.pickPoint" icon="pi pi-map-marker" severity="secondary" [outlined]="true" size="small" (onClick)="pick()" />
        <div class="field">
          <label for="site-height">{{ m.sites.baseHeight }}</label>
          <p-inputnumber inputId="site-height" name="height" [(ngModel)]="baseHeight" mode="decimal" [maxFractionDigits]="3" locale="hu-HU" fluid />
        </div>
        <div class="field">
          <label>{{ m.sites.extent }}: {{ extent ? m.sites.hasExtent : m.sites.noExtent }}</label>
          <div class="inline">
            <p-button [label]="m.sites.drawExtent" icon="pi pi-pencil" severity="secondary" [outlined]="true" size="small" (onClick)="drawExtent()" />
            @if (extent) {
              <p-button [label]="m.sites.clearExtent" icon="pi pi-times" severity="secondary" [text]="true" size="small" (onClick)="extent = null" />
            }
          </div>
        </div>
        <div class="actions">
          @if (editing(); as site) {
            <p-button [label]="m.common.delete" icon="pi pi-trash" severity="danger" [text]="true" (onClick)="remove(site)" />
          }
          <span class="spacer"></span>
          <p-button type="submit" [label]="m.common.save" icon="pi pi-save" [loading]="saving()" [disabled]="!name.trim() || lon == null || lat == null" />
        </div>
      </form>
    </p-dialog>
  `,
  styles: `
    .list-actions {
      margin: 0.25rem 0 0.75rem;
    }
    .inline,
    .actions {
      display: flex;
      gap: 0.5rem;
      align-items: center;
    }
    .spacer {
      flex: 1;
    }
  `,
})
export class SitesDialog {
  protected readonly m = inject(MESSAGES);
  protected readonly selection = inject(SelectionService);
  protected readonly tools = inject(MapToolsService);
  private readonly api = inject(ApiService);
  private readonly confirm = inject(ConfirmationService);
  private readonly toasts = inject(MessageService);

  readonly visible = model(false);

  protected readonly editing = signal<Site | null>(null);
  protected readonly saving = signal(false);
  protected name = '';
  protected lon: number | null = null;
  protected lat: number | null = null;
  protected baseHeight = 0;
  protected extent: GeoJsonPolygon | null = null;

  protected edit(site: Site | null): void {
    this.editing.set(site);
    this.name = site?.name ?? '';
    this.lon = site?.refPoint.coordinates[0] ?? null;
    this.lat = site?.refPoint.coordinates[1] ?? null;
    this.baseHeight = site?.baseHeightM ?? 0;
    this.extent = site?.extent ?? null;
    if (site) this.selection.selectSite(site.id);
  }

  protected pick(): void {
    this.tools.pickPoint(([lon, lat]) => {
      this.lon = Math.round(lon * 1e8) / 1e8;
      this.lat = Math.round(lat * 1e8) / 1e8;
    });
  }

  protected drawExtent(): void {
    this.tools.drawPolygon((ring) => (this.extent = { type: 'Polygon', coordinates: [ring] }));
  }

  protected save(): void {
    if (this.lon == null || this.lat == null) return;
    const body: Partial<Site> = {
      name: this.name.trim(),
      refPoint: { type: 'Point', coordinates: [this.lon, this.lat] },
      baseHeightM: this.baseHeight ?? 0,
      extent: this.extent,
    };
    const site = this.editing();
    this.saving.set(true);
    (site ? this.api.updateSite(site.id, body) : this.api.createSite(body)).subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.toasts.add({ severity: 'success', summary: this.m.common.saved, life: 2500 });
        this.selection.loadSites(saved.id);
        this.editing.set(saved);
      },
      error: () => this.saving.set(false),
    });
  }

  protected remove(site: Site): void {
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.sites.deleteConfirm(site.name),
      acceptButtonProps: { label: this.m.common.delete, severity: 'danger' },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () =>
        this.api.deleteSite(site.id).subscribe(() => {
          this.edit(null);
          this.selection.loadSites();
        }),
    });
  }
}
