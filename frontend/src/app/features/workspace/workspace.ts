import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Router } from '@angular/router';
import { SelectButton } from 'primeng/selectbutton';
import { FormsModule } from '@angular/forms';
import { MenuItem } from 'primeng/api';
import { Avatar } from 'primeng/avatar';
import { Button } from 'primeng/button';
import { Menu } from 'primeng/menu';
import { Select } from 'primeng/select';
import { Splitter } from 'primeng/splitter';
import { Tab, TabList, TabPanel, TabPanels, Tabs } from 'primeng/tabs';
import { AuthService } from '../../core/auth/auth.service';
import { Mode, Permissions } from '../../core/auth/permissions';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';
import { ModelViewer } from '../../viewer3d/model-viewer';
import { APP_VERSION } from '../../version';
import { DatasheetPanel } from '../datasheet/datasheet-panel';
import { GridPanel } from '../grid/grid-panel';
import { OverviewMap } from '../map/overview-map';
import { PanoramaPanel } from '../panorama/panorama-panel';
import { MapLayersDialog } from '../sites/map-layers-dialog';
import { SitesDialog } from '../sites/sites-dialog';
import { TreePanel } from '../tree/tree-panel';
import { UsersDialog } from '../users/users-dialog';

/**
 * Work area (spec 11, layout assumption): tree and grid on the left (tabs), map / 3D in the middle, data sheet on
 * the right. All views follow the central selection. Look of the header taken over from MapManager (NFR-8).
 * The 3D view (Cesium) is loaded only when its tab is opened (@defer), outside the initial bundle; the open panorama
 * stays beside the map and beside the 3D view.
 */
@Component({
  selector: 'app-workspace',
  imports: [
    FormsModule,
    Avatar,
    Button,
    Menu,
    Select,
    Splitter,
    Tabs,
    TabList,
    Tab,
    TabPanels,
    TabPanel,
    TreePanel,
    GridPanel,
    DatasheetPanel,
    OverviewMap,
    ModelViewer,
    PanoramaPanel,
    SitesDialog,
    MapLayersDialog,
    UsersDialog,
    SelectButton,
  ],
  template: `
    <header class="appbar">
      <div class="brand">
        <i class="pi pi-building"></i><span>{{ m.app.productName }}</span>
      </div>
      <p-select
        [options]="selection.sites()"
        optionLabel="name"
        optionValue="id"
        [ngModel]="selection.siteId()"
        (ngModelChange)="selection.selectSite($event)"
        [placeholder]="m.shell.chooseSite"
        size="small"
        styleClass="site-select"
        [filter]="selection.sites().length > 8"
      />
      @if (can.modes().length > 1) {
        <p-selectbutton
          [options]="modeOptions()"
          optionLabel="label"
          optionValue="value"
          [ngModel]="can.mode()"
          (ngModelChange)="switchMode($event)"
          [allowEmpty]="false"
          size="small"
          styleClass="modes"
        />
      } @else {
        <span class="mode-label">{{ m.modes['view'] }}</span>
      }
      @if (auth.isOrgAdmin() && can.mode() === 'prepare') {
        <p-button
          [label]="m.shell.sites"
          icon="pi pi-map"
          [text]="true"
          size="small"
          styleClass="bar-button"
          (onClick)="sitesOpen.set(true)"
        />
        <p-button
          [label]="m.shell.mapLayers"
          icon="pi pi-clone"
          [text]="true"
          size="small"
          styleClass="bar-button"
          (onClick)="layersOpen.set(true)"
        />
      }
      <span class="spacer"></span>
      <button
        type="button"
        class="user"
        (click)="userMenu.toggle($event)"
        [attr.aria-label]="m.shell.userMenu"
      >
        <span class="who">
          <b>{{ auth.user()?.displayName }}</b>
          <small
            >{{ auth.user()?.organizationName }} · {{ m.roles[auth.user()?.role ?? ''] }} · v{{
              version
            }}</small
          >
        </span>
        <p-avatar [label]="initials()" shape="circle" />
      </button>
      <p-menu #userMenu [model]="userMenuItems()" [popup]="true" appendTo="body" />
    </header>

    @if (selection.sites().length === 0) {
      <p class="muted hint">{{ m.shell.noSiteHint }}</p>
    }
    <p-splitter
      styleClass="main"
      [gutterSize]="6"
      [panelSizes]="[32, 44, 24]"
      [minSizes]="[18, 20, 15]"
    >
      <ng-template #panel>
        <p-tabs value="tree" class="pane">
          <p-tablist>
            <p-tab value="tree"><i class="pi pi-sitemap"></i> {{ m.shell.tree }}</p-tab>
            <p-tab value="grid"><i class="pi pi-table"></i> {{ m.shell.grid }}</p-tab>
          </p-tablist>
          <p-tabpanels>
            <p-tabpanel value="tree"><app-tree-panel /></p-tabpanel>
            <p-tabpanel value="grid"><app-grid-panel /></p-tabpanel>
          </p-tabpanels>
        </p-tabs>
      </ng-template>
      <ng-template #panel>
        <div class="pane">
          <p-tabs [value]="center()" (valueChange)="center.set($any($event))">
            <p-tablist>
              <p-tab value="map"><i class="pi pi-map"></i> {{ m.shell.map }}</p-tab>
              <p-tab value="globe"><i class="pi pi-globe"></i> {{ m.shell.globe }}</p-tab>
            </p-tablist>
          </p-tabs>
          <div class="viewport" [class.split]="selection.panoramaId() != null">
            @if (selection.panoramaId() != null) {
              <div class="pano">
                @defer {
                  <app-panorama-panel />
                } @loading {
                  <span class="muted loading">{{ m.common.loading }}</span>
                }
              </div>
            }
            <div class="map" [hidden]="center() !== 'map'"><app-overview-map /></div>
            @if (center() === 'globe') {
              @defer {
                <div class="map"><app-model-viewer /></div>
              } @loading {
                <span class="muted loading">{{ m.common.loading }}</span>
              }
            }
          </div>
        </div>
      </ng-template>
      <ng-template #panel>
        <div class="pane">
          <div class="pane-title"><i class="pi pi-id-card"></i> {{ m.datasheet.title }}</div>
          <app-datasheet-panel class="fill" />
        </div>
      </ng-template>
    </p-splitter>

    <!-- Administration tools are loaded only for organization admins, and not at all in the viewing mode. -->
    @if (auth.isOrgAdmin() && can.mode() === 'prepare') {
      @defer {
        <app-sites-dialog [(visible)]="sitesOpen" />
        <app-map-layers-dialog [(visible)]="layersOpen" />
      }
    }
    @if (auth.isOrgAdmin()) {
      @defer (when usersOpen()) {
        <app-users-dialog [(visible)]="usersOpen" />
      }
    }
  `,
  styleUrl: './workspace.scss',
})
export class Workspace {
  protected readonly m = inject(MESSAGES);
  protected readonly auth = inject(AuthService);
  protected readonly selection = inject(SelectionService);
  protected readonly version = APP_VERSION;

  protected readonly center = signal<'map' | 'globe'>('map');
  protected readonly sitesOpen = signal(false);
  protected readonly layersOpen = signal(false);
  protected readonly usersOpen = signal(false);
  protected readonly can = inject(Permissions);
  private readonly router = inject(Router);

  /** The mode from the route data (spec 1.5); the route guard already refused modes the user may not enter. */
  readonly mode = input<Mode>('view');

  protected readonly modeOptions = computed(() =>
    this.can.modes().map((value) => ({ value, label: this.m.modes[value] })),
  );

  protected readonly initials = computed(() =>
    (this.auth.user()?.displayName ?? '')
      .split(/\s+/)
      .map((w) => w[0])
      .join('')
      .slice(0, 2)
      .toUpperCase(),
  );

  protected readonly userMenuItems = computed<MenuItem[]>(() => [
    ...(this.auth.isOrgAdmin()
      ? [
          {
            label: this.m.users.title,
            icon: 'pi pi-users',
            command: () => this.usersOpen.set(true),
          },
        ]
      : []),
    { label: this.m.auth.logout, icon: 'pi pi-sign-out', command: () => this.auth.logout() },
  ]);

  protected switchMode(mode: Mode): void {
    void this.router.navigate(['/', mode]);
  }

  private readonly applyMode = effect(() => {
    const mode = this.mode();
    untracked(() => {
      this.can.mode.set(mode);
      // Tools of the previous mode (pending map picks) end with it.
      this.selection.changed();
    });
  });

  constructor() {
    this.selection.loadSites();
    this.selection.loadNodeTypes();
  }
}
