import { TestBed } from '@angular/core/testing';
import { BoardQuery } from '../../core/models';
import { BoardFilters } from './board-filters';

describe('BoardFilters', () => {
  function createFilters(query: BoardQuery = {}): BoardFilters {
    const fixture = TestBed.createComponent(BoardFilters);
    fixture.componentRef.setInput('query', query);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  function capture(filters: BoardFilters): BoardQuery[] {
    const emitted: BoardQuery[] = [];
    filters.queryChanged.subscribe((query) => emitted.push(query));
    return emitted;
  }

  beforeEach(() => {
    TestBed.configureTestingModule({});
  });

  it('emits a complete query rather than a partial change', () => {
    const filters = createFilters({ priority: 'High', groupBy: 'Priority' });
    const emitted = capture(filters);

    filters.update('assigneeId', 'member-1');

    expect(emitted).toEqual([
      { priority: 'High', groupBy: 'Priority', assigneeId: 'member-1', page: 1 },
    ]);
  });

  it('resets the page when a filter changes, since a page index does not survive a new filter', () => {
    const filters = createFilters({ page: 4, priority: 'High' });
    const emitted = capture(filters);

    filters.update('priority', 'Low');

    expect(emitted[0].page).toBe(1);
  });

  it('turns an empty select value into null so the filter is cleared, not set to an empty string', () => {
    const filters = createFilters({ assigneeId: 'member-1' });
    const emitted = capture(filters);

    filters.updateFromSelect('assigneeId', '');

    expect(emitted[0].assigneeId).toBeNull();
  });

  it('preserves presentation settings when filters are cleared', () => {
    const filters = createFilters({
      groupBy: 'Priority',
      orderBy: 'UpdatedAt',
      limit: 50,
      page: 3,
      priority: 'High',
      assigneeId: 'member-1',
      includeArchived: true,
    });
    const emitted = capture(filters);

    filters.clear();

    expect(emitted).toEqual([{ groupBy: 'Priority', orderBy: 'UpdatedAt', limit: 50, page: 1 }]);
  });

  it('reports active filters only when a filter is actually set', () => {
    expect(createFilters({ groupBy: 'Priority' }).hasActiveFilters()).toBe(false);
    expect(createFilters({ assigneeId: 'member-1' }).hasActiveFilters()).toBe(true);
    expect(createFilters({ includeArchived: true }).hasActiveFilters()).toBe(true);
  });

  it('emits the requested view without touching the query', () => {
    const filters = createFilters({ priority: 'High' });
    const views: string[] = [];
    filters.viewChanged.subscribe((view) => views.push(view));
    const emitted = capture(filters);

    filters.setView('list');

    expect(views).toEqual(['list']);
    expect(emitted).toEqual([]);
  });
});
