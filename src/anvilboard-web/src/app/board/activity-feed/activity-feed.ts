import { Component, input, output } from '@angular/core';
import { ActivityEntry } from '../../core/models';

/**
 * Renders an issue's history. Presentational only — the owning detail view does the fetching and
 * the realtime prepend, so the feed can be dropped into any surface that already has entries.
 *
 * Every row displays the server-rendered `text` verbatim rather than re-deriving a sentence from
 * `type` and `data`. A client that has never heard of a newly added event type still renders it
 * correctly, which is what lets the server add history kinds without a coordinated SPA release.
 */
@Component({
  imports: [],
  selector: 'app-activity-feed',
  styleUrl: './activity-feed.scss',
  templateUrl: './activity-feed.html',
})
export class ActivityFeed {
  readonly entries = input.required<ActivityEntry[]>();
  readonly loading = input(false);
  readonly hasMore = input(false);

  readonly loadMore = output<void>();

  glyph(type: string): string {
    switch (type) {
      case 'Created':
        return '✳';
      case 'StatusChanged':
        return '⇄';
      case 'AssigneeChanged':
        return '👤';
      case 'PriorityChanged':
        return '⚑';
      case 'CommentAdded':
        return '💬';
      case 'LabelsChanged':
        return '🏷';
      case 'SyncedFromExternal':
        return '⟳';
      case 'IssueLinkCreated':
      case 'IssueLinkUpdated':
      case 'IssueLinkRemoved':
        return '🔗';
      case 'ArtifactAttached':
      case 'ArtifactRefreshed':
      case 'ArtifactRemoved':
        return '📎';
      default:
        return '•';
    }
  }

  /**
   * A coarse relative stamp. Exact timestamps stay in the `title` attribute so the common case
   * reads quickly while the precise value remains available on hover.
   */
  relativeTime(occurredAt: string, now: Date = new Date()): string {
    const then = new Date(occurredAt).getTime();
    if (Number.isNaN(then)) {
      return occurredAt;
    }

    const seconds = Math.round((now.getTime() - then) / 1000);
    if (seconds < 60) return 'just now';
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
    if (seconds < 2592000) return `${Math.floor(seconds / 86400)}d ago`;
    return new Date(occurredAt).toLocaleDateString();
  }
}
