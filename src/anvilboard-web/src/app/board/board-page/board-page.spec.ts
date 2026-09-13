import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { of } from 'rxjs';
import { BoardApiService } from '../../core/board-api.service';
import { Issue, IssuePriority, IssueStatus, RealtimeChangeEnvelope } from '../../core/models';
import { RealtimeBoardSyncService } from '../../core/realtime-board-sync.service';
import { BoardPage } from './board-page';

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
  listIssuesCalls = 0;
  getIssueCalls: string[] = [];
  issues: Issue[] = [issue()];
  nextIssue: Issue = issue({ title: 'Updated title', version: 2 });

  listIssues() {
    this.listIssuesCalls++;
    return of(this.issues);
  }

  listTeams() {
    return of([]);
  }

  listWorkflowStates() {
    return of([]);
  }

  getIssue(id: string) {
    this.getIssueCalls.push(id);
    return of(this.nextIssue);
  }
}

describe('BoardPage realtime reconciliation', () => {
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

  it('patches the changed issue in place instead of re-listing the board', () => {
    const page = createPage();
    const listCallsAfterLoad = api.listIssuesCalls;

    realtime.changesSubject.next(envelope());

    expect(api.getIssueCalls).toEqual(['issue-1']);
    expect(api.listIssuesCalls).toBe(listCallsAfterLoad);
    expect(page.issues().map((entry) => entry.title)).toEqual(['Updated title']);
  });

  it('keeps a selected issue selected and updates it to the new version', () => {
    const page = createPage();
    page.openIssue(page.issues()[0]);

    realtime.changesSubject.next(envelope());

    expect(page.selectedIssue()?.id).toBe('issue-1');
    expect(page.selectedIssue()?.title).toBe('Updated title');
  });

  it('does not refresh the board for an activity event', () => {
    const page = createPage();
    const listCallsAfterLoad = api.listIssuesCalls;

    realtime.changesSubject.next(envelope({ eventType: 'activity.added' }));

    expect(api.listIssuesCalls).toBe(listCallsAfterLoad);
    expect(api.getIssueCalls).toEqual([]);
    expect(page).toBeTruthy();
  });

  it('re-fetches the whole board exactly once for an unknown event type', () => {
    const page = createPage();
    const listCallsAfterLoad = api.listIssuesCalls;

    realtime.changesSubject.next(envelope({ eventType: 'something.new' }));

    expect(api.listIssuesCalls).toBe(listCallsAfterLoad + 1);
    expect(api.getIssueCalls).toEqual([]);
    expect(page).toBeTruthy();
  });

  it('re-fetches the whole board for a change to an issue it has never seen', () => {
    const page = createPage();
    const listCallsAfterLoad = api.listIssuesCalls;

    realtime.changesSubject.next(envelope({ issueId: 'issue-unknown' }));

    expect(api.listIssuesCalls).toBe(listCallsAfterLoad + 1);
    expect(api.getIssueCalls).toEqual([]);
    expect(page).toBeTruthy();
  });

  it('re-fetches exactly once on reconnect, since the server never replays', () => {
    const page = createPage();
    const listCallsAfterLoad = api.listIssuesCalls;

    realtime.resyncSubject.next();

    expect(api.listIssuesCalls).toBe(listCallsAfterLoad + 1);
    expect(page).toBeTruthy();
  });

  it('ignores a stale re-fetch that would undo a newer version', () => {
    const page = createPage();
    api.nextIssue = issue({ title: 'Stale title', version: 0 });

    realtime.changesSubject.next(envelope());

    expect(page.issues()[0].title).toBe('Original title');
  });

  it('does not replace a selected issue with a stale re-fetch', () => {
    const page = createPage();
    page.openIssue(page.issues()[0]);
    api.nextIssue = issue({ title: 'Stale title', version: 0 });

    realtime.changesSubject.next(envelope());

    expect(page.selectedIssue()?.title).toBe('Original title');
  });

  it('opens the realtime connection on load', () => {
    createPage();

    expect(realtime.startCalls).toBe(1);
  });
});
