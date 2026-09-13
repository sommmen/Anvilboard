import { TestBed } from '@angular/core/testing';
import { Subject, of } from 'rxjs';
import { BoardApiService } from '../../core/board-api.service';
import {
  ActivityPage,
  Comment,
  Issue,
  IssueLink,
  IssueLinkDirection,
  IssuePriority,
  IssueStatus,
  RealtimeChangeEnvelope,
} from '../../core/models';
import { RealtimeBoardSyncService } from '../../core/realtime-board-sync.service';
import { IssueDetail } from './issue-detail';

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

function link(overrides: Partial<IssueLink> = {}): IssueLink {
  return {
    id: 'link-1',
    sourceIssueId: 'issue-1',
    targetIssueId: 'issue-2',
    type: 'RELATED',
    direction: IssueLinkDirection.Outgoing,
    ...overrides,
  } as IssueLink;
}

class FakeRealtimeBoardSyncService {
  readonly changesSubject = new Subject<RealtimeChangeEnvelope>();
  readonly resyncSubject = new Subject<void>();
  readonly changes = this.changesSubject.asObservable();
  readonly resyncRequired = this.resyncSubject.asObservable();

  start(): Promise<void> {
    return Promise.resolve();
  }
}

class FakeBoardApiService {
  listCommentCalls: string[] = [];
  listActivityCalls: { issueId: string; cursor?: string }[] = [];
  updateLinkCalls: { linkId: string; changes: { type?: string; description?: string } }[] = [];

  comments: Comment[] = [{ id: 'comment-1', issueId: 'issue-1', body: 'Persisted' } as Comment];
  activityPages: ActivityPage[] = [
    {
      entries: [
        {
          id: 'activity-1',
          type: 'CommentAdded',
          occurredAt: '2026-01-01T00:00:00Z',
          text: 'left a comment',
        },
      ],
      nextCursor: 'cursor-1',
    },
    {
      entries: [
        {
          id: 'activity-2',
          type: 'Created',
          occurredAt: '2025-12-31T00:00:00Z',
          text: 'created the issue',
        },
      ],
      nextCursor: null,
    },
  ];

  listIssueComments(issueId: string) {
    this.listCommentCalls.push(issueId);
    return of(this.comments);
  }

  listIssueActivity(issueId: string, options?: { limit?: number; cursor?: string }) {
    this.listActivityCalls.push({ issueId, cursor: options?.cursor });
    const page = options?.cursor ? this.activityPages[1] : this.activityPages[0];
    return of(page);
  }

  listIssueLinks() {
    return of([link()]);
  }

  updateIssueLink(
    _issueId: string,
    linkId: string,
    changes: { type?: string; description?: string },
  ) {
    this.updateLinkCalls.push({ linkId, changes });
    return of(link({ id: linkId, type: changes.type!, description: changes.description }));
  }

  listLinkTypes() {
    return of(['RELATED', 'BLOCKS']);
  }

  listIssues() {
    return of([]);
  }

  listWorkflowStates() {
    return of([]);
  }
}

describe('IssueDetail', () => {
  let api: FakeBoardApiService;
  let realtime: FakeRealtimeBoardSyncService;

  function createDetail(current: Issue = issue()): IssueDetail {
    const fixture = TestBed.createComponent(IssueDetail);
    fixture.componentRef.setInput('issue', current);
    // The constructor's effect only runs on flush, which is what loads the issue-scoped data.
    fixture.detectChanges();
    return fixture.componentInstance;
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

  it('loads persisted comments so reopening an issue does not show an empty thread', () => {
    const detail = createDetail();

    expect(api.listCommentCalls).toEqual(['issue-1']);
    expect(detail.comments().map((comment) => comment.body)).toEqual(['Persisted']);
  });

  it('loads the activity feed and keeps the cursor for paging', () => {
    const detail = createDetail();

    expect(detail.activity().map((entry) => entry.id)).toEqual(['activity-1']);
    expect(detail.activityCursor()).toBe('cursor-1');
  });

  it('appends the next activity page and clears the cursor when the feed is exhausted', () => {
    const detail = createDetail();

    detail.loadMoreActivity();

    expect(detail.activity().map((entry) => entry.id)).toEqual(['activity-1', 'activity-2']);
    expect(detail.activityCursor()).toBeNull();
  });

  it('does not page past the end of the feed', () => {
    const detail = createDetail();
    detail.loadMoreActivity();
    const callsAfterPaging = api.listActivityCalls.length;

    detail.loadMoreActivity();

    expect(api.listActivityCalls.length).toBe(callsAfterPaging);
  });

  it('refreshes the activity feed when a realtime activity event names this issue', () => {
    createDetail();
    const callsAfterLoad = api.listActivityCalls.length;

    realtime.changesSubject.next({
      eventType: 'activity.added',
      correlationId: 'correlation-1',
      occurredAt: '2026-01-01T00:00:01Z',
      issueId: 'issue-1',
    });

    expect(api.listActivityCalls.length).toBe(callsAfterLoad + 1);
  });

  it('ignores an activity event for a different issue', () => {
    createDetail();
    const callsAfterLoad = api.listActivityCalls.length;

    realtime.changesSubject.next({
      eventType: 'activity.added',
      correlationId: 'correlation-1',
      occurredAt: '2026-01-01T00:00:01Z',
      issueId: 'issue-other',
    });

    expect(api.listActivityCalls.length).toBe(callsAfterLoad);
  });

  it('edits a link in place instead of deleting and recreating it', () => {
    const detail = createDetail();
    detail.startEditingLink(detail.links()[0]);
    detail.editLinkType.set('BLOCKS');
    detail.editLinkDescription.set('now blocking');

    detail.submitLinkEdit(detail.links()[0]);

    expect(api.updateLinkCalls).toEqual([
      { linkId: 'link-1', changes: { type: 'BLOCKS', description: 'now blocking' } },
    ]);
    expect(detail.links()[0].type).toBe('BLOCKS');
    expect(detail.editingLinkId()).toBeNull();
  });

  it('refuses to save a link edit that would blank the type', () => {
    const detail = createDetail();
    detail.startEditingLink(detail.links()[0]);
    detail.editLinkType.set('   ');

    detail.submitLinkEdit(detail.links()[0]);

    expect(api.updateLinkCalls).toEqual([]);
  });

  it('takes the link-type vocabulary from the server rather than a hardcoded list', () => {
    const detail = createDetail();

    expect(detail.suggestedLinkTypes()).toEqual(['RELATED', 'BLOCKS']);
  });
});
