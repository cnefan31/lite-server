# NanoServer 安全审计报告

| 项 | 内容 |
|---|---|
| 审计对象 | NanoServer 库 + `samples/NanoServer.Sample` 示例服务 |
| 审计日期 | 2026-10-24 |
| 审计方式 | 黑盒实测（真机 HTTP 请求）+ 关键路径源码复核 |
| 环境 | macOS arm64 / .NET 10.0.5 / JIT 与 NativeAOT 双构建各验一轮 |
| 回归脚本 | `tools/security-test.sh`（28 项断言，可重复执行） |

## 结论摘要

| 级别 | 数量 | 处理 |
|---|---|---|
| 高危 | 1 | 已修复并验证 |
| 中危 | 2 | 已修复并验证 |
| 低危 / 加固建议 | 4 | 已记录，部分已处理 |
| 通过（无需处理） | 12 类 | 见「验证通过项」 |

审计覆盖 OWASP Top 10 中与「无框架裸 HttpListener」相关的面向：
路径穿越、注入、鉴权缺陷、SSRF、敏感数据暴露、安全配置错误、DoS。
不涉及：业务逻辑漏洞（本框架无业务）、第三方依赖 CVE（依赖仅 4 个 abstractions 包）。

---

## 已修复漏洞

### H-1 · SSRF：内网与云元数据服务可被访问（高危）

**位置** `samples/NanoServer.Sample/Program.cs` → `GET /api/upstream`

**问题** 原实现只校验 scheme 是否为 http(s)，完全不限制目标地址。实测可让服务器
访问任意内网地址：

```
GET /api/upstream?url=http://127.0.0.1:8090/api/health   -> 200（可访问自身）
GET /api/upstream?url=http://169.254.169.254/latest/     -> 未拦截（云元数据端点）
GET /api/upstream?url=http://10.0.0.1/                   -> 未拦截（内网）
```

**危害** 攻击者借此探测内网拓扑；在云环境中可请求 `169.254.169.254` 读取
IAM 临时凭证，进而横向接管云资源。这是 SSRF 最典型的严重后果。

**修复** 新增库级组件 `src/NanoServer/Security/SsrfGuard.cs`：

- 只允许 http/https
- **解析出 IP 之后**判定（而非只查域名字面量），拒绝：
  回环、私有网段（10/8、172.16/12、192.168/16、100.64/10）、
  链路本地（169.254/16，含云元数据）、保留/组播/文档段、IPv6 私有与链路本地
- 多 A 记录中只要有一个不合规即整体拒绝
- IPv4 映射的 IPv6 先归一化再判定
- 示例侧配合 `AllowAutoRedirect = false`，阻断「302 跳内网」绕过

**验证** 以下 10 种形态全部返回 400：

```
127.0.0.1 · localhost · 169.254.169.254 · 10.0.0.1 · 192.168.1.1
2130706433（十进制 IP）· 0177.0.0.1（八进制）· 0x7f000001（十六进制）
127.1（简写）· [::1] · [::ffff:127.0.0.1] · 127.0.0.1.nip.io · localtest.me
file:// · gopher:// · dict://
```

公网地址（`https://example.com`）仍可正常探活，功能未受影响。

---

### M-1 · CORS：任意 Origin 被回显且允许携带凭证（中危）

**位置** `src/NanoServer/NanoWebServer.cs` → `HandleCorsAsync`

**问题** `UseCors()` 无参调用时，原实现把请求的 `Origin` 原样回显，并强制附加
`Access-Control-Allow-Credentials: true`。实测：

```
Origin: https://evil.example  ->  Access-Control-Allow-Origin: https://evil.example
                                  Access-Control-Allow-Credentials: true
Origin: null                  ->  Access-Control-Allow-Origin: null（同样放行）
```

**危害** 任意站点都能带凭证读取本服务响应；`null` 放行更可被 sandbox iframe 利用。
等同把 CORS 变成「允许所有来源 + 允许凭证」，是经典的危险配置。

**修复**

- 未显式配置允许来源时，**只回显同源请求**（比对 scheme+host+port），跨源一律不加 CORS 头
- `null` Origin 永不通过（sandbox iframe / file:// / 跨源重定向都可能是它）
- 显式传参时仍严格白名单比对

**验证** `https://evil.example` 与 `null` 均不再返回 `Access-Control-Allow-Origin`；
同源请求仍正常获得 CORS 头。

---

### M-2 · 上传无大小限制：可耗尽磁盘（中危）

**位置** `samples/NanoServer.Sample/Program.cs` → `POST /api/upload`

**问题** 原实现来者不拒。实测：

```
单请求 100 MB   -> 200，完整落盘 100M
10 路并发 × 50MB -> 全部 200，累计落盘 500M
```

**危害** 攻击者可用少量请求打满磁盘，导致服务与同机其它进程写入失败。
属于低成本、可重复的 DoS。

**修复** 两道限制：

1. 请求进入时校验 `Content-Length64`，超过 32 MB 直接 413
2. 落盘后按**实际文件大小**再校验一次，超限则删除文件并返回 413
   （覆盖分块传输 `Content-Length = -1` 的情况——此时第一道检查失效）

**验证** 100 MB → 413；1 MB → 200 正常。

---

## 加固建议（未修复，需按部署场景决定）

### L-1 · 缺少通用安全响应头

当前响应缺少 `X-Frame-Options`、`Content-Security-Policy`、`Strict-Transport-Security`。
对纯 API 服务影响有限，但若用静态托管输出 HTML 页面，建议补上以防点击劫持。
框架可通过自定义中间件统一注入，未内置是为了不越权决定应用的安全策略。

### L-2 · `Server: Microsoft-NetCore/2.0` 暴露运行时信息

该头由 `HttpListener` 自动添加，框架未覆盖。若需隐藏，可在中间件里覆写 `Server` 头。

### L-3 · token 支持 `?token=` 查询参数（设计权衡）

浏览器原生 `EventSource` / `WebSocket` 无法设置请求头，故框架支持
`Authorization` 头、`?token=`、cookie 三通道。

**风险**：URL 中的 token 会进入浏览器历史、Referer、代理与访问日志。
实测确认框架自身**不记录** query string 到日志，但上游反代/CDN 通常会记录。

**建议**：生产环境优先用请求头；必须用查询参数时，确保 TLS 且缩短 token 有效期。

### L-4 · 示例凭据为硬编码明文

`nano-sample-secret` / `nano_sample_jwt_secret_2026` 直接写在示例代码里，仅作演示。
生产务必通过环境变量或密钥管理服务注入，并定期轮换。

---

## 验证通过项（无需处理）

以下均为**实测**结果，非静态推断。

### 路径穿越与静态文件防护

| 攻击形态 | 结果 |
|---|---|
| `../../etc/passwd` | 404（容纳校验拦截） |
| `..%2F..%2Fetc%2Fpasswd` | 403 |
| `..%252F`（双重编码） | 404 |
| `%2e%2e%2f`（编码点） | 403 |
| `..%5C`（反斜杠） | 404 |
| `%c0%ae%c0%ae`（UTF-8 过编码） | 404 |
| `//etc/passwd`（绝对路径） | 拦截 |
| 40 层 `../` 重复 | 拦截 |
| 空字节 `index.html%00.png` | 400 |

另放置应用根外的标记文件 `/tmp/outside_secret.txt` 尝试读取，**内容零泄露**。

### 隐藏文件

`.canary`（存在）→ 403；`.env` / `.git/config` / `web.config`（不存在）→ 404。
实现上对**路径任一段**以 `.` 开头即拒绝，且判定在文件 stat 之前，
不因文件存在与否泄露差异。

### JWT 攻击

| 攻击 | 结果 |
|---|---|
| 篡改签名末位 | 401 |
| `alg=none` 算法混淆 | 401 |
| 弱密钥字典猜测（secret/password/123456 等 5 组） | 全部 401 |
| 正确密钥但已过期 exp | 401 |
| 畸形 token（空 / 非 base64 / 10KB 超长） | 401 |

签名比对使用 `CryptographicOperations.FixedTimeEquals`（定长比较），避免时序侧信道。

### Token 黑名单

拉黑后原样请求、尾部加空格、`bearer` 小写变体 —— 全部 401，无法绕过。

### 响应头注入 / CRLF

路径、查询参数、`filename` 请求头中注入 `%0d%0a` 均未造成响应拆分；
实测响应头未被污染。

### 注入类

框架不拼 SQL / 命令 / 模板，无对应注入面。
错误响应仅返回纯文本短语（`invalid json body` / `not found`），
不含堆栈、不含物理路径 —— 已断言 `/Users/` 与 `at NanoServer` 均不出现。

### 目录枚举

`/src/` `/bin/` `/data/` `/.git/` `/obj/` 全部 404（静态中间件未命中即放行给路由，最终 404）。

### DoS 相关

| 场景 | 结果 |
|---|---|
| 慢速请求（Slowloris 半开连接挂起 6s） | 正常请求仍在 13ms 内响应，未被阻塞 |
| 超长 URL（16 KB query） | 200 正常处理，未崩溃 |
| 100 个自定义请求头 | 200 正常处理 |
| WebSocket 5 MB 单条消息（上限 4 MB） | 连接被断开（`MessageTooBig`） |

### WebSocket 安全

| 场景 | 结果 |
|---|---|
| 无 token 握手 | 拒绝（401） |
| 伪造 token 握手 | 拒绝（401） |
| 超过消息大小上限 | 主动断开 |

### Cookie 属性

```
Set-Cookie: lite_sid=...; Path=/; Expires=...; HttpOnly; SameSite=Lax
```

`HttpOnly` 与 `SameSite=Lax` 均已设置（`Secure` 未设，因示例跑在 http 上；
生产 HTTPS 环境应开启）。

### 输入解析健壮性

- 非法 JSON → 400（明确区分「格式错」与「凭据错」，不误报 401）
- 超长字段 → 400
- 源生成 JSON 已显式设 `CamelCase` 命名策略，避免字段静默变 `null`

---

## 复验方式

```bash
# 启动示例服务
dotnet run --project samples/NanoServer.Sample

# 执行安全回归（28 项断言）
./tools/security-test.sh
```

期望输出末行：`全部安全断言通过`。

脚本同时适用于 JIT 与 NativeAOT 构建，两者均已验证 28/28 通过。

## 局限说明

- 本次为**黑盒 + 关键路径源码复核**，非逐行全量代码审计
- 未做 fuzzing、未做并发竞态专项、未做 TLS 配置审计（示例未启用 HTTPS）
- 未覆盖依赖包 CVE 扫描（依赖仅 `Microsoft.Extensions.*.Abstractions` 4 个包）
- 性能与安全存在取舍：本轮为修 SSRF 关闭了自动重定向，若业务需要跟随重定向，
  须自行实现「每跳都重新做 SSRF 校验」
