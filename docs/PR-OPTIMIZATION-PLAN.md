# NanoServer 优化路线建议（PR 方案）

## 1. 背景

从当前仓库的设计与文档来看，路由层已经完成了重点优化：

- 路由匹配已从“每次分配”调整为“零分配读路径”
- `RouteTable` 使用不可变快照，读取期零锁零复制
- `RouteMatch` 已从类改为 `readonly struct`
- 通配路由已改成 `Span`/`Range` 逐段比较，避免 `string.Split`

因此，项目的核心瓶颈已从“路由算法”转移到“系统 I/O 与连接层”，尤其是：

- 静态文件服务的 `open/read/stat`
- socket I/O 与 keepalive 成本
- WebSocket 广播时的串行 fan-out
- 应答头的重复写入与集合查找

这意味着，后续最值得做的优化，不再是继续压路由，而是专注于真实热路径上的 I/O 和连接成本。

---

## 2. 结论先行：这次真正值得做的优化

### 2.1 主 PR：静态文件与响应头 hot path

推荐优先提交的主 PR，聚焦如下两部分：

1. `src/NanoServer/Middleware/StaticFilesMiddleware.cs`
2. `src/NanoServer/Http/ResponseExtensions.cs`

这是最符合当前项目状态的改造方向：

- 减少静态文件请求中的重复路径判定与文件访问成本
- 减少响应头写入时的重复查找与字符串处理
- 不改变外部行为，不破坏公开 API
- 更容易产生端到端收益，且风险相对可控

### 2.2 可选 Follow-up PR：WebSocket 广播并发发送

第二优先级的方向：

- `src/NanoServer/WebSockets/WebSocketHub.cs`

具体是把 `BroadcastAsync` 从串行 `foreach + await` 改为：

- 先拍一份连接快照
- 再并发发送 `SendAsync`

这类优化在连接数较多时收益更明显，但需要特别注意：

- 单连接失败不应影响其他连接
- 连接删除必须幂等
- 发送顺序不能被误解为严格保证，除非业务本身要求顺序

---

## 3. 主 PR 方案：静态文件与响应头 hot path

### 3.1 目标

在不破坏当前兼容性的前提下，减少以下开销：

- 每次静态文件请求中的重复路径处理
- 每次响应头写入导致的通用集合查找
- 高频字符串处理和重复判断

### 3.2 适用范围

- `src/NanoServer/Middleware/StaticFilesMiddleware.cs`
- `src/NanoServer/Http/ResponseExtensions.cs`

### 3.3 具体改动建议

#### 3.3.1 `StaticFilesMiddleware.cs`

重点关注：

- `InvokeAsync`
- `MatchesPrefix`
- `BuildETag`

建议做法：

- 保持现有安全逻辑不变：
  - 路径穿越防护
  - `..` / 编码绕过防护
  - dotfile 拒绝
  - `ETag` / `304` 处理
- 让热路径更加“快路径化”：
  - 对最常见路径直接走低分支
  - 避免重复做稳定动作
- 继续保留 `GetFullPath + 前缀校验`，不要回退到放纵式路径解析

明确不建议纳入：

- 静态文件元数据缓存
- 路径到文件元数据的长生命周期缓存

这条路在仓库文档中已经被明确标记为“不建议做”：

- 端到端收益几乎为 0
- 可能引入 ETag 过期问题
- 可能导致旧内容继续被缓存命中

#### 3.3.2 `ResponseExtensions.cs`

重点关注：

- `WriteAsync`
- `SetHeader`

建议做法：

- 把最常见响应头的设置收敛成专用快路径
- 例如：
  - `Content-Type`
  - `Content-Length`
  - `ETag`
  - `Cache-Control`
- 减少通用 `Headers[key] = value` 的重复大小写不敏感查找与对象处理
- 维持现有行为：状态码、内容类型、内容长度、关闭输出流等都应保持一致

### 3.4 这个 PR 不应做的事情

- 不要混入路由缓存
- 不要混入静态文件 metadata cache
- 不要改变公开 API 签名
- 不要把“看起来可观但没有端到端收益”的优化塞进来

这条原则非常重要，因为仓库已有先例：微基准收益看起来很大，但端到端压测没有变化，最终证明这不是值得提交的优化。

---

## 4. 可选 Follow-up PR：WebSocket 广播并发发送

### 4.1 适用范围

- `src/NanoServer/WebSockets/WebSocketHub.cs`

### 4.2 现状

当前 `BroadcastAsync` 的实现是：

- 先拿到连接列表
- 然后按遍历顺序逐个 `await SendAsync`

这会带来两个问题：

- 广播延迟随连接数增长而叠加
- 大规模广播下，首尾连接的发送时延可明显拉大

### 4.3 建议方案

- 先用快照数组保存连接 IDs：`_connections.Keys.ToArray()`
- 再并发调用 `SendAsync`
- 对每个连接单独做失败处理，不影响其他连接
- 需要保证以下要求：
  - 连接移除是幂等的
  - 单连接失败不会导致整个广播失败
  - 不要求严格顺序的发送语义

### 4.4 风险

- 如果对“单连接顺序”有强要求，直接并发发送可能改变期待行为
- 需要确保失败连接及时从字典中删除，避免卡住后续广播

### 4.5 建议验证方式

- 使用多个 WebSocket 客户端同时连接
- 触发批量广播
- 观察：
  - 广播时延是否下降
  - 单连接异常是否能被隔离
  - 集群/连接长时间稳定性

---

## 5. 不建议纳入本轮 PR 的优化项

根据仓库已有说明，以下方案都不建议继续作为主 PR：

### 5.1 路由结果缓存

原因：

- 当前路由已接近 0.048 µs / 零分配
- 额外加一层字典缓存，并不一定有净收益
- 还会引入失效管理和维护成本

### 5.2 静态文件元数据缓存

原因：

- 已实测端到端收益几乎为 0
- 可能造成 ETag 过期或内容不一致
- 该方案在本仓库中已整体回退

### 5.3 `string` 全链路替换为 `Span` / `ReadOnlySpan<char>` 的大改

原因：

- 会破坏公开 API 兼容性
- 只在极少数路径上有收益
- 对 AOT/NativeAOT 的实际收益有限
- 文档中已明确判定“不建议做”

---

## 6. 建议提交顺序

推荐顺序如下：

1. 主 PR：静态文件 + 响应头 hot path
2. Follow-up PR：WebSocket 广播并发发送
3. 之后再评估：
   - `RequestScope` 懒初始化
   - 按 IP 的连接限流
   - 更细粒度的日志采样与输出控制

这样最稳妥，且最符合这个项目已经验证过的真实瓶颈。

---

## 7. 验证建议

建议按下面的方式验证改动：

### 7.1 压测脚本

```bash
./tools/bench.sh
./tools/bench-ab.sh
```

### 7.2 重点观测对象

- `GET /tiny.txt`
- `GET /api/health`
- `GET /api/me`
- `GET /api/di`

### 7.3 检查指标

- req/s
- p50
- p99
- 连接稳定性
- 访问日志/ETag 是否正常

### 7.4 安全回归

- 路径穿越测试
- dotfile 拒绝测试
- `304 Not Modified` 行为正确
- 头写入不破坏标准 HTTP 语义

---

## 8. 最终结论

这个仓库的“路由层优化”已经基本接近上限；未来最值得投入的优化，集中在：

- 静态文件服务的 fast path
- 响应头写出
- WebSocket 广播 fan-out
- 连接层与 DI 层的惰性化和去冗余

而不再值得继续投入的，是更深层的路由缓存和大量微优化。

这部分结论已在仓库文档中得到充分验证，后续 PR 应以“端到端收益为准”作为唯一判断标准。
