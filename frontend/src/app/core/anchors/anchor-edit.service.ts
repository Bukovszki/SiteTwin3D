import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { ConfirmationService, MessageService } from 'primeng/api';
import { forkJoin } from 'rxjs';
import { ApiService } from '../api/api.service';
import { GeoJsonPoint } from '../api/models';
import { Permissions } from '../auth/permissions';
import { MESSAGES } from '../i18n/messages';
import { SelectionService } from '../selection/selection.service';

/** A staged change of an anchor: image angles on the panorama and / or its place on the floorplan. */
export interface AnchorPatch {
  yawDeg?: number;
  pitchDeg?: number;
  position?: GeoJsonPoint;
}

/**
 * Editing session for moving anchors (info points, points): outside a session nothing can be dragged, so a stray drag
 * never moves an anchor. Inside it, drags are only staged; they are written when the user accepts them (save) and
 * dropped on discard. Leaving with staged changes asks first.
 */
@Injectable({ providedIn: 'root' })
export class AnchorEditService {
  private readonly api = inject(ApiService);
  private readonly selection = inject(SelectionService);
  private readonly can = inject(Permissions);
  private readonly confirmations = inject(ConfirmationService);
  private readonly toasts = inject(MessageService);
  private readonly m = inject(MESSAGES);

  readonly active = signal(false);
  readonly pending = signal<ReadonlyMap<number, AnchorPatch>>(new Map());
  readonly count = computed(() => this.pending().size);
  readonly saving = signal(false);

  constructor() {
    // Leaving the editing modes (or the site) ends the session; anything not accepted is dropped.
    effect(() => {
      const allowed = this.can.anchors();
      this.selection.siteId();
      untracked(() => {
        if (!this.active() || allowed) return;
        if (this.count() > 0)
          this.toasts.add({ severity: 'warn', summary: this.m.anchorEdit.discarded, life: 4000 });
        this.end();
      });
    });
  }

  start(): void {
    if (this.can.anchors()) this.active.set(true);
  }

  /** Ends the session; with staged changes it asks whether to drop them. */
  stop(): void {
    if (this.count() === 0) {
      this.end();
      return;
    }
    this.confirmations.confirm({
      header: this.m.common.confirm,
      message: this.m.anchorEdit.confirmLeave,
      acceptButtonProps: { label: this.m.anchorEdit.discard, severity: 'danger' },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () => this.discard(),
    });
  }

  toggle(): void {
    if (this.active()) this.stop();
    else this.start();
  }

  stage(anchorId: number, patch: AnchorPatch): void {
    if (!this.active()) return;
    this.pending.update((all) => new Map(all).set(anchorId, { ...all.get(anchorId), ...patch }));
  }

  /** Accept: every staged change is written, then the session ends. */
  save(): void {
    const changes = [...this.pending()];
    if (changes.length === 0) {
      this.end();
      return;
    }
    this.saving.set(true);
    forkJoin(changes.map(([id, patch]) => this.api.updateAnchor(id, { ...patch }))).subscribe({
      next: () => {
        this.saving.set(false);
        this.toasts.add({
          severity: 'success',
          summary: this.m.anchorEdit.saved(changes.length),
          life: 2500,
        });
        this.end();
      },
      // The error is reported by the interceptor; the staged changes stay, so the user can retry or discard.
      error: () => this.saving.set(false),
    });
  }

  discard(): void {
    this.end();
  }

  /** Clears the session; views reload, so dragged markers return to their stored place. */
  private end(): void {
    const hadChanges = this.count() > 0;
    this.pending.set(new Map());
    this.active.set(false);
    if (hadChanges) this.selection.changed();
  }
}
