import { NodeType } from '../../core/api/models';
import { MESSAGES_HU } from '../../core/i18n/messages.hu';
import { allowedUnder } from './node-create-dialog';

const type = (code: string, allowedParents: string[] | null): NodeType => ({
  id: 1, code, name: code, aspect: 'location', isLevel: false, shared: true,
  attrSchema: null, baseSchema: null, extensionSchema: null, allowedParents,
});

describe('allowedUnder', () => {
  it('follows the parent rules of the type', () => {
    expect(allowedUnder(type('room', ['level']), 'level')).toBe(true);
    expect(allowedUnder(type('room', ['level']), 'building')).toBe(false);
    expect(allowedUnder(type('room', ['level']), null)).toBe(false);
    expect(allowedUnder(type('building', ['']), null)).toBe(true);
    expect(allowedUnder(type('cabinet', null), 'room')).toBe(true);
  });
});

describe('conversion messages', () => {
  it('words the codes in Hungarian, keeps unknown text', () => {
    const m = MESSAGES_HU.models;
    expect(m.message('textures_missing:rust.tga')).toBe('Hiányzó textúra (szürkén jelenik meg): rust.tga');
    expect(m.message('zip_too_many_files:6000|5000')).toContain('(6000; legfeljebb 5000)');
    expect(m.message('converter_unreachable')).toContain('nem érhető el');
    expect(m.message('Something else')).toBe('Something else');
    expect(m.message(null)).toBe('');
  });
});
