import { Component, inject, input, output, signal } from '@angular/core';
import { BoardApiService } from '../../core/board-api.service';
import {
  Comment,
  ISSUE_STATUSES,
  ISSUE_STATUS_LABEL,
  Issue,
  IssueStatus,
  PROVIDER_LABEL,
} from '../../core/models';

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

  readonly statuses = ISSUE_STATUSES;
  readonly statusLabels = ISSUE_STATUS_LABEL;
  readonly providerLabels = PROVIDER_LABEL;

  readonly comments = signal<Comment[]>([]);
  readonly newComment = signal('');

  changeStatus(status: IssueStatus): void {
    this.api.changeStatus(this.issue().id, status).subscribe(() => this.changed.emit());
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

  close(): void {
    this.closed.emit();
  }
}
