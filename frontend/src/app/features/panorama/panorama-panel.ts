import { Component, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Message } from 'primeng/message';
import { Tag } from 'primeng/tag';
import { Tooltip } from 'primeng/tooltip';
import { AnchorEditService } from '../../core/anchors/anchor-edit.service';
import { ApiService } from '../../core/api/api.service';
import { firstValueFrom } from 'rxjs';
import { Anchor, Panorama, PanoramaNeighbour } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { bearing, normalizeHeading } from '../../core/geo/geodesy';
import { Permissions } from '../../core/auth/permissions';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { MapToolsService } from '../map/map-tools.service';
import { PanoramaClick, PanoramaViewer } from './panorama-viewer';
import { PanoramaEditDialog } from './panorama-edit-dialog';
import { PrivacyReviewDialog } from './privacy-review-dialog';

const SHOW_BOXES_KEY = 'panorama.showBoxes';

function readShowBoxes(): boolean {
  try {
    return localStorage.getItem(SHOW_BOXES_KEY) !== 'false';
  } catch {
    return true;
  }
}

/**
 * The open panorama: viewer plus its state (privacy processing, orientation) and the editor tools – orientation
 * calibration (FR-P6: the same point clicked on the panorama and on the floorplan), privacy review (FR-P8), delete.
 */
@Component({
  selector: 'app-panorama-panel',
  imports: [Button, Message, Tag, Tooltip, PanoramaViewer, PrivacyReviewDialog, PanoramaEditDialog],
  template: `
    <div class="bar">
      @if (panorama(); as p) {
        <p-tag [severity]="severity()" [value]="m.panorama.status[p.privacyStatus]" />
        @if (p.imageIsOriginal) {
          <p-tag
            severity="secondary"
            icon="pi pi-eye"
            [value]="m.panorama.originalShort"
            [pTooltip]="m.panorama.original"
            tooltipPosition="bottom"
          />
        }
        <span class="spacer"></span>
        <p-button
          icon="pi pi-box"
          [text]="!showBoxes()"
          [outlined]="showBoxes()"
          size="small"
          [pTooltip]="showBoxes() ? m.panorama.hideBoxes : m.panorama.showBoxes"
          [ariaLabel]="showBoxes() ? m.panorama.hideBoxes : m.panorama.showBoxes"
          (onClick)="toggleBoxes()"
        />
        @if (can.anchors()) {
          <p-button
            icon="pi pi-map-marker"
            [text]="true"
            size="small"
            [pTooltip]="m.infoPoint.place"
            (onClick)="startInfoPoint()"
            [disabled]="!p.imageUrl || edits.active()"
          />
          <p-button
            icon="pi pi-arrows-alt"
            [text]="!edits.active()"
            [outlined]="edits.active()"
            size="small"
            [pTooltip]="edits.active() ? m.anchorEdit.stop : m.anchorEdit.start"
            (onClick)="edits.toggle()"
            [disabled]="!p.imageUrl"
          />
        }
        @if (can.prepare()) {
          <p-button
            icon="pi pi-pencil"
            [text]="true"
            size="small"
            [pTooltip]="m.panorama.edit"
            (onClick)="editing.set(true)"
          />
          <p-button
            icon="pi pi-share-alt"
            [text]="true"
            size="small"
            [pTooltip]="m.walk.auto"
            (onClick)="autoLink(p)"
          />
          <p-button
            icon="pi pi-link"
            [text]="true"
            size="small"
            [pTooltip]="m.walk.toggle"
            (onClick)="toggleLink(p)"
          />
          <p-button
            icon="pi pi-compass"
            [text]="true"
            size="small"
            [pTooltip]="m.panorama.calibrate"
            (onClick)="startCalibration()"
            [disabled]="!p.imageUrl"
          />
          @if (p.privacyStatus === 'review') {
            <p-button
              icon="pi pi-eye-slash"
              [label]="m.panorama.reviewShort"
              [pTooltip]="m.panorama.review"
              size="small"
              severity="warn"
              (onClick)="reviewing.set(true)"
            />
          }
          <p-button
            icon="pi pi-trash"
            [text]="true"
            size="small"
            severity="danger"
            [pTooltip]="m.panorama.delete"
            (onClick)="remove(p)"
          />
        }
      }
      <p-button
        icon="pi pi-times"
        [text]="true"
        size="small"
        severity="secondary"
        (onClick)="selection.openPanorama(null)"
      />
    </div>
    @if (hint(); as text) {
      <p-message severity="info" size="small" styleClass="hint">{{ text }}</p-message>
    } @else if (panorama()?.headingDeg == null && panorama()?.imageUrl) {
      <p-message severity="warn" size="small" styleClass="hint">{{
        m.panorama.notCalibrated
      }}</p-message>
    } @else if (panorama()?.processingError; as code) {
      <p-message severity="error" size="small" styleClass="hint">{{
        m.panorama.errors[code] ?? code
      }}</p-message>
    }
    <div class="view">
      <app-panorama-viewer
        #viewer
        (clicked)="onClick($event)"
        (navigate)="selection.openPanorama($event)"
        (infoPointClicked)="selection.focusNode($event)"
        (infoPointMoved)="moveInfoPoint($event)"
      />
    </div>
    @if (can.prepare() && panorama(); as p) {
      @defer {
        <app-privacy-review-dialog [(visible)]="reviewing" [panorama]="p" (changed)="reload()" />
        <app-panorama-edit-dialog [(visible)]="editing" [panorama]="p" />
      }
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.35rem 0.5rem;
      padding: 0.35rem 0.5rem;
      border-bottom: 1px solid var(--p-content-border-color);
      .spacer {
        flex: 1;
      }
    }
    :host ::ng-deep .hint {
      margin: 0.35rem 0.5rem;
    }
    .view {
      position: relative;
      flex: 1;
      min-height: 0;
      background: #111;
    }
  `,
})
export class PanoramaPanel {
  protected readonly m = inject(MESSAGES);
  protected readonly auth = inject(AuthService);
  protected readonly can = inject(Permissions);
  protected readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  protected readonly edits = inject(AnchorEditService);
  private readonly tools = inject(MapToolsService);
  private readonly toasts = inject(MessageService);
  private readonly confirm = inject(ConfirmationService);
  private readonly viewer = viewChild.required<PanoramaViewer>('viewer');

  protected readonly panorama = signal<Panorama | null>(null);
  protected readonly reviewing = signal(false);
  protected readonly editing = signal(false);
  protected readonly hint = signal<string | null>(null);
  private calibrating: 'panorama' | 'map' | null = null;
  private placing = false;
  protected readonly neighbours = signal<PanoramaNeighbour[]>([]);
  protected readonly infoPoints = signal<Anchor[]>([]);
  /** The selected record's boxes on the panorama: on by default; the choice is remembered in this browser. */
  protected readonly showBoxes = signal(readShowBoxes());

  protected readonly severity = computed(() => {
    switch (this.panorama()?.privacyStatus) {
      case 'approved':
        return 'success';
      case 'review':
        return 'warn';
      case 'failed':
        return 'danger';
      default:
        return 'info';
    }
  });

  constructor() {
    effect(() => {
      const id = this.selection.panoramaId();
      this.selection.version();
      untracked(() => this.load(id));
    });
    // Mutual selection (FR-G3): the selected node's info point is highlighted and turned to.
    effect(() => {
      const nodeId = this.selection.nodeId();
      this.selection.focusTick();
      untracked(() => {
        this.viewer().highlightNodeId.set(nodeId);
        const point = this.infoPoints().find((a) => a.nodeId === nodeId);
        if (point) this.viewer().lookAtInfoPoint(point);
        // Its boxes (M6) are drawn in the panorama too.
        this.viewer().boxes.set([]);
        if (nodeId != null)
          this.api.nodeAnchors(nodeId).subscribe((anchors) => {
            if (this.selection.nodeId() === nodeId)
              this.viewer().boxes.set(anchors.filter((a) => a.kind === 'region'));
          });
      });
    });
  }

  private readonly boxesShown = effect(() => {
    const show = this.showBoxes();
    untracked(() => this.viewer().showBoxes.set(show));
  });

  protected toggleBoxes(): void {
    const show = !this.showBoxes();
    this.showBoxes.set(show);
    try {
      localStorage.setItem(SHOW_BOXES_KEY, String(show));
    } catch {
      // No storage (private window): the choice lasts for this page only.
    }
  }

  /** Info points can be dragged only in an editing session (AnchorEditService), never by accident. */
  private readonly editable = effect(() => {
    const editable = this.edits.active();
    untracked(() => this.viewer().editable.set(editable));
  });

  /** The markers show the staged (not yet accepted) image angles of the session. */
  private readonly showStaged = effect(() => {
    const pending = this.edits.pending();
    const points = this.infoPoints().map((a) => {
      const patch = pending.get(a.id);
      return patch
        ? { ...a, yawDeg: patch.yawDeg ?? a.yawDeg, pitchDeg: patch.pitchDeg ?? a.pitchDeg }
        : a;
    });
    untracked(() => this.viewer().infoPoints.set(points));
  });

  /** An info point dragged on the picture: staged until the session is accepted (its floorplan place stays). */
  protected moveInfoPoint(move: { anchorId: number; yaw: number; pitch: number }): void {
    this.edits.stage(move.anchorId, {
      yawDeg: Math.round(move.yaw * 100) / 100,
      pitchDeg: Math.round(move.pitch * 100) / 100,
    });
  }

  private loadMarkers(id: number): void {
    this.api.neighbours(id).subscribe((n) => {
      this.neighbours.set(n);
      this.viewer().neighbours.set(n);
    });
    this.api.panoramaAnchors(id).subscribe((a) => {
      this.infoPoints.set(a);
    });
  }

  private load(id: number | null): void {
    if (id == null) return;
    this.api.panorama(id).subscribe({
      next: (p) => {
        if (this.selection.panoramaId() !== id) return;
        const reopen =
          this.panorama()?.id !== p.id ||
          this.panorama()?.imageUrl?.split('?')[0] !== p.imageUrl?.split('?')[0] ||
          this.panorama()?.headingDeg !== p.headingDeg;
        this.panorama.set(p);
        if (reopen) this.viewer().show.set(p);
        this.loadMarkers(p.id);
        // Still processing: look again shortly.
        if (p.privacyStatus === 'pending' || p.privacyStatus === 'rendering')
          setTimeout(() => this.selection.panoramaId() === id && this.load(id), 4000);
      },
      error: () => this.selection.openPanorama(null),
    });
  }

  protected reload(): void {
    this.selection.changed();
  }

  protected startCalibration(): void {
    this.placing = false;
    this.calibrating = 'panorama';
    this.viewer().picking.set(true);
    this.hint.set(this.m.panorama.calibrateStep1);
  }

  /** Info point (FR-A1): record selected → click on the panorama → approximate place on the floorplan. */
  protected startInfoPoint(): void {
    const node = this.selection.node();
    if (!node) {
      this.toasts.add({ severity: 'warn', summary: this.m.infoPoint.needNode, life: 4000 });
      return;
    }
    if (this.panorama()?.headingDeg == null)
      this.toasts.add({ severity: 'warn', summary: this.m.infoPoint.uncalibrated, life: 6000 });
    this.calibrating = null;
    this.placing = true;
    this.viewer().picking.set(true);
    this.hint.set(this.m.infoPoint.step1(node.name));
  }

  private async placeInfoPoint(click: PanoramaClick): Promise<void> {
    const p = this.panorama();
    const node = this.selection.node();
    this.placing = false;
    if (!p || !node) return;
    const body = {
      kind: 'panorama_point',
      panoramaId: p.id,
      yawDeg: click.yaw,
      pitchDeg: click.pitch,
      levelId: p.levelId,
      zRelM: p.zRelM,
    };
    const save = (position: [number, number], positionSource: string) =>
      this.api
        .createAnchor(node.id, {
          ...body,
          position: { type: 'Point', coordinates: position },
          positionSource,
        })
        .subscribe(() => {
          this.toasts.add({ severity: 'success', summary: this.m.infoPoint.created, life: 2500 });
          this.selection.changed();
        });
    // One element, one place: if the node has a place already, the info point uses it (no floorplan step).
    if (await firstValueFrom(this.api.place(node.id))) {
      this.hint.set(null);
      this.api.createAnchor(node.id, body).subscribe(() => {
        this.toasts.add({ severity: 'success', summary: this.m.infoPoint.created, life: 2500 });
        this.selection.changed();
      });
      return;
    }
    // Without a floorplan on the level the panorama's place is used for now (decided, FR-A2).
    const floorplans =
      p.levelId == null ? [] : await firstValueFrom(this.api.floorplans(p.levelId));
    if (floorplans.length === 0) {
      this.hint.set(null);
      this.toasts.add({ severity: 'info', summary: this.m.infoPoint.fallback, life: 5000 });
      save(p.position.coordinates, 'panorama_fallback');
      return;
    }
    this.hint.set(this.m.infoPoint.step2);
    this.tools.pickPoint((point) => {
      this.hint.set(null);
      save(point, 'manual');
    }, this.m.infoPoint.step2);
  }

  protected autoLink(p: Panorama): void {
    const siteId = this.selection.siteId();
    if (siteId == null) return;
    this.api.autoLink(siteId, p.levelId, 15).subscribe((n) => {
      this.toasts.add({ severity: 'success', summary: this.m.walk.autoDone(n), life: 3000 });
      this.selection.changed();
    });
  }

  /** Link or unlink: the next panorama clicked on the map. */
  protected toggleLink(p: Panorama): void {
    this.tools.pickPanorama((other) => {
      if (other === p.id) return;
      const linked = this.neighbours().some((n) => n.id === other);
      (linked ? this.api.unlink(p.id, other) : this.api.link(p.id, other)).subscribe(() => {
        this.toasts.add({
          severity: 'success',
          summary: linked ? this.m.walk.unlinked : this.m.walk.linked,
          life: 2500,
        });
        this.selection.changed();
      });
    }, this.m.walk.toggleHint);
  }

  /** Calibration: image yaw of a point here, then the same point on the floorplan → heading of the image centre. */
  protected onClick(click: PanoramaClick): void {
    // The direction is picked: markers select again.
    this.viewer().picking.set(false);
    if (this.placing) {
      void this.placeInfoPoint(click);
      return;
    }
    const p = this.panorama();
    if (this.calibrating !== 'panorama' || !p) return;
    this.calibrating = 'map';
    this.hint.set(this.m.panorama.calibrateStep2);
    this.tools.pickPoint((point) => {
      this.calibrating = null;
      this.hint.set(null);
      const heading = normalizeHeading(bearing(p.position.coordinates, point) - click.yaw);
      this.api.updatePanorama(p.id, { headingDeg: heading }).subscribe(() => {
        this.toasts.add({
          severity: 'success',
          summary: this.m.panorama.calibrated(heading),
          life: 3000,
        });
        this.selection.changed();
      });
    });
  }

  protected remove(p: Panorama): void {
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.panorama.deleteConfirm,
      acceptButtonProps: { label: this.m.common.delete, severity: 'danger' },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () =>
        this.api.deletePanorama(p.id).subscribe(() => {
          this.selection.openPanorama(null);
          this.selection.changed();
        }),
    });
  }
}
