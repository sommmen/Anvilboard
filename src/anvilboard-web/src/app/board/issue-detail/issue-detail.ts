import { Component, effect, inject, input, output, signal } from '@angular/core';
import { BoardApiService } from '../../core/board-api.service';
import {
  Comment,
  Issue,
  IssueLink,
  IssueLinkDirection,
  PROVIDER_LABEL,
  WorkflowState,
} from '../../core/models';

/** Suggested vocabulary only — Type is never server-validated beyond non-empty (FR-LNK-001 AC1). */
const SUGGESTED_LINK_TYPES = ['RELATED', 'PARENT', 'DUPLICATE', 'MENTIONED_IN', 'BLOCKS'];

@Component({
  imports: [],
  selector: 'app-issue-detail',
  styleUrl: './issue-detail.scss',
  templateUrl: './issue-detail.html',
})
export class IssueDetail {
  private readonly api = inject(BoardApiService);

  readonly issue = input.required<Issue>();
  readonly closed = output<void>();
  readonly changed = output<void>();

  readonly workflowStates = signal<WorkflowState[]>([]);
  readonly providerLabels = PROVIDER_LABEL;
  readonly linkDirections = IssueLinkDirection;
  readonly suggestedLinkTypes = SUGGESTED_LINK_TYPES;

  readonly comments = signal<Comment[]>([]);
  readonly newComment = signal('');

  readonly links = signal<IssueLink[]>([]);
  readonly linkableIssues = signal<Issue[]>([]);
  readonly linkTargetKey = signal('');
  readonly linkType = signal('');
  readonly linkDescription = signal('');
  readonly linkError = signal('');

  constructor() {
    effect(() => {
      const issueId = this.issue().id;
      this.refreshLinks(issueId);
    });
    this.api.listIssues().subscribe((issues) => this.linkableIssues.set(issues));
    this.api.listWorkflowStates().subscribe((states) => this.workflowStates.set(states));
  }

  changeStatus(workflowStateId: string): void {
    this.api.changeStatus(this.issue().id, workflowStateId).subscribe(() => this.changed.emit());
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
