import { Component, computed, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Checkbox } from 'primeng/checkbox';
import { InputNumber } from 'primeng/inputnumber';
import { InputText } from 'primeng/inputtext';
import { Select } from 'primeng/select';
import { AttrSchema, JsonSchemaProperty } from '../../core/api/models';

export interface SchemaField {
  name: string;
  label: string;
  property: JsonSchemaProperty;
  required: boolean;
}

/** The fields of a node type's JSON Schema, in schema order (form, grid columns). */
export function schemaFields(schema: AttrSchema | null | undefined): SchemaField[] {
  const required = new Set(schema?.required ?? []);
  return Object.entries(schema?.properties ?? {}).map(([name, property]) => ({
    name,
    label: property.title ?? name,
    property,
    required: required.has(name),
  }));
}

/**
 * Form generated from the node type's JSON Schema (FR-H5): a new field needs only a schema change. The backend
 * validates fully; here only the input kinds follow the schema (text, date, number, integer, boolean, enum).
 */
@Component({
  selector: 'app-schema-form',
  imports: [FormsModule, InputText, InputNumber, Checkbox, Select],
  template: `
    <div class="form-grid">
      @for (field of fields(); track field.name) {
        <div class="field">
          <label [for]="idPrefix() + field.name">{{ field.label }}@if (field.required) {<span class="req"> *</span>}</label>
          @if (field.property.enum) {
            <p-select [inputId]="idPrefix() + field.name" [options]="field.property.enum" [ngModel]="value()[field.name]"
              (ngModelChange)="set(field.name, $event)" [showClear]="!field.required" [disabled]="readonly()" fluid />
          } @else {
            @switch (field.property.type) {
              @case ('boolean') {
                <p-checkbox [inputId]="idPrefix() + field.name" [binary]="true" [ngModel]="value()[field.name] === true"
                  (ngModelChange)="set(field.name, $event)" [disabled]="readonly()" />
              }
              @case ('number') {
                <p-inputnumber [inputId]="idPrefix() + field.name" [ngModel]="value()[field.name]" (ngModelChange)="set(field.name, $event)"
                  mode="decimal" [maxFractionDigits]="6" locale="hu-HU" [min]="field.property.minimum ?? null" [max]="field.property.maximum ?? null"
                  [disabled]="readonly()" fluid />
              }
              @case ('integer') {
                <p-inputnumber [inputId]="idPrefix() + field.name" [ngModel]="value()[field.name]" (ngModelChange)="set(field.name, $event)"
                  [useGrouping]="false" [min]="field.property.minimum ?? null" [max]="field.property.maximum ?? null" [disabled]="readonly()" fluid />
              }
              @default {
                @if (field.property.format === 'date') {
                  <input pInputText type="date" [id]="idPrefix() + field.name" [ngModel]="value()[field.name] ?? ''"
                    (ngModelChange)="set(field.name, $event)" [disabled]="readonly()" />
                } @else {
                  <input pInputText [id]="idPrefix() + field.name" [ngModel]="value()[field.name] ?? ''" (ngModelChange)="set(field.name, $event)"
                    [disabled]="readonly()" />
                }
              }
            }
          }
        </div>
      }
    </div>
  `,
  styles: `
    .req {
      color: var(--p-red-500);
    }
  `,
})
export class SchemaForm {
  readonly schema = input<AttrSchema | null>(null);
  readonly readonly = input(false);
  readonly idPrefix = input('attr-');
  /** The technical data (attrs) edited in place; empty inputs remove the property. */
  readonly value = model<Record<string, unknown>>({});

  protected readonly fields = computed(() => schemaFields(this.schema()));

  protected set(name: string, raw: unknown): void {
    const next = { ...this.value() };
    if (raw === null || raw === undefined || raw === '') delete next[name];
    else next[name] = raw;
    this.value.set(next);
  }
}
