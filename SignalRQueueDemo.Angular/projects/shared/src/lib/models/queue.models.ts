/**
 * TypeScript mirrors of SignalRQueueDemo.Contracts/QueueDomainTypes.cs — update together. Property names are
 * camelCase (not the C# PascalCase) because ASP.NET Core minimal APIs serialize with System.Text.Json's "Web"
 * defaults, which lowercase the first letter of every property; nothing in this API opts out of that default.
 */

/**
 * Mirrors SignalRQueueDemo.Contracts.QueueStatus. System.Text.Json's default (Web) options serialize C# enums
 * as their underlying number, not their name — no JsonStringEnumConverter is registered in Program.cs — so the
 * numeric values below must stay in the exact declaration order of the C# enum, not just the same names.
 */
export enum QueueStatus {
  Waiting = 0,
  Serving = 1,
  Completed = 2,
}

/** Mirrors SignalRQueueDemo.Contracts.QueueEntry. */
export interface QueueEntry {
  readonly id: string;
  readonly displayName: string;
  readonly ticketNumber: string;
  /** ISO 8601 string as sent over JSON (DateTimeOffset) — left as a string here, parsed with `new Date(...)` only where a component actually needs to render it, so this stays a plain wire-shape mirror. */
  readonly checkedInAt: string;
  readonly status: QueueStatus;
  readonly servedBy: string | null;
  readonly servedAt: string | null;
}

/** Mirrors SignalRQueueDemo.Contracts.CheckInTokenResponse — the response to GET /checkin/token. */
export interface CheckInTokenResponse {
  readonly token: string;
}

/** Mirrors SignalRQueueDemo.Contracts.CheckInRequest — the payload for POST /checkin. */
export interface CheckInRequest {
  readonly displayName: string;
  readonly ticketNumber: string;
}

/** Mirrors SignalRQueueDemo.Contracts.CheckInResponse. */
export interface CheckInResponse {
  readonly entryId: string;
  readonly position: number;
  readonly sequenceNumber: number;
  readonly entry: QueueEntry;
}

/**
 * Mirrors SignalRQueueDemo.Contracts.QueueUpdated — the payload pushed by QueueHub's QueueUpdated method on
 * every state change. See QueueHubService for how sequenceNumber drives the reconnect/catch-up protocol.
 */
export interface QueueUpdated {
  readonly sequenceNumber: number;
  readonly changedEntry: QueueEntry;
  readonly summary: QueueSnapshot;
}

/** Mirrors SignalRQueueDemo.Contracts.QueueSnapshot. */
export interface QueueSnapshot {
  readonly totalWaiting: number;
  readonly totalServing: number;
  readonly totalCompleted: number;
  readonly queue: readonly QueueEntry[];
}

/** Mirrors SignalRQueueDemo.Contracts.QueueStateResponse — the response to GET /queue. */
export interface QueueStateResponse {
  readonly sequenceNumber: number;
  readonly snapshot: QueueSnapshot;
}

/** Mirrors SignalRQueueDemo.Contracts.QueueChangeEvent — one row of the GET /queue/since/{seq} replay log. */
export interface QueueChangeEvent {
  readonly sequenceNumber: number;
  readonly entry: QueueEntry;
}

/**
 * Mirrors SignalRQueueDemo.Contracts.QueueChangesSinceResponse — the response to GET /queue/since/{seq}.
 * `changes` and `snapshot` are mutually exclusive; which one is populated is told by `isSnapshot`, matching the
 * C# record where the server falls back to a full snapshot instead of erroring on an unrecognized sequence
 * number. See QueueHubService.catchUp for how a client picks between the two.
 */
export interface QueueChangesSinceResponse {
  readonly sequenceNumber: number;
  readonly isSnapshot: boolean;
  readonly changes: readonly QueueChangeEvent[] | null;
  readonly snapshot: QueueSnapshot | null;
}
