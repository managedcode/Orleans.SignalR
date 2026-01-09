using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ManagedCode.Orleans.SignalR.Core.Helpers;

/// <summary>
/// Provides pooling for common collection types to reduce allocations in hot paths.
/// Uses thread-safe concurrent bags for lock-free pooling.
/// </summary>
public static class CollectionPool
{
    private const int MaxPoolSize = 256;

    private static readonly ConcurrentBag<HashSet<string>> StringHashSetPool = new();
    private static readonly ConcurrentBag<List<string>> StringListPool = new();
    private static readonly ConcurrentBag<Dictionary<int, List<string>>> IntListDictionaryPool = new();

    /// <summary>
    /// Gets a HashSet&lt;string&gt; from the pool or creates a new one.
    /// </summary>
    public static HashSet<string> GetStringHashSet()
    {
        if (StringHashSetPool.TryTake(out var set))
        {
            return set;
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns a HashSet&lt;string&gt; to the pool after clearing it.
    /// </summary>
    public static void Return(HashSet<string> set)
    {
        if (set is null || StringHashSetPool.Count >= MaxPoolSize)
        {
            return;
        }

        set.Clear();
        StringHashSetPool.Add(set);
    }

    /// <summary>
    /// Gets a List&lt;string&gt; from the pool or creates a new one.
    /// </summary>
    public static List<string> GetStringList()
    {
        if (StringListPool.TryTake(out var list))
        {
            return list;
        }

        return new List<string>();
    }

    /// <summary>
    /// Gets a List&lt;string&gt; from the pool with specified capacity.
    /// </summary>
    public static List<string> GetStringList(int capacity)
    {
        if (StringListPool.TryTake(out var list))
        {
            if (list.Capacity < capacity)
            {
                list.Capacity = capacity;
            }
            return list;
        }

        return new List<string>(capacity);
    }

    /// <summary>
    /// Returns a List&lt;string&gt; to the pool after clearing it.
    /// </summary>
    public static void Return(List<string> list)
    {
        if (list is null || StringListPool.Count >= MaxPoolSize)
        {
            return;
        }

        list.Clear();
        StringListPool.Add(list);
    }

    /// <summary>
    /// Gets a Dictionary&lt;int, List&lt;string&gt;&gt; from the pool.
    /// </summary>
    public static Dictionary<int, List<string>> GetIntListDictionary()
    {
        if (IntListDictionaryPool.TryTake(out var dict))
        {
            return dict;
        }

        return new Dictionary<int, List<string>>();
    }

    /// <summary>
    /// Returns a Dictionary&lt;int, List&lt;string&gt;&gt; to the pool.
    /// The inner lists are also returned to their respective pools.
    /// </summary>
    public static void Return(Dictionary<int, List<string>> dict)
    {
        if (dict is null || IntListDictionaryPool.Count >= MaxPoolSize)
        {
            return;
        }

        // Return inner lists to their pool
        foreach (var list in dict.Values)
        {
            Return(list);
        }

        dict.Clear();
        IntListDictionaryPool.Add(dict);
    }

    /// <summary>
    /// A scope that automatically returns a HashSet to the pool when disposed.
    /// </summary>
    public readonly struct HashSetScope : IDisposable
    {
        public HashSet<string> Set { get; }

        public HashSetScope(HashSet<string> set)
        {
            Set = set;
        }

        public void Dispose()
        {
            Return(Set);
        }
    }

    /// <summary>
    /// A scope that automatically returns a List to the pool when disposed.
    /// </summary>
    public readonly struct ListScope : IDisposable
    {
        public List<string> List { get; }

        public ListScope(List<string> list)
        {
            List = list;
        }

        public void Dispose()
        {
            Return(List);
        }
    }

    /// <summary>
    /// Creates a scoped HashSet that is automatically returned to the pool.
    /// </summary>
    public static HashSetScope GetScopedStringHashSet()
    {
        return new HashSetScope(GetStringHashSet());
    }

    /// <summary>
    /// Creates a scoped List that is automatically returned to the pool.
    /// </summary>
    public static ListScope GetScopedStringList()
    {
        return new ListScope(GetStringList());
    }

    /// <summary>
    /// Creates a scoped List with capacity that is automatically returned to the pool.
    /// </summary>
    public static ListScope GetScopedStringList(int capacity)
    {
        return new ListScope(GetStringList(capacity));
    }
}
