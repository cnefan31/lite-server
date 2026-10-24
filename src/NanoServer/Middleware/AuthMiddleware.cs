using System.Net;
using NanoServer.Http;
using NanoServer.Logging;
using NanoServer.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NanoServer.Security;

/// <summary>鉴权中间件工厂。</summary>
public static class AuthMiddleware
{
    /// <summary>
    /// 简单 Token 校验：比对 Authorization 头的**原值**（不剥离 Bearer 前缀）。
    /// <para>
    /// 注意：本方法比对的是原始头值，不剥离 `Bearer ` 前缀。
    /// 新代码更推荐 <see cref="Jwt"/> 或 <see cref="BearerToken"/>，后者会剥离前缀。
    /// </para>
    /// </summary>
    public static Middleware.MiddlewareDelegate SimpleToken(string expectedToken, IReadOnlySet<string> whiteList, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedToken);
        ArgumentNullException.ThrowIfNull(whiteList);

        return (request, response) =>
        {
            if (IsWhiteListed(request, whiteList)) return Task.FromResult(true);

            string? raw = request.Headers["Authorization"];
            if (string.Equals(raw, expectedToken, StringComparison.Ordinal))
            {
                return Task.FromResult(true);
            }

            NanoLogger.AuthRejected(logger ?? NullLogger.Instance, request.HttpMethod ?? "-", request.Url?.AbsolutePath ?? "/", "token 不匹配");
            response.StatusCode = 401;
            return DenyAsync(response);
        };
    }

    /// <summary>Bearer Token 校验：剥离 `Bearer ` 前缀后比对。</summary>
    public static Middleware.MiddlewareDelegate BearerToken(string expectedToken, IReadOnlySet<string> whiteList, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedToken);
        ArgumentNullException.ThrowIfNull(whiteList);

        return (request, response) =>
        {
            if (IsWhiteListed(request, whiteList)) return Task.FromResult(true);

            string? token = request.ExtractToken();
            if (string.Equals(token, expectedToken, StringComparison.Ordinal))
            {
                return Task.FromResult(true);
            }

            response.StatusCode = 401;
            return DenyAsync(response);
        };
    }

    /// <summary>
    /// JWT 校验：验签 + 验 exp + 查黑名单。
    /// </summary>
    /// <param name="secret">HS256 密钥。</param>
    /// <param name="whiteList">免鉴权路径集合。</param>
    /// <param name="blacklist">可选的吊销名单。</param>
    /// <param name="clockSkew">允许的时钟偏移。</param>
    /// <param name="logger">日志器，用于记录鉴权失败原因。</param>
    public static Middleware.MiddlewareDelegate Jwt(
        string secret,
        IReadOnlySet<string> whiteList,
        TokenBlacklist? blacklist = null,
        TimeSpan? clockSkew = null,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(whiteList);

        return (request, response) =>
        {
            if (IsWhiteListed(request, whiteList)) return Task.FromResult(true);

            string? token = request.ExtractToken();
            if (string.IsNullOrEmpty(token))
            {
                NanoLogger.AuthRejected(logger ?? NullLogger.Instance, request.HttpMethod ?? "-", request.Url?.AbsolutePath ?? "/", "缺少 token");
                response.StatusCode = 401;
                return DenyAsync(response);
            }

            if (!Security.Jwt.Verify(token, secret, clockSkew))
            {
                NanoLogger.AuthRejected(logger ?? NullLogger.Instance, request.HttpMethod ?? "-", request.Url?.AbsolutePath ?? "/", "签名或有效期校验失败");
                response.StatusCode = 401;
                return DenyAsync(response);
            }

            if (blacklist is not null && blacklist.Contains(token))
            {
                NanoLogger.AuthRejected(logger ?? NullLogger.Instance, request.HttpMethod ?? "-", request.Url?.AbsolutePath ?? "/", "token 已吊销");
                response.StatusCode = 401;
                return DenyAsync(response);
            }

            return Task.FromResult(true);
        };
    }

    /// <summary>白名单判定：仅对精确路径生效，通配路由不进白名单。</summary>
    private static bool IsWhiteListed(HttpListenerRequest request, IReadOnlySet<string> whiteList)
    {
        string path = Routing.RouteTable.NormalizePath(request.Url?.AbsolutePath ?? "/");
        return whiteList.Contains(path);
    }

    /// <summary>写出拒绝响应并返回 false（终止管线）。</summary>
    private static async Task<bool> DenyAsync(HttpListenerResponse response)
    {
        int status = response.StatusCode;
        await response.WriteAsync(
            status == 401 ? "Unauthorized" : "Forbidden",
            ContentTypes.Plain,
            status).ConfigureAwait(false);
        return false;
    }
}
