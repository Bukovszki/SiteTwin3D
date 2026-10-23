import { HttpClient, HttpEventType, HttpHeaders } from '@angular/common/http';
import {
  Component,
  computed,
  effect,
  inject,
  input,
  model,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { InputNumber } from 'primeng/inputnumber';
import { Message } from 'primeng/message';
import { ProgressBar } from 'primeng/progressbar';
import { Select } from 'primeng/select';
import { firstValueFrom, last, tap } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import { errorMessage, silent } from '../../core/api/errors';
import { LevelInfo } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { LonLat, MapToolsService } from '../map/map-tools.service';

/**
 * Panorama upload (FR-P1): the file goes straight to the object store with a signed URL (never through the API,
 * NFR-3); its SHA-256 is computed here and checked by the server. Position is picked on the floorplan (WGS84), with
 * level, camera height above the floor and, if known, the heading. Non-modal, so the map stays clickable.
 */
@Component({
  selector: 'app-panorama-upload-dialog',
  imports: [FormsModule, Dialog, Button, InputNumber, Select, Message, ProgressBar],
  template: `
    <p-dialog
      [header]="m.panorama.upload"
      [(visible)]="visible"
      [modal]="false"
      position="right"
      [style]="{ width: '26rem' }"
      (onHide)="tools.cancel()"
    >
      <div class="form-grid">
        <div class="field">
          <label for="pano-file">{{ m.panorama.file }}</label>
          <input
            id="pano-file"
            type="file"
            accept=".jpg,.jpeg,image/jpeg"
            (change)="onFile($event)"
          />
        </div>
        <div class="field">
          <label>{{ m.panorama.node }}</label>
          <span>{{ nodeName() ?? m.common.none }}</span>
        </div>
        <div class="field">
          <label for="pano-level">{{ m.panorama.level }}</label>
          <p-select
            inputId="pano-level"
            [options]="levelOptions()"
            optionLabel="label"
            optionValue="value"
            [(ngModel)]="levelId"
            appendTo="body"
            fluid
          />
        </div>
        <div class="row">
          <div class="field">
            <label for="pano-z">{{ m.panorama.height }}</label>
            <p-inputnumber
              inputId="pano-z"
              [(ngModel)]="zRelM"
              mode="decimal"
              [maxFractionDigits]="2"
              locale="hu-HU"
              fluid
            />
          </div>
          <div class="field">
            <label for="pano-heading">{{ m.panorama.heading }}</label>
            <p-inputnumber
              inputId="pano-heading"
              [(ngModel)]="heading"
              mode="decimal"
              [maxFractionDigits]="1"
              [min]="0"
              [max]="360"
              locale="hu-HU"
              fluid
            />
          </div>
        </div>
        <div class="field">
          <label>{{ m.panorama.position }}</label>
          <div class="inline">
            <span>{{
              position()
                ? position()![1].toFixed(6) + ', ' + position()![0].toFixed(6)
                : m.panorama.noPosition
            }}</span>
            <p-button
              [label]="m.panorama.pickPosition"
              icon="pi pi-map-marker"
              severity="secondary"
              [outlined]="true"
              size="small"
              (onClick)="pick()"
            />
          </div>
        </div>
        @if (progress(); as text) {
          <p-progressbar [value]="percent()" [showValue]="false" />
          <small class="muted">{{ text }}</small>
        }
        @if (error(); as e) {
          <p-message severity="error">{{ e }}</p-message>
        }
      </div>
      <ng-template #footer>
        <p-button
          [label]="m.common.close"
          severity="secondary"
          [text]="true"
          (onClick)="visible.set(false)"
        />
        <p-button
          [label]="m.panorama.upload"
          icon="pi pi-upload"
          [loading]="busy()"
          [disabled]="!file() || !position() || nodeId() == null"
          (onClick)="upload()"
        />
      </ng-template>
    </p-dialog>
  `,
  styles: `
    .inline {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.5rem;
    }
  `,
})
export class PanoramaUploadDialog {
  protected readonly m = inject(MESSAGES);
  protected readonly tools = inject(MapToolsService);
  private readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(MessageService);

  readonly visible = model(false);
  readonly levels = input<LevelInfo[]>([]);

  protected readonly file = signal<File | null>(null);
  protected readonly position = signal<LonLat | null>(null);
  protected readonly busy = signal(false);
  protected readonly progress = signal<string | null>(null);
  protected readonly percent = signal(0);
  protected readonly error = signal<string | null>(null);
  protected levelId: number | null = null;
  protected zRelM = 1.6;
  protected heading: number | null = null;

  /** Linked to the selected node, or else to the current level. */
  protected readonly nodeId = computed(() => this.selection.nodeId() ?? this.selection.levelId());
  protected readonly nodeName = computed(
    () =>
      this.selection.node()?.name ??
      this.levels().find((l) => l.id === this.selection.levelId())?.name ??
      null,
  );
  protected readonly levelOptions = computed(() => [
    { label: this.m.levels.outdoor, value: null },
    ...this.levels().map((l) => ({ label: l.name, value: l.id })),
  ]);

  private readonly resetOnOpen = effect(() => {
    if (this.visible())
      untracked(() => {
        this.file.set(null);
        this.position.set(null);
        this.progress.set(null);
        this.error.set(null);
        this.levelId = this.selection.levelId();
        this.zRelM = 1.6;
        this.heading = null;
      });
  });

  protected onFile(event: Event): void {
    this.file.set((event.target as HTMLInputElement).files?.[0] ?? null);
  }

  protected pick(): void {
    this.tools.pickPoint((point) => this.position.set(point));
  }

  protected async upload(): Promise<void> {
    const file = this.file();
    const position = this.position();
    const siteId = this.selection.siteId();
    const nodeId = this.nodeId();
    if (!file || !position || siteId == null || nodeId == null) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      this.progress.set(this.m.panorama.hashing);
      const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer());
      const sha256 = Array.from(new Uint8Array(digest), (b) =>
        b.toString(16).padStart(2, '0'),
      ).join('');

      const ticket = await firstValueFrom(
        this.api.panoramaUpload(siteId, file.name, file.size, silent()),
      );
      // Straight to the object store (same origin, /objects/…), with progress.
      await firstValueFrom(
        this.http
          .put(ticket.url, file, {
            headers: new HttpHeaders({ 'Content-Type': ticket.contentType }),
            reportProgress: true,
            observe: 'events',
            context: silent(),
          })
          .pipe(
            tap((e) => {
              if (e.type === HttpEventType.UploadProgress && e.total) {
                this.percent.set(Math.round((e.loaded / e.total) * 100));
                this.progress.set(this.m.panorama.uploading(this.percent()));
              }
            }),
            last(),
          ),
      );
      const panorama = await firstValueFrom(
        this.api.createPanorama(
          {
            uploadKey: ticket.key,
            sha256,
            nodeId,
            levelId: this.levelId,
            zRelM: this.zRelM,
            headingDeg: this.heading,
            position: { type: 'Point', coordinates: position },
          },
          silent(),
        ),
      );
      this.toasts.add({ severity: 'success', summary: this.m.panorama.uploaded, life: 4000 });
      this.visible.set(false);
      if (this.levelId !== this.selection.levelId()) this.selection.levelId.set(this.levelId);
      this.selection.changed();
      this.selection.openPanorama(panorama.id);
    } catch (e) {
      this.error.set(errorMessage(e, this.m));
    } finally {
      this.busy.set(false);
      this.progress.set(null);
    }
  }
}
