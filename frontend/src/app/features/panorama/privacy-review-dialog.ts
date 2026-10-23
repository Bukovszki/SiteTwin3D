import {
  Component,
  computed,
  effect,
  inject,
  input,
  model,
  output,
  signal,
  untracked,
} from '@angular/core';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { FormsModule } from '@angular/forms';
import { SelectButton } from 'primeng/selectbutton';
import { ApiService } from '../../core/api/api.service';
import { Panorama, PrivacyRegion } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';

interface Box {
  region: PrivacyRegion;
  left: number;
  top: number;
  width: number;
  height: number;
}

/**
 * Human approval of the privacy regions (FR-P8): the original image with the suggested regions; a click excludes a
 * false suggestion (or includes it again), dragging marks a new area by hand. Approval burns the accepted regions into
 * the display version; only then do viewers see the panorama.
 */
@Component({
  selector: 'app-privacy-review-dialog',
  imports: [FormsModule, Dialog, Button, SelectButton],
  template: `
    <p-dialog
      [header]="m.privacy.title"
      [(visible)]="visible"
      [modal]="true"
      [maximizable]="true"
      [style]="{ width: '90vw' }"
    >
      <div class="top">
        <p class="muted">{{ m.privacy.hint }}</p>
        <p-selectbutton
          [options]="zooms"
          optionLabel="label"
          optionValue="value"
          [(ngModel)]="zoom"
          [allowEmpty]="false"
          size="small"
        />
      </div>
      <div class="scroll">
        <div
          class="image"
          [style.width.%]="zoom * 100"
          (mousedown)="down($event)"
          (mousemove)="move($event)"
          (mouseup)="up($event)"
          (mouseleave)="drawing.set(null)"
        >
          @if (panorama().imageUrl; as url) {
            <img [src]="url" alt="" draggable="false" />
          }
          @for (box of boxes(); track $index) {
            <div
              class="box"
              [class.rejected]="!box.region.accepted"
              [class.manual]="box.region.source === 'manual'"
              [style.left.%]="box.left"
              [style.top.%]="box.top"
              [style.width.%]="box.width"
              [style.height.%]="box.height"
              (mousedown)="$event.stopPropagation()"
              (click)="toggle(box.region)"
              [title]="
                box.region.source === 'auto'
                  ? m.privacy.auto + ' ' + ((box.region.score ?? 0) * 100).toFixed(0) + '%'
                  : m.privacy.manual
              "
            ></div>
          }
          @if (drawing(); as d) {
            <div
              class="box drawing"
              [style.left.%]="Math.min(d.x0, d.x1)"
              [style.top.%]="Math.min(d.y0, d.y1)"
              [style.width.%]="Math.abs(d.x1 - d.x0)"
              [style.height.%]="Math.abs(d.y1 - d.y0)"
            ></div>
          }
        </div>
      </div>
      <ng-template #footer>
        <span class="count">{{ m.privacy.count(accepted(), regions().length) }}</span>
        <p-button
          [label]="m.common.save"
          icon="pi pi-save"
          severity="secondary"
          [outlined]="true"
          [loading]="saving()"
          (onClick)="save()"
        />
        <p-button
          [label]="m.privacy.approve"
          icon="pi pi-check"
          [loading]="saving()"
          (onClick)="approve()"
        />
      </ng-template>
    </p-dialog>
  `,
  styles: `
    .top {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 1rem;
    }
    .scroll {
      max-height: 65vh;
      overflow: auto;
    }
    .image {
      position: relative;
      user-select: none;
      cursor: crosshair;
      img {
        display: block;
        width: 100%;
      }
    }
    .box {
      position: absolute;
      border: 2px solid #22c55e;
      background: rgb(34 197 94 / 0.2);
      cursor: pointer;
      &.manual {
        border-color: #3b82f6;
        background: rgb(59 130 246 / 0.2);
      }
      &.rejected {
        border: 2px dashed #ef4444;
        background: transparent;
      }
      &.drawing {
        border-style: dashed;
        pointer-events: none;
      }
    }
    .count {
      margin-right: auto;
      color: var(--p-text-muted-color);
    }
  `,
})
export class PrivacyReviewDialog {
  protected readonly m = inject(MESSAGES);
  private readonly api = inject(ApiService);
  private readonly toasts = inject(MessageService);
  private readonly confirm = inject(ConfirmationService);
  protected readonly Math = Math;
  /** Faces are small on a whole panorama: the image can be enlarged and scrolled. */
  protected readonly zooms = [1, 2, 4].map((value) => ({ label: `${value}×`, value }));
  protected zoom = 2;

  readonly visible = model(false);
  readonly panorama = input.required<Panorama>();
  readonly changed = output<void>();

  protected readonly regions = signal<PrivacyRegion[]>([]);
  protected readonly saving = signal(false);
  /** The rectangle being drawn, in % of the image. */
  protected readonly drawing = signal<{ x0: number; y0: number; x1: number; y1: number } | null>(
    null,
  );

  protected readonly accepted = computed(() => this.regions().filter((r) => r.accepted).length);

  /** Regions as % rectangles over the image; a region across the ±180° seam is drawn on both edges. */
  protected readonly boxes = computed<Box[]>(() =>
    this.regions().flatMap((region) => {
      const left = ((region.yaw - region.width / 2) / 360 + 0.5) * 100;
      const width = (region.width / 360) * 100;
      const top = (0.5 - (region.pitch + region.height / 2) / 180) * 100;
      const height = (region.height / 180) * 100;
      const boxes = [{ region, left, top, width, height }];
      if (left < 0) boxes.push({ region, left: left + 100, top, width, height });
      if (left + width > 100) boxes.push({ region, left: left - 100, top, width, height });
      return boxes;
    }),
  );

  constructor() {
    effect(() => {
      if (this.visible())
        untracked(() => this.regions.set(structuredClone(this.panorama().privacyRegions)));
    });
  }

  protected toggle(region: PrivacyRegion): void {
    this.regions.update((list) =>
      list.map((r) => (r === region ? { ...r, accepted: !r.accepted } : r)),
    );
  }

  private percent(event: MouseEvent): { x: number; y: number } {
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    return {
      x: ((event.clientX - rect.left) / rect.width) * 100,
      y: ((event.clientY - rect.top) / rect.height) * 100,
    };
  }

  protected down(event: MouseEvent): void {
    const { x, y } = this.percent(event);
    this.drawing.set({ x0: x, y0: y, x1: x, y1: y });
  }

  protected move(event: MouseEvent): void {
    const d = this.drawing();
    if (!d) return;
    const { x, y } = this.percent(event);
    this.drawing.set({ ...d, x1: x, y1: y });
  }

  /** % rectangle → image angles (equirectangular: linear in both directions). */
  protected up(event: MouseEvent): void {
    const d = this.drawing();
    this.drawing.set(null);
    if (!d) return;
    const { x, y } = this.percent(event);
    const [x0, x1] = [Math.min(d.x0, x), Math.max(d.x0, x)];
    const [y0, y1] = [Math.min(d.y0, y), Math.max(d.y0, y)];
    if (x1 - x0 < 0.2 || y1 - y0 < 0.2) return;
    const region: PrivacyRegion = {
      id: crypto.randomUUID().replace(/-/g, '').slice(0, 12),
      yaw: ((x0 + x1) / 200 - 0.5) * 360,
      pitch: (0.5 - (y0 + y1) / 200) * 180,
      width: ((x1 - x0) / 100) * 360,
      height: ((y1 - y0) / 100) * 180,
      source: 'manual',
      accepted: true,
    };
    this.regions.update((list) => [...list, region]);
  }

  protected save(after?: () => void): void {
    this.saving.set(true);
    this.api.setPrivacyRegions(this.panorama().id, this.regions()).subscribe({
      next: () => {
        this.saving.set(false);
        if (after) after();
        else {
          this.toasts.add({ severity: 'success', summary: this.m.privacy.saved, life: 2500 });
          this.changed.emit();
        }
      },
      error: () => this.saving.set(false),
    });
  }

  protected approve(): void {
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.privacy.approveConfirm,
      acceptButtonProps: { label: this.m.privacy.approve },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () =>
        this.save(() =>
          this.api.approvePanorama(this.panorama().id).subscribe(() => {
            this.visible.set(false);
            this.changed.emit();
          }),
        ),
    });
  }
}
