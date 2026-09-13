import { Component, input, output } from '@angular/core';
import { BoardIssue } from '../../core/models';

/**
 * A card in the board projection. It takes `BoardIssue` rather than the full `Issue` because
 * `GET /api/board` already sends exactly what a card needs — widening it to the full record would
 * put description bodies on the wire for every card on screen.
 *
 * `priority` and `provider` arrive as server-rendered strings, so the card shows them as given
 * instead of mapping through a client-side numeric enum that could drift from the server's.
 */
@Component({
  imports: [],
  selector: 'app-issue-card',
  styleUrl: './issue-card.scss',
  templateUrl: './issue-card.html',
})
export class IssueCard {
  readonly issue = input.required<BoardIssue>();
  readonly open = output<BoardIssue>();

  priorityGlyph(priority: string): string {
    switch (priority) {
      case 'Urgent':
        return '🔥';
      case 'High':
        return '▲';
      case 'Medium':
        return '●';
      case 'Low':
        return '▽';
      default:
        return '·';
    }
  }
}
