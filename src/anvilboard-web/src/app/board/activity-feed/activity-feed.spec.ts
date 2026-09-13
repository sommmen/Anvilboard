import { TestBed } from '@angular/core/testing';
import { ActivityFeed } from './activity-feed';

describe('ActivityFeed', () => {
  function createFeed(): ActivityFeed {
    return TestBed.runInInjectionContext(() => new ActivityFeed());
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('maps known event types to distinct glyphs', () => {
    const feed = createFeed();

    expect(feed.glyph('CommentAdded')).toBe('💬');
    expect(feed.glyph('StatusChanged')).toBe('⇄');
    expect(feed.glyph('StatusChanged')).not.toBe(feed.glyph('CommentAdded'));
  });

  it('shares one glyph across the link event family', () => {
    const feed = createFeed();

    expect(feed.glyph('IssueLinkUpdated')).toBe('🔗');
    expect(feed.glyph('IssueLinkCreated')).toBe(feed.glyph('IssueLinkRemoved'));
  });

  it('falls back to a neutral glyph for an event type it has never seen', () => {
    const feed = createFeed();

    expect(feed.glyph('SomethingTheServerAddedLater')).toBe('•');
  });

  it('renders coarse relative stamps', () => {
    const feed = createFeed();
    const now = new Date('2026-01-02T12:00:00Z');

    expect(feed.relativeTime('2026-01-02T11:59:30Z', now)).toBe('just now');
    expect(feed.relativeTime('2026-01-02T11:30:00Z', now)).toBe('30m ago');
    expect(feed.relativeTime('2026-01-02T09:00:00Z', now)).toBe('3h ago');
    expect(feed.relativeTime('2025-12-31T12:00:00Z', now)).toBe('2d ago');
  });

  it('returns an unparseable timestamp verbatim rather than showing NaN', () => {
    const feed = createFeed();

    expect(feed.relativeTime('not-a-date')).toBe('not-a-date');
  });
});
