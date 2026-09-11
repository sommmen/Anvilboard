import { DestroyRef, Injectable, inject } from '@angular/core';
import { Observable, Subject } from 'rxjs';
import type { HubConnection } from '@microsoft/signalr';
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import { RealtimeChangeEnvelope } from './models';

/** Path the API maps the workspace hub at (see Anvilboard.Api.Realtime.WorkspaceRealtimeHub). */
export const WORKSPACE_HUB_PATH = '/hubs/workspace';

/** SignalR method name the server invokes to deliver a change envelope. */
export const REALTIME_CHANGE_METHOD = 'change';

/**
 * Owns the single SignalR connection the SPA holds open and turns it into two streams: the change
 * envelopes themselves, and a separate "you missed changes, re-fetch" signal.
 *
 * The two are kept apart deliberately. The server never replays what a disconnected client missed,
 * so a reconnect is not a change — it is a gap, and the only correct recovery is one full re-fetch.
 * Emitting it on the same stream would force every consumer to distinguish the two cases itself.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeBoardSyncService {
  private readonly destroyRef = inject(DestroyRef);

  private readonly changesSubject = new Subject<RealtimeChangeEnvelope>();
  private readonly resyncSubject = new Subject<void>();
  private connection: HubConnection | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private stopped = false;

  /** Change envelopes as they arrive, in delivery order. */
  readonly changes: Observable<RealtimeChangeEnvelope> = this.changesSubject.asObservable();

  /**
   * Fires when the client has provably missed changes and must re-fetch: after a reconnect, and
   * after the connection is re-established following a drop.
   */
  readonly resyncRequired: Observable<void> = this.resyncSubject.asObservable();

  constructor() {
    this.destroyRef.onDestroy(() => void this.stop());
  }

  /**
   * Opens the connection if it is not already open. Safe to call from several components: the
   * connection is shared, since the hub scopes delivery to the workspace rather than to a view.
   */
  async start(): Promise<void> {
    this.stopped = false;
    if (this.connection) {
      return;
    }
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    await this.connectAndListen(false);
  }

  async stop(): Promise<void> {
    this.stopped = true;
    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }

    const connection = this.connection;
    this.connection = null;
    if (connection) {
      await connection.stop();
    }
  }

  /**
   * Creates the connection, wires up its handlers and attempts to start it. On success following
   * an automatic-recovery attempt, emits a resync signal — but only then, since the caller (an
   * initial `start()`) already knows a fresh connection has no changes to catch up on.
   */
  private async connectAndListen(isRecoveryAttempt: boolean): Promise<void> {
    const connection = this.createConnection();
    this.connection = connection;

    connection.on(REALTIME_CHANGE_METHOD, (envelope: RealtimeChangeEnvelope) =>
      this.changesSubject.next(envelope),
    );

    // Automatic reconnect hands back a connection with no history of what happened while it was
    // down, so a re-fetch is the only way back to a correct board.
    connection.onreconnected(() => this.resyncSubject.next());
    connection.onclose(() => this.scheduleReconnect());

    try {
      await connection.start();
      if (isRecoveryAttempt) {
        this.resyncSubject.next();
      }
    } catch {
      // A failed initial connect must not break the board: polling-free live updates are an
      // enhancement, and the REST surface still works without them.
      if (this.connection === connection) {
        this.connection = null;
        this.scheduleReconnect();
      }
    }
  }

  private scheduleReconnect(): void {
    if (this.stopped || this.reconnectTimer) {
      return;
    }

    this.connection = null;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      void this.connectAndListen(true);
    }, 1_000);
  }

  /** Overridable in tests, which cannot open a real WebSocket. */
  protected createConnection(): HubConnection {
    return new HubConnectionBuilder()
      .withUrl(WORKSPACE_HUB_PATH)
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();
  }
}
