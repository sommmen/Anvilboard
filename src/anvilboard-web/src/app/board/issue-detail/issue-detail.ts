import { Component, DestroyRef, effect, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BoardApiService } from '../../core/board-api.service';
import {
  ActivityEntry,
  Comment,
  Issue,
  IssueLink,
  IssueLinkDirection,
  PROVIDER_LABEL,
  REALTIME_ACTIVITY_ADDED,
  WorkflowState,
} from '../../core/models';
import { RealtimeBoardSyncService } from '../../core/realtime-board-sync.service';
import { ActivityFeed } from '../activity-feed/activity-feed';

const ACTIVITY_PAGE_SIZE = 20;

@Component({
  imports: [ActivityFeed],
  selector: 'app-issue-detail',
  styleUrl: './issue-detail.scss',
  templateUrl: './issue-detail.html',
})
export class IssueDetail {
  private readonly api = inject(BoardApiService);
  private readonly realtime = inject(RealtimeBoardSyncService, { optional: true });
  private readonly destroyRef = inject(DestroyRef);

  readonly issue = input.required<Issue>();
  readonly closed = output<void>();
  readonly changed = output<void>();

  readonly workflowStates = signal<WorkflowState[]>([]);
  readonly providerLabels = PROVIDER_LABEL;
  readonly linkDirections = IssueLinkDirection;

  /** Server-owned vocabulary; empty until it loads, which only costs the datalist its suggestions. */
  readonly suggestedLinkTypes = signal<string[]>([]);

  readonly comments = signal<Comment[]>([]);
  readonly commentsLoading = signal(false);
  readonly newComment = signal('');

  readonly activity = signal<ActivityEntry[]>([]);
  readonly activityLoading = signal(false);
  readonly activityCursor = signal<string | null>(null);

  readonly links = signal<IssueLink[]>([]);
  readonly linkableIssues = signal<Issue[]>([]);
  readonly linkTargetKey = signal('');
  readonly linkType = signal('');
  readonly linkDescription = signal('');
  readonly linkError = signal('');

  /** Id of the link currently being edited inline, or `null` when nothing is being edited. */
  readonly editingLinkId = signal<string | null>(null);
  readonly editLinkType = signal('');
  readonly editLinkDescription = signal('');

  constructor() {
    effect(() => {
      const issueId = this.issue().id;
      this.refreshLinks(issueId);
      this.refreshComments(issueId);
      this.refreshActivity(issueId);
    });

    this.api.listIssues().subscribe((issues) => this.linkableIssues.set(issues));
    this.api.listWorkflowStates().subscribe((states) => this.workflowStates.set(states));
    this.api.listLinkTypes().subscribe({
      next: (types) => this.suggestedLinkTypes.set(types),
      error: () => {},
    });

    this.realtime?.changes.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((envelope) => {
      // Anything that mutates an issue also writes history, so a single activity refetch keeps the
      // panel current without a per-event-type mapping that would go stale as the server grows new
      // event kinds.
      if (envelope.eventType === REALTIME_ACTIVITY_ADDED && envelope.issueId === this.issue().id) {
        this.refreshActivity(this.issue().id);
      }
    });
  }

  changeStatus(workflowStateId: string): void {
    this.api.changeStatus(this.issue().id, workflowStateId).subscribe(() => this.changed.emit());
  }

  /**
   * Loads the persisted comments. Before this existed the panel only ever showed comments added in
   * the current session, so reopening an issue — or reloading the page — presented an empty
   * thread even when the issue had a long history.
   */
  refreshComments(issueId: string): void {
    this.commentsLoading.set(true);
    this.api.listIssueComments(issueId).subscribe({
      next: (comments) => {
        this.comments.set(comments);
        this.commentsLoading.set(false);
      },
      error: () => this.commentsLoading.set(false),
    });
  }

  refreshActivity(issueId: string): void {
    this.activityLoading.set(true);
    this.api.listIssueActivity(issueId, { limit: ACTIVITY_PAGE_SIZE }).subscribe({
      next: (page) => {
        this.activity.set(page.entries);
        this.activityCursor.set(page.nextCursor ?? null);
        this.activityLoading.set(false);
      },
      error: () => this.activityLoading.set(false),
    });
  }

  loadMoreActivity(): void {
    const cursor = this.activityCursor();
    if (!cursor || this.activityLoading()) {
      return;
    }

    this.activityLoading.set(true);
    this.api.listIssueActivity(this.issue().id, { limit: ACTIVITY_PAGE_SIZE, cursor }).subscribe({
      next: (page) => {
        this.activity.update((existing) => [...existing, ...page.entries]);
        this.activityCursor.set(page.nextCursor ?? null);
        this.activityLoading.set(false);
      },
      error: () => this.activityLoading.set(false),
    });
  }

  submitComment(): void {
    const body = this.newComment().trim();
    if (!body) {
      return;
    }
    this.api.addComment(this.issue().id, body).subscribe((comment) => {
      this.comments.update((existing) => [...existing, comment]);
      this.newComment.set('');
    });
  }

  refreshLinks(issueId: string): void {
    this.api.listIssueLinks(issueId).subscribe((links) => this.links.set(links));
  }

  submitLink(): void {
    const targetKey = this.linkTargetKey().trim();
    const type = this.linkType().trim();
    if (!targetKey || !type) {
      return;
    }
    const target = this.linkableIssues().find(
      (candidate) => candidate.key.toUpperCase() === targetKey.toUpperCase(),
    );
    if (!target) {
      this.linkError.set(`No issue found with key "${targetKey}".`);
      return;
    }
    this.linkError.set('');
    this.api
      .createIssueLink(this.issue().id, target.id, type, this.linkDescription().trim() || undefined)
      .subscribe({
        next: (link) => {
          this.links.update((existing) => [...existing, link]);
          this.linkTargetKey.set('');
          this.linkType.set('');
          this.linkDescription.set('');
        },
        error: (err) => {
          this.linkError.set(
            err?.error?.detail ?? err?.error?.title ?? 'Could not create the link.',
          );
        },
      });
  }

  startEditingLink(link: IssueLink): void {
    this.editingLinkId.set(link.id);
    this.editLinkType.set(link.type);
    this.editLinkDescription.set(link.description ?? '');
    this.linkError.set('');
  }

  cancelEditingLink(): void {
    this.editingLinkId.set(null);
  }

  /**
   * Saves an inline link edit. Correcting a mistyped type or description previously meant deleting
   * the link and recreating it, which discarded the link's history; `PATCH` preserves it.
   */
  submitLinkEdit(link: IssueLink): void {
    const type = this.editLinkType().trim();
    if (!type) {
      return;
    }

    this.api
      .updateIssueLink(this.issue().id, link.id, {
        type,
        description: this.editLinkDescription().trim(),
      })
      .subscribe({
        next: (updated) => {
          this.links.update((existing) =>
            existing.map((candidate) => (candidate.id === updated.id ? updated : candidate)),
          );
          this.editingLinkId.set(null);
          this.linkError.set('');
        },
        error: (err) => {
          this.linkError.set(
            err?.error?.detail ?? err?.error?.title ?? 'Could not update the link.',
          );
        },
      });
  }

  removeLink(link: IssueLink): void {
    this.api.removeIssueLink(this.issue().id, link.id).subscribe({
      next: () => {
        this.links.update((existing) => existing.filter((candidate) => candidate.id !== link.id));
      },
      error: (err) => {
        this.linkError.set(err?.error?.detail ?? err?.error?.title ?? 'Could not remove the link.');
      },
    });
  }

  linkedIssueLabel(link: IssueLink): string {
    const linkedId =
      link.direction === IssueLinkDirection.Outgoing ? link.targetIssueId : link.sourceIssueId;
    return this.linkableIssues().find((candidate) => candidate.id === linkedId)?.key ?? linkedId;
  }

  close(): void {
    this.closed.emit();
  }
}
