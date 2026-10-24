import { autoMapBoxes, toBoxRows } from './box-import-dialog';

describe('box import', () => {
  it('maps the template headers and usual variants', () => {
    expect(
      autoMapBoxes([
        'Rekord kódja',
        'Szint (kód vagy név)',
        'Kelet (EOV Y / hosszúság)',
        'Észak (EOV X / szélesség)',
        'Alsó magasság a padlótól (m)',
        'Szélesség (m)',
        'Mélység (m)',
        'Magasság (m)',
        'Elforgatás (°, északtól)',
      ]),
    ).toEqual(['code', 'level', 'east', 'north', 'bottom', 'sizeX', 'sizeY', 'sizeZ', 'heading']);
    expect(autoMapBoxes(['KKS', 'EOV Y', 'EOV X', 'Width', 'Depth', 'Height', 'Megjegyzés'])).toEqual([
      'code',
      'east',
      'north',
      'sizeX',
      'sizeY',
      'sizeZ',
      '',
    ]);
  });

  it('builds rows: text trimmed, numbers as read, empty cells left out', () => {
    const rows = toBoxRows(
      {
        headers: ['Kód', 'Szint', 'Kelet', 'Észak', 'Szélesség', 'Mélység', 'Magasság', 'Elforgatás'],
        rows: [[' =P01 ', 'F0', '650123,45', 239876.5, '1,2', 0.8, 1.5, '']],
      },
      ['code', 'level', 'east', 'north', 'sizeX', 'sizeY', 'sizeZ', 'heading'],
    );
    expect(rows).toEqual([
      { code: '=P01', level: 'F0', east: '650123,45', north: 239876.5, sizeX: '1,2', sizeY: 0.8, sizeZ: 1.5 },
    ]);
  });
});
