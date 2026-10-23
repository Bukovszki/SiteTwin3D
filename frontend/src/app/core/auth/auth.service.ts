import { HttpContext } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { ApiService } from '../api/api.service';
import { Me } from '../api/models';

const LAST_ORGANIZATION_KEY = 'auth.lastOrganization';

/**
 * The signed-in user (cookie session, spec 3.3). Checked once on startup; a 401 later means the session ended and
 * the sign-in screen appears again.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiService);

  readonly user = signal<Me | null>(null);
  /** The startup session check has finished. */
  readonly checked = signal(false);
  /** The session ended while the app was in use (not the first sign-in). */
  readonly expired = signal(false);

  /** Signed in and the password is not temporary: the app may start. */
  readonly ready = computed(() => !!this.user() && !this.user()!.mustChangePassword);
  readonly isMember = computed(() => !!this.user()?.organizationId);
  /** May edit at least one site (the editing modes are offered). */
  readonly canEditAny = computed(() => {
    const user = this.user();
    return ['OrgAdmin', 'Editor'].includes(user?.role ?? '') && (user?.editableSiteIds == null || user.editableSiteIds.length > 0);
  });
  readonly isPlatformAdmin = computed(() => this.user()?.role === 'PlatformAdmin');
  readonly isOrgAdmin = computed(() => this.user()?.role === 'OrgAdmin');

  /** Site-level write permission (spec 3.2); the backend enforces it too. */
  canEditSite(siteId: number | null): boolean {
    const user = this.user();
    if (siteId == null || !user || !['OrgAdmin', 'Editor'].includes(user.role)) return false;
    return user.editableSiteIds == null || user.editableSiteIds.includes(siteId);
  }

  /** Reloads the user (site roles may have changed). */
  refresh(): void {
    this.api.me().subscribe((me) => this.user.set(me));
  }

  check(context?: HttpContext): void {
    this.api.me(context).subscribe({
      next: (me) => {
        this.user.set(me);
        this.checked.set(true);
      },
      error: () => {
        this.user.set(null);
        this.checked.set(true);
      },
    });
  }

  login(organization: string, username: string, password: string, context?: HttpContext): Observable<Me> {
    return this.api.login(organization, username, password, context).pipe(
      tap((me) => {
        this.user.set(me);
        this.expired.set(false);
        try {
          localStorage.setItem(LAST_ORGANIZATION_KEY, organization);
        } catch {
          // Storage unavailable (private mode): only a convenience.
        }
      }),
    );
  }

  changePassword(current: string, next: string, context?: HttpContext): Observable<Me> {
    return this.api.changePassword(current, next, context).pipe(tap((me) => this.user.set(me)));
  }

  logout(): void {
    this.api.logout().subscribe({ complete: () => this.user.set(null), error: () => this.user.set(null) });
  }

  sessionExpired(): void {
    if (this.user()) this.expired.set(true);
    this.user.set(null);
  }

  lastOrganization(): string {
    try {
      return localStorage.getItem(LAST_ORGANIZATION_KEY) ?? '';
    } catch {
      return '';
    }
  }
}
