using System.Collections.Concurrent;
using System.Net;

namespace LiteServer.Routing;

/// <summary>路由处理器签名。</summary>
public delegate Task RequestHandler(HttpListenerRequest request, HttpListenerResponse response);

/// <summary>
/// 一条路由。
/// <para>
/// 关键设计：
/// <b>同一路径可注册多个方法</b>。
/// 内部以 (路径, 方法) 为组合键；方法为 null 表示该路径接受任意方法。
/// </para>
/// </summary>
public sealed class RouteEntry
{
    public required string Path { get; init; }

    /// <summary>归一化后的方法名（大写）；null = 任意方法。</summary>
    public string? Method { get; init; }

    public required RequestHandler Handler { get; init; }

    /// <summary>原始注册顺序，越小越先匹配（精准路由优先于通配路由由分层保证）。</summary>
    public int Order { get; init; }

    /// <summary>是否含通配段。</summary>
    public bool IsWildcard => Path.Contains('*');

    /// <summary>通配路由的段数组，段数决定匹配优先级。</summary>
    public string[] Segments { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 路由表。两级结构：精准路由走哈希直查，通配路由按段数分桶后线性比对。
/// </summary>
public sealed class RouteTable
{
    private const int MaxPathLength = 2048;
    private const int MaxWildcardSegments = 16;

    private readonly ConcurrentDictionary<string, List<RouteEntry>> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, List<RouteEntry>> _wildcard = new();
    private int _order;

    /// <summary>注册路由。同一 (路径, 方法) 重复注册时，后注册者覆盖前者。</summary>
    public void Add(string path, RequestHandler handler, string? method = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(handler);

        string normalizedPath = NormalizePath(path);
        string? normalizedMethod = string.IsNullOrWhiteSpace(method) ? null : method.Trim().ToUpperInvariant();
        int order = Interlocked.Increment(ref _order);

        var entry = new RouteEntry
        {
            Path = normalizedPath,
            Method = normalizedMethod,
            Handler = handler,
            Order = order,
            Segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries),
        };

        if (entry.IsWildcard)
        {
            List<RouteEntry> bucket = _wildcard.GetOrAdd(entry.Segments.Length, _ => new List<RouteEntry>());
            lock (bucket)
            {
                bucket.RemoveAll(e => string.Equals(e.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)
                                      && e.Method == normalizedMethod);
                bucket.Add(entry);
                // 段数多者更具体，先匹配。
                bucket.Sort((a, b) => b.Order.CompareTo(a.Order));
            }
        }
        else
        {
            List<RouteEntry> list = _exact.GetOrAdd(normalizedPath, _ => new List<RouteEntry>());
            lock (list)
            {
                list.RemoveAll(e => e.Method == normalizedMethod);
                list.Add(entry);
            }
        }
    }

    /// <summary>
    /// 查找路由。
    /// <para>
    /// 匹配顺序：精准路径 → 通配路径（段数从多到少）。
    /// 命中路径但方法不符时返回 <see cref="RouteMatch.MethodNotAllowed"/> 并附可用方法。
    /// </para>
    /// </summary>
    public RouteMatch Match(string rawPath, string method)
    {
        if (string.IsNullOrEmpty(rawPath)) return RouteMatch.NotFound();

        string path = NormalizePath(rawPath);
        if (path.Length > MaxPathLength || path.Contains("..", StringComparison.Ordinal))
        {
            return RouteMatch.NotFound();
        }

        // 1) 精准匹配
        if (_exact.TryGetValue(path, out List<RouteEntry>? exactList))
        {
            RouteMatch? m = TryMatchInList(exactList, method);
            if (m is not null) return m;
        }

        // 2) 通配匹配：段数多者优先（更具体）
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= MaxWildcardSegments)
        {
            for (int len = segments.Length; len >= 1; len--)
            {
                if (!_wildcard.TryGetValue(len, out List<RouteEntry>? bucket)) continue;

                RouteEntry[] snapshot;
                lock (bucket) snapshot = bucket.ToArray();

                List<RouteEntry>? matchedPath = null;
                foreach (RouteEntry entry in snapshot)
                {
                    if (SegmentsMatch(entry.Segments, segments))
                    {
                        (matchedPath ??= new List<RouteEntry>()).Add(entry);
                    }
                }
                if (matchedPath is null) continue;

                RouteMatch? m = TryMatchInList(matchedPath, method, allowAnyMethodEntry: true);
                if (m is not null) return m;
            }
        }

        return RouteMatch.NotFound();
    }

    private static RouteMatch? TryMatchInList(List<RouteEntry> list, string method, bool allowAnyMethodEntry = false)
    {
        RouteEntry[] snapshot;
        lock (list) snapshot = list.ToArray();

        // 先找方法精确匹配，再退回任意方法路由。
        foreach (RouteEntry entry in snapshot)
        {
            if (entry.Method is not null && string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase))
            {
                return RouteMatch.Ok(entry);
            }
        }
        foreach (RouteEntry entry in snapshot)
        {
            if (entry.Method is null)
            {
                return RouteMatch.Ok(entry);
            }
        }

        // 路径命中但方法不符。
        if (snapshot.Length > 0)
        {
            string[] allowed = snapshot
                .Where(e => e.Method is not null)
                .Select(e => e.Method!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return RouteMatch.MethodNotAllowed(allowed);
        }
        return null;
    }

    /// <summary>逐段比较，'*' 匹配任意单段。</summary>
    private static bool SegmentsMatch(string[] routeSegments, string[] requestSegments)
    {
        if (routeSegments.Length != requestSegments.Length) return false;
        for (int i = 0; i < routeSegments.Length; i++)
        {
            if (routeSegments[i] == "*") continue;
            if (!string.Equals(routeSegments[i], requestSegments[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>清空全部路由。</summary>
    public void Clear()
    {
        _exact.Clear();
        _wildcard.Clear();
    }

    /// <summary>当前注册的路由总数。</summary>
    public int Count =>
        _exact.Values.Sum(l => { lock (l) return l.Count; }) +
        _wildcard.Values.Sum(l => { lock (l) return l.Count; });

    /// <summary>列出全部路由（调试与文档用）。</summary>
    public IReadOnlyList<RouteEntry> Entries()
    {
        var result = new List<RouteEntry>();
        foreach (List<RouteEntry> l in _exact.Values) { lock (l) result.AddRange(l); }
        foreach (List<RouteEntry> l in _wildcard.Values) { lock (l) result.AddRange(l); }
        return result.OrderBy(e => e.Order).ToArray();
    }

    /// <summary>统一路径形态：保证以 '/' 开头、去掉尾部多余的 '/'。根路径归一为 "/"。</summary>
    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";
        string p = path.StartsWith('/') ? path : "/" + path;
        if (p.Length > 1 && p.EndsWith('/')) p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }
}

/// <summary>路由匹配结果。</summary>
public sealed class RouteMatch
{
    public RouteEntry? Entry { get; private init; }

    public RouteMatchKind Kind { get; private init; }

    /// <summary>方法不符时给出的 Allow 列表。</summary>
    public string[] AllowedMethods { get; private init; } = Array.Empty<string>();

    public bool IsMatched => Kind == RouteMatchKind.Matched;

    public static RouteMatch Ok(RouteEntry entry) => new() { Entry = entry, Kind = RouteMatchKind.Matched };

    public static RouteMatch NotFound() => new() { Kind = RouteMatchKind.NotFound };

    public static RouteMatch MethodNotAllowed(string[] allowed) =>
        new() { Kind = RouteMatchKind.MethodNotAllowed, AllowedMethods = allowed };
}

public enum RouteMatchKind
{
    NotFound = 0,
    Matched = 1,
    MethodNotAllowed = 2,
}
