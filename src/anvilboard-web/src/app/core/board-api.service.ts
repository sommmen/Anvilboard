import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Comment,
  DashboardSummary,
  Issue,
  IssuePriority,
  IssueStatus,
  Member,
  Team,
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

  changeStatus(issueId: string, status: IssueStatus): Observable<Issue> {
    return this.http.patch<Issue>(`/api/issues/${issueId}/status`, { status });
  }

  assign(issueId: string, assigneeId: string | null): Observable<Issue> {
    return this.http.patch<Issue>(`/api/issues/${issueId}/assignee`, { assigneeId });
  }

  addComment(issueId: string, body: string, authorId?: string): Observable<Comment> {
    return this.http.post<Comment>(`/api/issues/${issueId}/comments`, { body, authorId });
  }

  getDashboardSummary(teamId?: string): Observable<DashboardSummary> {
    const params: Record<string, string> = {};
    if (teamId) params['teamId'] = teamId;
    return this.http.get<DashboardSummary>('/api/dashboard/summary', { params });
  }
}
