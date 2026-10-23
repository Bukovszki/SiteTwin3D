import { schemaFields } from '../datasheet/schema-form';
import { autoMap, readCsv, readTable, toImportRows } from './table-reader';

const equipmentSchema = {
  type: 'object' as const,
  properties: {
    manufacturer: { type: 'string' as const, title: 'Gyártó' },
    model: { type: 'string' as const, title: 'Típus' },
    power_kw: { type: 'number' as const, title: 'Teljesítmény (kW)' },
    install_year: { type: 'integer' as const, title: 'Telepítés éve' },
  },
};

// Same layout as docs/samples/berendezesek.csv (Hungarian Excel export: ; separator, decimal comma, BOM).
const csv = '﻿Kód;Megnevezés;Gyártó;Típus;Teljesítmény (kW);Telepítés éve\r\n=P01;Befolyó szivattyú 1;Grundfos;SE1.80;7,5;2015\r\n=M01;Iszapszivattyú;Flygt;NP 3127;1 200,5;\r\n';

describe('table reader', () => {
  it('reads a Hungarian CSV with semicolons', () => {
    const rows = readCsv(csv);

    expect(rows[0]).toEqual(['Kód', 'Megnevezés', 'Gyártó', 'Típus', 'Teljesítmény (kW)', 'Telepítés éve']);
    expect(rows[1]).toEqual(['=P01', 'Befolyó szivattyú 1', 'Grundfos', 'SE1.80', '7,5', '2015']);
    expect(rows).toHaveLength(3);
  });

  it('maps columns by Hungarian headers and schema titles', () => {
    const [headers] = readCsv(csv) as string[][];

    expect(autoMap(headers, schemaFields(equipmentSchema))).toEqual([
      'code',
      'name',
      'attrs.manufacturer',
      'attrs.model',
      'attrs.power_kw',
      'attrs.install_year',
    ]);
  });

  it('leaves unknown columns unmapped and maps each target once', () => {
    expect(autoMap(['Név', 'Megjegyzés', 'Név'], [])).toEqual(['name', '', '']);
  });

  it('builds import rows; values stay as read (the backend converts by the schema)', () => {
    const [headers, ...rows] = readCsv(csv) as string[][];
    const targets = autoMap(headers, schemaFields(equipmentSchema));

    const result = toImportRows({ headers, rows }, targets);

    expect(result[0]).toEqual({
      code: '=P01',
      name: 'Befolyó szivattyú 1',
      attrs: { manufacturer: 'Grundfos', model: 'SE1.80', power_kw: '7,5', install_year: '2015' },
    });
    expect(result[1].attrs['power_kw']).toBe('1 200,5');
  });
});

describe('readTable', () => {
  it('reads a CSV file', async () => {
    const table = await readTable(new File([csv], 'berendezesek.csv', { type: 'text/csv' }));

    expect(table.headers[0]).toBe('Kód');
    expect(table.rows).toHaveLength(2);
  });
});
