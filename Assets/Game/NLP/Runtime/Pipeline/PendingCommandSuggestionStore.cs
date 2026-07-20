using System;
using System.Collections.Generic;

namespace Game.NLP
{
    /// <summary>
    /// Thread-safe, single-batch suggestion storage. Replacing the active batch
    /// makes every older UI decision stale and prevents cross-command approval.
    /// </summary>
    public sealed class PendingCommandSuggestionStore
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly TimeSpan lifetime;
        private string activeBatchId = string.Empty;

        public PendingCommandSuggestionStore(TimeSpan lifetime)
        {
            this.lifetime = lifetime > TimeSpan.Zero ? lifetime : TimeSpan.FromMinutes(2);
        }

        public int Count
        {
            get
            {
                lock (sync)
                {
                    RemoveExpiredLocked(DateTime.UtcNow);
                    return entries.Count;
                }
            }
        }

        public void ReplaceBatch(string batchId, CommandSuggestion[] suggestions)
        {
            lock (sync)
            {
                entries.Clear();
                activeBatchId = batchId ?? string.Empty;
                if (suggestions == null)
                {
                    return;
                }

                DateTime expiresAt = DateTime.UtcNow.Add(lifetime);
                foreach (CommandSuggestion suggestion in suggestions)
                {
                    if (suggestion == null
                        || string.IsNullOrWhiteSpace(suggestion.suggestion_id)
                        || string.IsNullOrWhiteSpace(suggestion.suggested_command))
                    {
                        continue;
                    }

                    entries[suggestion.suggestion_id] = new Entry(activeBatchId, suggestion, expiresAt);
                }
            }
        }

        public bool TryConsume(string suggestionId, out CommandSuggestion suggestion)
        {
            lock (sync)
            {
                RemoveExpiredLocked(DateTime.UtcNow);
                if (string.IsNullOrWhiteSpace(suggestionId)
                    || !entries.TryGetValue(suggestionId, out Entry entry)
                    || !string.Equals(entry.batchId, activeBatchId, StringComparison.Ordinal))
                {
                    suggestion = null;
                    return false;
                }

                suggestion = entry.suggestion;
                entries.Clear();
                activeBatchId = string.Empty;
                return true;
            }
        }

        public bool Reject(string suggestionId)
        {
            lock (sync)
            {
                RemoveExpiredLocked(DateTime.UtcNow);
                bool valid = !string.IsNullOrWhiteSpace(suggestionId)
                    && entries.TryGetValue(suggestionId, out Entry entry)
                    && string.Equals(entry.batchId, activeBatchId, StringComparison.Ordinal);
                if (valid)
                {
                    entries.Clear();
                    activeBatchId = string.Empty;
                }
                return valid;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                entries.Clear();
                activeBatchId = string.Empty;
            }
        }

        private void RemoveExpiredLocked(DateTime now)
        {
            if (entries.Count == 0)
            {
                return;
            }

            bool expired = false;
            foreach (Entry entry in entries.Values)
            {
                if (entry.expiresAt <= now)
                {
                    expired = true;
                    break;
                }
            }

            if (expired)
            {
                entries.Clear();
                activeBatchId = string.Empty;
            }
        }

        private sealed class Entry
        {
            public readonly string batchId;
            public readonly CommandSuggestion suggestion;
            public readonly DateTime expiresAt;

            public Entry(string batchId, CommandSuggestion suggestion, DateTime expiresAt)
            {
                this.batchId = batchId;
                this.suggestion = suggestion;
                this.expiresAt = expiresAt;
            }
        }
    }
}
