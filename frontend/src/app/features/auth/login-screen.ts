import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Button } from 'primeng/button';
import { InputText } from 'primeng/inputtext';
import { Message } from 'primeng/message';
import { Password } from 'primeng/password';
import { errorMessage, silent } from '../../core/api/errors';
import { AuthService } from '../../core/auth/auth.service';
import { MESSAGES } from '../../core/i18n/messages';
import { APP_VERSION } from '../../version';

/**
 * Sign-in with organization code, username and password; after a temporary password the mandatory password
 * change (spec 3.3). Look taken over from the MapManager login screen (NFR-8).
 */
@Component({
  selector: 'app-login-screen',
  imports: [FormsModule, Button, InputText, Message, Password],
  template: `
    <div class="screen">
      <div class="card">
        <div class="brand"><i class="pi pi-building"></i> {{ m.app.productName }}</div>
        <small class="slogan">{{ m.app.slogan }}</small>
        @if (auth.user()?.mustChangePassword) {
          <h2>{{ m.auth.changeTitle }}</h2>
          <p class="muted">{{ m.auth.changeIntro }}</p>
          <form class="form-grid" (ngSubmit)="change()">
            <div class="field">
              <label for="current">{{ m.auth.currentPassword }}</label>
              <p-password inputId="current" name="current" [(ngModel)]="current" [feedback]="false" [toggleMask]="true" autocomplete="current-password" fluid />
            </div>
            <div class="field">
              <label for="next">{{ m.auth.newPassword }}</label>
              <p-password inputId="next" name="next" [(ngModel)]="next" [feedback]="false" [toggleMask]="true" autocomplete="new-password" fluid />
            </div>
            <div class="field">
              <label for="again">{{ m.auth.newPasswordAgain }}</label>
              <p-password inputId="again" name="again" [(ngModel)]="again" [feedback]="false" [toggleMask]="true" autocomplete="new-password" fluid />
            </div>
            @if (error(); as message) {
              <p-message severity="error">{{ message }}</p-message>
            }
            <p-button type="submit" [label]="m.auth.change" icon="pi pi-check" [loading]="loading()" [disabled]="!current || !next || !again" styleClass="w-full" />
            <p-button [label]="m.auth.logout" severity="secondary" [text]="true" (onClick)="auth.logout()" styleClass="w-full" />
          </form>
        } @else {
          <h2>{{ m.auth.title }}</h2>
          @if (auth.expired()) {
            <p-message severity="warn" styleClass="expired">{{ m.auth.expired }}</p-message>
          }
          <form class="form-grid" (ngSubmit)="login()">
            <div class="field">
              <label for="organization">{{ m.auth.organization }}</label>
              <input pInputText id="organization" name="organization" [(ngModel)]="organization" autocomplete="organization" />
            </div>
            <div class="field">
              <label for="username">{{ m.auth.username }}</label>
              <input pInputText id="username" name="username" [(ngModel)]="username" autocomplete="username" />
            </div>
            <div class="field">
              <label for="password">{{ m.auth.password }}</label>
              <p-password inputId="password" name="password" [(ngModel)]="password" [feedback]="false" [toggleMask]="true" autocomplete="current-password" fluid />
            </div>
            @if (error(); as message) {
              <p-message severity="error">{{ message }}</p-message>
            }
            <p-button type="submit" [label]="m.auth.login" icon="pi pi-sign-in" [loading]="loading()"
              [disabled]="!organization.trim() || !username.trim() || !password" styleClass="w-full" />
          </form>
        }
        <small class="version">{{ m.app.productName }} v{{ version }}</small>
      </div>
    </div>
  `,
  styles: `
    .screen {
      display: flex;
      align-items: center;
      justify-content: center;
      height: 100dvh;
      padding: 1rem;
      background: linear-gradient(160deg, var(--p-primary-50), var(--p-surface-100));
    }
    .card {
      width: 100%;
      max-width: 24rem;
      padding: 1.75rem 2rem;
      background: var(--p-content-background);
      border: 1px solid var(--p-content-border-color);
      border-radius: 12px;
      box-shadow: 0 10px 30px rgb(0 0 0 / 0.08);
    }
    .brand {
      display: flex;
      align-items: center;
      gap: 0.5rem;
      font-size: 1.3rem;
      font-weight: 700;
      color: var(--p-primary-color);
    }
    .slogan {
      color: var(--p-text-muted-color);
    }
    h2 {
      margin: 1.25rem 0 0.75rem;
      font-size: 1.15rem;
    }
    :host ::ng-deep .expired {
      margin-bottom: 0.75rem;
    }
    .version {
      display: block;
      margin-top: 0.75rem;
      text-align: center;
      color: var(--p-text-muted-color);
      font-size: 0.75rem;
    }
  `,
})
export class LoginScreen {
  protected readonly m = inject(MESSAGES);
  protected readonly auth = inject(AuthService);
  protected readonly version = APP_VERSION;

  protected organization = this.auth.lastOrganization();
  protected username = '';
  protected password = '';
  protected current = '';
  protected next = '';
  protected again = '';
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);

  protected login(): void {
    this.run(this.auth.login(this.organization.trim(), this.username.trim(), this.password, silent()), () => (this.password = ''));
  }

  protected change(): void {
    if (this.next !== this.again) {
      this.error.set(this.m.auth.mismatch);
      return;
    }
    this.run(this.auth.changePassword(this.current, this.next, silent()), () => (this.current = this.next = this.again = ''));
  }

  private run(request: ReturnType<AuthService['login']>, onError: () => void): void {
    this.loading.set(true);
    this.error.set(null);
    request.subscribe({
      next: () => this.loading.set(false),
      error: (err) => {
        this.loading.set(false);
        onError();
        this.error.set(errorMessage(err, this.m));
      },
    });
  }
}
