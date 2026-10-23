import Papa from 'papaparse';
import { ImportRow } from '../../core/api/models';
import { SchemaField } from '../datasheet/schema-form';

/** A table read from a file: header row and data rows (cells as text or typed XLSX values). */
export interface Table {
  headers: string[];
  rows: unknown[][];
}

/** Column target: skipped, code, name or an attribute of the node type. */
export type Target = '' | 'code' | 'name' | `attrs.${string}`;

/** Reads CSV (delimiter detected: ; or , or tab; UTF-8) or XLSX (first sheet). */
export async function readTable(file: File): Promise<Table> {
  const rows = /\.xlsx$/i.test(file.name) ? await readXlsx(file) : readCsv(await file.text());
  const nonEmpty = rows.filter((r) => r.some((c) => c !== null && c !== undefined && String(c).trim() !== ''));
  const [header = [], ...data] = nonEmpty;
  return { headers: header.map((h) => String(h ?? '').trim()), rows: data };
}

export function readCsv(text: string): unknown[][] {
  const result = Papa.parse<string[]>(text.replace(/^﻿/, ''), { skipEmptyLines: 'greedy' });
  return result.data;
}

async function readXlsx(file: File): Promise<unknown[][]> {
  const { readSheet } = await import('read-excel-file/browser');
  return (await readSheet(file)) as unknown[][];
}

const normalize = (text: string) =>
  text
    .toLocaleLowerCase('hu')
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/\(.*?\)/g, '')
    .replace(/[^a-z0-9]/g, '');

/** Suggested target of each column: code / name by usual Hungarian and English headers, attributes by name or title. */
export function autoMap(headers: string[], fields: SchemaField[]): Target[] {
  const codes = new Set(['kod', 'code', 'azonosito', 'jelolés', 'jeloles', 'kks']);
  const names = new Set(['nev', 'megnevezes', 'name', 'elnevezes']);
  const used = new Set<string>();
  return headers.map((header) => {
    const h = normalize(header);
    const candidates: Target[] = [];
    if (codes.has(h)) candidates.push('code');
    if (names.has(h)) candidates.push('name');
    for (const f of fields) if (normalize(f.name) === h || normalize(f.label) === h) candidates.push(`attrs.${f.name}`);
    const target = candidates.find((c) => !used.has(c)) ?? '';
    if (target) used.add(target);
    return target;
  });
}

/** Rows for the API by the column targets; values go as read, the backend converts them by the schema. */
export function toImportRows(table: Table, targets: Target[]): ImportRow[] {
  return table.rows.map((cells) => {
    const row: ImportRow = { attrs: {} };
    targets.forEach((target, i) => {
      const value = cells[i];
      if (!target || value === undefined || value === null) return;
      if (target === 'code' || target === 'name') {
        const text = String(value).trim();
        if (text) row[target] = text;
      } else row.attrs[target.slice('attrs.'.length)] = value instanceof Date ? value.toISOString().slice(0, 10) : value;
    });
    return row;
  });
}
