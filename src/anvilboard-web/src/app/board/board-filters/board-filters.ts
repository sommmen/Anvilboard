import { Component, input, output } from '@angular/core';
import {
  BOARD_GROUP_BY_LABEL,
  BOARD_GROUP_BY_OPTIONS,
  BOARD_ORDER_BY_LABEL,
  BOARD_ORDER_BY_OPTIONS,
  BOARD_SYNC_CONDITION_LABEL,
  BOARD_SYNC_CONDITION_OPTIONS,
  BoardGroupBy,
  BoardOrderBy,
  BoardQuery,
  BoardSyncCondition,
  ISSUE_PRIORITY_LABEL,
  IssuePriority,
  LabelSummary,
  Member,
  PROVIDER_LABEL,
  ProjectSummary,
  WorkflowState,
} from '../../core/models';

export type BoardView = 'kanban' | 'list';

/**
 * Purely presentational filter stack. It owns no fetch and no URL state: it renders the query it is
 * given and emits a complete replacement query on every change. That keeps a single owner
 * (`board-page`) responsible for deciding what a filter change means — fetch, URL sync, or both —
 * so the two can never disagree about which query is currently displayed.
 */
@Component({
  imports: [],
  selector: 'app-board-filters',
  styleUrl: './board-filters.scss',
  templateUrl: './board-filters.html',
})
export class BoardFilters {
  readonly query = input.required<BoardQuery>();
  readonly workflowStates = input<WorkflowState[]>([]);
  readonly members = input<Member[]>([]);
  readonly projects = input<ProjectSummary[]>([]);
  readonly labels = input<LabelSummary[]>([]);
  readonly view = input<BoardView>('kanban');

  readonly queryChanged = output<BoardQuery>();
  readonly viewChanged = output<BoardView>();

  readonly groupByOptions = BOARD_GROUP_BY_OPTIONS;
  readonly groupByLabels = BOARD_GROUP_BY_LABEL;
  readonly orderByOptions = BOARD_ORDER_BY_OPTIONS;
  readonly orderByLabels = BOARD_ORDER_BY_LABEL;
  readonly syncConditionOptions = BOARD_SYNC_CONDITION_OPTIONS;
  readonly syncConditionLabels = BOARD_SYNC_CONDITION_LABEL;
  readonly providerOptions = ['Local', 'GitHub', 'Linear', 'Custom'];
  readonly priorityOptions = [
    IssuePriority.Urgent,
    IssuePriority.High,
    IssuePriority.Medium,
    IssuePriority.Low,
    IssuePriority.None,
  ];

  readonly priorityLabels = ISSUE_PRIORITY_LABEL;
  readonly providerLabels = PROVIDER_LABEL;

  /** True when any filter is set, so the template can offer a one-click reset. */
  hasActiveFilters(): boolean {
    const query = this.query();
    return Boolean(
      query.workflowStateId ||
        query.assigneeId ||
        query.provider ||
        query.projectId ||
        query.priority ||
        query.type ||
        query.labelId ||
        query.syncCondition ||
        query.includeArchived,
    );
  }

  /**
   * Emits a new query with one field replaced. Changing any filter resets the page, because
   * page 3 of the previous result set is meaningless against a different filter.
   */
  update<K extends keyof BoardQuery>(key: K, value: BoardQuery[K]): void {
    this.queryChanged.emit({ ...this.query(), [key]: value, page: 1 });
  }

  updateFromSelect<K extends keyof BoardQuery>(key: K, raw: string): void {
    this.update(key, (raw === '' ? null : raw) as BoardQuery[K]);
  }

  setGroupBy(raw: string): void {
    this.update('groupBy', raw as BoardGroupBy);
  }

  setOrderBy(raw: string): void {
    this.update('orderBy', raw as BoardOrderBy);
  }

  setSyncCondition(raw: string): void {
    this.update('syncCondition', raw === '' ? null : (raw as BoardSyncCondition));
  }

  setIncludeArchived(checked: boolean): void {
    this.update('includeArchived', checked);
  }

  clear(): void {
    // Grouping, ordering and page size describe how the board is presented rather than what it
    // contains, so a filter reset deliberately preserves them.
    const query = this.query();
    this.queryChanged.emit({
      groupBy: query.groupBy,
      orderBy: query.orderBy,
      limit: query.limit,
      page: 1,
    });
  }

  setView(view: BoardView): void {
    this.viewChanged.emit(view);
  }
}
