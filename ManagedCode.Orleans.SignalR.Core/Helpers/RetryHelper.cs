using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace ManagedCode.Orleans.SignalR.Core.Helpers;

/// <summary>
/// Provides retry functionality with exponential backoff for transient failures.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// Default configuration for retry operations.
    /// </summary>
    public static readonly RetryPolicy DefaultPolicy = new(
        maxAttempts: 3,
        initialDelay: TimeSpan.FromMilliseconds(100),
        maxDelay: TimeSpan.FromSeconds(5),
        exponentialBase: 2.0);

    /// <summary>
    /// Executes an action with retry logic using exponential backoff.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> action,
        RetryPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= DefaultPolicy;

        var attempt = 0;
        var delay = policy.InitialDelay;

        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < policy.MaxAttempts - 1)
            {
                attempt++;
                await Task.Delay(delay, cancellationToken);
                delay = CalculateNextDelay(delay, policy);
            }
        }
    }

    /// <summary>
    /// Executes a function with retry logic using exponential backoff.
    /// </summary>
    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> func,
        RetryPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= DefaultPolicy;

        var attempt = 0;
        var delay = policy.InitialDelay;

        while (true)
        {
            try
            {
                return await func();
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < policy.MaxAttempts - 1)
            {
                attempt++;
                await Task.Delay(delay, cancellationToken);
                delay = CalculateNextDelay(delay, policy);
            }
        }
    }

    /// <summary>
    /// Executes a grain call with retry logic, handling Orleans-specific transient failures.
    /// </summary>
    public static async Task ExecuteGrainCallAsync(
        Func<Task> grainCall,
        RetryPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= DefaultPolicy;

        var attempt = 0;
        var delay = policy.InitialDelay;

        while (true)
        {
            try
            {
                await grainCall();
                return;
            }
            catch (Exception ex) when (IsOrleansTransient(ex) && attempt < policy.MaxAttempts - 1)
            {
                attempt++;
                await Task.Delay(delay, cancellationToken);
                delay = CalculateNextDelay(delay, policy);
            }
        }
    }

    /// <summary>
    /// Executes a grain call with retry logic and returns a result.
    /// </summary>
    public static async Task<T> ExecuteGrainCallAsync<T>(
        Func<Task<T>> grainCall,
        RetryPolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        policy ??= DefaultPolicy;

        var attempt = 0;
        var delay = policy.InitialDelay;

        while (true)
        {
            try
            {
                return await grainCall();
            }
            catch (Exception ex) when (IsOrleansTransient(ex) && attempt < policy.MaxAttempts - 1)
            {
                attempt++;
                await Task.Delay(delay, cancellationToken);
                delay = CalculateNextDelay(delay, policy);
            }
        }
    }

    private static TimeSpan CalculateNextDelay(TimeSpan currentDelay, RetryPolicy policy)
    {
        // Calculate next delay with exponential backoff
        var nextDelay = TimeSpan.FromTicks((long)(currentDelay.Ticks * policy.ExponentialBase));

        // Add jitter (±10%) to prevent thundering herd
        var jitterRange = nextDelay.Ticks / 10;
        var jitter = Random.Shared.NextInt64(-jitterRange, jitterRange);
        nextDelay = TimeSpan.FromTicks(nextDelay.Ticks + jitter);

        // Ensure we don't exceed max delay
        return nextDelay > policy.MaxDelay ? policy.MaxDelay : nextDelay;
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is TimeoutException
            or TaskCanceledException
            or OperationCanceledException
            or OrleansException;
    }

    private static bool IsOrleansTransient(Exception ex)
    {
        // Handle Orleans-specific transient exceptions
        return ex is TimeoutException
            or TaskCanceledException
            or OrleansMessageRejectionException
            or SiloUnavailableException
            or GatewayTooBusyException;
    }
}

/// <summary>
/// Configuration for retry operations.
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>
    /// Maximum number of retry attempts.
    /// </summary>
    public int MaxAttempts { get; }

    /// <summary>
    /// Initial delay between retries.
    /// </summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>
    /// Maximum delay between retries.
    /// </summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// Base for exponential backoff calculation.
    /// </summary>
    public double ExponentialBase { get; }

    public RetryPolicy(int maxAttempts, TimeSpan initialDelay, TimeSpan maxDelay, double exponentialBase = 2.0)
    {
        MaxAttempts = Math.Max(1, maxAttempts);
        InitialDelay = initialDelay > TimeSpan.Zero ? initialDelay : TimeSpan.FromMilliseconds(100);
        MaxDelay = maxDelay > InitialDelay ? maxDelay : TimeSpan.FromSeconds(30);
        ExponentialBase = Math.Max(1.1, exponentialBase);
    }

    /// <summary>
    /// Creates a policy optimized for fast operations.
    /// </summary>
    public static RetryPolicy Fast => new(
        maxAttempts: 3,
        initialDelay: TimeSpan.FromMilliseconds(50),
        maxDelay: TimeSpan.FromMilliseconds(500));

    /// <summary>
    /// Creates a policy for slow operations with longer delays.
    /// </summary>
    public static RetryPolicy Slow => new(
        maxAttempts: 5,
        initialDelay: TimeSpan.FromMilliseconds(500),
        maxDelay: TimeSpan.FromSeconds(30));

    /// <summary>
    /// Creates a policy for aggressive retrying of critical operations.
    /// </summary>
    public static RetryPolicy Aggressive => new(
        maxAttempts: 10,
        initialDelay: TimeSpan.FromMilliseconds(100),
        maxDelay: TimeSpan.FromSeconds(60));
}
