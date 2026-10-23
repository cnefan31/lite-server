using System.Collections.Concurrent;

namespace LiteServer.Security;

/// <summary>
/// JWT 黑名单：让未过期的 token 提前失效（登出、改密、封禁）。
/// <para>
/// 语义：<see cref="Add"/> 的第二参是
/// <b>绝对 Unix 秒时间戳</b>，不是 TTL。传 TTL 会被当成 1970 年，条目立即失效。
/// </para>
/// </summary>
public sealed class TokenBlacklist : IDisposable
{
    private readonly ConcurrentDictionary<string, long> _entries = new(StringComparer.Ordinal);
    private readonly object _cleanupLock = new();
    private CancellationTokenSource? _cleanupCts;
    private Timer? _timer;

    /// <summary>当前条目数（含尚未清理但已过期的）。</summary>
    public int Count => _entries.Count;

    /// <summary>加入黑名单。</summary>
    /// <param name="tokenId">token 原文或其唯一标识（jti）。</param>
    /// <param name="expireAtUnixSeconds">该条目失效的绝对时间（Unix 秒）。通常直接取 token 的 exp。</param>
    public void Add(string tokenId, long expireAtUnixSeconds)
    {
        if (string.IsNullOrEmpty(tokenId)) return;
        _entries[tokenId] = expireAtUnixSeconds;
    }

    /// <summary>是否在黑名单中。已过期的条目会被顺带移除并返回 false。</summary>
    public bool Contains(string? tokenId)
    {
        if (string.IsNullOrEmpty(tokenId)) return false;

        if (!_entries.TryGetValue(tokenId, out long expireAt)) return false;
        if (expireAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            _entries.TryRemove(tokenId, out _);
            return false;
        }
        return true;
    }

    /// <summary>移出黑名单（误封时用）。</summary>
    public bool Remove(string tokenId) => _entries.TryRemove(tokenId, out _);

    /// <summary>清理所有已过期条目，返回清理数量。</summary>
    public int CleanExpired()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int removed = 0;
        foreach (KeyValuePair<string, long> kv in _entries)
        {
            if (kv.Value <= now && _entries.TryRemove(kv.Key, out _)) removed++;
        }
        return removed;
    }

    /// <summary>
    /// 启动后台定时清理。间隔小于等于 0 时不启动（依赖读取时的惰性清理）。
    /// 重复调用幂等。
    /// </summary>
    public void StartAutoCleanup(TimeSpan interval, long? maxEntries = null)
    {
        if (interval <= TimeSpan.Zero) return;

        lock (_cleanupLock)
        {
            if (_timer is not null) return;

            _cleanupCts = new CancellationTokenSource();
            _timer = new Timer(_ =>
            {
                try { CleanExpired(); }
                catch (Exception) { /* 后台清理失败不影响主流程 */ }
            }, null, interval, interval);

            // 防止无人登出时黑名单无限增长：超过上限即强制清理一轮。
            if (maxEntries is > 0)
            {
                _ = Task.Run(async () =>
                {
                    CancellationToken ct = _cleanupCts?.Token ?? CancellationToken.None;
                    while (!ct.IsCancellationRequested)
                    {
                        if (_entries.Count > maxEntries.Value) CleanExpired();
                        try { await Task.Delay(interval, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }, CancellationToken.None);
            }
        }
    }

    /// <summary>停止后台清理。</summary>
    public void StopAutoCleanup()
    {
        lock (_cleanupLock)
        {
            _timer?.Dispose();
            _timer = null;

            if (_cleanupCts is not null)
            {
                try { _cleanupCts.Cancel(); } catch (ObjectDisposedException) { }
                _cleanupCts.Dispose();
                _cleanupCts = null;
            }
        }
    }

    public void Dispose() => StopAutoCleanup();
}

/// <summary>内存中的简单令牌桶限流器。用于按 IP / 路由做粗粒度限流。</summary>
public sealed class RateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private readonly int _capacity;
    private readonly TimeSpan _window;

    public RateLimiter(int capacity = 100, TimeSpan? window = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _window = window ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>尝试获取一个配额。返回 false 表示已超限。</summary>
    public bool TryAcquire(string key)
    {
        if (string.IsNullOrEmpty(key)) key = "default";

        Bucket bucket = _buckets.GetOrAdd(key, _ => new Bucket(_capacity, _window));
        return bucket.TryTake();
    }

    private sealed class Bucket
    {
        private readonly int _capacity;
        private readonly TimeSpan _window;
        private readonly object _lock = new();
        private int _tokens;
        private long _resetAtTicks;

        public Bucket(int capacity, TimeSpan window)
        {
            _capacity = capacity;
            _window = window;
            _tokens = capacity;
            _resetAtTicks = DateTime.UtcNow.Add(window).Ticks;
        }

        public bool TryTake()
        {
            lock (_lock)
            {
                long now = DateTime.UtcNow.Ticks;
                if (now >= _resetAtTicks)
                {
                    _tokens = _capacity;
                    _resetAtTicks = now + _window.Ticks;
                }
                if (_tokens <= 0) return false;
                _tokens--;
                return true;
            }
        }
    }
}
