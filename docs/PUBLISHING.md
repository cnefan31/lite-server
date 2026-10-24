# 发布指南

本项目通过 GitHub Actions 自动发布到 NuGet.org。本文说明**你需要手动做的一次性配置**与发布流程。

---

## 一、一次性配置（必须）

本项目使用 **NuGet 受信任发布（Trusted Publishing）**：不保存任何长期 API Key，
由 GitHub Actions 的 OIDC 令牌临时换取一次性密钥。官方文档：
https://learn.microsoft.com/zh-cn/nuget/nuget-org/trusted-publishing

### 1. 在 nuget.org 配置受信任发布策略

登录 https://www.nuget.org/ → 点用户名 → **受信任的发布** → 添加新策略。

本仓库对应填法（**已配置完成**，此处记录以便核对）：

| 字段 | 值 |
|---|---|
| 策略名称 | `NanoServer` |
| Package owner | `cnefan31` |
| Scopes | Push new packages and package versions |
| Glob Patterns | `NanoServer*` |
| Publisher | GitHub Actions |
| Repository Owner | `cnefan31`（ID 71433755） |
| Repository | `lite-server`（ID 1431294409） |
| Workflow | `release.yml` |
| Environment | `release` |

> **三处必须与 workflow 严格对应**，任一不符 OIDC 验证就会失败：
> 1. `Workflow` 只写**文件名** `release.yml`，不含 `.github/workflows/` 路径
> 2. `Repository` 是 `lite-server`（仓库名），不是包名
> 3. `Environment` 为 `release` → workflow 的 job 必须声明 `environment: release`

### 2. 在 GitHub 仓库添加 `NUGET_USER` 秘密

临时密钥换取需要提供 nuget.org 的**用户名（profile name）**，**不是邮箱**。

1. 打开 https://github.com/cnefan31/lite-server/settings/secrets/actions
2. **New repository secret**
3. Name 填 `NUGET_USER`
4. Secret 填你的 nuget.org 用户名（本仓库为 `cnefan31`，可在
   https://www.nuget.org/account 页面确认）

> 这是唯一需要配置的 secret。**不再需要 `NUGET_API_KEY`** ——
> 若之前建过，可以删掉。

### 3. GitHub Environment

策略里 Environment 填了 `release`，因此仓库需存在同名 environment（**已创建**）：

```bash
# 查看
gh api repos/cnefan31/lite-server/environments --jq '.environments[].name'

# 若不存在则创建
gh api -X PUT repos/cnefan31/lite-server/environments/release
```

若希望发布前需人工批准，可给该 environment 加 Required reviewers（可选）。

### 4. 确认包名可用性

```bash
# 200 = 已占用，404 = 可用
curl -sS -o /dev/null -w "%{http_code}\n" https://api.nuget.org/v3-flatcontainer/nanoserver/index.json
```

当前状态：**404（可用）**。

## 二、发布流程

### 打 tag 自动发布

```bash
# 1. 确认主分支通过 CI
git checkout main && git pull

# 2. 打语义化版本 tag
git tag v1.0.0

# 3. 推送 tag 触发发布
git push origin v1.0.0
```

workflow 会依次执行：版本校验 → 还原 → 构建（CS 警告视为错误）→ 起服务跑 28 项安全回归 → 打包 → 推 NuGet → 建 GitHub Release。

**任一环节失败即中止**，不会推送半成品到 NuGet。

### 版本号规则

tag 必须是 `vMAJOR.MINOR.PATCH` 形式，可带预发布后缀：

| tag | 结果 |
|---|---|
| `v1.0.0` | ✅ 发布 1.0.0 |
| `v1.2.3` | ✅ 发布 1.2.3 |
| `v1.0.0-beta.1` | ✅ 发布预发布版（NuGet 上标为 Prerelease） |
| `v1.0` | ❌ 校验失败，workflow 中止 |
| `vbad` | ❌ 校验失败 |

> NuGet 上**同版本号不可覆盖**。若发现发布错误，只能发新版本号（如 `v1.0.1`）。

### 预发布验证流程

先用预发布版验证整条链路：

```bash
git tag v1.0.0-beta.1
git push origin v1.0.0-beta.1
```

确认 NuGet 上能正常安装后，再发正式版 `v1.0.0`。

---

## 二·五、链路验证结果（已实测）

发布流程**已在 GitHub 上真机验证**，除最后一步推送外全部通过：

| 步骤 | CI (main push) | Release (tag) |
|---|---|---|
| 检出 / 安装 .NET | ✅ | ✅ |
| 还原 | ✅ | ✅ |
| 构建（CS 警告视为错误） | ✅ | ✅ |
| 打包 | ✅ | ✅ |
| 上传产物 / 校验包内容 | ✅ | ✅ |
| 起服务跑 28 项安全回归 | ✅ | ✅ |
| 从 tag 推导版本号 | — | ✅ |
| **发布到 NuGet.org** | — | ⬜ 待配置 secret |
| 创建 GitHub Release | — | ⬜ 依赖上一步 |

> 验证方式：推送 `v1.0.0-beta.1` tag 触发真实运行，确认前 9 步成功后删除该 tag
> （NuGet 上未留下任何版本）。**配置好受信任发布策略与 `NUGET_USER` 后即可正式发布。**

## 三、本地打包（不上传）

```bash
# 默认版本（csproj 里的 Version）
dotnet pack src/NanoServer/NanoServer.csproj -c Release -o artifacts

# 指定版本
dotnet pack src/NanoServer/NanoServer.csproj -c Release -o artifacts /p:Version=1.2.3

# 检查包内容
unzip -l artifacts/NanoServer.*.nupkg
```

产出两个文件：

| 文件 | 说明 |
|---|---|
| `NanoServer.x.y.z.nupkg` | 主包 |
| `NanoServer.x.y.z.snupkg` | 符号包（调试用，随主包一起推） |

包内包含：`lib/net10.0/NanoServer.dll`、`NanoServer.xml`（XML 注释）、`README.md`、`docs/API.md`、`docs/CONFIGURATION.md`。

---

## 四、手动发布（兜底）

受信任发布的设计目标是**消除长期密钥**，因此手动发布若也用 OIDC 会比较绕。
两种兜底方式：

**方式 A：重跑 workflow（推荐）**

```bash
gh run rerun <run-id>          # 重跑失败的运行
# 或重新打 tag
git tag -d v1.0.0 && git push origin :refs/tags/v1.0.0
git tag v1.0.0 && git push origin v1.0.0
```

**方式 B：临时创建 API Key（会削弱受信任发布的意义）**

仅在 workflow 本身故障、急需发版时使用：到 nuget.org 手动创建一个短期
API Key（限定 Glob 为 `NanoServer`、有效期设为最短），用它推送，**用完立即撤销**。

```bash
dotnet nuget push artifacts/NanoServer.1.0.0.nupkg \
  --api-key <临时 key> \
  --source https://api.nuget.org/v3/index.json
```

`--skip-duplicate` 表示版本已存在时不报错（用于幂等重跑）。

---

## 五、发布后验证

```bash
# 1. 等 5–15 分钟（NuGet 索引有延迟）后查包是否存在
curl -sS https://api.nuget.org/v3-flatcontainer/nanoserver/index.json

# 2. 在临时工程里实测安装
mkdir /tmp/verify && cd /tmp/verify
dotnet new console
dotnet add package NanoServer
```

或直接访问 https://www.nuget.org/packages/NanoServer

---

## 六、常见问题

| 现象 | 原因 | 处理 |
|---|---|---|
| `403 Forbidden` | 策略 Scopes 不足 | 确认含 `Push new packages and package versions` |
| `409 Conflict` | 该版本号已存在 | NuGet 不允许覆盖，改版本号重发 |
| `401 Unauthorized` / `bearer token could not be parsed` | `NUGET_USER` 未配置或填了邮箱 | 填 nuget.org 用户名（profile name） |
| OIDC 令牌交换被拒 | 策略与 workflow 不匹配 | 逐项核对：workflow 文件名、仓库名、environment 名 |
| `No matching trust policy` | 策略的 Glob 不匹配包名 | 确认 Glob 为 `NanoServer*` |
| workflow 卡在安全回归 | 服务启动慢或端口占用 | 查看日志，workflow 有 20 秒等待上限 |
| 包推上去但搜不到 | 索引延迟 | 等 15 分钟，或直接访问包页面 URL |
| `TreatWarningsAsErrors` 报 NU1900 | 这是环境性 NuGet 警告 | workflow 已限定只把 CS 警告视为错误 |

---

## 七、版本号建议

遵循[语义化版本](https://semver.org/lang/zh-CN/)：

| 变更类型 | 版本位 | 例子 |
|---|---|---|
| 破坏性 API 变更 | MAJOR | 改了 `MapGet` 签名 → 2.0.0 |
| 新增功能（向后兼容） | MINOR | 加了 `UseXxx` 中间件 → 1.1.0 |
| Bug 修复 | PATCH | 修了 Range 请求越界 → 1.0.1 |

> **本项目已做过一次破坏性重命名**（LiteServer → NanoServer，见 git 历史 `704734c`）。
> 首次发布即为 1.0.0，无需体现该次重命名。
