import { AttrSchema, JsonSchemaProperty } from '../../core/api/models';

/** The kinds of field an administrator can add (each maps to a JSON Schema property). */
export type FieldKind = 'text' | 'integer' | 'number' | 'boolean' | 'choice' | 'date';

/** A field as edited in the types dialog. */
export interface FieldDraft {
  /** Property name in the data (attrs); fixed once saved, so that the entered values stay with it. */
  key: string;
  title: string;
  kind: FieldKind;
  required: boolean;
  /** Choice list: the options, one per line. */
  options: string;
  min: number | null;
  max: number | null;
  /** Already in the saved schema (its key can no longer change). */
  saved: boolean;
}

export const FIELD_KINDS: FieldKind[] = ['text', 'integer', 'number', 'boolean', 'choice', 'date'];

const KEY = /^[a-z][a-z0-9_]{0,62}$/;

export function kindOf(property: JsonSchemaProperty): FieldKind {
  if (property.enum) return 'choice';
  if (property.format === 'date') return 'date';
  switch (property.type) {
    case 'integer':
    case 'number':
    case 'boolean':
      return property.type;
    default:
      return 'text';
  }
}

/** The fields of a schema, in order. */
export function toDrafts(schema: AttrSchema | null | undefined): FieldDraft[] {
  const required = new Set(schema?.required ?? []);
  return Object.entries(schema?.properties ?? {}).map(([key, p]) => ({
    key,
    title: p.title ?? key,
    kind: kindOf(p),
    required: required.has(key),
    options: (p.enum ?? []).join('\n'),
    min: p.minimum ?? null,
    max: p.maximum ?? null,
    saved: true,
  }));
}

/** The schema of the fields, in their order; null if there are none. */
export function toSchema(fields: FieldDraft[]): AttrSchema | null {
  if (fields.length === 0) return null;
  const properties: Record<string, JsonSchemaProperty> = {};
  for (const f of fields) {
    const title = f.title.trim();
    const property: JsonSchemaProperty = (() => {
      switch (f.kind) {
        case 'choice':
          return { type: 'string', title, enum: optionsOf(f) };
        case 'date':
          return { type: 'string', title, format: 'date' };
        case 'integer':
        case 'number':
          return {
            type: f.kind,
            title,
            ...(f.min != null ? { minimum: f.min } : {}),
            ...(f.max != null ? { maximum: f.max } : {}),
          };
        case 'boolean':
          return { type: 'boolean', title };
        default:
          return { type: 'string', title };
      }
    })();
    properties[f.key] = property;
  }
  const required = fields.filter((f) => f.required).map((f) => f.key);
  return { type: 'object', properties, ...(required.length ? { required } : {}) };
}

export function optionsOf(field: FieldDraft): string[] {
  return [
    ...new Set(
      field.options
        .split('\n')
        .map((o) => o.trim())
        .filter(Boolean),
    ),
  ];
}

/** A property name from a title: lowercase letters, digits and _, starting with a letter, not one of `taken`. */
export function keyFromTitle(title: string, taken: Iterable<string>): string {
  const used = new Set(taken);
  let key = title
    .toLocaleLowerCase('hu')
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .slice(0, 50);
  if (!/^[a-z]/.test(key)) key = `f_${key}`.replace(/_+$/, '');
  if (key === 'f') key = 'field';
  let unique = key;
  for (let i = 2; used.has(unique); i++) unique = `${key}_${i}`;
  return unique;
}

/** What is wrong with the fields (empty: they can be saved); `reserved` are names that may not be used. */
export function fieldErrors(fields: FieldDraft[], reserved: Iterable<string> = []): FieldError[] {
  const errors: FieldError[] = [];
  const seen = new Set(reserved);
  fields.forEach((f, index) => {
    if (!f.title.trim()) errors.push({ index, problem: 'title' });
    if (!KEY.test(f.key) || f.key === 'mapmanager_ref') errors.push({ index, problem: 'key' });
    else if (seen.has(f.key)) errors.push({ index, problem: 'duplicate' });
    seen.add(f.key);
    if (f.kind === 'choice' && optionsOf(f).length === 0)
      errors.push({ index, problem: 'options' });
    if (
      (f.kind === 'integer' || f.kind === 'number') &&
      f.min != null &&
      f.max != null &&
      f.min > f.max
    )
      errors.push({ index, problem: 'range' });
  });
  return errors;
}

export interface FieldError {
  index: number;
  problem: 'title' | 'key' | 'duplicate' | 'options' | 'range';
}
