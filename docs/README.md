# NanoServer 文档

| 文档 | 内容 | 适合谁读 |
|---|---|---|
| [../README.md](../README.md) | 项目概览、快速开始、能力清单 | 初次接触 |
| [API.md](API.md) | **全部公开 API**：方法签名、参数、返回、示例 | 开发时查阅 |
| [CONFIGURATION.md](CONFIGURATION.md) | **全部可配置参数**：默认值、边界、调优建议与实测数据 | 部署调优 |
| [OPTIMIZATION.md](OPTIMIZATION.md) | 性能优化评估：已做项数据、可继续做的方向、不建议做的项 | 关心性能 |
| [SECURITY-AUDIT.md](SECURITY-AUDIT.md) | 安全审计：已修漏洞、验证通过项、加固建议 | 安全评审 |

## 快速索引

**想改监听端口** → CONFIGURATION § 一、宿主与监听

**想开 WebSocket** → CONFIGURATION § 二、WebSocket / API § 十一

**鉴权配不对** → CONFIGURATION § 三、鉴权 / API § 八、JWT

**静态文件 401 而非 403** → CONFIGURATION § 四、静态文件的 `StaticFilesBeforeAuth`

**跨域被拦** → CONFIGURATION § 五、CORS

**日志太多影响性能** → CONFIGURATION § 六、访问日志（含实测开销表）

**想知道为什么不用路由缓存** → CONFIGURATION § 七、路由 / OPTIMIZATION

**要防止 SSRF** → CONFIGURATION § 九、SSRF 防护 / API § 九

**上传大小限制** → CONFIGURATION § 十、文件传输（库不限制，需业务层做）

**后台推送前端** → README「服务端主动推送」/ `samples/NanoServer.Sample/Push/`

## 实测数据说明

本仓库所有性能数字均标注了测量条件。**绝对吞吐无跨机可比性**，只适合同机 A/B 对照。
测量方法见 `tools/bench.sh`（多轮取中位数）与 `tools/bench-ab.sh`（JIT vs AOT）。

一个必须记住的教训：本项目有过一次「微基准显示 1.96 µs/次的可观收益、端到端压测毫无变化」
的经历（见 OPTIMIZATION 的「被否决的优化」）。**任何优化都须端到端验证。**
