import { HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import OlMap from 'ol/Map';
import Feature from 'ol/Feature';
import GeoJSON from 'ol/format/GeoJSON';
import { Geometry } from 'ol/geom';
import { Draw, Modify, Select, Snap } from 'ol/interaction';
import VectorLayer from 'ol/layer/Vector';
import VectorSource from 'ol/source/Vector';
import { Circle, Fill, Stroke, Style } from 'ol/style';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Checkbox } from 'primeng/checkbox';
import { InputNumber } from 'primeng/inputnumber';
import { InputText } from 'primeng/inputtext';
import { Message } from 'primeng/message';
import { Tooltip } from 'primeng/tooltip';
import { ApiService } from '../../core/api/api.service';
import { errorCode, errorMessage, silent } from '../../core/api/errors';
import { DrawError, DrawLayer, DrawTransaction, GeoJsonGeometry } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { DrawLayerDialog } from './draw-layer-dialog';

/** All elements of the drawing layers travel in EPSG:3857 (the map's projection); the backend stores WGS84. */
const format = new GeoJSON({ dataProjection: 'EPSG:3857', featureProjection: 'EPSG:3857' });

interface Shown {
  layer: DrawLayer;
  source: VectorSource<Feature<Geometry>>;
  olLayer: VectorLayer<VectorSource<Feature<Geometry>>>;
}

/**
 * Drawing layers of the current level (spec 8.6, FR-AR6–7): shown over the floorplan; editors draw, modify and
 * delete elements with snapping to existing ones and edit their attributes; changes are saved in one transaction
 * (all or nothing, every error listed; a stale version reloads the layer).
 */
@Component({
  selector: 'app-draw-panel',
  imports: [FormsModule, Button, Checkbox, InputText, InputNumber, Message, Tooltip, DrawLayerDialog],
  template: `
    <div class="panel">
      <div class="head">
        <b>{{ m.drawing.layers }}</b>
        @if (can.prepare() && !editing()) {
          <p-button icon="pi pi-plus" [text]="true" size="small" [pTooltip]="m.drawing.newLayer" (onClick)="creating.set(true)" />
        }
      </div>
      @for (s of shown(); track s.layer.id) {
        <div class="layer">
          <p-checkbox [binary]="true" [ngModel]="s.olLayer.getVisible()" (ngModelChange)="s.olLayer.setVisible($event)" />
          <span class="swatch" [style.background]="color(s.layer)"></span>
          <span class="name">{{ s.layer.name }}</span>
          @if (can.prepare() && !editing()) {
            <p-button icon="pi pi-pencil" [text]="true" size="small" [pTooltip]="m.drawing.edit" (onClick)="startEdit(s)" />
            <p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" (onClick)="removeLayer(s.layer)" />
          }
        </div>
      } @empty {
        <small class="muted">{{ m.drawing.none }}</small>
      }

      @if (editing(); as e) {
        <div class="tools">
          <p-button icon="pi pi-pencil" [label]="m.drawing.draw" size="small" [outlined]="mode() !== 'draw'" (onClick)="setMode('draw')" />
          <p-button icon="pi pi-arrows-alt" [label]="m.drawing.modify" size="small" [outlined]="mode() !== 'modify'" (onClick)="setMode('modify')" />
          <p-button icon="pi pi-trash" size="small" severity="danger" [outlined]="true" [pTooltip]="m.drawing.remove" [disabled]="!selected()" (onClick)="deleteSelected()" />
        </div>
        @if (selected(); as f) {
          <div class="attrs form-grid">
            <small class="muted">{{ m.drawing.attributes }}</small>
            @for (field of e.layer.fields; track field.name) {
              <div class="field">
                <label>{{ field.alias || field.name }}</label>
                @switch (field.type) {
                  @case ('boolean') {
                    <p-checkbox [binary]="true" [ngModel]="attr(f, field.name) === true" (ngModelChange)="setAttr(f, field.name, $event)" />
                  }
                  @case ('integer') {
                    <p-inputnumber [ngModel]="attr(f, field.name)" (ngModelChange)="setAttr(f, field.name, $event)" [useGrouping]="false" size="small" fluid />
                  }
                  @case ('number') {
                    <p-inputnumber [ngModel]="attr(f, field.name)" (ngModelChange)="setAttr(f, field.name, $event)" mode="decimal" [maxFractionDigits]="6" locale="hu-HU" size="small" fluid />
                  }
                  @default {
                    <input pInputText [ngModel]="attr(f, field.name) ?? ''" (ngModelChange)="setAttr(f, field.name, $event)" [placeholder]="field.type === 'date' ? 'ÉÉÉÉ-HH-NN' : ''" />
                  }
                }
              </div>
            }
          </div>
        } @else if (mode() === 'modify') {
          <small class="muted">{{ m.drawing.selectHint }}</small>
        }
        @if (errors().length > 0) {
          <p-message severity="error" size="small">
            {{ m.drawing.errors }}:
            @for (e of errors(); track $index) {
              <div>#{{ e.index + 1 }} {{ e.field ?? '' }} – {{ m.errors[e.code] ?? e.message }}</div>
            }
          </p-message>
        }
        <div class="save">
          <small class="muted">{{ m.drawing.unsaved(changes()) }}</small>
          <p-button [label]="m.drawing.save" icon="pi pi-save" size="small" [loading]="saving()" [disabled]="changes() === 0" (onClick)="save()" />
          <p-button [label]="m.drawing.discard" size="small" severity="secondary" [text]="true" (onClick)="reload(e)" />
          <p-button [label]="m.drawing.stop" size="small" severity="secondary" [text]="true" (onClick)="stopEdit()" />
        </div>
      }
    </div>
    @if (can.prepare()) {
      @defer {
        <app-draw-layer-dialog [(visible)]="creating" [siteId]="selection.siteId()" [levelId]="selection.levelId()" (created)="selection.changed()" />
      }
    }
  `,
  styles: `
    .panel {
      display: flex;
      flex-direction: column;
      gap: 0.35rem;
      width: 18rem;
      max-height: 60vh;
      overflow: auto;
      padding: 0.5rem 0.75rem;
      background: var(--p-content-background);
      border: 1px solid var(--p-content-border-color);
      border-radius: 8px;
      box-shadow: 0 2px 8px rgb(0 0 0 / 0.12);
      font-size: 0.85rem;
    }
    .head,
    .layer,
    .tools,
    .save {
      display: flex;
      align-items: center;
      gap: 0.35rem;
    }
    .head {
      justify-content: space-between;
    }
    .name {
      flex: 1;
    }
    .swatch {
      width: 0.8rem;
      height: 0.8rem;
      border-radius: 2px;
    }
    .tools,
    .save {
      flex-wrap: wrap;
      padding-top: 0.35rem;
      border-top: 1px solid var(--p-content-border-color);
    }
    .attrs {
      gap: 0.4rem;
    }
  `,
})
export class DrawPanel {
  protected readonly m = inject(MESSAGES);
  protected readonly auth = inject(AuthService);
  protected readonly can = inject(Permissions);
  protected readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  private readonly toasts = inject(MessageService);
  private readonly confirm = inject(ConfirmationService);

  readonly map = input<OlMap | null>(null);

  protected readonly shown = signal<Shown[]>([]);
  protected readonly editing = signal<Shown | null>(null);
  protected readonly mode = signal<'draw' | 'modify'>('draw');
  protected readonly selected = signal<Feature<Geometry> | null>(null);
  protected readonly errors = signal<DrawError[]>([]);
  protected readonly saving = signal(false);
  protected readonly creating = signal(false);
  /** Changed elements: new (no id) or modified; deleted ones with their version. */
  private readonly changed = new Set<Feature<Geometry>>();
  private deleted: { id: number; version: number }[] = [];
  protected readonly changes = signal(0);
  private interactions: (Draw | Modify | Select | Snap)[] = [];

  constructor() {
    effect(() => {
      const map = this.map();
      const siteId = this.selection.siteId();
      const levelId = this.selection.levelId();
      this.selection.version();
      untracked(() => {
        if (this.editing()) return; // do not drop unsaved edits; reloaded after the edit ends
        this.clear();
        if (map && siteId != null) this.api.drawLayers(siteId, levelId).subscribe((layers) => this.show(map, layers));
      });
    });
    inject(DestroyRef).onDestroy(() => {
      this.stopEdit();
      this.clear();
    });
  }

  protected color(layer: DrawLayer): string {
    return layer.style?.color ?? '#7c3aed';
  }

  private show(map: OlMap, layers: DrawLayer[]): void {
    const shown = layers.map((layer, i) => {
      const source = new VectorSource<Feature<Geometry>>();
      const olLayer = new VectorLayer({ source, zIndex: 90 + i, style: this.style(this.color(layer)) });
      map.addLayer(olLayer);
      const s: Shown = { layer, source, olLayer };
      this.load(s);
      return s;
    });
    this.shown.set(shown);
  }

  private clear(): void {
    this.shown().forEach((s) => this.map()?.removeLayer(s.olLayer));
    this.shown.set([]);
  }

  /** Features with their record id, version and attributes (foreign members of the API's GeoJSON). */
  private load(s: Shown): void {
    this.api.drawFeatures(s.layer.id).subscribe((collection) => {
      s.source.clear();
      for (const f of (collection as { features: { id: number; version: number; properties: Record<string, unknown> }[] }).features) {
        const feature = format.readFeature(f) as Feature<Geometry>;
        feature.setId(f.id);
        feature.set('version', f.version);
        feature.set('attrs', { ...(f.properties ?? {}) });
        s.source.addFeature(feature);
      }
    });
  }

  private style(color: string, selected = false): Style {
    const stroke = new Stroke({ color: selected ? '#facc15' : color, width: selected ? 3 : 2 });
    return new Style({
      stroke,
      fill: new Fill({ color: `${color}33` }),
      image: new Circle({ radius: selected ? 7 : 5, fill: new Fill({ color }), stroke: new Stroke({ color: '#fff', width: 2 }) }),
    });
  }

  protected startEdit(s: Shown): void {
    this.editing.set(s);
    this.errors.set([]);
    this.setMode('draw');
  }

  protected stopEdit(): void {
    this.removeInteractions();
    const wasEditing = this.editing();
    this.editing.set(null);
    this.selected.set(null);
    this.resetChanges();
    if (wasEditing) this.selection.changed();
  }

  protected reload(s: Shown): void {
    this.resetChanges();
    this.selected.set(null);
    this.errors.set([]);
    this.load(s);
  }

  protected setMode(mode: 'draw' | 'modify'): void {
    const s = this.editing();
    const map = this.map();
    if (!s || !map) return;
    this.removeInteractions();
    this.mode.set(mode);
    this.selected.set(null);
    if (mode === 'draw') {
      const type = s.layer.geometryType === 'MultiPolygon' ? 'Polygon' : s.layer.geometryType === 'MultiLineString' ? 'LineString' : 'Point';
      const draw = new Draw({ source: s.source, type });
      draw.on('drawend', (e) => {
        e.feature.set('attrs', {});
        this.markChanged(e.feature as Feature<Geometry>);
        // Attributes of the new element right away.
        setTimeout(() => this.selectFeature(e.feature as Feature<Geometry>));
      });
      this.interactions = [draw];
    } else {
      const select = new Select({ layers: [s.olLayer], style: this.style(this.color(s.layer), true) });
      select.on('select', (e) => this.selected.set((e.selected[0] as Feature<Geometry>) ?? null));
      const modify = new Modify({ features: select.getFeatures() });
      modify.on('modifyend', (e) => e.features.forEach((f) => this.markChanged(f as Feature<Geometry>)));
      this.interactions = [select, modify];
    }
    // Snapping to the existing elements of the layer (FR-AR6, pixel tolerance on screen).
    this.interactions.push(new Snap({ source: s.source, pixelTolerance: 10 }));
    this.interactions.forEach((i) => map.addInteraction(i));
  }

  private selectFeature(feature: Feature<Geometry>): void {
    this.setMode('modify');
    const select = this.interactions.find((i): i is Select => i instanceof Select);
    select?.getFeatures().push(feature);
    this.selected.set(feature);
  }

  private removeInteractions(): void {
    this.interactions.forEach((i) => this.map()?.removeInteraction(i));
    this.interactions = [];
  }

  protected attr(feature: Feature<Geometry>, name: string): unknown {
    return (feature.get('attrs') as Record<string, unknown>)[name];
  }

  protected setAttr(feature: Feature<Geometry>, name: string, value: unknown): void {
    feature.set('attrs', { ...(feature.get('attrs') as Record<string, unknown>), [name]: value === '' ? null : value });
    this.markChanged(feature);
  }

  protected deleteSelected(): void {
    const s = this.editing();
    const feature = this.selected();
    if (!s || !feature) return;
    const id = feature.getId() as number | undefined;
    if (id != null) this.deleted.push({ id, version: feature.get('version') as number });
    this.changed.delete(feature);
    s.source.removeFeature(feature);
    this.interactions.find((i): i is Select => i instanceof Select)?.getFeatures().clear();
    this.selected.set(null);
    this.changes.set(this.changed.size + this.deleted.length);
  }

  private markChanged(feature: Feature<Geometry>): void {
    this.changed.add(feature);
    this.changes.set(this.changed.size + this.deleted.length);
  }

  private resetChanges(): void {
    this.changed.clear();
    this.deleted = [];
    this.changes.set(0);
  }

  protected save(): void {
    const s = this.editing();
    if (!s) return;
    const geometry = (f: Feature<Geometry>) => format.writeGeometryObject(f.getGeometry()!) as unknown as GeoJsonGeometry;
    const changed = [...this.changed];
    const inserts = changed.filter((f) => f.getId() == null);
    const tx: DrawTransaction = {
      inserts: inserts.map((f) => ({ geometry: geometry(f), attrs: f.get('attrs') ?? {} })),
      updates: changed
        .filter((f) => f.getId() != null)
        .map((f) => ({ id: f.getId() as number, version: f.get('version') as number, geometry: geometry(f), attrs: f.get('attrs') ?? {} })),
      deletes: this.deleted,
    };
    this.saving.set(true);
    this.errors.set([]);
    this.api.drawTransaction(s.layer.id, tx, silent()).subscribe({
      next: () => {
        this.saving.set(false);
        this.toasts.add({ severity: 'success', summary: this.m.drawing.saved, life: 2500 });
        this.reload(s);
      },
      error: (err: unknown) => {
        this.saving.set(false);
        if (errorCode(err) === 'draw.version_conflict') {
          this.toasts.add({ severity: 'warn', summary: this.m.drawing.conflict, life: 6000 });
          this.reload(s);
        } else if (err instanceof HttpErrorResponse && Array.isArray(err.error?.errors)) this.errors.set(err.error.errors as DrawError[]);
        else this.toasts.add({ severity: 'error', summary: errorMessage(err, this.m), life: 6000 });
      },
    });
  }

  protected removeLayer(layer: DrawLayer): void {
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.drawing.deleteLayerConfirm(layer.name),
      acceptButtonProps: { label: this.m.common.delete, severity: 'danger' },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () => this.api.deleteDrawLayer(layer.id).subscribe(() => this.selection.changed()),
    });
  }
}
