import { Component, computed, inject, signal } from '@angular/core';
import { BoardApiService } from '../../core/board-api.service';
import {
  ISSUE_STATUSES,
  ISSUE_STATUS_LABEL,
  Issue,
  IssuePriority,
  IssueStatus,
  Team,
} from '../../core/models';
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

  readonly statuses = ISSUE_STATUSES;
  readonly statusLabels = ISSUE_STATUS_LABEL;

  readonly issues = signal<Issue[]>([]);
  readonly teams = signal<Team[]>([]);
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
  }

  refresh(): void {
    this.api.listIssues().subscribe((issues) => this.issues.set(issues));
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
          this.api.changeStatus(issue.id, status).subscribe(() => this.refresh());
        } else {
          this.refresh();
        }
        this.creatingForStatus.set(null);
      });
  }

  onStatusChanged(): void {
    this.refresh();
    this.selectedIssue.set(null);
  }
}
