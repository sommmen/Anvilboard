import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { BoardApiService } from '../../core/board-api.service';
import {
  BoardGroup,
  BoardIssue,
  BoardQuery,
  Issue,
  IssuePriority,
  LabelSummary,
  Member,
  ProjectSummary,
  REALTIME_ACTIVITY_ADDED,
  REALTIME_ISSUE_CHANGED,
  RealtimeChangeEnvelope,
  Team,
  WorkflowState,
} from '../../core/models';
import { RealtimeBoardSyncService } from '../../core/realtime-board-sync.service';
import { BoardFilters, BoardView } from '../board-filters/board-filters';
import { IssueCard } from '../issue-card/issue-card';
import { IssueDetail } from '../issue-detail/issue-detail';

/** Query keys mirrored to the URL, so a reload or a shared link restores the same view. */
const URL_QUERY_KEYS: (keyof BoardQuery)[] = [
  'workflowStateId',
  'assigneeId',
  'provider',
  'projectId',
  'priority',
  'type',
  'labelId',
  'syncCondition',
  'groupBy',
  'orderBy',
  'page',
  'limit',
  'includeArchived',
];

@Component({
  imports: [BoardFilters, IssueCard, IssueDetail],
  selector: 'app-board-page',
  styleUrl: './board-page.scss',
  templateUrl: './board-page.html',
})
export class BoardPage {
  private readonly api = inject(BoardApiService);
  private readonly realtime = inject(RealtimeBoardSyncService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly router = inject(Router, { optional: true });
  private readonly route = inject(ActivatedRoute, { optional: true });

  readonly groups = signal<BoardGroup[]>([]);
  readonly totalCount = signal(0);
  readonly query = signal<BoardQuery>({});
  readonly view = signal<BoardView>('kanban');
  readonly loading = signal(false);

  readonly teams = signal<Team[]>([]);
  readonly members = signal<Member[]>([]);
  readonly workflowStates = signal<WorkflowState[]>([]);
  readonly projects = signal<ProjectSummary[]>([]);
  readonly labels = signal<LabelSummary[]>([]);

  readonly selectedIssue = signal<Issue | null>(null);
  readonly creatingForGroup = signal<string | null>(null);
  readonly newIssueTitle = signal('');

  constructor() {
    this.query.set(this.readQueryFromUrl());
    this.refresh();

    this.api.listTeams().subscribe((teams) => this.teams.set(teams));
    this.api.listMembers().subscribe((members) => this.members.set(members));
    this.api.listWorkflowStates().subscribe((states) => this.workflowStates.set(states));
    this.api.listProjects().subscribe({ next: (projects) => this.projects.set(projects), error: () => {} });
    this.api.listLabels().subscribe({ next: (labels) => this.labels.set(labels), error: () => {} });

    this.realtime.changes
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe((envelope) => this.applyChange(envelope));

    // A reconnect means changes were missed while the connection was down and nothing
    // replays them, so one full re-fetch is the documented recovery path.
    this.realtime.resyncRequired.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.refresh());

    void this.realtime.start();
  }

  refresh(): void {
    this.loading.set(true);
    this.api.queryBoard(this.query()).subscribe({
      next: (result) => {
        this.groups.set(result.groups);
        this.totalCount.set(result.totalCount);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  /**
   * Replaces the whole query, mirrors it to the URL, and refetches. Grouping and ordering are
   * server-side concerns, so there is no local re-sort path that could disagree with the server.
   */
  applyQuery(query: BoardQuery): void {
    this.query.set(query);
    this.writeQueryToUrl(query);
    this.refresh();
  }

  setView(view: BoardView): void {
    this.view.set(view);
  }

  /** The list view is a flattened board: the same groups, rendered as rows instead of columns. */
  listRows(): { group: BoardGroup; issue: BoardIssue }[] {
    return this.groups().flatMap((group) => group.issues.map((issue) => ({ group, issue })));
  }

  /**
   * Converges the board after a realtime change. Unlike the unfiltered board this replaced, a
   * filtered, server-grouped view cannot decide locally whether a changed issue still belongs —
   * a status change can move it between groups, out of the current filter, or onto another page —
   * so the current query is re-run rather than an array element patched in place.
   */
  private applyChange(envelope: RealtimeChangeEnvelope): void {
    // Activity is consumed by the issue-detail surface. It never changes a board card, so
    // refreshing here would make every mutation redraw the whole board twice.
    if (envelope.eventType === REALTIME_ACTIVITY_ADDED) {
      return;
    }

    this.refresh();

    if (envelope.eventType === REALTIME_ISSUE_CHANGED && envelope.issueId) {
      this.refreshSelectedIssue(envelope.issueId);
    }
  }

  private refreshSelectedIssue(issueId: string): void {
    const selected = this.selectedIssue();
    if (selected?.id !== issueId) {
      return;
    }

    this.api.getIssue(issueId).subscribe({
      next: (issue) => {
        const current = this.selectedIssue();
        // A stale response must not overwrite a newer one; version is monotonic per issue.
        if (current?.id === issue.id && current.version <= issue.version) {
          this.selectedIssue.set(issue);
        }
      },
      error: () => this.selectedIssue.set(null),
    });
  }

  openIssue(boardIssue: BoardIssue): void {
    // The board projection is deliberately slimmer than an issue; the detail view needs the full
    // record, so opening one fetches it rather than widening every card's payload.
    this.api.getIssue(boardIssue.id).subscribe((issue) => this.selectedIssue.set(issue));
  }

  closeDetail(): void {
    this.selectedIssue.set(null);
  }

  startCreating(groupKey: string): void {
    this.creatingForGroup.set(groupKey);
    this.newIssueTitle.set('');
  }

  cancelCreating(): void {
    this.creatingForGroup.set(null);
  }

  /**
   * Quick-create inside a group. The new issue only lands in the originating group when the board
   * is grouped by workflow state and that group names a real state; under any other grouping the
   * issue is created with defaults and the board refetch decides where it belongs.
   */
  submitCreate(): void {
    const groupKey = this.creatingForGroup();
    const title = this.newIssueTitle().trim();
    const team = this.teams()[0];
    if (groupKey === null || !title || !team) {
      this.creatingForGroup.set(null);
      return;
    }

    const targetState =
      (this.query().groupBy ?? 'WorkflowState') === 'WorkflowState'
        ? this.workflowStates().find((state) => state.id === groupKey)
        : undefined;

    this.api.createIssue({ teamId: team.id, title, priority: IssuePriority.None }).subscribe((issue) => {
      if (targetState && targetState.id !== issue.workflowStateId) {
        this.api.changeStatus(issue.id, targetState.id).subscribe({
          next: () => this.refresh(),
          error: () => this.refresh(),
        });
      } else {
        this.refresh();
      }
      this.creatingForGroup.set(null);
    });
  }

  onIssueChanged(): void {
    this.refresh();
    this.selectedIssue.set(null);
  }

  /**
   * Rebuilds the query from the URL. Values arrive as strings; only `page`/`limit` are coerced to
   * numbers and `includeArchived` to a boolean, because every other field is already a string on
   * the wire and re-parsing it would only risk mangling an id.
   */
  private readQueryFromUrl(): BoardQuery {
    const params = this.route?.snapshot?.queryParamMap;
    if (!params) {
      return {};
    }

    const query: Record<string, unknown> = {};
    for (const key of URL_QUERY_KEYS) {
      const raw = params.get(key);
      if (raw === null || raw === '') {
        continue;
      }

      if (key === 'page' || key === 'limit') {
        const parsed = Number(raw);
        if (Number.isFinite(parsed)) {
          query[key] = parsed;
        }
      } else if (key === 'includeArchived') {
        query[key] = raw === 'true';
      } else {
        query[key] = raw;
      }
    }

    return query as BoardQuery;
  }

  private writeQueryToUrl(query: BoardQuery): void {
    if (!this.router || !this.route) {
      return;
    }

    const queryParams: Record<string, string | null> = {};
    for (const key of URL_QUERY_KEYS) {
      const value = query[key];
      // `null` removes the key from the URL, so a cleared filter leaves no trace to restore.
      queryParams[key] = value === undefined || value === null || value === '' ? null : String(value);
    }

    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams,
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
}
