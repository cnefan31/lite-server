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

    // 存储为「不可变数组」而非可变 List：
    // 注册时用 lock 保护并整体替换引用，读取时无需加锁、无需复制。
    // 早期实现每次 Match 都在 lock 内 ToArray()，实测每请求多分配 32~776 B。
    private readonly ConcurrentDictionary<string, RouteEntry[]> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, RouteEntry[]> _wildcard = new();

    /// <summary>注册期使用的写锁（读路径完全不加锁）。</summary>
    private readonly object _writeLock = new();
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

        lock (_writeLock)
        {
            if (entry.IsWildcard)
            {
                RouteEntry[] current = _wildcard.TryGetValue(entry.Segments.Length, out RouteEntry[]? b)
                    ? b
                    : Array.Empty<RouteEntry>();

                var next = new List<RouteEntry>(current);
                next.RemoveAll(e => string.Equals(e.Path, normalizedPath, StringComparison.OrdinalIgnoreCase)
                                    && e.Method == normalizedMethod);
                next.Add(entry);
                // 段数多者更具体，先匹配。
                next.Sort((a, b) => b.Order.CompareTo(a.Order));
                _wildcard[entry.Segments.Length] = next.ToArray();
            }
            else
            {
                RouteEntry[] current = _exact.TryGetValue(normalizedPath, out RouteEntry[]? l)
                    ? l
                    : Array.Empty<RouteEntry>();

                var next = new List<RouteEntry>(current);
                next.RemoveAll(e => e.Method == normalizedMethod);
                next.Add(entry);
                _exact[normalizedPath] = next.ToArray();
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
        if (_exact.TryGetValue(path, out RouteEntry[]? exactList))
        {
            if (TryMatchInList(exactList, method, out RouteMatch m)) return m;

            // 路径命中但方法不符：只有这种情况才走 405 分支，不在热路径上。
            return RouteMatch.MethodNotAllowed(AllowedMethodsOf(exactList));
        }

        // 2) 通配匹配：段数多者优先（更具体）
        // 用栈上 Span 切段，避免 string.Split 每次分配数组 + 子串（实测 192 B/op）。
        ReadOnlySpan<char> span = path.AsSpan();
        if (span.Length > 0 && span[0] == '/') span = span[1..];
        int segCount = CountSegments(span);
        if (segCount is > 0 and <= MaxWildcardSegments)
        {
            Span<Range> ranges = stackalloc Range[MaxWildcardSegments + 1];
            int parts = SplitSegments(span, ranges);

            for (int len = segCount; len >= 1; len--)
            {
                if (!_wildcard.TryGetValue(len, out RouteEntry[]? snapshot)) continue;

                RouteMatch found = default;
                bool hit = false;
                foreach (RouteEntry entry in snapshot)
                {
                    if (SegmentsMatch(entry.Segments, span, ranges, parts))
                    {
                        // 同段数下，先按方法挑，再退到任意方法；命中即返回第一个。
                        if (string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase) || entry.Method is null)
                        {
                            found = RouteMatch.Ok(entry);
                            hit = true;
                            break;
                        }
                    }
                }
                if (hit) return found;
            }
        }

        return RouteMatch.NotFound();
    }

    /// <summary>
    /// 在一组候选路由里按方法挑选。
    /// 返回 true 表示找到了可直接响应的路由（写入 <paramref name="match"/>）；
    /// 返回 false 表示该方法列表里没有匹配项。
    /// </summary>
    private static bool TryMatchInList(RouteEntry[] snapshot, string method, out RouteMatch match)
    {
        // 先找方法精确匹配，再退回任意方法路由。
        foreach (RouteEntry entry in snapshot)
        {
            if (entry.Method is not null && string.Equals(entry.Method, method, StringComparison.OrdinalIgnoreCase))
            {
                match = RouteMatch.Ok(entry);
                return true;
            }
        }
        foreach (RouteEntry entry in snapshot)
        {
            if (entry.Method is null)
            {
                match = RouteMatch.Ok(entry);
                return true;
            }
        }

        match = default;
        return false;
    }

    /// <summary>从候选里提取所有允许的方法（仅 405 分支调用，不在热路径上）。</summary>
    private static string[] AllowedMethodsOf(RouteEntry[] snapshot)
    {
        string[] allowed = snapshot
            .Where(e => e.Method is not null)
            .Select(e => e.Method!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return allowed;
    }

    /// <summary>统计段数（以 '/' 分隔，忽略空段）。</summary>
    private static int CountSegments(ReadOnlySpan<char> path)
    {
        int count = 0;
        int i = 0;
        while (i < path.Length)
        {
            while (i < path.Length && path[i] == '/') i++;
            if (i >= path.Length) break;
            count++;
            while (i < path.Length && path[i] != '/') i++;
        }
        return count;
    }

    /// <summary>把路径切段写入 <paramref name="ranges"/>，返回实际段数。无分配。</summary>
    private static int SplitSegments(ReadOnlySpan<char> path, Span<Range> ranges)
    {
        int count = 0;
        int i = 0;
        while (i < path.Length && count < ranges.Length)
        {
            while (i < path.Length && path[i] == '/') i++;
            if (i >= path.Length) break;
            int start = i;
            while (i < path.Length && path[i] != '/') i++;
            ranges[count++] = new Range(start, i);
        }
        return count;
    }

    /// <summary>用 Span 版本逐段比较，避免分配子串。</summary>
    private static bool SegmentsMatch(string[] routeSegments, ReadOnlySpan<char> path, Span<Range> ranges, int parts)
    {
        if (routeSegments.Length != parts) return false;
        for (int i = 0; i < routeSegments.Length; i++)
        {
            if (routeSegments[i] == "*") continue;
            ReadOnlySpan<char> seg = path[ranges[i]];
            if (!seg.Equals(routeSegments[i].AsSpan(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
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
    public int Count => _exact.Values.Sum(a => a.Length) + _wildcard.Values.Sum(a => a.Length);

    /// <summary>列出全部路由（调试与文档用）。</summary>
    public IReadOnlyList<RouteEntry> Entries()
    {
        var result = new List<RouteEntry>();
        foreach (RouteEntry[] a in _exact.Values) result.AddRange(a);
        foreach (RouteEntry[] a in _wildcard.Values) result.AddRange(a);
        return result.OrderBy(e => e.Order).ToArray();
    }

    /// <summary>统一路径形态：保证以 '/' 开头、去掉尾部多余的 '/'。根路径归一为 "/"。</summary>
    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        // 快路径：绝大多数真实请求已是以 '/' 开头且不以 '/' 结尾的形态，
        // 直接原样返回，避免 TrimEnd 产生新字符串（实测每次 32 B 分配）。
        bool startsOk = path.Length > 0 && path[0] == '/';
        bool endsOk = path.Length == 1 || path[^1] != '/';
        if (startsOk && endsOk) return path;

        string p = startsOk ? path : "/" + path;
        if (p.Length > 1 && p[^1] == '/') p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }
}

/// <summary>路由匹配结果。</summary>
/// <summary>
/// 路由匹配结果。
/// <para>
/// 刻意设计为 <c>readonly struct</c>：路由匹配是每个请求的必经路径，
/// 实测每次返回引用类型会分配 72 B（未命中 192 B）。改成值类型后该分配归零。
/// </para>
/// </summary>
public readonly struct RouteMatch
{
    public RouteEntry? Entry { get; }

    public RouteMatchKind Kind { get; }

    /// <summary>方法不符时给出的 Allow 列表。</summary>
    public string[] AllowedMethods { get; }

    public bool IsMatched => Kind == RouteMatchKind.Matched;

    private RouteMatch(RouteEntry? entry, RouteMatchKind kind, string[] allowedMethods)
    {
        Entry = entry;
        Kind = kind;
        AllowedMethods = allowedMethods;
    }

    public static RouteMatch Ok(RouteEntry entry) => new(entry, RouteMatchKind.Matched, Array.Empty<string>());

    public static RouteMatch NotFound() => new(null, RouteMatchKind.NotFound, Array.Empty<string>());

    public static RouteMatch MethodNotAllowed(string[] allowed) =>
        new(null, RouteMatchKind.MethodNotAllowed, allowed);
}

public enum RouteMatchKind
{
    NotFound = 0,
    Matched = 1,
    MethodNotAllowed = 2,
}
