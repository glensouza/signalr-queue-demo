/*
 * Public API Surface of shared — the plumbing every app in this workspace (public-checkin, internal-queue,
 * queue-display) consumes instead of re-implementing. See CLAUDE.md / issue #8 for why this exists as a
 * library rather than copy-pasted per app.
 */

export * from './lib/config/runtime-config';
export * from './lib/config/runtime-config.service';
export * from './lib/models/document.models';
export * from './lib/models/queue.models';
export * from './lib/services/queue-api.service';
export * from './lib/services/queue-hub.service';
