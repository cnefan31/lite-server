using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiteServer.Security;

/// <summary>
/// 极简 JWT（HS256）。只做签名/验签与 payload 解码，不做声明（claim）语义校验。
/// <para>
/// 刻意不引入 System.IdentityModel.Tokens.Jwt：本库目标是零依赖 + NativeAOT，
/// HS256 的实现量很小，自己写反而更容易保证 AOT 兼容且启动无反射开销。
/// </para>
/// </summary>
public static class Jwt
{
    private static readonly string HeaderJson = Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));

    /// <summary>生成 token。<paramref name="payloadJson"/> 必须是合法 JSON 对象文本。</summary>
    public static string Generate(string payloadJson, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (string.IsNullOrWhiteSpace(payloadJson)) payloadJson = "{}";

        string payload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        string signingInput = HeaderJson + "." + payload;
        string signature = Sign(signingInput, secret);
        return signingInput + "." + signature;
    }

    /// <summary>
    /// 完整校验：结构、算法、签名、exp。
    /// 任一不通过返回 false。exp 不存在时视为不过期（交由调用方决定策略）。
    /// </summary>
    public static bool Verify(string? token, string secret, TimeSpan? clockSkew = null)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret)) return false;

        string[] parts = token.Split('.');
        if (parts.Length != 3) return false;

        string signingInput = parts[0] + "." + parts[1];
        string expected = Sign(signingInput, secret);

        // 定长比较，避免时序侧信道。
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(parts[2])))
        {
            return false;
        }

        string payloadJson = DecodePayload(token);
        if (payloadJson.Length == 0) return false;

        long? exp = ReadLongClaim(payloadJson, "exp");
        if (exp is null) return true;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long skew = (long)(clockSkew ?? TimeSpan.Zero).TotalSeconds;
        return exp.Value + skew > now;
    }

    /// <summary>解码 payload 为 JSON 文本。失败返回空串。</summary>
    public static string DecodePayload(string? token)
    {
        if (string.IsNullOrEmpty(token)) return string.Empty;
        string[] parts = token.Split('.');
        if (parts.Length < 2) return string.Empty;

        try
        {
            byte[] bytes = Base64UrlDecode(parts[1]);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>读取 payload 中的整数型 claim（如 exp / nbf / iat）。</summary>
    public static long? ReadLongClaim(string payloadJson, string claim)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(claim, out JsonElement el)) return null;

            return el.ValueKind switch
            {
                JsonValueKind.Number when el.TryGetInt64(out long v) => v,
                JsonValueKind.String when long.TryParse(el.GetString(), out long v) => v,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>读取 payload 中的字符串型 claim。</summary>
    public static string? ReadStringClaim(string payloadJson, string claim)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(claim, out JsonElement el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Sign(string signingInput, string secret)
    {
        byte[] key = Encoding.UTF8.GetBytes(secret);
        byte[] data = Encoding.UTF8.GetBytes(signingInput);
        byte[] hash = HMACSHA256.HashData(key, data);
        return Base64UrlEncode(hash);
    }

    internal static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static byte[] Base64UrlDecode(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => Convert.FromBase64String(s + "=="),
            3 => Convert.FromBase64String(s + "="),
            _ => Convert.FromBase64String(s),
        };
    }
}
