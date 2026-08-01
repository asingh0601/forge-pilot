using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ForgePilot.Services.Completions;

/// <summary>
/// Wraps a provider with the two things that decide whether inline completion
/// is affordable: an LRU cache, and suppression of requests the user has
/// already answered by typing.
///
/// Both exist for the same reason. Every request costs money and latency, and
/// a naive implementation issues one per keystroke pause — including for
/// context it has already seen a moment ago, and for text the user is simply
/// typing along with an existing suggestion.
/// </summary>
public sealed class CachingCompletionService
{
    private readonly ICompletionProvider _inner;
    private readonly int _capacity;

    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recency = new();

    /// <summary>Requests actually sent to the provider this session.</summary>
    public int RequestCount { get; private set; }

    /// <summary>Requests answered from cache.</summary>
    public int CacheHitCount { get; private set; }

    public CachingCompletionService(ICompletionProvider inner, int capacity = 256)
    {
        _inner = inner;
        _capacity = Math.Max(16, capacity);
    }

    public bool IsConfigured => _inner.IsConfigured;

    public async Task<string?> CompleteAsync(CompletionContext context, CancellationToken cancellationToken)
    {
        var key = context.CacheKey;

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                Touch(key);
                CacheHitCount++;
                return cached;
            }
        }

        var result = await _inner.CompleteAsync(context, cancellationToken).ConfigureAwait(false);

        // Don't cache cancellations: the user simply kept typing, and storing
        // null here would suppress a real suggestion for that context forever.
        if (cancellationToken.IsCancellationRequested) return null;

        lock (_gate)
        {
            RequestCount++;
            Store(key, result);
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="typed"/> is the user typing along with a
    /// suggestion already on screen. The caller skips the request entirely and
    /// keeps showing the remainder.
    /// </summary>
    public static bool IsTypingAlong(string activeSuggestion, string typed) =>
        !string.IsNullOrEmpty(activeSuggestion) &&
        !string.IsNullOrEmpty(typed) &&
        activeSuggestion.StartsWith(typed, StringComparison.Ordinal);

    private void Store(string key, string? value)
    {
        if (_cache.ContainsKey(key))
        {
            _cache[key] = value;
            Touch(key);
            return;
        }

        if (_cache.Count >= _capacity)
        {
            var oldest = _recency.Last;
            if (oldest is not null)
            {
                _recency.RemoveLast();
                _cache.Remove(oldest.Value);
            }
        }

        _cache[key] = value;
        _recency.AddFirst(key);
    }

    private void Touch(string key)
    {
        var node = _recency.Find(key);
        if (node is null) return;
        _recency.Remove(node);
        _recency.AddFirst(node);
    }
}
