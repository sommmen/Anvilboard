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

export interface DashboardSummary {
  issuesByStatus: Record<string, number>;
  issuesBySource: Record<string, number>;
  createdLast7Days: number;
  completedLast7Days: number;
  openIssuesByAssignee: { assigneeId: string; openIssueCount: number }[];
}
