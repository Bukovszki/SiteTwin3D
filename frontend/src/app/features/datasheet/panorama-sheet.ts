import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Select } from 'primeng/select';
import { Tag } from 'primeng/tag';
import { ApiService } from '../../core/api/api.service';
import { LevelInfo, Node, Panorama } from '../../core/api/models';
import { Permissions } from '../../core/auth/permissions';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { MapToolsService } from '../map/map-tools.service';

type LonLat = [number, number];

/**
 * The data sheet of a panorama picked in the data tree (user report: its place in the hierarchy could not be changed
 * on the data sheet): the node it belongs to, its level and position. Editable in both editing modes – binding a
 * panorama to a node is an assignment too; choosing another node sets the level from the hierarchy.
 */
@Component({
  selector: 'app-panorama-sheet',
  imports: [FormsModule, Button, Select, Tag],
  template: `
    @if (panorama(); as p) {
      <div class="crumbs">
        <i class="pi pi-camera"></i>
        <b>{{ m.tree.panorama(p.id, p.capturedAt) }}</b>
      </div>
      <form class="form-grid body" (ngSubmit)="save()">
        <div class="field">
          <label>{{ m.panorama.statusLabel }}</label>
          <span><p-tag [value]="m.panorama.status[p.privacyStatus]" [severity]="p.privacyStatus === 'failed' ? 'danger' : p.privacyStatus === 'approved' ? 'success' : 'warn'" /></span>
        </div>
        <div class="field">
          <label for="ps-node">{{ m.panorama.node }}</label>
          @if (can.anchors()) {
            <p-select inputId="ps-node" name="node" [options]="places()" optionLabel="label" optionValue="value"
              [ngModel]="nodeId()" (ngModelChange)="setNode($event)" [filter]="true" filterBy="label" appendTo="body" fluid />
          } @else {
            <a (click)="selection.focusNode(p.nodeId)">{{ placeLabel(p.nodeId) }}</a>
          }
        </div>
        <div class="field">
          <label for="ps-level">{{ m.panorama.level }}</label>
          @if (can.anchors()) {
            <p-select inputId="ps-level" name="level" [options]="levelOptions()" optionLabel="label" optionValue="value"
              [ngModel]="levelId()" (ngModelChange)="levelId.set($event); dirty.set(true)" appendTo="body" fluid />
            <small class="muted">{{ m.panorama.levelFollows }}</small>
          } @else {
            <span>{{ levelLabel(p.levelId) }}</span>
          }
        </div>
        <div class="field">
          <label>{{ m.panorama.position }}</label>
          <div class="position">
            <span>{{ position() ? position()![1].toFixed(6) + ', ' + position()![0].toFixed(6) : m.panorama.noPosition }}</span>
            @if (can.anchors()) {
              <p-button [label]="m.panorama.pickPosition" icon="pi pi-map-marker" severity="secondary" [outlined]="true" size="small"
                (onClick)="pick()" />
            }
          </div>
        </div>
        @if (can.anchors()) {
          <div class="actions">
            <p-button [label]="m.common.cancel" severity="secondary" [text]="true" [disabled]="!dirty()" (onClick)="reset()" />
            <p-button type="submit" [label]="m.common.save" icon="pi pi-save" [loading]="saving()" [disabled]="!dirty() || nodeId() == null" />
          </div>
        }
      </form>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .crumbs {
      display: flex;
      align-items: center;
      gap: 0.4rem;
      padding: 0.5rem 0.75rem;
      border-bottom: 1px solid var(--p-content-border-color);
    }
    .body {
      padding: 0.75rem;
    }
    .position {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 0.5rem;
    }
    .actions {
      display: flex;
      justify-content: flex-end;
      gap: 0.5rem;
    }
    a {
      cursor: pointer;
      color: var(--p-primary-color);
    }
  `,
})
export class PanoramaSheet {
  protected readonly m = inject(MESSAGES);
  protected readonly can = inject(Permissions);
  protected readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  private readonly tools = inject(MapToolsService);
  private readonly toasts = inject(MessageService);

  readonly panoramaId = input.required<number>();

  protected readonly panorama = signal<Panorama | null>(null);
  protected readonly nodeId = signal<number | null>(null);
  protected readonly levelId = signal<number | null>(null);
  protected readonly position = signal<LonLat | null>(null);
  protected readonly dirty = signal(false);
  protected readonly saving = signal(false);
  private readonly nodes = signal<Node[]>([]);
  private readonly levels = signal<LevelInfo[]>([]);

  /** The location nodes by their path (building › level › room). */
  protected readonly places = computed(() =>
    this.nodes()
      .map((n) => ({ value: n.id, label: this.path(n) }))
      .sort((a, b) => a.label.localeCompare(b.label, 'hu')),
  );
  protected readonly levelOptions = computed(() => [
    { label: this.m.levels.outdoor, value: null as number | null },
    ...this.levels().map((l) => ({ label: l.name, value: l.id as number | null })),
  ]);

  private readonly loader = effect(() => {
    const id = this.panoramaId();
    const siteId = this.selection.siteId();
    this.selection.version();
    untracked(() => {
      this.api.panorama(id).subscribe((p) => {
        if (this.panoramaId() !== id) return;
        this.panorama.set(p);
        this.reset();
      });
      if (siteId == null) return;
      this.api.tree(siteId, 'location').subscribe((nodes) => this.nodes.set(nodes));
      this.api.levels(siteId).subscribe((levels) => this.levels.set(levels));
    });
  });

  private path(n: Node): string {
    const byId = new Map(this.nodes().map((x) => [x.id, x]));
    return n.path
      .split('.')
      .map((id) => byId.get(Number(id))?.name ?? '?')
      .join(' › ');
  }

  protected placeLabel(id: number): string {
    const node = this.nodes().find((n) => n.id === id);
    return node ? this.path(node) : '…';
  }

  protected levelLabel(id: number | null): string {
    return id == null ? this.m.levels.outdoor : (this.levels().find((l) => l.id === id)?.name ?? '…');
  }

  /** The form as the panorama is stored. */
  protected reset(): void {
    const p = this.panorama();
    this.nodeId.set(p?.nodeId ?? null);
    this.levelId.set(p?.levelId ?? null);
    this.position.set((p?.position.coordinates as LonLat | undefined) ?? null);
    this.dirty.set(false);
  }

  /** Another place: the level follows it (the deepest level in its path; above the levels the current one stays). */
  protected setNode(id: number | null): void {
    this.nodeId.set(id);
    this.dirty.set(true);
    const node = this.nodes().find((n) => n.id === id);
    if (!node) return;
    const levelIds = new Set(this.levels().map((l) => l.id));
    const level = node.path.split('.').map(Number).reverse().find((x) => levelIds.has(x));
    if (level != null) this.levelId.set(level);
    else if (this.levelId() != null && !this.levelInside(this.levelId()!, node.id)) this.levelId.set(null);
  }

  private levelInside(levelId: number, nodeId: number): boolean {
    const level = this.nodes().find((n) => n.id === levelId);
    return !!level && level.path.split('.').map(Number).includes(nodeId);
  }

  protected pick(): void {
    this.tools.pickPoint((point) => {
      this.position.set(point);
      this.dirty.set(true);
    }, this.m.panorama.pickHint);
  }

  protected save(): void {
    const p = this.panorama();
    if (!p || this.nodeId() == null) return;
    this.saving.set(true);
    this.api
      .updatePanorama(p.id, {
        nodeId: this.nodeId(),
        levelId: this.levelId(),
        ...(this.position() ? { position: { type: 'Point', coordinates: this.position() } } : {}),
      })
      .subscribe({
        next: (saved) => {
          this.saving.set(false);
          this.toasts.add({ severity: 'success', summary: this.m.panorama.saved, life: 2500 });
          // The map shows it on its new level.
          if (saved.levelId !== this.selection.levelId()) this.selection.levelId.set(saved.levelId);
          this.selection.changed();
        },
        error: () => this.saving.set(false),
      });
  }
}
