import { HttpClient, HttpHeaders } from '@angular/common/http';
import {
  Component,
  computed,
  effect,
  inject,
  model,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Dialog } from 'primeng/dialog';
import { Checkbox } from 'primeng/checkbox';
import { InputNumber } from 'primeng/inputnumber';
import { InputText } from 'primeng/inputtext';
import { Message } from 'primeng/message';
import { Select } from 'primeng/select';
import { SelectButton } from 'primeng/selectbutton';
import { TableModule } from 'primeng/table';
import { Tag } from 'primeng/tag';
import { ApiService } from '../core/api/api.service';
import { errorMessage, silent } from '../core/api/errors';
import { ModelAsset, ModelPlacement, Node } from '../core/api/models';
import { MESSAGES } from '../core/i18n/messages';
import { SelectionService } from '../core/selection/selection.service';

const MESH = ['.obj', '.fbx', '.dae', '.stl', '.ply', '.3ds', '.x3d', '.off', '.gltf'];
const POINT_CLOUDS = ['.las', '.laz', '.e57'];
/** Files that may come with a mesh: materials, textures, buffers. */
const COMPANIONS = [
  '.mtl',
  '.png',
  '.jpg',
  '.jpeg',
  '.tga',
  '.bmp',
  '.tif',
  '.tiff',
  '.bin',
  '.webp',
];
const ext = (path: string) => path.slice(path.lastIndexOf('.')).toLowerCase();

/** The main file of an upload (the API applies the same rules). */
function detectMain(paths: string[]): string | null {
  if (paths.includes('tileset.json')) return 'tileset.json';
  const mains = paths.filter((p) => ['.glb', '.ifc', ...MESH, ...POINT_CLOUDS].includes(ext(p)));
  return mains.length === 1 ? mains[0] : null;
}

function detectFormat(paths: string[]): string | null {
  // A ZIP of a model with its files: the converter unpacks it and finds the model in it.
  if (paths.length === 1 && ext(paths[0]) === '.zip') return 'zip';
  const main = detectMain(paths);
  if (!main) return null;
  if (main === 'tileset.json') return '3dtiles';
  const format =
    ext(main) === '.glb'
      ? 'glb'
      : ext(main) === '.ifc'
        ? 'ifc'
        : POINT_CLOUDS.includes(ext(main))
          ? 'pointcloud'
          : 'mesh';
  const others = paths.filter((p) => p !== main);
  return others.length === 0 ||
    (format === 'mesh' && others.every((p) => COMPANIONS.includes(ext(p))))
    ? format
    : null;
}

/** Parallel signed PUTs of a 3D Tiles package. */
const PARALLEL_UPLOADS = 4;

interface PendingFile {
  path: string;
  file: File;
}

/**
 * 3D models of the site (FR-I1): a single GLB (placed by WGS84 point, height above the site base and heading) or a
 * ready 3D Tiles package as a folder (georeferenced, tileset.json at its root). Every file goes straight to the object
 * store with a signed URL (NFR-3). Conversion from IFC / DWG / photogrammetry is done outside (spec 9.2).
 */
@Component({
  selector: 'app-models-dialog',
  imports: [
    FormsModule,
    Select,
    Button,
    Checkbox,
    Dialog,
    InputNumber,
    InputText,
    Message,
    SelectButton,
    TableModule,
    Tag,
  ],
  template: `
    <p-dialog
      [header]="m.models.title"
      [(visible)]="visible"
      [modal]="true"
      [style]="{ width: '50rem' }"
    >
      <p-table
        [value]="models()"
        size="small"
        dataKey="id"
        selectionMode="single"
        [selection]="edited()"
        (onRowSelect)="edit($any($event.data))"
      >
        <ng-template #header>
          <tr>
            <th>{{ m.models.name }}</th>
            <th>{{ m.models.source }}</th>
            <th>{{ m.models.placement }}</th>
            <th>{{ m.models.status }}</th>
            <th></th>
          </tr>
        </ng-template>
        <ng-template #body let-x>
          <tr [pSelectableRow]="x">
            <td>{{ x.name }}</td>
            <td>
              {{ m.models.kinds[x.kind] }}
              @if (x.segmented) {
                · {{ m.models.segmented }}
              }
            </td>
            <td>
              @if (x.placement; as p) {
                {{ p.lon.toFixed(6) }}, {{ p.lat.toFixed(6) }} · {{ p.heightM }} m ·
                {{ p.headingDeg }}°
              } @else {
                <span class="muted">{{ m.models.georeferenced }}</span>
              }
            </td>
            <td>
              @switch (x.status) {
                @case ('converting') {
                  <p-tag
                    severity="info"
                    icon="pi pi-spin pi-spinner"
                    [value]="m.models.converting"
                  />
                }
                @case ('failed') {
                  <p-tag
                    severity="danger"
                    [value]="m.models.failed"
                    [title]="m.models.message(x.processingError)"
                  />
                }
                @default {
                  <p-tag severity="success" [value]="m.models.ready" />
                }
              }
            </td>
            <td class="right">
              <p-button
                icon="pi pi-trash"
                [text]="true"
                size="small"
                severity="danger"
                [ariaLabel]="m.models.delete"
                (onClick)="remove(x)"
              />
            </td>
          </tr>
        </ng-template>
        <ng-template #emptymessage
          ><tr>
            <td colspan="5" class="muted">{{ m.models.empty }}</td>
          </tr></ng-template
        >
      </p-table>

      <div class="form-grid editor">
        <h4>{{ edited()?.name ?? m.models.upload }}</h4>
        @if (!edited()) {
          <p-selectbutton
            [options]="sources"
            optionLabel="label"
            optionValue="value"
            [(ngModel)]="source"
            [allowEmpty]="false"
            size="small"
          />
          @if (source === 'file') {
            <input
              id="model-file"
              type="file"
              [accept]="accept"
              multiple
              (change)="onFiles($event)"
            />
            <small class="muted">{{ m.models.fileHint }}</small>
            @if (files().length) {
              @if (format(); as f) {
                <p-message severity="secondary" size="small">{{ m.models.formats[f] }}</p-message>
              } @else {
                <p-message severity="warn" size="small">{{ m.models.unknownFiles }}</p-message>
              }
            }
          } @else {
            <input
              id="model-folder"
              type="file"
              webkitdirectory
              multiple
              (change)="onFiles($event)"
            />
            <small class="muted">{{ m.models.tilesHint }}</small>
          }
          <div class="field">
            <label for="model-name">{{ m.models.name }}</label
            ><input pInputText id="model-name" [(ngModel)]="name" />
          </div>
        }
        <div class="field">
          <label for="model-node">{{ m.models.node }}</label>
          <p-select
            inputId="model-node"
            [options]="places()"
            optionLabel="label"
            optionValue="value"
            [(ngModel)]="nodeId"
            [filter]="true"
            filterBy="label"
            [showClear]="true"
            [placeholder]="m.models.nodeNone"
            appendTo="body"
            fluid
          />
          <small class="muted">{{ m.models.nodeHint }}</small>
        </div>
        @if (!edited() && (format() === 'pointcloud' || format() === 'zip')) {
          <div class="field">
            <label for="model-srs">{{ m.models.srs }}</label>
            <input pInputText id="model-srs" [(ngModel)]="srs" placeholder="EPSG:23700" />
            <small class="muted">{{ m.models.srsHint }}</small>
          </div>
        }
        @if (!edited() && placementOptional()) {
          <label class="check"
            ><p-checkbox [binary]="true" [(ngModel)]="placeIt" /> {{ m.models.placeIt }}</label
          >
        }
        @if (showElementId()) {
          <div class="field">
            <label for="model-element-id">{{ m.models.elementIdProperty }}</label>
            <input
              pInputText
              id="model-element-id"
              [(ngModel)]="elementIdProperty"
              placeholder="element_id"
            />
            <small class="muted">{{ m.models.elementIdHint }}</small>
          </div>
        }
        @if (edited()?.conversion; as c) {
          <small class="muted">{{ conversionText(edited()!) }}</small>
        }
        @if (showPlacement()) {
          <label>{{ m.models.placement }}</label>
          <div class="row">
            <div class="field">
              <label for="m-lon">{{ m.models.lon }}</label
              ><p-inputnumber
                inputId="m-lon"
                [(ngModel)]="placement.lon"
                [maxFractionDigits]="7"
                [min]="-180"
                [max]="180"
              />
            </div>
            <div class="field">
              <label for="m-lat">{{ m.models.lat }}</label
              ><p-inputnumber
                inputId="m-lat"
                [(ngModel)]="placement.lat"
                [maxFractionDigits]="7"
                [min]="-90"
                [max]="90"
              />
            </div>
            <div class="field">
              <label for="m-h">{{ m.models.height }}</label
              ><p-inputnumber
                inputId="m-h"
                [(ngModel)]="placement.heightM"
                [maxFractionDigits]="3"
              />
            </div>
            <div class="field">
              <label for="m-hd">{{ m.models.heading }}</label
              ><p-inputnumber
                inputId="m-hd"
                [(ngModel)]="placement.headingDeg"
                [maxFractionDigits]="2"
                [min]="0"
                [max]="360"
              />
            </div>
          </div>
        }
        @if (error()) {
          <p-message severity="error" size="small">{{ error() }}</p-message>
        }
        @if (checkResult(); as text) {
          <p-message severity="info" size="small">{{ text }}</p-message>
        }
        <small class="muted">{{ m.models.processing }}</small>
        <div class="actions">
          @if (edited(); as x) {
            <p-button [label]="m.models.upload" [text]="true" (onClick)="edit(null)" />
            @if (x.segmented) {
              <p-button
                [label]="m.models.check"
                icon="pi pi-check-square"
                severity="secondary"
                [outlined]="true"
                [loading]="busy()"
                (onClick)="check(x)"
              />
            }
            <p-button [label]="m.common.save" icon="pi pi-save" (onClick)="save(x)" />
          } @else {
            <span class="muted">{{ progress() }}</span>
            <p-button
              [label]="m.common.add"
              icon="pi pi-upload"
              [loading]="busy()"
              [disabled]="files().length === 0 || !name.trim() || (source === 'file' && !format())"
              (onClick)="upload()"
            />
          }
        </div>
      </div>
    </p-dialog>
  `,
  styles: `
    .editor {
      margin-top: 1rem;
      h4 {
        margin: 0;
      }
    }
    .row {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 0.5rem;
    }
    .actions {
      display: flex;
      align-items: center;
      justify-content: flex-end;
      gap: 0.5rem;
    }
    .right {
      text-align: right;
    }
    .check {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
    :host ::ng-deep .row .p-inputnumber input {
      width: 100%;
    }
  `,
})
export class ModelsDialog {
  protected readonly m = inject(MESSAGES);
  private readonly selection = inject(SelectionService);
  private readonly api = inject(ApiService);
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(MessageService);
  private readonly confirm = inject(ConfirmationService);

  readonly visible = model(false);
  readonly changed = output<void>();

  protected readonly models = signal<ModelAsset[]>([]);
  protected readonly edited = signal<ModelAsset | null>(null);
  protected readonly files = signal<PendingFile[]>([]);
  protected readonly busy = signal(false);
  protected readonly progress = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly checkResult = signal<string | null>(null);
  protected readonly sources = [
    { value: 'file', label: this.m.models.files },
    { value: 'tiles', label: this.m.models.tiles },
  ];
  protected source: 'file' | 'tiles' = 'file';
  protected readonly accept = [
    '.zip',
    '.glb',
    '.ifc',
    ...MESH,
    ...COMPANIONS,
    ...POINT_CLOUDS,
  ].join(',');
  protected srs = '';
  protected placeIt = false;
  /** What the chosen files are (same rules as the API): glb | ifc | mesh | pointcloud | 3dtiles. */
  protected readonly format = computed(() => detectFormat(this.files().map((f) => f.path)));
  /** IFC and point clouds may place themselves (georeference / coordinate system). */
  protected readonly placementOptional = computed(
    () => this.format() === 'ifc' || this.format() === 'pointcloud' || this.format() === 'zip',
  );
  protected name = '';
  /** The location node the model belongs to (data tree; null: the whole site). */
  protected nodeId: number | null = null;
  private readonly locations = signal<Node[]>([]);
  /** Location nodes with their path (building › level › room), in tree order. */
  protected readonly places = computed(() => {
    const byId = new Map(this.locations().map((n) => [n.id, n]));
    const path = (n: Node): string[] => {
      const parent = n.parentId != null ? byId.get(n.parentId) : undefined;
      return parent ? [...path(parent), n.name] : [n.name];
    };
    return this.locations()
      .map((n) => ({ value: n.id, label: path(n).join(' › ') }))
      .sort((a, b) => a.label.localeCompare(b.label, 'hu'));
  });
  protected elementIdProperty = '';
  protected placement: ModelPlacement = { lon: 0, lat: 0, heightM: 0, headingDeg: 0 };

  private readonly loadOnOpen = effect(() => {
    if (this.visible())
      untracked(() => {
        this.load();
        this.edit(null);
      });
  });

  private load(): void {
    const siteId = this.selection.siteId();
    if (siteId == null) return;
    if (this.locations()[0]?.siteId !== siteId)
      this.api.tree(siteId, 'location').subscribe((nodes) => this.locations.set(nodes));
    this.api.models(siteId).subscribe((models) => {
      const finished = this.models().some(
        (old) =>
          old.status === 'converting' &&
          models.find((x) => x.id === old.id)?.status !== 'converting',
      );
      this.models.set(models);
      const edited = this.edited();
      if (edited) this.edited.set(models.find((x) => x.id === edited.id) ?? null);
      if (finished) this.changed.emit();
      // Conversions run in the background: look again while one is running and the dialog is open.
      if (models.some((x) => x.status === 'converting') && this.visible())
        setTimeout(() => this.visible() && this.load(), 3000);
    });
  }

  protected showElementId(): boolean {
    const edited = this.edited();
    if (edited)
      return (
        edited.sourceFormat == null ||
        edited.sourceFormat === 'ifc' ||
        edited.sourceFormat === 'glb'
      );
    return this.source === 'tiles' || this.format() === 'glb';
  }

  protected showPlacement(): boolean {
    const edited = this.edited();
    if (edited) return edited.status !== 'converting' && edited.placement != null;
    const format = this.format();
    return format === 'glb' || format === 'mesh' || (this.placementOptional() && this.placeIt);
  }

  protected conversionText(model: ModelAsset): string {
    const c = model.conversion!;
    return this.m.models.conversionInfo(
      c.source,
      c.elements ?? null,
      c.points ?? null,
      c.crs ?? null,
      c.warnings ?? [],
    );
  }

  protected edit(model: ModelAsset | null): void {
    this.edited.set(model);
    this.error.set(null);
    this.checkResult.set(null);
    this.files.set([]);
    this.name = '';
    this.elementIdProperty = model?.elementIdProperty ?? '';
    // A new model: where the user is in the location tree.
    const selected = this.selection.node();
    this.nodeId = model ? model.nodeId : selected?.aspect === 'location' ? selected.id : null;
    this.srs = '';
    this.placeIt = false;
    const [lon, lat] = this.selection.site()?.refPoint.coordinates ?? [0, 0];
    this.placement = { ...(model?.placement ?? { lon, lat, heightM: 0, headingDeg: 0 }) };
  }

  /** Folder upload: paths relative to the chosen folder (its own name dropped). */
  protected onFiles(event: Event): void {
    const list = Array.from((event.target as HTMLInputElement).files ?? []);
    this.files.set(
      list.map((file) => {
        const relative =
          (file as File & { webkitRelativePath?: string }).webkitRelativePath || file.name;
        return {
          path:
            this.source === 'tiles'
              ? relative.split('/').slice(1).join('/') || file.name
              : file.name,
          file,
        };
      }),
    );
    if (!this.name.trim() && list.length > 0) {
      const first = (list[0] as File & { webkitRelativePath?: string }).webkitRelativePath?.split(
        '/',
      )[0];
      const main =
        this.files().find((f) => f.path === detectMain(this.files().map((x) => x.path)))?.path ??
        list[0].name;
      this.name = this.source === 'tiles' && first ? first : main.replace(/\.[^.]+$/, '');
    }
  }

  protected async upload(): Promise<void> {
    const siteId = this.selection.siteId();
    const files = this.files();
    if (siteId == null || files.length === 0) return;
    this.busy.set(true);
    this.error.set(null);
    try {
      const ticket = await firstValueFrom(
        this.api.modelUpload(
          siteId,
          files.map((f) => ({ path: f.path, size: f.file.size })),
          silent(),
        ),
      );
      const byPath = new Map(files.map((f) => [f.path, f.file]));
      let done = 0;
      const queue = [...ticket.files];
      const worker = async () => {
        for (let next = queue.shift(); next; next = queue.shift()) {
          await firstValueFrom(
            this.http.put(next.url, byPath.get(next.path), {
              headers: new HttpHeaders({ 'Content-Type': next.contentType }),
              context: silent(),
            }),
          );
          this.progress.set(this.m.models.uploading(++done, ticket.files.length));
        }
      };
      await Promise.all(Array.from({ length: Math.min(PARALLEL_UPLOADS, queue.length) }, worker));
      await firstValueFrom(
        this.api.createModel(
          siteId,
          {
            prefix: ticket.prefix,
            name: this.name.trim(),
            nodeId: this.nodeId,
            placement: this.showPlacement() ? this.placement : null,
            elementIdProperty: this.showElementId() ? this.elementIdProperty.trim() || null : null,
            srs:
              this.format() === 'pointcloud' || this.format() === 'zip'
                ? this.srs.trim() || null
                : null,
          },
          silent(),
        ),
      );
      this.selection.changed();
      const converting = this.format() !== 'glb' && this.source === 'file';
      this.toasts.add({
        severity: 'success',
        summary: converting ? this.m.models.uploadedConverting : this.m.models.uploaded,
        life: 4000,
      });
      if (converting) {
        // Stays open: the list shows the conversion until it is ready.
        this.edit(null);
        this.load();
        return;
      }
      this.visible.set(false);
      this.changed.emit();
    } catch (e) {
      this.error.set(errorMessage(e, this.m));
    } finally {
      this.busy.set(false);
      this.progress.set('');
    }
  }

  protected save(model: ModelAsset): void {
    const patch: Record<string, unknown> = {
      elementIdProperty: this.elementIdProperty.trim() || null,
      nodeId: this.nodeId,
    };
    if (model.placement) patch['placement'] = this.placement;
    this.api.updateModel(model.id, patch).subscribe((saved) => {
      this.toasts.add({ severity: 'success', summary: this.m.models.saved, life: 2500 });
      this.load();
      this.edited.set(saved);
      this.changed.emit();
      this.selection.changed();
    });
  }

  /** Import check of the element ids (spec 9.1): missing and duplicated ids. */
  protected check(model: ModelAsset): void {
    this.busy.set(true);
    this.api.elementCheck(model.id).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.checkResult.set(this.m.models.checkResult(result));
      },
      error: () => this.busy.set(false),
    });
  }

  protected remove(model: ModelAsset): void {
    this.confirm.confirm({
      header: this.m.common.confirm,
      message: this.m.models.deleteConfirm,
      acceptButtonProps: { label: this.m.common.delete, severity: 'danger' },
      rejectButtonProps: { label: this.m.common.cancel, severity: 'secondary', text: true },
      accept: () =>
        this.api.deleteModel(model.id).subscribe(() => {
          this.selection.changed();
          this.load();
          this.edit(null);
          this.changed.emit();
        }),
    });
  }
}
