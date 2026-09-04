import { Routes } from '@angular/router';
import { AppShell } from './shell/app-shell/app-shell';

export const routes: Routes = [
  {
    path: '',
    component: AppShell,
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'board' },
      {
        path: 'board',
        loadComponent: () => import('./board/board-page/board-page').then((m) => m.BoardPage),
      },
      {
        path: 'dashboard',
        loadComponent: () =>
          import('./dashboard/dashboard-page/dashboard-page').then((m) => m.DashboardPage),
      },
    ],
  },
];
