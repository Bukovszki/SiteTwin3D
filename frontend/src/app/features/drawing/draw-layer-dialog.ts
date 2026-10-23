import { Component, computed, effect, inject, input, model, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { ColorPicker } from 'primeng/colorpicker';
import { Dialog } from 'primeng/dialog';
import { InputText } from 'primeng/inputtext';
import { Select } from 'primeng/select';
import { ApiService } from '../../core/api/api.service';
import { DrawField, DrawLayer } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';

/** New drawing layer (spec 8.6): one geometry type and its fields (like MapManager's adatstruktura). */
@Component({
  selector: 'app-draw-layer-dialog',
  imports: [FormsModule, Dialog, Button, InputText, Select, ColorPicker],
  template: `
    <p-dialog [header]="m.drawing.newLayer" [(visible)]="visible" [modal]="true" [style]="{ width: '34rem' }">
      <div class="form-grid">
        <div class="row">
          <div class="field">
            <label for="dl-name">{{ m.drawing.name }}</label>
            <input pInputText id="dl-name" [(ngModel)]="name" />
          </div>
          <div class="field">
            <label for="dl-type">{{ m.drawing.geometryType }}</label>
            <p-select inputId="dl-type" [options]="types()" optionLabel="label" optionValue="value" [(ngModel)]="geometryType" appendTo="body" fluid />
          </div>
        </div>
        <div class="field">
          <label>{{ m.drawing.color }}</label>
          <p-colorpicker [(ngModel)]="color" appendTo="body" />
        </div>
        <div class="field">
          <label>{{ m.drawing.fields }}</label>
          @for (f of fields(); track $index) {
            <div class="field-row">
              <input pInputText [placeholder]="m.drawing.fieldName" [(ngModel)]="f.name" class="grow" />
              <p-select [options]="fieldTypes()" optionLabel="label" optionValue="value" [(ngModel)]="f.type" appendTo="body" size="small" />
              <input pInputText [placeholder]="m.drawing.alias" [(ngModel)]="f.alias" class="grow" />
              <p-button icon="pi pi-times" [text]="true" severity="secondary" size="small" (onClick)="removeField($index)" />
            </div>
          }
          <p-button [label]="m.drawing.addField" icon="pi pi-plus" [text]="true" size="small" (onClick)="addField()" />
        </div>
      </div>
      <ng-template #footer>
        <p-button [label]="m.common.cancel" severity="secondary" [text]="true" (onClick)="visible.set(false)" />
        <p-button [label]="m.common.save" icon="pi pi-check" [loading]="saving()" [disabled]="!name.trim()" (onClick)="save()" />
      </ng-template>
    </p-dialog>
  `,
  styles: `
    .field-row {
      display: flex;
      gap: 0.35rem;
      align-items: center;
      margin-bottom: 0.35rem;
      .grow {
        flex: 1;
        min-width: 0;
      }
    }
  `,
})
export class DrawLayerDialog {
  protected readonly m = inject(MESSAGES);
  private readonly api = inject(ApiService);

  readonly visible = model(false);
  readonly siteId = input<number | null>(null);
  readonly levelId = input<number | null>(null);
  readonly created = output<DrawLayer>();

  protected name = '';
  protected geometryType: DrawLayer['geometryType'] = 'MultiPolygon';
  protected color = '7c3aed';
  protected readonly fields = signal<DrawField[]>([]);
  protected readonly saving = signal(false);

  protected readonly types = computed(() =>
    (['MultiPolygon', 'MultiLineString', 'Point'] as const).map((value) => ({ value, label: this.m.drawing.types[value] })),
  );
  protected readonly fieldTypes = computed(() =>
    (['text', 'integer', 'number', 'boolean', 'date'] as const).map((value) => ({ value, label: this.m.drawing.fieldTypes[value] })),
  );

  private readonly resetOnOpen = effect(() => {
    if (this.visible())
      untracked(() => {
        this.name = '';
        this.geometryType = 'MultiPolygon';
        this.fields.set([{ name: 'megjegyzes', type: 'text', alias: 'Megjegyzés' }]);
      });
  });

  protected addField(): void {
    this.fields.update((f) => [...f, { name: '', type: 'text', alias: '' }]);
  }

  protected removeField(index: number): void {
    this.fields.update((f) => f.filter((_, i) => i !== index));
  }

  protected save(): void {
    const siteId = this.siteId();
    if (siteId == null) return;
    this.saving.set(true);
    this.api
      .createDrawLayer({
        siteId,
        levelId: this.levelId(),
        name: this.name.trim(),
        geometryType: this.geometryType,
        fields: this.fields().filter((f) => f.name.trim()).map((f) => ({ ...f, name: f.name.trim(), alias: f.alias?.trim() || null })),
        style: { color: `#${this.color.replace('#', '')}` },
      })
      .subscribe({
        next: (layer) => {
          this.saving.set(false);
          this.visible.set(false);
          this.created.emit(layer);
        },
        error: () => this.saving.set(false),
      });
  }
}
