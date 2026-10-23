import { GridColumn, formatValue, parseFilter } from './grid-columns';

const text: GridColumn = { field: 'attrs.manufacturer', label: 'Gyártó', kind: 'text', editable: true };
const number: GridColumn = { field: 'attrs.power_kw', label: 'Teljesítmény', kind: 'number', editable: true };
const flag: GridColumn = { field: 'attrs.redundant', label: 'Tartalék', kind: 'boolean', editable: true };

describe('grid filters', () => {
  it('text filters are "contains"', () => {
    expect(parseFilter(text, ' grund ')).toEqual({ field: 'attrs.manufacturer', op: 'contains', value: 'grund' });
    expect(parseFilter(text, '')).toBeNull();
  });

  it('number filters accept comparison and Hungarian decimals', () => {
    expect(parseFilter(number, '>=5')).toEqual({ field: 'attrs.power_kw', op: 'gte', value: 5 });
    expect(parseFilter(number, '<= 10,5')).toEqual({ field: 'attrs.power_kw', op: 'lte', value: 10.5 });
    expect(parseFilter(number, '7,5')).toEqual({ field: 'attrs.power_kw', op: 'eq', value: 7.5 });
    expect(parseFilter(number, '1 200')).toEqual({ field: 'attrs.power_kw', op: 'eq', value: 1200 });
    expect(parseFilter(number, 'sok')).toBeNull();
  });

  it('boolean filters are equality', () => {
    expect(parseFilter(flag, false)).toEqual({ field: 'attrs.redundant', op: 'eq', value: false });
    expect(parseFilter(flag, null)).toBeNull();
  });

  it('formats values the Hungarian way', () => {
    expect(formatValue(1200.5, 'Igen', 'Nem')).toBe((1200.5).toLocaleString('hu-HU'));
    expect(formatValue(true, 'Igen', 'Nem')).toBe('Igen');
    expect(formatValue(null, 'Igen', 'Nem')).toBe('');
  });
});
