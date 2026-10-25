import { FieldDraft, fieldErrors, keyFromTitle, toDrafts, toSchema } from './type-fields';

const draft = (change: Partial<FieldDraft>): FieldDraft => ({
  key: 'f',
  title: 'F',
  kind: 'text',
  required: false,
  options: '',
  min: null,
  max: null,
  saved: false,
  ...change,
});

describe('type fields', () => {
  it('builds an ordered schema from the fields and reads it back', () => {
    const fields = [
      draft({ key: 'floor_area_m2', title: 'Alapterület (m²)', kind: 'number', required: true, min: 0 }),
      draft({ key: 'finish', title: 'Burkolat', kind: 'choice', options: 'beton\ncsempe\n\nepoxi\nbeton' }),
      draft({ key: 'inspected_on', title: 'Felülvizsgálat', kind: 'date' }),
      draft({ key: 'heated', title: 'Fűtött', kind: 'boolean' }),
    ];
    const schema = toSchema(fields)!;

    expect(Object.keys(schema.properties!)).toEqual(['floor_area_m2', 'finish', 'inspected_on', 'heated']);
    expect(schema.properties!['floor_area_m2']).toEqual({ type: 'number', title: 'Alapterület (m²)', minimum: 0 });
    expect(schema.properties!['finish'].enum).toEqual(['beton', 'csempe', 'epoxi']);
    expect(schema.properties!['inspected_on']).toEqual({ type: 'string', title: 'Felülvizsgálat', format: 'date' });
    expect(schema.required).toEqual(['floor_area_m2']);
    expect(toDrafts(schema).map((f) => [f.key, f.kind, f.required, f.saved])).toEqual([
      ['floor_area_m2', 'number', true, true],
      ['finish', 'choice', false, true],
      ['inspected_on', 'date', false, true],
      ['heated', 'boolean', false, true],
    ]);
    expect(toSchema([])).toBeNull();
  });

  it('makes property names from Hungarian titles, unique', () => {
    expect(keyFromTitle('Alapterület (m²)', [])).toBe('alapterulet_m');
    expect(keyFromTitle('Burkolat', ['burkolat'])).toBe('burkolat_2');
    expect(keyFromTitle('2. emelet', [])).toBe('f_2_emelet');
    expect(keyFromTitle('', [])).toBe('field');
  });

  it('reports missing titles, clashing names, empty choice lists and wrong ranges', () => {
    const errors = fieldErrors(
      [
        draft({ key: 'a', title: '' }),
        draft({ key: 'manufacturer', title: 'Gyártó' }),
        draft({ key: 'c', title: 'C', kind: 'choice', options: '  ' }),
        draft({ key: 'd', title: 'D', kind: 'integer', min: 5, max: 1 }),
        draft({ key: 'a', title: 'A' }),
      ],
      ['manufacturer'],
    );
    expect(errors).toEqual([
      { index: 0, problem: 'title' },
      { index: 1, problem: 'duplicate' },
      { index: 2, problem: 'options' },
      { index: 3, problem: 'range' },
      { index: 4, problem: 'duplicate' },
    ]);
  });
});
