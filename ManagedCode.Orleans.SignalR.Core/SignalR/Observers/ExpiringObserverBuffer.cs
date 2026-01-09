using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

/// <summary>
/// Buffers messages for observers in the grace period before hard expiration.
/// This handles timing edge cases where heartbeats are delayed due to GC pauses,
/// network latency, or silo overload.
/// </summary>
public sealed class ExpiringObserverBuffer
{
    private readonly Dictionary<string, ObserverBufferState> _buffers = new(StringComparer.Ordinal);
    private readonly TimeSpan _gracePeriod;
    private readonly int _maxBufferedMessages;
    private readonly object _lock = new();

    public ExpiringObserverBuffer(TimeSpan gracePeriod, int maxBufferedMessages)
    {
        _gracePeriod = gracePeriod;
        _maxBufferedMessages = Math.Max(1, maxBufferedMessages);
    }

    /// <summary>
    /// Gets whether the buffer is enabled (grace period > 0).
    /// </summary>
    public bool IsEnabled => _gracePeriod > TimeSpan.Zero;

    /// <summary>
    /// Starts the grace period for an observer, buffering messages until restored or expired.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <returns>True if grace period started, false if already in grace period.</returns>
    public bool StartGracePeriod(string connectionId)
    {
        if (!IsEnabled)
        {
            return false;
        }

        lock (_lock)
        {
            if (_buffers.ContainsKey(connectionId))
            {
                return false; // Already in grace period
            }

            _buffers[connectionId] = new ObserverBufferState(_gracePeriod, _maxBufferedMessages);
            return true;
        }
    }

    /// <summary>
    /// Checks if an observer is in the grace period.
    /// </summary>
    public bool IsInGracePeriod(string connectionId)
    {
        lock (_lock)
        {
            if (!_buffers.TryGetValue(connectionId, out var state))
            {
                return false;
            }

            // Check if grace period has expired
            if (state.IsExpired)
            {
                _buffers.Remove(connectionId);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Buffers a message for an observer in the grace period.
    /// </summary>
    /// <returns>True if buffered, false if not in grace period or buffer full.</returns>
    public bool BufferMessage(string connectionId, HubMessage message)
    {
        if (!IsEnabled)
        {
            return false;
        }

        lock (_lock)
        {
            if (!_buffers.TryGetValue(connectionId, out var state))
            {
                return false;
            }

            if (state.IsExpired)
            {
                _buffers.Remove(connectionId);
                return false;
            }

            return state.AddMessage(message);
        }
    }

    /// <summary>
    /// Restores an observer from the grace period and returns buffered messages.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <returns>Buffered messages, or empty if not in grace period.</returns>
    public IReadOnlyList<HubMessage> RestoreAndGetMessages(string connectionId)
    {
        lock (_lock)
        {
            if (!_buffers.Remove(connectionId, out var state))
            {
                return Array.Empty<HubMessage>();
            }

            return state.GetMessages();
        }
    }

    /// <summary>
    /// Expires an observer's grace period and discards buffered messages.
    /// </summary>
    /// <returns>Number of messages discarded.</returns>
    public int Expire(string connectionId)
    {
        lock (_lock)
        {
            if (!_buffers.Remove(connectionId, out var state))
            {
                return 0;
            }

            return state.MessageCount;
        }
    }

    /// <summary>
    /// Checks and removes expired grace periods.
    /// </summary>
    /// <returns>List of connection IDs that expired.</returns>
    public List<string> CleanupExpired()
    {
        var expired = new List<string>();

        lock (_lock)
        {
            foreach (var (connectionId, state) in _buffers)
            {
                if (state.IsExpired)
                {
                    expired.Add(connectionId);
                }
            }

            foreach (var connectionId in expired)
            {
                _buffers.Remove(connectionId);
            }
        }

        return expired;
    }

    /// <summary>
    /// Gets the remaining grace period time for a connection.
    /// </summary>
    public TimeSpan? GetRemainingGracePeriod(string connectionId)
    {
        lock (_lock)
        {
            if (_buffers.TryGetValue(connectionId, out var state) && !state.IsExpired)
            {
                return state.RemainingTime;
            }

            return null;
        }
    }

    /// <summary>
    /// Gets statistics about the buffer.
    /// </summary>
    public BufferStatistics GetStatistics()
    {
        lock (_lock)
        {
            var stats = new BufferStatistics();

            foreach (var state in _buffers.Values)
            {
                if (state.IsExpired)
                {
                    continue;
                }

                stats.ObserversInGracePeriod++;
                stats.TotalBufferedMessages += state.MessageCount;
            }

            return stats;
        }
    }

    /// <summary>
    /// Clears all buffers.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _buffers.Clear();
        }
    }

    private sealed class ObserverBufferState
    {
        private readonly DateTime _expiresAt;
        private readonly int _maxMessages;
        private readonly List<HubMessage> _messages = new();

        public ObserverBufferState(TimeSpan gracePeriod, int maxMessages)
        {
            _expiresAt = DateTime.UtcNow + gracePeriod;
            _maxMessages = maxMessages;
        }

        public bool IsExpired => DateTime.UtcNow >= _expiresAt;

        public TimeSpan RemainingTime
        {
            get
            {
                var remaining = _expiresAt - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public int MessageCount => _messages.Count;

        public bool AddMessage(HubMessage message)
        {
            if (_messages.Count >= _maxMessages)
            {
                // Drop oldest message to make room
                _messages.RemoveAt(0);
            }

            _messages.Add(message);
            return true;
        }

        public IReadOnlyList<HubMessage> GetMessages()
        {
            return _messages;
        }
    }
}

/// <summary>
/// Statistics about the expiring observer buffer.
/// </summary>
public sealed class BufferStatistics
{
    public int ObserversInGracePeriod { get; set; }
    public int TotalBufferedMessages { get; set; }
}
