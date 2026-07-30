export class PendingReleaseStore {
  constructor({ ttlMs = 15 * 60 * 1000, maxEntries = 100, now = () => Date.now() } = {}) {
    this.ttlMs = ttlMs;
    this.maxEntries = maxEntries;
    this.now = now;
    this.entries = new Map();
  }

  put(id, draft) {
    this.prune();

    if (this.entries.size >= this.maxEntries) {
      const oldestKey = this.entries.keys().next().value;
      this.entries.delete(oldestKey);
    }

    this.entries.set(id, {
      draft,
      expiresAt: this.now() + this.ttlMs,
    });
  }

  take(id) {
    const entry = this.entries.get(id);
    this.entries.delete(id);

    if (!entry || entry.expiresAt <= this.now()) {
      return undefined;
    }

    return entry.draft;
  }

  prune() {
    const now = this.now();
    for (const [id, entry] of this.entries) {
      if (entry.expiresAt <= now) {
        this.entries.delete(id);
      }
    }
  }
}
