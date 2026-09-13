import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ActivityPage,
  BoardQuery,
  BoardResult,
  Comment,
  DashboardSummary,
  Issue,
  IssueLink,
  IssuePriority,
  IssueStatus,
  LabelSummary,
  Member,
  ProjectSummary,
  Team,
  WorkflowState,
} from './models';

/**
 * Thin client over Anvilboard.Api's minimal-API endpoints. Kept as one flat service (rather than
 * per-resource services) since the whole surface is small — mirrors the CLI/MCP agent surface's
 * BoardAgentService one-to-one so the web client and an agent exercise the same operations.
 */
@Injectable({ providedIn: 'root' })
export class BoardApiService {
  private readonly http = inject(HttpClient);

  listTeams(): Observable<Team[]> {
    return this.http.get<Team[]>('/api/teams');
  }

  createTeam(name: string, key: string): Observable<Team> {
    return this.http.post<Team>('/api/teams', { name, key });
  }

  listMembers(): Observable<Member[]> {
    return this.http.get<Member[]>('/api/members');
  }

  createMember(displayName: string, email?: string, isAgent = false): Observable<Member> {
    return this.http.post<Member>('/api/members', { displayName, email, isAgent });
  }

  listIssues(filter?: {
    teamId?: string;
    status?: IssueStatus;
    assigneeId?: string;
  }): Observable<Issue[]> {
    const params: Record<string, string> = {};
    if (filter?.teamId) params['teamId'] = filter.teamId;
    if (filter?.status !== undefined) params['status'] = String(filter.status);
    if (filter?.assigneeId) params['assigneeId'] = filter.assigneeId;
    return this.http.get<Issue[]>('/api/issues', { params });
  }

  getIssue(id: string): Observable<Issue> {
    return this.http.get<Issue>(`/api/issues/${id}`);
  }

  createIssue(request: {
    teamId: string;
    title: string;
    description?: string;
    priority?: IssuePriority;
    assigneeId?: string;
  }): Observable<Issue> {
    return this.http.post<Issue>('/api/issues', request);
  }

  listWorkflowStates(): Observable<WorkflowState[]> {
    return this.http.get<WorkflowState[]>('/api/workflow/states');
  }

  changeStatus(issueId: string, workflowStateId: string): Observable<Issue> {
    return this.http.patch<Issue>(`/api/issues/${issueId}/status`, { workflowStateId });
  }

  assign(issueId: string, assigneeId: string | null): Observable<Issue> {
    return this.http.patch<Issue>(`/api/issues/${issueId}/assignee`, { assigneeId });
  }

  addComment(issueId: string, body: string, authorId?: string): Observable<Comment> {
    return this.http.post<Comment>(`/api/issues/${issueId}/comments`, { body, authorId });
  }

  listIssueLinks(issueId: string): Observable<IssueLink[]> {
    return this.http.get<IssueLink[]>(`/api/issues/${issueId}/links`);
  }

  createIssueLink(
    issueId: string,
    targetIssueId: string,
    type: string,
    description?: string,
    actorId?: string,
  ): Observable<IssueLink> {
    return this.http.post<IssueLink>(`/api/issues/${issueId}/links`, {
      targetIssueId,
      type,
      description,
      actorId,
    });
  }

  removeIssueLink(issueId: string, linkId: string, actorId?: string): Observable<void> {
    const params: Record<string, string> = {};
    if (actorId) params['actorId'] = actorId;
    return this.http.delete<void>(`/api/issues/${issueId}/links/${linkId}`, { params });
  }

  /**
   * Partially updates a link. Omitting a field leaves it unchanged, so the caller can retype a
   * link without having to resend the description it never touched.
   */
  updateIssueLink(
    issueId: string,
    linkId: string,
    changes: { type?: string; description?: string },
    actorId?: string,
  ): Observable<IssueLink> {
    return this.http.patch<IssueLink>(`/api/issues/${issueId}/links/${linkId}`, {
      ...changes,
      actorId,
    });
  }

  /**
   * The grouped, filtered board. Unlike {@link listIssues}, grouping and ordering happen on the
   * server, so the client renders whatever groups come back instead of assuming a fixed set of
   * status columns — which is what lets group-by-assignee or group-by-label work at all.
   */
  queryBoard(query: BoardQuery = {}): Observable<BoardResult> {
    const params: Record<string, string> = {};
    for (const [key, value] of Object.entries(query)) {
      if (value !== undefined && value !== null && value !== '') {
        params[key] = String(value);
      }
    }
    return this.http.get<BoardResult>('/api/board', { params });
  }

  listIssueComments(issueId: string): Observable<Comment[]> {
    return this.http.get<Comment[]>(`/api/issues/${issueId}/comments`);
  }

  listIssueActivity(
    issueId: string,
    options?: { limit?: number; cursor?: string },
  ): Observable<ActivityPage> {
    const params: Record<string, string> = {};
    if (options?.limit !== undefined) params['limit'] = String(options.limit);
    if (options?.cursor) params['cursor'] = options.cursor;
    return this.http.get<ActivityPage>(`/api/issues/${issueId}/activity`, { params });
  }

  listProjects(): Observable<ProjectSummary[]> {
    return this.http.get<ProjectSummary[]>('/api/projects');
  }

  listLabels(): Observable<LabelSummary[]> {
    return this.http.get<LabelSummary[]>('/api/labels');
  }

  /** The server-owned link-type vocabulary, so the client never hardcodes a stale suggestion list. */
  listLinkTypes(): Observable<string[]> {
    return this.http.get<string[]>('/api/issue-link-types');
  }

  getDashboardSummary(teamId?: string): Observable<DashboardSummary> {
    const params: Record<string, string> = {};
    if (teamId) params['teamId'] = teamId;
    return this.http.get<DashboardSummary>('/api/dashboard/summary', { params });
  }
}
