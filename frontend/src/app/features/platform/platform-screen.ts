import { HttpClient } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Checkbox } from 'primeng/checkbox';
import { InputText } from 'primeng/inputtext';
import { Message } from 'primeng/message';
import { Password } from 'primeng/password';
import { TableModule } from 'primeng/table';
import { Organization } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { MESSAGES } from '../../core/i18n/messages';

const API = '/api/v1/platform/organizations';

/**
 * Platform administration (spec 3.1): organizations with their first admin, name, active state and MapManager link
 * (URL and the REFERENCE of the service token). The platform admin never sees organization data.
 */
@Component({
  selector: 'app-platform-screen',
  imports: [FormsModule, Button, Checkbox, InputText, Message, Password, TableModule],
  template: `
    <header class="appbar">
      <div class="brand"><i class="pi pi-building"></i><span>{{ m.app.productName }} – {{ m.platform.title }}</span></div>
      <span class="spacer"></span>
      <span>{{ auth.user()?.displayName }}</span>
      <p-button [label]="m.auth.logout" icon="pi pi-sign-out" [text]="true" styleClass="bar-button" (onClick)="auth.logout()" />
    </header>
    <main class="content">
      <p-message severity="info" size="small">{{ m.auth.platformNoData }}</p-message>
      <div class="layout">
        <p-table [value]="organizations()" size="small" selectionMode="single" [selection]="edited()" (onRowSelect)="edit($any($event.data))" dataKey="id">
          <ng-template #header>
            <tr><th>{{ m.platform.code }}</th><th>{{ m.platform.name }}</th><th>{{ m.platform.users }}</th><th>{{ m.platform.active }}</th><th>MapManager</th></tr>
          </ng-template>
          <ng-template #body let-o>
            <tr [pSelectableRow]="o">
              <td>{{ o.code }}</td><td>{{ o.name }}</td><td>{{ o.userCount }}</td><td>{{ o.active ? m.common.yes : m.common.no }}</td>
              <td>{{ o.mapmanagerUrl ?? m.common.none }}</td>
            </tr>
          </ng-template>
        </p-table>
        <div class="form-grid editor">
          @if (edited(); as o) {
            <h4>{{ o.code }}</h4>
            <div class="field"><label for="o-name">{{ m.platform.name }}</label><input pInputText id="o-name" [(ngModel)]="name" /></div>
            <label class="check"><p-checkbox [binary]="true" [(ngModel)]="active" /> {{ m.platform.active }}</label>
            <div class="field"><label for="o-mm">{{ m.platform.mapmanagerUrl }}</label><input pInputText id="o-mm" [(ngModel)]="mapmanagerUrl" placeholder="https://mapmanager.example.hu" /></div>
            <div class="field">
              <label for="o-token">{{ m.platform.tokenRef }}</label><input pInputText id="o-token" [(ngModel)]="tokenRef" />
              <small class="muted">{{ m.platform.tokenHint }}</small>
            </div>
            <div class="actions">
              <p-button [label]="m.platform.new" [text]="true" (onClick)="edit(null)" />
              <p-button [label]="m.common.save" icon="pi pi-save" (onClick)="save(o)" />
            </div>
          } @else {
            <h4>{{ m.platform.new }}</h4>
            <div class="field"><label for="n-code">{{ m.platform.code }}</label><input pInputText id="n-code" [(ngModel)]="code" /></div>
            <div class="field"><label for="n-name">{{ m.platform.name }}</label><input pInputText id="n-name" [(ngModel)]="name" /></div>
            <div class="field"><label for="n-admin">{{ m.platform.adminUsername }}</label><input pInputText id="n-admin" [(ngModel)]="adminUsername" /></div>
            <div class="field">
              <label for="n-pw">{{ m.platform.adminPassword }}</label>
              <p-password inputId="n-pw" [(ngModel)]="adminPassword" [feedback]="false" [toggleMask]="true" fluid />
            </div>
            <div class="actions">
              <p-button [label]="m.common.add" icon="pi pi-plus" [disabled]="!code.trim() || !name.trim() || !adminUsername.trim() || adminPassword.length < 8" (onClick)="create()" />
            </div>
          }
        </div>
      </div>
    </main>
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100dvh;
    }
    .appbar {
      display: flex;
      align-items: center;
      gap: 0.75rem;
      height: 3.25rem;
      padding: 0 1rem;
      background: var(--p-primary-color);
      color: var(--p-primary-contrast-color);
      .brand {
        display: flex;
        gap: 0.5rem;
        align-items: center;
        font-weight: 700;
        font-size: 1.1rem;
      }
      .spacer {
        flex: 1;
      }
      ::ng-deep .bar-button.p-button {
        color: inherit;
      }
    }
    .content {
      padding: 1rem;
      overflow: auto;
    }
    .layout {
      display: grid;
      grid-template-columns: 1fr 24rem;
      gap: 1rem;
      margin-top: 1rem;
    }
    .editor h4 {
      margin: 0;
    }
    .check,
    .actions {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
    .actions {
      justify-content: flex-end;
    }
  `,
})
export class PlatformScreen {
  protected readonly m = inject(MESSAGES);
  protected readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  private readonly toasts = inject(MessageService);

  protected readonly organizations = signal<Organization[]>([]);
  protected readonly edited = signal<Organization | null>(null);
  protected code = '';
  protected name = '';
  protected active = true;
  protected adminUsername = 'admin';
  protected adminPassword = '';
  protected mapmanagerUrl = '';
  protected tokenRef = '';

  constructor() {
    this.load();
  }

  private load(): void {
    this.http.get<Organization[]>(API).subscribe((list) => this.organizations.set(list));
  }

  protected edit(o: Organization | null): void {
    this.edited.set(o);
    this.code = '';
    this.name = o?.name ?? '';
    this.active = o?.active ?? true;
    this.mapmanagerUrl = o?.mapmanagerUrl ?? '';
    this.tokenRef = o?.mapmanagerTokenRef ?? '';
    this.adminPassword = '';
  }

  protected create(): void {
    this.http
      .post<Organization>(API, { code: this.code.trim(), name: this.name.trim(), adminUsername: this.adminUsername.trim(), adminPassword: this.adminPassword })
      .subscribe(() => {
        this.toasts.add({ severity: 'success', summary: this.m.platform.saved, life: 2500 });
        this.load();
        this.edit(null);
      });
  }

  protected save(o: Organization): void {
    this.http.patch<Organization>(`${API}/${o.id}`, { name: this.name.trim(), active: this.active }).subscribe(() =>
      this.http.put<void>(`${API}/${o.id}/mapmanager`, { mapmanagerUrl: this.mapmanagerUrl.trim() || null, mapmanagerTokenRef: this.tokenRef.trim() || null })
        .subscribe(() => {
          this.toasts.add({ severity: 'success', summary: this.m.platform.saved, life: 2500 });
          this.load();
        }),
    );
  }
}
