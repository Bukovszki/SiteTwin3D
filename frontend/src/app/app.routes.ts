import { inject } from '@angular/core';
import { Routes } from '@angular/router';
import { AuthService } from './core/auth/auth.service';

const workspace = () => import('./features/workspace/workspace').then((m) => m.Workspace);

/** The editing modes only for users who may edit somewhere (spec 1.5): viewers cannot even match the route. */
const canEdit = () => inject(AuthService).canEditAny();

/** The three modes (spec 1.5) as routes; the workspace takes its mode from the route data. */
export const routes: Routes = [
  { path: 'prepare', canMatch: [canEdit], loadComponent: workspace, data: { mode: 'prepare' } },
  { path: 'assign', canMatch: [canEdit], loadComponent: workspace, data: { mode: 'assign' } },
  { path: 'view', loadComponent: workspace, data: { mode: 'view' } },
  { path: '**', redirectTo: () => (inject(AuthService).canEditAny() ? 'prepare' : 'view') },
];
