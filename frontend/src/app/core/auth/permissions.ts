import { Injectable, computed, inject, signal } from '@angular/core';
import { SelectionService } from '../selection/selection.service';
import { AuthService } from './auth.service';

/**
 * The three modes (spec 1.5): workspace and permission gate at once.
 * prepare – data, hierarchy, import, floorplans, panoramas, drawing; assign – info points and anchors;
 * view – browsing only, no editing.
 */
export type Mode = 'prepare' | 'assign' | 'view';

export const MODES: Mode[] = ['prepare', 'assign', 'view'];

/**
 * What the UI offers right now: the mode (from the route) combined with the site-level permission of the current
 * site. The backend enforces the same (403 site.read_only); this only decides which tools are loaded at all.
 */
@Injectable({ providedIn: 'root' })
export class Permissions {
  private readonly auth = inject(AuthService);
  private readonly selection = inject(SelectionService);

  readonly mode = signal<Mode>('view');

  /** Modes the user may enter: viewers only "view". */
  readonly modes = computed<Mode[]>(() => (this.auth.canEditAny() ? MODES : ['view']));

  private readonly siteEditable = computed(() => this.auth.canEditSite(this.selection.siteId()));

  /** Preparation tools (data, hierarchy, floorplans, panoramas, drawing). */
  readonly prepare = computed(() => this.mode() === 'prepare' && this.siteEditable());
  /** Assignment tools (info points, anchors). */
  readonly assign = computed(() => this.mode() === 'assign' && this.siteEditable());
  /** Anchors may be moved / deleted in both editing modes. */
  readonly anchors = computed(() => this.prepare() || this.assign());
  readonly admin = computed(() => this.auth.isOrgAdmin());
}
