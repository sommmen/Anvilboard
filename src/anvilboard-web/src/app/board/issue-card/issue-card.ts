import { Component, input, output } from '@angular/core';
import { ISSUE_PRIORITY_LABEL, Issue, IssuePriority, PROVIDER_LABEL } from '../../core/models';

@Component({
  imports: [],
  selector: 'app-issue-card',
  styleUrl: './issue-card.scss',
  templateUrl: './issue-card.html',
})
export class IssueCard {
  readonly issue = input.required<Issue>();
  readonly open = output<Issue>();

  readonly priorityLabels = ISSUE_PRIORITY_LABEL;
  readonly providerLabels = PROVIDER_LABEL;
  readonly Priority = IssuePriority;

  priorityGlyph(priority: IssuePriority): string {
    switch (priority) {
      case IssuePriority.Urgent:
        return '🔥';
      case IssuePriority.High:
        return '▲';
      case IssuePriority.Medium:
        return '●';
      case IssuePriority.Low:
        return '▽';
      default:
        return '·';
    }
  }
}
