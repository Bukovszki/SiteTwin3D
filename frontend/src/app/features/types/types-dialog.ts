import { Component, computed, effect, inject, model, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Checkbox } from 'primeng/checkbox';
import { Dialog } from 'primeng/dialog';
import { InputNumber } from 'primeng/inputnumber';
import { InputText } from 'primeng/inputtext';
import { Message } from 'primeng/message';
import { MultiSelect } from 'primeng/multiselect';
import { Select } from 'primeng/select';
import { Tag } from 'primeng/tag';
import { Textarea } from 'primeng/textarea';
import { Tooltip } from 'primeng/tooltip';
import { Observable } from 'rxjs';
import { ApiService } from '../../core/api/api.service';
import { NodeType } from '../../core/api/models';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import {
  FIELD_KINDS,
  FieldDraft,
  fieldErrors,
  keyFromTitle,
  toDrafts,
  toSchema,
} from './type-fields';

/** What the editor shows: nothing, a new own type, or an existing type. */
type Editing = { kind: 'none' } | { kind: 'new' } | { kind: 'type'; type: NodeType };

/**
 * Node types and their fields (FR-H2, FR-H5; organization admin): new own types, and fields – the organization's own
 * ones added to a shared base type (user decision: the base type itself stays as it is), or an own type's fields. The
 * form, the grid columns and the import of every record of the type follow the fields; no code change.
 */
@Component({
  selector: 'app-types-dialog',
  imports: [
    FormsModule,
    Dialog,
    Button,
    Checkbox,
    InputNumber,
    InputText,
    Message,
    MultiSelect,
    Select,
    Tag,
    Textarea,
    Tooltip,
  ],
  template: `
    <p-dialog
      [header]="m.types.title"
      [(visible)]="visible"
      [modal]="true"
      [style]="{ width: '70rem' }"
      [maximizable]="true"
    >
      <div class="layout">
        <aside class="list">
          <p-button
            [label]="m.types.new"
            icon="pi pi-plus"
            [text]="true"
            size="small"
            (onClick)="open({ kind: 'new' })"
          />
          @for (group of groups(); track group.aspect) {
            <h5>{{ m.aspects[group.aspect] }}</h5>
            @for (t of group.types; track t.id) {
              <button
                type="button"
                class="item"
                [class.active]="activeId() === t.id"
                (click)="open({ kind: 'type', type: t })"
              >
                <span>{{ t.name }}</span>
                <small
                  >{{ t.shared ? m.types.shared : m.types.own }} ·
                  {{ m.types.fieldCount(fieldCount(t)) }}</small
                >
              </button>
            }
          }
        </aside>

        <section class="editor">
          @switch (editing().kind) {
            @case ('none') {
              <p class="muted">{{ m.types.hint }}</p>
            }
            @default {
              @if (editing().kind === 'new') {
                <h4>{{ m.types.new }}</h4>
                <div class="row">
                  <div class="field">
                    <label for="ty-name">{{ m.types.name }}</label>
                    <input
                      pInputText
                      id="ty-name"
                      [ngModel]="name()"
                      (ngModelChange)="setName($event)"
                    />
                  </div>
                  <div class="field">
                    <label for="ty-code">{{ m.types.code }}</label>
                    <input
                      pInputText
                      id="ty-code"
                      [ngModel]="code()"
                      (ngModelChange)="code.set($event); dirty.set(true)"
                    />
                    <small class="muted">{{ m.types.codeHint }}</small>
                  </div>
                </div>
                <div class="row">
                  <div class="field">
                    <label for="ty-aspect">{{ m.types.aspect }}</label>
                    <p-select
                      inputId="ty-aspect"
                      [options]="aspects"
                      optionLabel="label"
                      optionValue="value"
                      [ngModel]="aspect()"
                      (ngModelChange)="aspect.set($event); isLevel.set(false)"
                      appendTo="body"
                      fluid
                    />
                  </div>
                  @if (aspect() === 'location') {
                    <label class="check"
                      ><p-checkbox
                        [binary]="true"
                        [ngModel]="isLevel()"
                        (ngModelChange)="isLevel.set($event)"
                      />
                      {{ m.types.isLevel }}</label
                    >
                  }
                </div>
              } @else if (current(); as t) {
                <div class="head">
                  @if (t.shared) {
                    <h4>{{ t.name }}</h4>
                  } @else {
                    <input
                      pInputText
                      class="title-input"
                      [ngModel]="name()"
                      (ngModelChange)="setName($event)"
                      [attr.aria-label]="m.types.name"
                    />
                  }
                  <p-tag
                    [value]="t.shared ? m.types.shared : m.types.own"
                    [severity]="t.shared ? 'secondary' : 'info'"
                  />
                  <small class="muted"
                    >{{ m.aspects[t.aspect] }} · {{ t.code
                    }}{{ t.isLevel ? ' · ' + m.types.level : '' }}</small
                  >
                </div>
                @if (t.shared) {
                  <p class="muted">{{ m.types.sharedHint }}</p>
                  @if (baseFields().length) {
                    <h5>{{ m.types.baseFields }}</h5>
                    <ul class="base">
                      @for (f of baseFields(); track f.key) {
                        <li>
                          {{ f.title }}
                          <small class="muted"
                            >· {{ m.types.kinds[f.kind]
                            }}{{ f.required ? ' · ' + m.types.required : '' }}</small
                          >
                        </li>
                      }
                    </ul>
                  }
                }
              }

              <!-- Where records of the type may be in the tree (an own type: set here; a shared one: fixed). -->
              @if (current()?.shared) {
                <p class="muted">{{ m.types.parents }}: {{ parentsText(current()!) }}</p>
              } @else {
                <div class="field">
                  <label for="ty-parents">{{ m.types.parents }}</label>
                  <p-multiselect
                    inputId="ty-parents"
                    [options]="parentOptions()"
                    optionLabel="label"
                    optionValue="value"
                    [ngModel]="parents()"
                    (ngModelChange)="parents.set($event); dirty.set(true)"
                    [placeholder]="m.types.anywhere"
                    display="chip"
                    appendTo="body"
                    fluid
                  />
                  <small class="muted">{{ m.types.parentsHint }}</small>
                </div>
              }

              <h5>{{ current()?.shared ? m.types.ownFields : m.types.fields }}</h5>
              @if (fields().length === 0) {
                <p class="muted">{{ m.types.noFields }}</p>
              }
              @for (f of fields(); track $index; let i = $index) {
                <div class="field-row" [class.invalid]="hasError(i)">
                  <input
                    pInputText
                    [ngModel]="f.title"
                    (ngModelChange)="setTitle(i, $event)"
                    [placeholder]="m.types.fieldTitle"
                    [attr.aria-label]="m.types.fieldTitle"
                    class="f-title"
                  />
                  <p-select
                    [options]="kinds"
                    optionLabel="label"
                    optionValue="value"
                    [ngModel]="f.kind"
                    (ngModelChange)="patch(i, { kind: $event })"
                    size="small"
                    appendTo="body"
                    [ariaLabel]="m.types.kind"
                    styleClass="f-kind"
                  />
                  <label class="check"
                    ><p-checkbox
                      [binary]="true"
                      [ngModel]="f.required"
                      (ngModelChange)="patch(i, { required: $event })"
                    />
                    {{ m.types.required }}</label
                  >
                  <span class="spacer"></span>
                  <p-button
                    icon="pi pi-arrow-up"
                    [text]="true"
                    size="small"
                    severity="secondary"
                    [disabled]="i === 0"
                    [ariaLabel]="m.types.up"
                    (onClick)="move(i, -1)"
                  />
                  <p-button
                    icon="pi pi-arrow-down"
                    [text]="true"
                    size="small"
                    severity="secondary"
                    [disabled]="i === fields().length - 1"
                    [ariaLabel]="m.types.down"
                    (onClick)="move(i, 1)"
                  />
                  <p-button
                    icon="pi pi-trash"
                    [text]="true"
                    size="small"
                    severity="danger"
                    [pTooltip]="f.saved ? m.types.removeSavedHint : ''"
                    [ariaLabel]="m.common.delete"
                    (onClick)="remove(i)"
                  />
                  @if (f.kind === 'choice') {
                    <textarea
                      pTextarea
                      rows="3"
                      class="f-extra"
                      [ngModel]="f.options"
                      (ngModelChange)="patch(i, { options: $event })"
                      [placeholder]="m.types.optionsHint"
                      [attr.aria-label]="m.types.options"
                    ></textarea>
                  }
                  @if (f.kind === 'integer' || f.kind === 'number') {
                    <div class="f-extra range">
                      <p-inputnumber
                        [ngModel]="f.min"
                        (ngModelChange)="patch(i, { min: $event })"
                        [placeholder]="m.types.min"
                        mode="decimal"
                        [maxFractionDigits]="6"
                        locale="hu-HU"
                        size="small"
                        [ariaLabel]="m.types.min"
                      />
                      <p-inputnumber
                        [ngModel]="f.max"
                        (ngModelChange)="patch(i, { max: $event })"
                        [placeholder]="m.types.max"
                        mode="decimal"
                        [maxFractionDigits]="6"
                        locale="hu-HU"
                        size="small"
                        [ariaLabel]="m.types.max"
                      />
                    </div>
                  }
                  <small class="muted f-key"
                    >{{ m.types.key }}: {{ f.key
                    }}{{ f.saved ? '' : ' (' + m.types.newField + ')' }}</small
                  >
                </div>
              }
              <p-button
                [label]="m.types.addField"
                icon="pi pi-plus"
                [text]="true"
                size="small"
                (onClick)="addField()"
              />

              @if (errors().length) {
                <p-message severity="warn" size="small">{{ errorText() }}</p-message>
              }
              <small class="muted">{{ m.types.changeHint }}</small>
              <div class="actions">
                @if (current(); as t) {
                  @if (!t.shared) {
                    <p-button
                      [label]="m.types.delete"
                      icon="pi pi-trash"
                      severity="danger"
                      [text]="true"
                      (onClick)="deleteType(t)"
                    />
                  }
                }
                <span class="spacer"></span>
                <p-button
                  [label]="m.common.save"
                  icon="pi pi-save"
                  [loading]="saving()"
                  [disabled]="!canSave()"
                  (onClick)="save()"
                />
              </div>
            }
          }
        </section>
      </div>
    </p-dialog>
  `,
  styles: `
    .layout {
      display: grid;
      grid-template-columns: 16rem 1fr;
      gap: 1rem;
      min-height: 28rem;
    }
    .list {
      border-right: 1px solid var(--p-content-border-color);
      padding-right: 0.75rem;
      overflow: auto;
    }
    h5 {
      margin: 0.75rem 0 0.25rem;
      color: var(--p-text-muted-color);
    }
    .item {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
      width: 100%;
      padding: 0.35rem 0.5rem;
      border: 0;
      border-radius: 6px;
      background: none;
      color: inherit;
      font: inherit;
      text-align: left;
      cursor: pointer;
      &:hover {
        background: var(--p-content-hover-background);
      }
      &.active {
        background: var(--p-highlight-background);
        color: var(--p-highlight-color);
      }
      small {
        color: var(--p-text-muted-color);
      }
    }
    .editor {
      display: flex;
      flex-direction: column;
      gap: 0.5rem;
      min-width: 0;
    }
    .head {
      display: flex;
      align-items: center;
      gap: 0.6rem;
      flex-wrap: wrap;
      h4 {
        margin: 0;
      }
    }
    .title-input {
      font-weight: 600;
    }
    .row {
      display: flex;
      gap: 1rem;
      align-items: flex-end;
      .field {
        flex: 1;
        display: flex;
        flex-direction: column;
        gap: 0.25rem;
      }
    }
    .base {
      margin: 0;
      padding-left: 1.2rem;
    }
    .field-row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.5rem;
      padding: 0.5rem;
      border: 1px solid var(--p-content-border-color);
      border-radius: 6px;
      &.invalid {
        border-color: var(--p-orange-400);
      }
    }
    .f-title {
      flex: 1 1 14rem;
    }
    :host ::ng-deep .f-kind {
      width: 11rem;
    }
    .f-extra {
      flex: 1 1 100%;
    }
    .range {
      display: flex;
      gap: 0.5rem;
    }
    .f-key {
      flex: 1 1 100%;
    }
    .check {
      display: flex;
      align-items: center;
      gap: 0.35rem;
      white-space: nowrap;
    }
    .spacer {
      flex: 1;
    }
    .actions {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      margin-top: 0.5rem;
    }
  `,
})
export class TypesDialog {
  protected readonly m = inject(MESSAGES);
  private readonly api = inject(ApiService);
  private readonly selection = inject(SelectionService);
  private readonly confirm = inject(ConfirmationService);
  private readonly toasts = inject(MessageService);

  readonly visible = model(false);

  protected readonly editing = signal<Editing>({ kind: 'none' });
  protected readonly fields = signal<FieldDraft[]>([]);
  protected readonly name = signal('');
  protected readonly code = signal('');
  protected readonly aspect = signal<'location' | 'function'>('location');
  protected readonly isLevel = signal(false);
  /** Allowed parent type codes ('' = top of the tree); empty: anywhere. */
  protected readonly parents = signal<string[]>([]);
  protected readonly dirty = signal(false);
  protected readonly saving = signal(false);

  protected readonly aspects = [
    { label: this.m.aspects['location'], value: 'location' },
    { label: this.m.aspects['function'], value: 'function' },
  ];
  protected readonly kinds = FIELD_KINDS.map((k) => ({ label: this.m.types.kinds[k], value: k }));

  /** Types by aspect, shared ones first (as the API lists them). */
  protected readonly groups = computed(() =>
    (['location', 'function'] as const).map((aspect) => ({
      aspect,
      types: this.selection.nodeTypes().filter((t) => t.aspect === aspect),
    })),
  );
  protected readonly current = computed(() => {
    const e = this.editing();
    return e.kind === 'type'
      ? (this.selection.nodeTypes().find((t) => t.id === e.type.id) ?? e.type)
      : null;
  });
  protected readonly activeId = computed(() => this.current()?.id ?? null);
  /** A shared type's own fields (read only here); their names cannot be used again. */
  protected readonly baseFields = computed(() =>
    this.current()?.shared ? toDrafts(this.current()!.baseSchema) : [],
  );
  protected readonly errors = computed(() =>
    fieldErrors(
      this.fields(),
      this.baseFields().map((f) => f.key),
    ),
  );
  protected readonly canSave = computed(() => {
    if (this.saving() || this.errors().length > 0) return false;
    if (this.editing().kind === 'new')
      return !!this.name().trim() && /^[a-z][a-z0-9_]{1,63}$/.test(this.code());
    return this.dirty() && (!!this.current()?.shared || !!this.name().trim());
  });

  private readonly loadOnOpen = effect(() => {
    if (this.visible())
      untracked(() => {
        this.selection.loadNodeTypes();
        this.editing.set({ kind: 'none' });
      });
  });

  /** The top of the tree and the types of the same tree (the type itself too: e.g. a zone in a zone). */
  protected readonly parentOptions = computed(() => {
    const own = this.current();
    const code = own?.code ?? this.code();
    const aspect = own?.aspect ?? this.aspect();
    return [
      { label: this.m.types.top, value: '' },
      ...this.selection
        .nodeTypes()
        .filter((t) => t.aspect === aspect && t.code !== code)
        .map((t) => ({ label: t.name, value: t.code })),
      ...(code ? [{ label: this.m.types.itself, value: code }] : []),
    ];
  });

  protected parentsText(t: NodeType): string {
    if (!t.allowedParents) return this.m.types.anywhere;
    const names = new Map(this.selection.nodeTypes().map((x) => [x.code, x.name]));
    return t.allowedParents
      .map((p) => (p === '' ? this.m.types.top : p === t.code ? this.m.types.itself : (names.get(p) ?? p)))
      .join(', ');
  }

  protected fieldCount(t: NodeType): number {
    return Object.keys(t.attrSchema?.properties ?? {}).length;
  }

  protected hasError(index: number): boolean {
    return this.errors().some((e) => e.index === index);
  }

  protected errorText(): string {
    const e = this.errors()[0];
    return this.m.types.errors[e.problem](e.index + 1);
  }

  /** Opens a type (or a new one); unsaved changes are asked about first. */
  protected open(target: Editing): void {
    const go = () => {
      this.editing.set(target);
      const type = target.kind === 'type' ? target.type : null;
      this.fields.set(
        toDrafts(type ? (type.shared ? type.extensionSchema : type.attrSchema) : null),
      );
      this.name.set(type?.name ?? '');
      this.code.set('');
      this.aspect.set(type?.aspect ?? 'location');
      this.isLevel.set(false);
      this.parents.set(type?.allowedParents ?? []);
      this.dirty.set(false);
    };
    if (!this.dirty()) {
      go();
      return;
    }
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.types.discardConfirm,
      acceptButtonProps: { label: this.m.common.yes },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: go,
    });
  }

  protected setName(name: string): void {
    this.name.set(name);
    this.dirty.set(true);
    // A new type's code follows its name until it is typed in.
    if (this.editing().kind === 'new')
      this.code.set(
        keyFromTitle(
          name,
          this.selection.nodeTypes().map((t) => t.code),
        ),
      );
  }

  protected setTitle(index: number, title: string): void {
    const field = this.fields()[index];
    const others = [...this.baseFields(), ...this.fields().filter((_, i) => i !== index)].map(
      (f) => f.key,
    );
    // An unsaved field's key follows its title; a saved one keeps its key (its values are stored under it).
    this.patch(index, { title, ...(field.saved ? {} : { key: keyFromTitle(title, others) }) });
  }

  protected patch(index: number, change: Partial<FieldDraft>): void {
    this.fields.update((list) => list.map((f, i) => (i === index ? { ...f, ...change } : f)));
    this.dirty.set(true);
  }

  protected addField(): void {
    const taken = [...this.baseFields(), ...this.fields()].map((f) => f.key);
    this.fields.update((list) => [
      ...list,
      {
        key: keyFromTitle(this.m.types.newField, taken),
        title: '',
        kind: 'text',
        required: false,
        options: '',
        min: null,
        max: null,
        saved: false,
      },
    ]);
    this.dirty.set(true);
  }

  protected move(index: number, by: number): void {
    this.fields.update((list) => {
      const next = [...list];
      const [field] = next.splice(index, 1);
      next.splice(index + by, 0, field);
      return next;
    });
    this.dirty.set(true);
  }

  protected remove(index: number): void {
    this.fields.update((list) => list.filter((_, i) => i !== index));
    this.dirty.set(true);
  }

  protected save(): void {
    const schema = toSchema(this.fields());
    const editing = this.editing();
    let request: Observable<NodeType>;
    if (editing.kind === 'new')
      request = this.api.createNodeType({
        code: this.code(),
        name: this.name().trim(),
        aspect: this.aspect(),
        isLevel: this.aspect() === 'location' && this.isLevel(),
        attrSchema: schema,
        allowedParents: this.parents().length ? this.parents() : null,
      });
    else {
      const type = this.current()!;
      request = type.shared
        ? this.api.setNodeTypeExtension(type.id, schema)
        : this.api.updateNodeType(type.id, {
            name: this.name().trim(),
            attrSchema: schema,
            allowedParents: this.parents().length ? this.parents() : null,
          });
    }
    this.saving.set(true);
    request.subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.toasts.add({ severity: 'success', summary: this.m.types.saved, life: 2500 });
        // The form, the grid and the import of the type's records follow the new fields.
        this.selection.loadNodeTypes();
        this.dirty.set(false);
        this.open({ kind: 'type', type: saved });
      },
      error: () => this.saving.set(false),
    });
  }

  protected deleteType(type: NodeType): void {
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.types.deleteConfirm(type.name),
      acceptButtonProps: { label: this.m.common.delete, severity: 'danger' },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () =>
        this.api.deleteNodeType(type.id).subscribe(() => {
          this.toasts.add({ severity: 'success', summary: this.m.types.deleted, life: 2500 });
          this.selection.loadNodeTypes();
          this.dirty.set(false);
          this.editing.set({ kind: 'none' });
        }),
    });
  }
}
