import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BoardApiService } from '../../core/board-api.service';
import {
  ISSUE_STATUSES,
  ISSUE_STATUS_LABEL,
  Issue,
  IssuePriority,
  IssueStatus,
  REALTIME_ACTIVITY_ADDED,
  REALTIME_ISSUE_CHANGED,
  RealtimeChangeEnvelope,
  Team,
  WorkflowState,
} from '../../core/models';
import { RealtimeBoardSyncService } from '../../core/realtime-board-sync.service';
import { IssueCard } from '../issue-card/issue-card';
import { IssueDetail } from '../issue-detail/issue-detail';

@Component({
  imports: [IssueCard, IssueDetail],
  selector: 'app-board-page',
  styleUrl: './board-page.scss',
  templateUrl: './board-page.html',
})
export class BoardPage {
  private readonly api = inject(BoardApiService);
  private readonly realtime = inject(RealtimeBoardSyncService);
  private readonly destroyRef = inject(DestroyRef);

  readonly statuses = ISSUE_STATUSES;
  readonly statusLabels = ISSUE_STATUS_LABEL;

  readonly issues = signal<Issue[]>([]);
  readonly teams = signal<Team[]>([]);
  readonly workflowStates = signal<WorkflowState[]>([]);
  readonly selectedIssue = signal<Issue | null>(null);
  readonly creatingForStatus = signal<IssueStatus | null>(null);
  readonly newIssueTitle = signal('');

  readonly columns = computed(() => {
    const all = this.issues();
    return this.statuses.map((status) => ({
      status,
      label: this.statusLabels[status],
      issues: all.filter((issue) => issue.status === status),
    }));
  });

  constructor() {
    this.refresh();
    this.api.listTeams().subscribe((teams) => this.teams.set(teams));
    this.api.listWorkflowStates().subscribe((states) => this.workflowStates.set(states));

    this.realtime.changes
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((envelope) => this.applyChange(envelope));

    // A reconnect means changes were missed while the connection was down; the server never
    // replays them, so one full re-fetch is the documented recovery path.
    this.realtime.resyncRequired
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.refresh());

    void this.realtime.start();
  }

  refresh(): void {
    this.api.listIssues().subscribe((issues) => this.issues.set(issues));
  }

  /**
   * Converges the board on a single change without redrawing it. Only the one affected issue is
   * re-fetched and swapped in place, so selection, scroll position, and every other column's DOM
   * survive an update (AC-RT-004). Anything this client cannot interpret — an unknown event type, a
   * change to an issue it has never seen, or a version gap — degrades to one full re-fetch rather
   * than to a stale or partially applied board.
   */
  private applyChange(envelope: RealtimeChangeEnvelope): void {
    // Activity is consumed by issue-detail surfaces. It never changes a board card, so refreshing
    // here would make every issue mutation perform both a targeted fetch and a full board redraw.
    if (envelope.eventType === REALTIME_ACTIVITY_ADDED) {
      return;
    }

    if (envelope.eventType !== REALTIME_ISSUE_CHANGED || !envelope.issueId) {
      this.refresh();
      return;
    }

    const issueId = envelope.issueId;
    const known = this.issues().find((issue) => issue.id === issueId);
    if (!known) {
      this.refresh();
      return;
    }

    // The envelope carries no issue body, only the fact that one changed — re-fetching the single
    // issue is what keeps the wire payload free of data a client may not be allowed to see.
    this.api.getIssue(issueId).subscribe({
      next: (issue) => this.replaceIssue(issue),
      error: () => this.refresh(),
    });
  }

  private replaceIssue(issue: Issue): void {
    const current = this.issues().find((candidate) => candidate.id === issue.id);
    if (!current || current.version > issue.version) {
      return;
    }

    const selected = this.selectedIssue();
    this.issues.update((issues) =>
      issues.map((candidate) => (candidate.id === issue.id ? issue : candidate)),
    );

    if (selected?.id === issue.id && selected.version <= issue.version) {
      this.selectedIssue.set(issue);
    }
  }

  openIssue(issue: Issue): void {
    this.selectedIssue.set(issue);
  }

  closeDetail(): void {
    this.selectedIssue.set(null);
  }

  startCreating(status: IssueStatus): void {
    this.creatingForStatus.set(status);
    this.newIssueTitle.set('');
  }

  cancelCreating(): void {
    this.creatingForStatus.set(null);
  }

  submitCreate(): void {
    const status = this.creatingForStatus();
    const title = this.newIssueTitle().trim();
    const team = this.teams()[0];
    if (status === null || !title || !team) {
      this.creatingForStatus.set(null);
      return;
    }

    this.api
      .createIssue({ teamId: team.id, title, priority: IssuePriority.None })
      .subscribe((issue) => {
        if (status !== IssueStatus.Backlog) {
          const target = this.workflowStates().find((state) => state.key === this.statusKey(status));
          if (target) {
            this.api.changeStatus(issue.id, target.id).subscribe(() => this.refresh());
          } else {
            this.refresh();
          }
        } else {
          this.refresh();
        }
        this.creatingForStatus.set(null);
      });
  }

  private statusKey(status: IssueStatus): string {
    return ['backlog', 'todo', 'in_progress', 'in_review', 'done', 'cancelled'][status];
  }

  onStatusChanged(): void {
    this.refresh();
    this.selectedIssue.set(null);
  }
}
