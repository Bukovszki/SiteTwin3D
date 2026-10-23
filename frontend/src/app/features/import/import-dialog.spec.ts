import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MessageService } from 'primeng/api';
import { SelectionService } from '../../core/selection/selection.service';
import { ImportDialog } from './import-dialog';

const csv = 'Kód;Megnevezés;Gyártó;Teljesítmény (kW)\n=P01;Szivattyú;Grundfos;7,5\n=P02;Keverő;Flygt;3,1\n';

describe('ImportDialog', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ImportDialog],
      providers: [provideHttpClient(), provideHttpClientTesting(), MessageService],
    }).compileComponents();
    const selection = TestBed.inject(SelectionService);
    selection.nodeTypes.set([
      {
        id: 6, code: 'equipment', name: 'Berendezés', aspect: 'function', isLevel: false, shared: true,
        attrSchema: { type: 'object', properties: { manufacturer: { type: 'string', title: 'Gyártó' }, power_kw: { type: 'number', title: 'Teljesítmény (kW)' } } },
      },
    ]);
    selection.siteId.set(1);
  });

  it('reads the chosen file, suggests the mapping and sends the rows', async () => {
    const fixture = TestBed.createComponent(ImportDialog);
    fixture.componentRef.setInput('typeId', 6);
    fixture.componentRef.setInput('visible', true);
    fixture.detectChanges();
    await fixture.whenStable();

    const input = document.querySelector<HTMLInputElement>('#imp-file')!;
    Object.defineProperty(input, 'files', { value: [new File([csv], 'berendezesek.csv', { type: 'text/csv' })] });
    input.dispatchEvent(new Event('change'));
    await new Promise((r) => setTimeout(r, 50));
    fixture.detectChanges();
    await fixture.whenStable();

    expect(document.body.textContent).toContain('Oszlopok hozzárendelése');
    const run = Array.from(document.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.includes('Importálás'))!;
    expect(run.disabled).toBe(false);

    run.click();
    const request = TestBed.inject(HttpTestingController).expectOne('/api/v1/sites/1/import/attributes');
    expect(request.request.body).toEqual({
      typeId: 6,
      parentId: null,
      rows: [
        { code: '=P01', name: 'Szivattyú', attrs: { manufacturer: 'Grundfos', power_kw: '7,5' } },
        { code: '=P02', name: 'Keverő', attrs: { manufacturer: 'Flygt', power_kw: '3,1' } },
      ],
    });
  });
});
