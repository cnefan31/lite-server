using System.Net;
using System.Net.Sockets;

namespace LiteServer.Security;

/// <summary>
/// SSRF（服务端请求伪造）防护。
/// <para>
/// 场景：服务端按用户提供的 URL 发起请求（网络探活、webhook、图片代理等）。
/// 若不校验目标地址，攻击者可让服务器去访问内网、回环、链路本地地址，
/// 进而探测内网拓扑，或在云环境中读取元数据服务（169.254.169.254）拿到 IAM 凭证。
/// </para>
/// <para>
/// 本类在**解析出 IP 之后**判断，而不是只判断字面量域名——否则
/// <c>localtest.me</c>、<c>127.0.0.1.nip.io</c> 这类解析到内网的域名可绕过。
/// </para>
/// </summary>
public static class SsrfGuard
{
    /// <summary>判定结果。</summary>
    public enum Verdict
    {
        /// <summary>允许访问。</summary>
        Allowed = 0,
        /// <summary>scheme 不是 http/https。</summary>
        BadScheme = 1,
        /// <summary>目标是回环地址。</summary>
        Loopback = 2,
        /// <summary>目标是私有/内网地址。</summary>
        Private = 3,
        /// <summary>目标是链路本地地址（含云元数据 169.254.169.254）。</summary>
        LinkLocal = 4,
        /// <summary>目标是保留/文档/组播等不可路由地址。</summary>
        Reserved = 5,
        /// <summary>域名无法解析。</summary>
        Unresolvable = 6,
    }

    /// <summary>
    /// 校验 URL 是否可安全访问。
    /// </summary>
    /// <param name="url">待校验的 URL。</param>
    /// <param name="allowPrivateNetworks">
    /// 是否允许内网地址。仅在受信任的内部场景（如探活自己的集群）才应设为 true。
    /// </param>
    /// <param name="verdict">具体判定原因，便于日志与排查。</param>
    /// <param name="resolvedAddresses">解析出的 IP 列表（用于后续连接时固定地址，避免 DNS 重绑定）。</param>
    public static bool IsSafe(
        string? url,
        out Verdict verdict,
        out IPAddress[] resolvedAddresses,
        bool allowPrivateNetworks = false)
    {
        verdict = Verdict.BadScheme;
        resolvedAddresses = Array.Empty<IPAddress>();

        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        // 直接就是 IP 字面量：不必解析。
        if (IPAddress.TryParse(uri.Host, out IPAddress? literal))
        {
            verdict = Classify(literal, allowPrivateNetworks);
            resolvedAddresses = new[] { literal };
            return verdict == Verdict.Allowed;
        }

        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(uri.Host);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            verdict = Verdict.Unresolvable;
            return false;
        }

        if (addresses.Length == 0)
        {
            verdict = Verdict.Unresolvable;
            return false;
        }

        // 只要有一个解析结果不合规就拒绝——避免「多 A 记录里夹一个内网地址」绕过。
        foreach (IPAddress addr in addresses)
        {
            Verdict v = Classify(addr, allowPrivateNetworks);
            if (v != Verdict.Allowed)
            {
                verdict = v;
                return false;
            }
        }

        verdict = Verdict.Allowed;
        resolvedAddresses = addresses;
        return true;
    }

    /// <summary>便捷重载。</summary>
    public static bool IsSafe(string? url, out Verdict verdict, bool allowPrivateNetworks = false)
        => IsSafe(url, out verdict, out _, allowPrivateNetworks);

    /// <summary>把单个 IP 归类。</summary>
    private static Verdict Classify(IPAddress address, bool allowPrivateNetworks)
    {
        // IPv4 映射到 IPv6 的统一处理。
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return Verdict.Loopback;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();

            // 链路本地 169.254.0.0/16 —— 云元数据服务就在这里。
            if (b[0] == 169 && b[1] == 254) return Verdict.LinkLocal;

            // 0.0.0.0/8
            if (b[0] == 0) return Verdict.Reserved;

            bool isPrivate =
                b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)              // 192.168.0.0/16
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // 100.64.0.0/10 CGNAT

            if (isPrivate) return allowPrivateNetworks ? Verdict.Allowed : Verdict.Private;

            // 组播 224.0.0.0/4、保留 240.0.0.0/4、文档段
            if (b[0] >= 224) return Verdict.Reserved;
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) return Verdict.Reserved;   // TEST-NET-1
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19)) return Verdict.Reserved; // TEST-NET-2
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return Verdict.Reserved;  // TEST-NET-3

            return Verdict.Allowed;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal) return Verdict.LinkLocal;   // fe80::/10
            if (address.IsIPv6SiteLocal) return Verdict.Private;     // fec0::/10
            if (address.IsIPv6Multicast) return Verdict.Reserved;    // ff00::/8

            byte[] b = address.GetAddressBytes();
            // 唯一本地地址 fc00::/7
            if ((b[0] & 0xFE) == 0xFC)
            {
                return allowPrivateNetworks ? Verdict.Allowed : Verdict.Private;
            }
            // ::ffff:0:0/96 已在上方映射处理；::/128 未指定地址
            if (address.Equals(IPAddress.IPv6Any)) return Verdict.Reserved;

            return Verdict.Allowed;
        }

        return Verdict.Reserved;
    }
}
