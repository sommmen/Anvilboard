import { Component, computed, inject, signal } from '@angular/core';
import { BoardApiService } from '../../core/board-api.service';
import { DashboardSummary } from '../../core/models';

@Component({
  imports: [],
  selector: 'app-dashboard-page',
  styleUrl: './dashboard-page.scss',
  templateUrl: './dashboard-page.html',
})
export class DashboardPage {
  private readonly api = inject(BoardApiService);

  readonly summary = signal<DashboardSummary | null>(null);

  readonly statusEntries = computed(() => {
    const summary = this.summary();
    if (!summary) return [];
    const entries = Object.entries(summary.issuesByStatus);
    const max = Math.max(1, ...entries.map(([, count]) => count));
    return entries.map(([status, count]) => ({ status, count, pct: (count / max) * 100 }));
  });

  readonly sourceEntries = computed(() => {
    const summary = this.summary();
    if (!summary) return [];
    const entries = Object.entries(summary.issuesBySource);
    const max = Math.max(1, ...entries.map(([, count]) => count));
    return entries.map(([source, count]) => ({ source, count, pct: (count / max) * 100 }));
  });

  constructor() {
    this.api.getDashboardSummary().subscribe((summary) => this.summary.set(summary));
  }
}
