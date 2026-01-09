using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace ManagedCode.Orleans.SignalR.Core.SignalR.Observers;

/// <summary>
/// Buffers messages for observers in the grace period before hard expiration.
/// This handles timing edge cases where heartbeats are delayed due to GC pauses,
/// network latency, or silo overload.
///
/// Note: This class is designed to be used within Orleans grains which provide single-threaded
/// execution guarantees. No explicit locking is required.
/// </summary>
public sealed class ExpiringObserverBuffer
{
    private readonly Dictionary<string, ObserverBufferState> _buffers = new(StringComparer.Ordinal);
    private readonly TimeSpan _gracePeriod;
    private readonly int _maxBufferedMessages;

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

        if (_buffers.ContainsKey(connectionId))
        {
            return false; // Already in grace period
        }

        _buffers[connectionId] = new ObserverBufferState(_gracePeriod, _maxBufferedMessages);
        return true;
    }

    /// <summary>
    /// Checks if an observer is in the grace period.
    /// </summary>
    public bool IsInGracePeriod(string connectionId)
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

    /// <summary>
    /// Restores an observer from the grace period and returns buffered messages.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <returns>Buffered messages, or empty if not in grace period.</returns>
    public IReadOnlyList<HubMessage> RestoreAndGetMessages(string connectionId)
    {
        if (!_buffers.Remove(connectionId, out var state))
        {
            return Array.Empty<HubMessage>();
        }

        return state.GetMessages();
    }

    /// <summary>
    /// Expires an observer's grace period and discards buffered messages.
    /// </summary>
    /// <returns>Number of messages discarded.</returns>
    public int Expire(string connectionId)
    {
        if (!_buffers.Remove(connectionId, out var state))
        {
            return 0;
        }

        return state.MessageCount;
    }

    /// <summary>
    /// Checks and removes expired grace periods.
    /// </summary>
    /// <returns>List of connection IDs that expired.</returns>
    public List<string> CleanupExpired()
    {
        var expired = new List<string>();

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

        return expired;
    }

    /// <summary>
    /// Gets the remaining grace period time for a connection.
    /// </summary>
    public TimeSpan? GetRemainingGracePeriod(string connectionId)
    {
        if (_buffers.TryGetValue(connectionId, out var state) && !state.IsExpired)
        {
            return state.RemainingTime;
        }

        return null;
    }

    /// <summary>
    /// Gets statistics about the buffer.
    /// </summary>
    public BufferStatistics GetStatistics()
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

    /// <summary>
    /// Clears all buffers.
    /// </summary>
    public void Clear()
    {
        _buffers.Clear();
    }

    /// <summary>
    /// Circular buffer state for a single observer, optimized for O(1) enqueue/dequeue.
    /// </summary>
    private sealed class ObserverBufferState
    {
        private readonly DateTime _expiresAt;
        private readonly HubMessage[] _messages;
        private int _head; // Index of first (oldest) message
        private int _count; // Number of messages in buffer

        public ObserverBufferState(TimeSpan gracePeriod, int maxMessages)
        {
            _expiresAt = DateTime.UtcNow + gracePeriod;
            _messages = new HubMessage[maxMessages];
            _head = 0;
            _count = 0;
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

        public int MessageCount => _count;

        public bool AddMessage(HubMessage message)
        {
            if (_count >= _messages.Length)
            {
                // Buffer is full - overwrite oldest message (drop oldest)
                // The head points to the oldest, so we overwrite it and advance head
                _messages[_head] = message;
                _head = (_head + 1) % _messages.Length;
                // _count stays the same since we're replacing
            }
            else
            {
                // Buffer has space - add at tail position
                var tail = (_head + _count) % _messages.Length;
                _messages[tail] = message;
                _count++;
            }

            return true;
        }

        public IReadOnlyList<HubMessage> GetMessages()
        {
            if (_count == 0)
            {
                return Array.Empty<HubMessage>();
            }

            // Return messages in order (oldest to newest)
            var result = new HubMessage[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _messages[(_head + i) % _messages.Length];
            }

            return result;
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
