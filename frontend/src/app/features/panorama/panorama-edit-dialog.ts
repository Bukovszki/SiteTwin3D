import { Component, computed, effect, inject, input, model, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { Select } from 'primeng/select';
import { ApiService } from '../../core/api/api.service';
import { LevelInfo, Node, Panorama } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { MapToolsService } from '../map/map-tools.service';

type LonLat = [number, number];

/**
 * A placed panorama moved afterwards (user report): the place it belongs to, its level and its position. Choosing
 * another place sets the level from the hierarchy (a level itself or a room on it). Non-modal, so the map stays
 * clickable for the position.
 */
@Component({
  selector: 'app-panorama-edit-dialog',
  imports: [FormsModule, Button, Dialog, Select],
  template: `
    <p-dialog [header]="m.panorama.edit" [(visible)]="visible" [modal]="false" [style]="{ width: '28rem' }">
      <form class="form-grid" (ngSubmit)="save()">
        <div class="field">
          <label for="pe-node">{{ m.panorama.node }}</label>
          <p-select inputId="pe-node" name="node" [options]="places()" optionLabel="label" optionValue="value"
            [ngModel]="nodeId()" (ngModelChange)="setNode($event)" [filter]="true" filterBy="label" appendTo="body" fluid />
        </div>
        <div class="field">
          <label for="pe-level">{{ m.panorama.level }}</label>
          <p-select inputId="pe-level" name="level" [options]="levelOptions()" optionLabel="label" optionValue="value"
            [ngModel]="levelId()" (ngModelChange)="levelId.set($event)" appendTo="body" fluid />
          <small class="muted">{{ m.panorama.levelFollows }}</small>
        </div>
        <div class="field">
          <label>{{ m.panorama.position }}</label>
          <div class="position">
            <span>{{ position() ? position()![1].toFixed(6) + ', ' + position()![0].toFixed(6) : m.panorama.noPosition }}</span>
            <p-button [label]="m.panorama.pickPosition" icon="pi pi-map-marker" severity="secondary" [outlined]="true" size="small"
              (onClick)="pick()" />
          </div>
        </div>
        <div class="actions">
          <p-button [label]="m.common.cancel" severity="secondary" [text]="true" (onClick)="visible.set(false)" />
          <p-button type="submit" [label]="m.common.save" icon="pi pi-save" [loading]="saving()" [disabled]="nodeId() == null" />
        </div>
      </form>
    </p-dialog>
  `,
  styles: `
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
  `,
})
export class PanoramaEditDialog {
  protected readonly m = inject(MESSAGES);
  private readonly api = inject(ApiService);
  private readonly selection = inject(SelectionService);
  private readonly tools = inject(MapToolsService);
  private readonly toasts = inject(MessageService);

  readonly visible = model(false);
  readonly panorama = input<Panorama | null>(null);

  protected readonly nodeId = signal<number | null>(null);
  protected readonly levelId = signal<number | null>(null);
  protected readonly position = signal<LonLat | null>(null);
  protected readonly saving = signal(false);
  private readonly nodes = signal<Node[]>([]);
  private readonly levels = signal<LevelInfo[]>([]);

  /** The location nodes by their path (building › level › room). */
  protected readonly places = computed(() => {
    const byId = new Map(this.nodes().map((n) => [n.id, n]));
    return this.nodes()
      .map((n) => ({
        value: n.id,
        label: n.path
          .split('.')
          .map((id) => byId.get(Number(id))?.name ?? '?')
          .join(' › '),
      }))
      .sort((a, b) => a.label.localeCompare(b.label, 'hu'));
  });
  protected readonly levelOptions = computed(() => [
    { label: this.m.levels.outdoor, value: null as number | null },
    ...this.levels().map((l) => ({ label: l.name, value: l.id as number | null })),
  ]);

  /** Reset when opened, from the panorama as it is. */
  private readonly resetOnOpen = effect(() => {
    if (!this.visible()) return;
    untracked(() => {
      const p = this.panorama();
      const siteId = this.selection.siteId();
      this.nodeId.set(p?.nodeId ?? null);
      this.levelId.set(p?.levelId ?? null);
      this.position.set((p?.position.coordinates as LonLat | undefined) ?? null);
      if (siteId == null) return;
      this.api.tree(siteId, 'location').subscribe((nodes) => this.nodes.set(nodes));
      this.api.levels(siteId).subscribe((levels) => this.levels.set(levels));
    });
  });

  /** Another place: the level follows it (the deepest level in its path; above the levels the current one stays). */
  protected setNode(id: number | null): void {
    this.nodeId.set(id);
    const node = this.nodes().find((n) => n.id === id);
    if (!node) return;
    const levelIds = new Set(this.levels().map((l) => l.id));
    const path = node.path.split('.').map(Number);
    const level = [...path].reverse().find((x) => levelIds.has(x));
    if (level != null) this.levelId.set(level);
    else if (this.levelId() != null && !this.levelInside(this.levelId()!, node.id)) this.levelId.set(null);
  }

  private levelInside(levelId: number, nodeId: number): boolean {
    const level = this.nodes().find((n) => n.id === levelId);
    return !!level && level.path.split('.').map(Number).includes(nodeId);
  }

  protected pick(): void {
    this.tools.pickPoint((point) => this.position.set(point), this.m.panorama.pickHint);
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
          this.visible.set(false);
          this.toasts.add({ severity: 'success', summary: this.m.panorama.saved, life: 2500 });
          // The map shows it on its new level.
          if (saved.levelId !== this.selection.levelId()) this.selection.levelId.set(saved.levelId);
          this.selection.changed();
        },
        error: () => this.saving.set(false),
      });
  }
}
