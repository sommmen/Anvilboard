import { TestBed } from '@angular/core/testing';
import type { HubConnection } from '@microsoft/signalr';
import { RealtimeChangeEnvelope } from './models';
import { REALTIME_CHANGE_METHOD, RealtimeBoardSyncService } from './realtime-board-sync.service';

/**
 * Stands in for a real SignalR connection so the service's own behaviour — stream separation,
 * reconnect handling, idempotent start — is testable without a WebSocket or a server.
 */
class FakeHubConnection {
  private changeHandler: ((envelope: RealtimeChangeEnvelope) => void) | null = null;
  private reconnectedHandler: (() => void) | null = null;
  private closeHandler: (() => void) | null = null;

  startCalls = 0;
  stopCalls = 0;
  startRejection: Error | null = null;

  on(method: string, handler: (envelope: RealtimeChangeEnvelope) => void): void {
    if (method === REALTIME_CHANGE_METHOD) {
      this.changeHandler = handler;
    }
  }

  onreconnected(handler: () => void): void {
    this.reconnectedHandler = handler;
  }

  onclose(handler: () => void): void {
    this.closeHandler = handler;
  }

  start(): Promise<void> {
    this.startCalls++;
    return this.startRejection ? Promise.reject(this.startRejection) : Promise.resolve();
  }

  stop(): Promise<void> {
    this.stopCalls++;
    return Promise.resolve();
  }

  emitChange(envelope: RealtimeChangeEnvelope): void {
    this.changeHandler?.(envelope);
  }

  emitReconnected(): void {
    this.reconnectedHandler?.();
  }

  emitClose(): void {
    this.closeHandler?.();
  }
}

class TestableRealtimeBoardSyncService extends RealtimeBoardSyncService {
  readonly fake = new FakeHubConnection();

  protected override createConnection(): HubConnection {
    return this.fake as unknown as HubConnection;
  }
}

function envelope(overrides: Partial<RealtimeChangeEnvelope> = {}): RealtimeChangeEnvelope {
  return {
    eventType: 'issue.changed',
    correlationId: 'correlation-1',
    occurredAt: new Date().toISOString(),
    issueId: 'issue-1',
    version: 1,
    changeKind: 'UPDATED',
    ...overrides,
  };
}

describe('RealtimeBoardSyncService', () => {
  let service: TestableRealtimeBoardSyncService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        { provide: RealtimeBoardSyncService, useClass: TestableRealtimeBoardSyncService },
      ],
    });
    service = TestBed.inject(RealtimeBoardSyncService) as TestableRealtimeBoardSyncService;
  });

  it('emits every received envelope on the change stream', async () => {
    const received: RealtimeChangeEnvelope[] = [];
    service.changes.subscribe((change) => received.push(change));
    await service.start();

    service.fake.emitChange(envelope({ issueId: 'issue-1' }));
    service.fake.emitChange(envelope({ issueId: 'issue-2' }));

    expect(received.map((change) => change.issueId)).toEqual(['issue-1', 'issue-2']);
  });

  it('signals a resync on reconnect rather than replaying the missed changes', async () => {
    const changes: RealtimeChangeEnvelope[] = [];
    let resyncs = 0;
    service.changes.subscribe((change) => changes.push(change));
    service.resyncRequired.subscribe(() => resyncs++);
    await service.start();

    service.fake.emitReconnected();

    expect(resyncs).toBe(1);
    expect(changes).toEqual([]);
  });

  it('shares one connection across repeated start calls', async () => {
    await service.start();
    await service.start();

    expect(service.fake.startCalls).toBe(1);
  });

  it('leaves the board usable when the connection cannot be established', async () => {
    service.fake.startRejection = new Error('hub unreachable');

    await expect(service.start()).resolves.toBeUndefined();
  });

  it('retries and requests a resync after a closed connection', async () => {
    vi.useFakeTimers();
    let resyncs = 0;
    service.resyncRequired.subscribe(() => resyncs++);
    await service.start();

    service.fake.emitClose();
    await vi.advanceTimersByTimeAsync(1_000);

    expect(service.fake.startCalls).toBe(2);
    expect(resyncs).toBe(1);
    vi.useRealTimers();
  });

  it('retries after a failed initial connect and resyncs once recovered', async () => {
    vi.useFakeTimers();
    let resyncs = 0;
    service.resyncRequired.subscribe(() => resyncs++);
    service.fake.startRejection = new Error('hub unreachable');

    await service.start();
    expect(service.fake.startCalls).toBe(1);
    expect(resyncs).toBe(0);

    service.fake.startRejection = null;
    await vi.advanceTimersByTimeAsync(1_000);

    expect(service.fake.startCalls).toBe(2);
    expect(resyncs).toBe(1);
    vi.useRealTimers();
  });

  it('backs off exponentially across consecutive reconnect failures, capped at 30s', async () => {
    vi.useFakeTimers();
    service.fake.startRejection = new Error('hub unreachable');
    await service.start();
    expect(service.fake.startCalls).toBe(1);

    // 1st retry after 1s
    await vi.advanceTimersByTimeAsync(1_000);
    expect(service.fake.startCalls).toBe(2);

    // 2nd retry after 2s
    await vi.advanceTimersByTimeAsync(1_999);
    expect(service.fake.startCalls).toBe(2);
    await vi.advanceTimersByTimeAsync(1);
    expect(service.fake.startCalls).toBe(3);

    // 3rd retry after 4s
    await vi.advanceTimersByTimeAsync(3_999);
    expect(service.fake.startCalls).toBe(3);
    await vi.advanceTimersByTimeAsync(1);
    expect(service.fake.startCalls).toBe(4);

    // 4th retry after 8s
    await vi.advanceTimersByTimeAsync(8_000);
    expect(service.fake.startCalls).toBe(5);

    // 5th retry after 16s
    await vi.advanceTimersByTimeAsync(16_000);
    expect(service.fake.startCalls).toBe(6);

    // 6th retry would be 32s uncapped, but must be capped at 30s
    await vi.advanceTimersByTimeAsync(29_999);
    expect(service.fake.startCalls).toBe(6);
    await vi.advanceTimersByTimeAsync(1);
    expect(service.fake.startCalls).toBe(7);

    // Further retries stay capped at 30s rather than continuing to grow.
    await vi.advanceTimersByTimeAsync(29_999);
    expect(service.fake.startCalls).toBe(7);
    await vi.advanceTimersByTimeAsync(1);
    expect(service.fake.startCalls).toBe(8);

    vi.useRealTimers();
  });

  it('resets the backoff delay to 1s after a successful reconnect following failures', async () => {
    vi.useFakeTimers();
    service.fake.startRejection = new Error('hub unreachable');
    await service.start(); // startCalls=1, fails; next retry in 1s

    await vi.advanceTimersByTimeAsync(1_000); // 1st retry, still fails; next retry in 2s
    expect(service.fake.startCalls).toBe(2);

    service.fake.startRejection = null; // the next attempt will succeed
    await vi.advanceTimersByTimeAsync(2_000); // 2nd retry succeeds and resets the backoff
    expect(service.fake.startCalls).toBe(3);

    service.fake.emitClose(); // connection drops again after the successful recovery
    await vi.advanceTimersByTimeAsync(999);
    expect(service.fake.startCalls).toBe(3);
    await vi.advanceTimersByTimeAsync(1); // proves the delay reset back to 1s, not 4s
    expect(service.fake.startCalls).toBe(4);

    vi.useRealTimers();
  });

  it('cancels a pending retry and does not reconnect after an explicit stop', async () => {
    vi.useFakeTimers();
    await service.start();

    service.fake.emitClose();
    await service.stop();
    await vi.advanceTimersByTimeAsync(5_000);

    expect(service.fake.startCalls).toBe(1);
    vi.useRealTimers();
  });

  it('stops the underlying connection', async () => {
    await service.start();
    await service.stop();

    expect(service.fake.stopCalls).toBe(1);
  });
});
