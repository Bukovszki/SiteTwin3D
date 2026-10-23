import { KeyValuePipe } from '@angular/common';
import { Component, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { InputText } from 'primeng/inputtext';
import { Message } from 'primeng/message';
import { Tag } from 'primeng/tag';
import { ApiService } from '../../core/api/api.service';
import { errorCode, silent } from '../../core/api/errors';
import { Anchor, Node } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { Permissions } from '../../core/auth/permissions';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { SchemaForm } from './schema-form';

/**
 * Data sheet of the selected node: breadcrumb (FR-H7), name, code, type and the technical data as a form generated
 * from the type's JSON Schema; read only for viewers. A MapManager-linked record shows its live attributes from
 * MapManager, read only (spec 6.3); if MapManager is down, the sheet says so and the rest stays usable.
 */
@Component({
  selector: 'app-datasheet-panel',
  imports: [FormsModule, KeyValuePipe, Button, InputText, Message, Tag, SchemaForm],
  template: `
    @if (node(); as n) {
      <div class="crumbs">
        @for (a of ancestors(); track a.id) {
          <a (click)="selection.selectNode(a.id)">{{ a.name }}</a><i class="pi pi-angle-right"></i>
        }
        <b>{{ n.name }}</b>
      </div>
      <form class="form-grid body" (ngSubmit)="save()">
        <div class="field">
          <label>{{ m.node.type }}</label>
          <span>
            {{ type()?.name ?? n.typeCode }}
            @if (linked()) {
              <p-tag severity="info" icon="pi pi-link" [value]="m.grid.linked" />
            }
          </span>
        </div>
        <div class="row">
          <div class="field">
            <label for="ds-name">{{ m.node.name }}</label>
            <input pInputText id="ds-name" name="name" [(ngModel)]="name" [disabled]="!can.prepare()" />
          </div>
          <div class="field">
            <label for="ds-code">{{ m.node.code }}</label>
            <input pInputText id="ds-code" name="code" [(ngModel)]="code" [disabled]="!can.prepare()" />
          </div>
        </div>

        @if (linked()) {
          <h4>{{ m.datasheet.linkedTitle }}</h4>
          <small class="muted">{{ m.datasheet.layer }}: {{ n.attrs.mapmanager_ref?.layer }}</small>
          @if (linkedError(); as error) {
            <p-message severity="warn">{{ error }}</p-message>
          } @else if (linkedAttrs(); as attrs) {
            <table class="kv">
              @for (entry of attrs | keyvalue; track entry.key) {
                <tr><th>{{ entry.key }}</th><td>{{ entry.value ?? m.common.none }}</td></tr>
              }
            </table>
          } @else {
            <span class="muted">{{ m.common.loading }}</span>
          }
        } @else {
          <h4>{{ m.datasheet.technical }}</h4>
          @if (type()?.attrSchema; as schema) {
            <app-schema-form [schema]="schema" [(value)]="attrs" [readonly]="!can.prepare()" idPrefix="ds-attr-" />
          } @else {
            <span class="muted">{{ m.datasheet.noSchema }}</span>
          }
        }

        <h4>{{ m.infoPoint.anchors }}</h4>
        @for (a of anchors(); track a.id) {
          <div class="anchor">
            <i [class]="a.kind === 'panorama_point' ? 'pi pi-eye' : 'pi pi-map-marker'"></i>
            <span>{{ m.infoPoint.kinds[a.kind] }}@if (a.positionSource === 'panorama_fallback') { <small class="muted">*</small> }</span>
            <span class="spacer"></span>
            @if (a.panoramaId != null) {
              <p-button icon="pi pi-external-link" [text]="true" size="small" [ariaLabel]="m.infoPoint.open" (onClick)="selection.openPanorama(a.panoramaId)" />
            }
            @if (can.anchors()) {
              <p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" [ariaLabel]="m.infoPoint.delete" (onClick)="deleteAnchor(a)" />
            }
          </div>
        } @empty {
          <small class="muted">{{ m.infoPoint.noAnchors }}</small>
        }

        @if (can.prepare()) {
          <div class="actions">
            <p-button type="submit" [label]="m.common.save" icon="pi pi-save" [loading]="saving()" [disabled]="!name.trim()" />
          </div>
        }
      </form>
    } @else {
      <p class="muted empty">{{ m.datasheet.nothingSelected }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
      height: 100%;
      overflow: auto;
    }
    .crumbs {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.25rem;
      padding: 0.6rem 0.75rem;
      border-bottom: 1px solid var(--p-content-border-color);
      font-size: 0.85rem;
      a {
        color: var(--p-primary-color);
        cursor: pointer;
      }
      i {
        font-size: 0.7rem;
        color: var(--p-text-muted-color);
      }
    }
    .body {
      padding: 0.75rem;
    }
    h4 {
      margin: 0.5rem 0 0;
    }
    .kv {
      border-collapse: collapse;
      th,
      td {
        padding: 0.25rem 0.5rem;
        border-bottom: 1px solid var(--p-content-border-color);
        text-align: left;
        vertical-align: top;
      }
      th {
        color: var(--p-text-muted-color);
        font-weight: 500;
      }
    }
    .anchor {
      display: flex;
      align-items: center;
      gap: 0.4rem;
      .spacer {
        flex: 1;
      }
    }
    .actions {
      display: flex;
      justify-content: flex-end;
    }
    .empty {
      padding: 1rem;
    }
  `,
})
export class DatasheetPanel {
  protected readonly m = inject(MESSAGES);
  protected readonly auth = inject(AuthService);
  protected readonly can = inject(Permissions);
  protected readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  private readonly toasts = inject(MessageService);

  protected readonly node = signal<Node | null>(null);
  protected readonly ancestors = signal<Node[]>([]);
  protected readonly linkedAttrs = signal<Record<string, unknown> | null>(null);
  protected readonly linkedError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly anchors = signal<Anchor[]>([]);
  protected name = '';
  protected code = '';
  protected attrs: Record<string, unknown> = {};

  protected readonly type = computed(() => this.selection.typeOf(this.node()?.typeId));
  protected readonly linked = computed(() => !!this.node()?.attrs.mapmanager_ref);

  constructor() {
    effect(() => {
      const id = this.selection.nodeId();
      this.selection.version();
      untracked(() => this.load(id));
    });
  }

  private load(id: number | null): void {
    if (id == null) {
      this.show(null);
      return;
    }
    this.api.node(id).subscribe({
      next: (node) => {
        if (this.selection.nodeId() !== id) return;
        this.show(node);
        this.api.ancestors(id).subscribe((ancestors) => this.ancestors.set(ancestors));
        this.api.nodeAnchors(id).subscribe((anchors) => this.anchors.set(anchors));
        if (node.attrs.mapmanager_ref) this.loadLinked(node.id);
      },
      error: () => this.selection.selectNode(null),
    });
  }

  private show(node: Node | null): void {
    this.node.set(node);
    this.selection.node.set(node);
    this.ancestors.set([]);
    this.anchors.set([]);
    this.linkedAttrs.set(null);
    this.linkedError.set(null);
    this.name = node?.name ?? '';
    this.code = node?.code ?? '';
    const { mapmanager_ref: _, ...attrs } = node?.attrs ?? {};
    this.attrs = attrs;
  }

  private loadLinked(id: number): void {
    this.api.linkedAttributes(id, silent()).subscribe({
      next: (attrs) => this.linkedAttrs.set(attrs ?? {}),
      error: (err) =>
        this.linkedError.set(errorCode(err) === 'maps.feature_not_found' ? this.m.datasheet.orphan : this.m.datasheet.linkedUnavailable),
    });
  }

  protected deleteAnchor(anchor: Anchor): void {
    this.api.deleteAnchor(anchor.id).subscribe(() => this.selection.changed());
  }

  protected save(): void {
    const node = this.node();
    if (!node) return;
    const patch: Record<string, unknown> = { name: this.name.trim(), code: this.code.trim() || null };
    if (!this.linked() && this.type()?.attrSchema) patch['attrs'] = this.attrs;
    this.saving.set(true);
    this.api.updateNode(node.id, patch).subscribe({
      next: () => {
        this.saving.set(false);
        this.toasts.add({ severity: 'success', summary: this.m.common.saved, life: 2500 });
        this.selection.changed();
      },
      error: () => this.saving.set(false),
    });
  }
}
