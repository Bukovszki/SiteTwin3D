import { Component, effect, inject, untracked } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { Router, RouterOutlet } from '@angular/router';
import { ConfirmDialog } from 'primeng/confirmdialog';
import { Toast } from 'primeng/toast';
import { silent } from './core/api/errors';
import { AuthService } from './core/auth/auth.service';
import { ChangeReasonDialog } from './core/history/change-reason-dialog';
import { MESSAGES } from './core/i18n/messages';
import { LoginScreen } from './features/auth/login-screen';
import { PlatformScreen } from './features/platform/platform-screen';

/** App frame: session check, then the sign-in screen or the work area (routes; the modes of spec 1.5 come in M5). */
@Component({
  imports: [RouterOutlet, Toast, ConfirmDialog, LoginScreen, PlatformScreen, ChangeReasonDialog],
  selector: 'app-root',
  template: `
    <!-- Bottom left: the data sheet (right) keeps its Save button free, the map keeps its tool hint (bottom centre). -->
    <p-toast position="bottom-left" />
    <p-confirmdialog [style]="{ width: '28rem' }" />
    <app-change-reason-dialog />
    @if (!auth.checked()) {
      <!-- Nothing during the startup session check. -->
    } @else if (!auth.ready()) {
      <app-login-screen />
    } @else if (auth.isPlatformAdmin()) {
      <!-- Only the platform admin downloads the platform screen. -->
      @defer {
        <app-platform-screen />
      }
    } @else {
      <router-outlet />
    }
  `,
})
export class App {
  protected readonly m = inject(MESSAGES);
  protected readonly auth = inject(AuthService);

  private readonly router = inject(Router);
  private navigated = false;

  constructor() {
    // Route only once the user (and so the allowed modes) is known; after a later sign-in the guards run again.
    effect(() => {
      if (!this.auth.ready()) return;
      untracked(() => {
        if (!this.navigated) {
          this.navigated = true;
          this.router.initialNavigation();
        } else void this.router.navigateByUrl(this.router.url);
      });
    });
    inject(Title).setTitle(this.m.app.productName);
    // 401 before sign-in is expected: no toast.
    this.auth.check(silent());
  }
}
