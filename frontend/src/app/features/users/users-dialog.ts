import { HttpClient } from '@angular/common/http';
import { Component, computed, effect, inject, model, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MessageService } from 'primeng/api';
import { Button } from 'primeng/button';
import { Checkbox } from 'primeng/checkbox';
import { Dialog } from 'primeng/dialog';
import { InputText } from 'primeng/inputtext';
import { Password } from 'primeng/password';
import { Select } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { SiteRole, User } from '../../core/api/models';
import { AuthService } from '../../core/auth/auth.service';
import { MESSAGES } from '../../core/i18n/messages';
import { SelectionService } from '../../core/selection/selection.service';

const API = '/api/v1/users';

/**
 * Users of the organization (spec 3.2; organization admin): create with a temporary password, name, role, active,
 * password reset and site-level roles. The backend ends the sessions on role / state changes and keeps an admin.
 */
@Component({
  selector: 'app-users-dialog',
  imports: [FormsModule, Dialog, Button, Checkbox, InputText, Password, Select, TableModule],
  template: `
    <p-dialog [header]="m.users.title" [(visible)]="visible" [modal]="true" [style]="{ width: '56rem' }" [maximizable]="true">
      <div class="layout">
        <p-table [value]="users()" size="small" selectionMode="single" [selection]="edited()" (onRowSelect)="edit($any($event.data))" dataKey="id"
          [scrollable]="true" scrollHeight="24rem">
          <ng-template #header>
            <tr><th>{{ m.users.username }}</th><th>{{ m.users.displayName }}</th><th>{{ m.users.role }}</th><th>{{ m.users.active }}</th></tr>
          </ng-template>
          <ng-template #body let-u>
            <tr [pSelectableRow]="u" [class.inactive]="!u.active">
              <td>{{ u.username }}@if (u.mustChangePassword) { <small class="muted"> ({{ m.users.temporary }})</small> }</td>
              <td>{{ u.displayName }}</td>
              <td>{{ m.roles[u.role] }}</td>
              <td>{{ u.active ? m.common.yes : m.common.no }}</td>
            </tr>
          </ng-template>
        </p-table>

        <div class="form-grid editor">
          @if (edited(); as u) {
            <h4>{{ u.username }}</h4>
            <div class="field">
              <label for="u-name">{{ m.users.displayName }}</label>
              <input pInputText id="u-name" [(ngModel)]="displayName" />
            </div>
            <div class="row">
              <div class="field">
                <label for="u-role">{{ m.users.role }}</label>
                <p-select inputId="u-role" [options]="roles()" optionLabel="label" optionValue="value" [(ngModel)]="role" appendTo="body" fluid />
              </div>
              <label class="check"><p-checkbox [binary]="true" [(ngModel)]="active" /> {{ m.users.active }}</label>
            </div>
            @if (role !== 'OrgAdmin') {
              <div class="field">
                <label>{{ m.users.sites }}</label>
                <small class="muted">{{ m.users.sitesHint }}</small>
                @for (site of selection.sites(); track site.id) {
                  <div class="site-row">
                    <span>{{ site.name }}</span>
                    <p-select [options]="siteRoleOptions()" optionLabel="label" optionValue="value" [ngModel]="siteRoles()[site.id] ?? ''"
                      (ngModelChange)="setSiteRole(site.id, $event)" size="small" appendTo="body" />
                  </div>
                }
              </div>
            }
            <div class="actions">
              <p-button [label]="m.common.save" icon="pi pi-save" [loading]="saving()" (onClick)="save(u)" />
            </div>
            <div class="field">
              <label for="u-reset">{{ m.users.resetPassword }}</label>
              <div class="inline">
                <p-password inputId="u-reset" [(ngModel)]="resetPassword" [feedback]="false" [toggleMask]="true" fluid />
                <p-button icon="pi pi-key" severity="secondary" [outlined]="true" [disabled]="resetPassword.length < 8" (onClick)="reset(u)" />
              </div>
            </div>
          } @else {
            <h4>{{ m.users.new }}</h4>
            <div class="field">
              <label for="n-username">{{ m.users.username }}</label>
              <input pInputText id="n-username" [(ngModel)]="newUsername" />
            </div>
            <div class="field">
              <label for="n-name">{{ m.users.displayName }}</label>
              <input pInputText id="n-name" [(ngModel)]="displayName" />
            </div>
            <div class="field">
              <label for="n-role">{{ m.users.role }}</label>
              <p-select inputId="n-role" [options]="roles()" optionLabel="label" optionValue="value" [(ngModel)]="role" appendTo="body" fluid />
            </div>
            <div class="field">
              <label for="n-password">{{ m.users.password }}</label>
              <p-password inputId="n-password" [(ngModel)]="newPassword" [feedback]="false" [toggleMask]="true" fluid />
              <small class="muted">{{ m.users.passwordHint }}</small>
            </div>
            <div class="actions">
              <p-button [label]="m.common.add" icon="pi pi-plus" [loading]="saving()" [disabled]="!newUsername.trim() || newPassword.length < 8" (onClick)="create()" />
            </div>
          }
        </div>
      </div>
      <ng-template #footer>
        @if (edited()) {
          <p-button [label]="m.users.new" icon="pi pi-user-plus" [text]="true" (onClick)="edit(null)" />
        }
        <p-button [label]="m.common.close" severity="secondary" [text]="true" (onClick)="visible.set(false)" />
      </ng-template>
    </p-dialog>
  `,
  styles: `
    .layout {
      display: grid;
      grid-template-columns: 1fr 22rem;
      gap: 1rem;
    }
    .editor h4 {
      margin: 0;
    }
    .check,
    .inline,
    .site-row,
    .actions {
      display: flex;
      align-items: center;
      gap: 0.5rem;
    }
    .site-row {
      justify-content: space-between;
    }
    .actions {
      justify-content: flex-end;
    }
    tr.inactive td {
      color: var(--p-text-muted-color);
      text-decoration: line-through;
    }
  `,
})
export class UsersDialog {
  protected readonly m = inject(MESSAGES);
  protected readonly selection = inject(SelectionService);
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly toasts = inject(MessageService);

  readonly visible = model(false);

  protected readonly users = signal<User[]>([]);
  protected readonly edited = signal<User | null>(null);
  protected readonly siteRoles = signal<Record<number, string>>({});
  protected readonly saving = signal(false);
  protected displayName = '';
  protected role: User['role'] = 'Viewer';
  protected active = true;
  protected resetPassword = '';
  protected newUsername = '';
  protected newPassword = '';

  protected readonly roles = computed(() => (['OrgAdmin', 'Editor', 'Viewer'] as const).map((value) => ({ value, label: this.m.roles[value] })));
  protected readonly siteRoleOptions = computed(() => [
    { value: '', label: this.m.users.noAccess },
    { value: 'Editor', label: this.m.roles['Editor'] },
    { value: 'Viewer', label: this.m.roles['Viewer'] },
  ]);

  private readonly loadOnOpen = effect(() => {
    if (this.visible())
      untracked(() => {
        this.load();
        this.edit(null);
      });
  });

  private load(): void {
    this.http.get<User[]>(API).subscribe((users) => this.users.set(users));
  }

  protected edit(user: User | null): void {
    this.edited.set(user);
    this.displayName = user?.displayName ?? '';
    this.role = user?.role ?? 'Viewer';
    this.active = user?.active ?? true;
    this.resetPassword = '';
    this.newUsername = '';
    this.newPassword = '';
    this.siteRoles.set(Object.fromEntries((user?.siteRoles ?? []).map((r) => [r.siteId, r.role])));
  }

  protected setSiteRole(siteId: number, role: string): void {
    this.siteRoles.update((r) => {
      const next = { ...r };
      if (role) next[siteId] = role;
      else delete next[siteId];
      return next;
    });
  }

  protected create(): void {
    this.saving.set(true);
    this.http.post<User>(API, { username: this.newUsername.trim(), displayName: this.displayName.trim(), role: this.role, password: this.newPassword }).subscribe({
      next: (user) => {
        this.saving.set(false);
        this.toasts.add({ severity: 'success', summary: this.m.users.saved, life: 2500 });
        this.load();
        this.edit(user);
      },
      error: () => this.saving.set(false),
    });
  }

  protected save(user: User): void {
    this.saving.set(true);
    const roles: SiteRole[] = Object.entries(this.siteRoles()).map(([siteId, role]) => ({ siteId: Number(siteId), role: role as SiteRole['role'] }));
    this.http.patch<User>(`${API}/${user.id}`, { displayName: this.displayName.trim(), role: this.role, active: this.active }).subscribe({
      next: () =>
        this.http.put<User>(`${API}/${user.id}/site-roles`, { roles: this.role === 'OrgAdmin' ? [] : roles }).subscribe({
          next: (saved) => {
            this.saving.set(false);
            this.toasts.add({ severity: 'success', summary: this.m.users.saved, life: 2500 });
            this.load();
            this.edit(saved);
            if (saved.id === this.auth.user()?.id) this.auth.refresh();
          },
          error: () => this.saving.set(false),
        }),
      error: () => this.saving.set(false),
    });
  }

  protected reset(user: User): void {
    this.http.post<void>(`${API}/${user.id}/password`, { password: this.resetPassword }).subscribe(() => {
      this.resetPassword = '';
      this.toasts.add({ severity: 'success', summary: this.m.users.resetDone, life: 2500 });
      this.load();
    });
  }
}
