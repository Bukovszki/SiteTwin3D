import { FilterOp, GridFilter } from '../../core/api/models';

export type ColumnKind = 'text' | 'number' | 'integer' | 'boolean';

export interface GridColumn {
  /** API field: name, code, attrs.<x> (own data) or the layer's field name (MapManager). */
  field: string;
  label: string;
  kind: ColumnKind;
  editable: boolean;
}

/**
 * The filter typed into a column's filter cell → API filter. Text: contains; number: "7,5" (equals),
 * ">=5", "<=10", ">5"/"<10" (treated as inclusive); boolean: true/false from the select.
 */
export function parseFilter(column: GridColumn, raw: unknown): GridFilter | null {
  if (raw === null || raw === undefined || raw === '') return null;
  if (column.kind === 'boolean') return typeof raw === 'boolean' ? { field: column.field, op: 'eq', value: raw } : null;
  const text = String(raw).trim();
  if (text.length === 0) return null;
  if (column.kind === 'text') return { field: column.field, op: 'contains', value: text };

  const match = /^(>=|<=|>|<|=)?\s*(-?[\d\s.,]+)$/.exec(text);
  if (!match) return null;
  const value = Number(match[2].replace(/\s/g, '').replace(',', '.'));
  if (Number.isNaN(value)) return null;
  const op: FilterOp = match[1]?.startsWith('>') ? 'gte' : match[1]?.startsWith('<') ? 'lte' : 'eq';
  return { field: column.field, op, value };
}

/** Display of a cell value (Hungarian number format). */
export function formatValue(value: unknown, yes: string, no: string): string {
  if (value === null || value === undefined || value === '') return '';
  if (typeof value === 'boolean') return value ? yes : no;
  if (typeof value === 'number') return value.toLocaleString('hu-HU', { maximumFractionDigits: 6 });
  return String(value);
}
