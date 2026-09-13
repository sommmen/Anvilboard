// Mirrors Anvilboard.Domain's wire shapes (Anvilboard.Api serializes enums as numbers, matching
// these numeric enum values one-for-one with Anvilboard.Domain.IssueStatus/IssuePriority/IntegrationProvider).

export enum IssueStatus {
  Backlog = 0,
  Todo = 1,
  InProgress = 2,
  InReview = 3,
  Done = 4,
  Cancelled = 5,
}

export const ISSUE_STATUSES: IssueStatus[] = [
  IssueStatus.Backlog,
  IssueStatus.Todo,
  IssueStatus.InProgress,
  IssueStatus.InReview,
  IssueStatus.Done,
  IssueStatus.Cancelled,
];

export const ISSUE_STATUS_LABEL: Record<IssueStatus, string> = {
  [IssueStatus.Backlog]: 'Backlog',
  [IssueStatus.Todo]: 'Todo',
  [IssueStatus.InProgress]: 'In Progress',
  [IssueStatus.InReview]: 'In Review',
  [IssueStatus.Done]: 'Done',
  [IssueStatus.Cancelled]: 'Cancelled',
};

export enum IssuePriority {
  None = 0,
  Low = 1,
  Medium = 2,
  High = 3,
  Urgent = 4,
}

export const ISSUE_PRIORITY_LABEL: Record<IssuePriority, string> = {
  [IssuePriority.None]: 'No priority',
  [IssuePriority.Low]: 'Low',
  [IssuePriority.Medium]: 'Medium',
  [IssuePriority.High]: 'High',
  [IssuePriority.Urgent]: 'Urgent',
};

export enum IntegrationProvider {
  Local = 0,
  GitHub = 1,
  Linear = 2,
  Custom = 99,
}

export const PROVIDER_LABEL: Record<number, string> = {
  [IntegrationProvider.Local]: 'Local',
  [IntegrationProvider.GitHub]: 'GitHub',
  [IntegrationProvider.Linear]: 'Linear',
  [IntegrationProvider.Custom]: 'Custom',
};

export interface WorkflowState {
  id: string;
  key: string;
  displayName: string;
  order: number;
  isTerminal: boolean;
  isArchived: boolean;
}

export interface Team {
  id: string;
  workspaceId: string;
  name: string;
  key: string;
  nextIssueNumber: number;
  createdAt: string;
}

export interface Member {
  id: string;
  workspaceId: string;
  displayName: string;
  email?: string | null;
  avatarUrl?: string | null;
  isAgent: boolean;
}

export interface Issue {
  id: string;
  teamId: string;
  projectId?: string | null;
  key: string;
  title: string;
  description?: string | null;
  status: IssueStatus;
  workflowStateId: string;
  /** Optimistic-concurrency version; realtime clients compare it to detect a missed change. */
  version: number;
  priority: IssuePriority;
  assigneeId?: string | null;
  createdById?: string | null;
  source: IntegrationProvider;
  createdAt: string;
  updatedAt: string;
  completedAt?: string | null;
  labelIds: string[];
}

export interface Comment {
  id: string;
  issueId: string;
  authorId?: string | null;
  body: string;
  createdAt: string;
}

export enum IssueLinkDirection {
  Outgoing = 0,
  Incoming = 1,
}

export interface IssueLink {
  id: string;
  sourceIssueId: string;
  targetIssueId: string;
  type: string;
  description: string;
  createdById?: string | null;
  createdAt: string;
  direction: IssueLinkDirection;
}

export interface DashboardSummary {
  issuesByStatus: Record<string, number>;
  issuesBySource: Record<string, number>;
  createdLast7Days: number;
  completedLast7Days: number;
  openIssuesByAssignee: { assigneeId: string; openIssueCount: number }[];
}

/**
 * Mirrors Anvilboard.Api.Realtime.RealtimeChangeEnvelope. Every field past `eventType` is optional
 * so the server can add fields without a coordinated client release — a client that meets an
 * `eventType` it does not know falls back to a re-fetch rather than failing to parse.
 *
 * `workspaceId` is deliberately absent: a connection only ever receives its own workspace's changes.
 */
export interface RealtimeChangeEnvelope {
  eventType: string;
  correlationId: string;
  occurredAt: string;
  issueId?: string | null;
  version?: number | null;
  changeKind?: RealtimeIssueChangeKind | null;
  activityEventId?: string | null;
  summaryVersion?: string | null;
  pluginEventType?: string | null;
}

export type RealtimeIssueChangeKind = 'CREATED' | 'UPDATED';

export const REALTIME_ISSUE_CHANGED = 'issue.changed';
export const REALTIME_ACTIVITY_ADDED = 'activity.added';
export const REALTIME_DASHBOARD_CHANGED = 'dashboard.changed';

// --- Board query ---------------------------------------------------------------------------
// `GET /api/board` takes and echoes its vocabulary as strings, unlike the numeric enums above
// that `/api/issues` serializes. These are string unions rather than TypeScript enums so an
// unrecognised server value is a compile-time type error at the call site instead of an
// `undefined` lookup at runtime.

export type BoardGroupBy = 'WorkflowState' | 'Type' | 'Priority' | 'Assignee' | 'Label';

export type BoardOrderBy = 'CreatedAt' | 'UpdatedAt' | 'Priority' | 'Manual';

export type BoardSyncCondition = 'Fresh' | 'Stale' | 'Paused' | 'Failed';

export const BOARD_GROUP_BY_OPTIONS: BoardGroupBy[] = [
  'WorkflowState',
  'Type',
  'Priority',
  'Assignee',
  'Label',
];

export const BOARD_GROUP_BY_LABEL: Record<BoardGroupBy, string> = {
  WorkflowState: 'Workflow state',
  Type: 'Type',
  Priority: 'Priority',
  Assignee: 'Assignee',
  Label: 'Label',
};

export const BOARD_ORDER_BY_OPTIONS: BoardOrderBy[] = [
  'CreatedAt',
  'UpdatedAt',
  'Priority',
  'Manual',
];

export const BOARD_ORDER_BY_LABEL: Record<BoardOrderBy, string> = {
  CreatedAt: 'Created',
  UpdatedAt: 'Updated',
  Priority: 'Priority',
  Manual: 'Manual',
};

export const BOARD_SYNC_CONDITION_OPTIONS: BoardSyncCondition[] = [
  'Fresh',
  'Stale',
  'Paused',
  'Failed',
];

export const BOARD_SYNC_CONDITION_LABEL: Record<BoardSyncCondition, string> = {
  Fresh: 'Fresh',
  Stale: 'Stale',
  Paused: 'Paused',
  Failed: 'Failed',
};

/** Query parameters for `GET /api/board`. Every field is optional; the server supplies defaults. */
export interface BoardQuery {
  workflowStateId?: string | null;
  assigneeId?: string | null;
  provider?: string | null;
  projectId?: string | null;
  priority?: string | null;
  type?: string | null;
  labelId?: string | null;
  syncCondition?: BoardSyncCondition | null;
  groupBy?: BoardGroupBy | null;
  orderBy?: BoardOrderBy | null;
  page?: number | null;
  limit?: number | null;
  cursor?: string | null;
  includeArchived?: boolean | null;
}

export interface BoardIssue {
  id: string;
  key: string;
  title: string;
  workflowStateId: string;
  type?: string | null;
  priority: string;
  assigneeId?: string | null;
  projectId?: string | null;
  provider: string;
  labelIds: string[];
  createdAt: string;
  updatedAt: string;
  archivedAt?: string | null;
}

export interface BoardGroup {
  key: string;
  displayName: string;
  issues: BoardIssue[];
}

/** The server's resolved view of the request, so the client can tell which filters took effect. */
export interface BoardAppliedQuery {
  workflowStateId?: string | null;
  assigneeId?: string | null;
  provider?: string | null;
  projectId?: string | null;
  priority?: string | null;
  type?: string | null;
  labelId?: string | null;
  syncCondition?: string | null;
  groupBy: BoardGroupBy;
  orderBy: BoardOrderBy;
  page: number;
  limit: number;
  includeArchived: boolean;
}

export interface BoardResult {
  groups: BoardGroup[];
  totalCount: number;
  page: number;
  limit: number;
  nextCursor?: string | null;
  appliedQuery: BoardAppliedQuery;
}

// --- Activity feed -------------------------------------------------------------------------

/**
 * One rendered history entry. `text` is the server-rendered sentence — the client displays it
 * verbatim so a new `type` the client has never heard of still reads correctly, and only reaches
 * into `data` for the richer presentation of the types it does recognise.
 */
export interface ActivityEntry {
  id: string;
  type: string;
  actorId?: string | null;
  actorDisplayName?: string | null;
  occurredAt: string;
  data?: unknown;
  text: string;
}

/** A `null` `nextCursor` means the feed is exhausted; there is no separate `hasMore` flag. */
export interface ActivityPage {
  entries: ActivityEntry[];
  nextCursor?: string | null;
}

// --- Taxonomy ------------------------------------------------------------------------------

export interface ProjectSummary {
  id: string;
  teamId: string;
  name: string;
  description?: string | null;
}

export interface LabelSummary {
  id: string;
  name: string;
  color: string;
}
