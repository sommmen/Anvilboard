import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { BoardApiService } from '../../core/board-api.service';
import {
  BoardIssue,
  BoardQuery,
  BoardResult,
  Issue,
  IssuePriority,
  IssueStatus,
  RealtimeChangeEnvelope,
} from '../../core/models';
import { RealtimeBoardSyncService } from '../../core/realtime-board-sync.service';
import { BoardPage } from './board-page';

function boardIssue(overrides: Partial<BoardIssue> = {}): BoardIssue {
  return {
    id: 'issue-1',
    key: 'RT-1',
    title: 'Original title',
    workflowStateId: 'workflow-state-backlog',
    priority: 'None',
    provider: 'Local',
    labelIds: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function issue(overrides: Partial<Issue> = {}): Issue {
  return {
    id: 'issue-1',
    teamId: 'team-1',
    key: 'RT-1',
    title: 'Original title',
    status: IssueStatus.Backlog,
    workflowStateId: 'workflow-state-backlog',
    version: 1,
    priority: IssuePriority.None,
    source: 0,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    labelIds: [],
    ...overrides,
  } as Issue;
}

function envelope(overrides: Partial<RealtimeChangeEnvelope> = {}): RealtimeChangeEnvelope {
  return {
    eventType: 'issue.changed',
    correlationId: 'correlation-1',
    occurredAt: '2026-01-01T00:00:01Z',
    issueId: 'issue-1',
    version: 2,
    changeKind: 'UPDATED',
    ...overrides,
  };
}

class FakeRealtimeBoardSyncService {
  readonly changesSubject = new Subject<RealtimeChangeEnvelope>();
  readonly resyncSubject = new Subject<void>();
  readonly changes = this.changesSubject.asObservable();
  readonly resyncRequired = this.resyncSubject.asObservable();
  startCalls = 0;

  start(): Promise<void> {
    this.startCalls++;
    return Promise.resolve();
  }
}

class FakeBoardApiService {
  queryBoardCalls: BoardQuery[] = [];
  getIssueCalls: string[] = [];
  issues: BoardIssue[] = [boardIssue()];
  nextIssue: Issue = issue({ title: 'Updated title', version: 2 });

  queryBoard(query: BoardQuery = {}) {
    this.queryBoardCalls.push(query);
    const result: BoardResult = {
      groups: [{ key: 'workflow-state-backlog', displayName: 'Backlog', issues: this.issues }],
      totalCount: this.issues.length,
      page: 1,
      limit: 25,
      appliedQuery: {
        groupBy: 'WorkflowState',
        orderBy: 'CreatedAt',
        page: 1,
        limit: 25,
        includeArchived: false,
      },
    };
    return of(result);
  }

  listIssues() {
    return of([]);
  }

  listTeams() {
    return of([]);
  }

  listMembers() {
    return of([]);
  }

  listWorkflowStates() {
    return of([]);
  }

  listProjects() {
    return of([]);
  }

  listLabels() {
    return of([]);
  }

  getIssue(id: string) {
    this.getIssueCalls.push(id);
    return of(this.nextIssue);
  }
}

describe('BoardPage', () => {
  let api: FakeBoardApiService;
  let realtime: FakeRealtimeBoardSyncService;

  function createPage(): BoardPage {
    return TestBed.runInInjectionContext(() => new BoardPage());
  }

  beforeEach(() => {
    api = new FakeBoardApiService();
    realtime = new FakeRealtimeBoardSyncService();
    TestBed.configureTestingModule({
      providers: [
        { provide: BoardApiService, useValue: api },
        { provide: RealtimeBoardSyncService, useValue: realtime },
      ],
    });
  });

  it('loads the board through the query endpoint', () => {
    const page = createPage();

    expect(api.queryBoardCalls.length).toBe(1);
    expect(page.groups().map((group) => group.displayName)).toEqual(['Backlog']);
    expect(page.totalCount()).toBe(1);
  });

  it('re-runs the query when a filter changes so the server decides membership', () => {
    const page = createPage();
    const callsAfterLoad = api.queryBoardCalls.length;

    page.applyQuery({ priority: 'Urgent', page: 1 });

    expect(api.queryBoardCalls.length).toBe(callsAfterLoad + 1);
    expect(api.queryBoardCalls.at(-1)).toEqual({ priority: 'Urgent', page: 1 });
    expect(page.query().priority).toBe('Urgent');
  });

  it('re-runs the query for an issue change, because the issue may no longer match', () => {
    createPage();
    const callsAfterLoad = api.queryBoardCalls.length;

    realtime.changesSubject.next(envelope());

    expect(api.queryBoardCalls.length).toBe(callsAfterLoad + 1);
  });

  it('does not refresh the board for an activity event', () => {
    createPage();
    const callsAfterLoad = api.queryBoardCalls.length;

    realtime.changesSubject.next(envelope({ eventType: 'activity.added' }));

    expect(api.queryBoardCalls.length).toBe(callsAfterLoad);
    expect(api.getIssueCalls).toEqual([]);
  });

  it('re-fetches the board exactly once for an unknown event type', () => {
    createPage();
    const callsAfterLoad = api.queryBoardCalls.length;

    realtime.changesSubject.next(envelope({ eventType: 'something.new' }));

    expect(api.queryBoardCalls.length).toBe(callsAfterLoad + 1);
  });

  it('re-fetches exactly once on reconnect, since the server never replays', () => {
    createPage();
    const callsAfterLoad = api.queryBoardCalls.length;

    realtime.resyncSubject.next();

    expect(api.queryBoardCalls.length).toBe(callsAfterLoad + 1);
  });

  it('fetches the full issue when a card is opened', () => {
    const page = createPage();

    page.openIssue(page.groups()[0].issues[0]);

    expect(api.getIssueCalls).toEqual(['issue-1']);
    expect(page.selectedIssue()?.title).toBe('Updated title');
  });

  it('keeps a selected issue selected and updates it to the new version', () => {
    const page = createPage();
    page.openIssue(page.groups()[0].issues[0]);

    realtime.changesSubject.next(envelope());

    expect(page.selectedIssue()?.id).toBe('issue-1');
    expect(page.selectedIssue()?.title).toBe('Updated title');
  });

  it('does not replace a selected issue with a stale re-fetch', () => {
    const page = createPage();
    page.openIssue(page.groups()[0].issues[0]);
    api.nextIssue = issue({ title: 'Stale title', version: 0 });

    realtime.changesSubject.next(envelope());

    expect(page.selectedIssue()?.title).toBe('Updated title');
  });

  it('does not fetch a detail that is not open', () => {
    createPage();

    realtime.changesSubject.next(envelope());

    expect(api.getIssueCalls).toEqual([]);
  });

  it('flattens groups into rows for the list view', () => {
    const page = createPage();

    const rows = page.listRows();

    expect(rows.length).toBe(1);
    expect(rows[0].group.displayName).toBe('Backlog');
    expect(rows[0].issue.id).toBe('issue-1');
  });

  it('opens the realtime connection on load', () => {
    createPage();

    expect(realtime.startCalls).toBe(1);
  });
});
