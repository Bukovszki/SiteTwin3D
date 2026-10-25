import { Component, inject, signal } from '@angular/core';
import { Button } from 'primeng/button';
import { MESSAGES } from '../../core/i18n/messages';
import { panoramaIcon, placeIcon, siteIcon } from '../../core/markers/marker-icons';

/** The map's legend: the point symbols (core/markers) with their meaning; collapsed by default. */
@Component({
  selector: 'app-map-legend',
  imports: [Button],
  template: `
    @if (open()) {
      <div class="legend">
        <div class="head">
          <b>{{ m.legend.title }}</b>
          <p-button
            icon="pi pi-times"
            [text]="true"
            size="small"
            severity="secondary"
            [ariaLabel]="m.common.close"
            (onClick)="open.set(false)"
          />
        </div>
        @for (row of rows; track row.label) {
          <div class="row">
            <span class="sym">
              @if (row.icon) {
                <img [src]="row.icon" alt="" />
              } @else {
                <span class="footprint"></span>
              }
            </span>
            <span>{{ row.label }}</span>
          </div>
        }
      </div>
    } @else {
      <p-button
        icon="pi pi-info-circle"
        [label]="m.legend.title"
        size="small"
        severity="secondary"
        [raised]="true"
        (onClick)="open.set(true)"
      />
    }
  `,
  styles: `
    .legend {
      width: 15.5rem;
      padding: 0.4rem 0.6rem 0.5rem;
      background: var(--p-content-background);
      border: 1px solid var(--p-content-border-color);
      border-radius: 8px;
      box-shadow: 0 2px 8px rgb(0 0 0 / 0.12);
      font-size: 0.8rem;
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    .row {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      min-height: 1.9rem;
    }
    .sym {
      flex: 0 0 2rem;
      display: flex;
      justify-content: center;
    }
    .sym img {
      max-height: 1.75rem;
    }
    .footprint {
      width: 1.5rem;
      height: 1rem;
      border: 1.5px dashed rgb(71 85 105 / 0.8);
      background: rgb(37 99 235 / 0.06);
    }
  `,
})
export class MapLegend {
  protected readonly m = inject(MESSAGES);
  protected readonly open = signal(false);

  protected readonly rows = [
    { icon: placeIcon({ typeCode: 'equipment' }), label: this.m.legend.equipment },
    { icon: placeIcon({ typeCode: 'room' }), label: this.m.legend.room },
    {
      icon: placeIcon({ typeCode: 'equipment', approximate: true }),
      label: this.m.legend.approximate,
    },
    { icon: placeIcon({ typeCode: 'equipment', selected: true }), label: this.m.legend.selected },
    { icon: null, label: this.m.legend.box },
    { icon: panoramaIcon('approved'), label: this.m.legend.panorama },
    { icon: panoramaIcon('pending'), label: this.m.legend.panoramaPending },
    { icon: panoramaIcon('failed'), label: this.m.legend.panoramaFailed },
    { icon: panoramaIcon('approved', true), label: this.m.legend.panoramaOpen },
    { icon: siteIcon(), label: this.m.legend.site },
  ];
}
