# 发布指南

本项目通过 GitHub Actions 自动发布到 NuGet.org。本文说明**你需要手动做的一次性配置**与发布流程。

---

## 一、一次性配置（必须）

### 1. 在 NuGet.org 创建 API Key

1. 登录 https://www.nuget.org/ ，用 GitHub 账号（cnefan31）即可
2. 进入 **Account → API keys → Create**
3. 按以下填写：

| 字段 | 值 |
|---|---|
| Key name | `github-actions-nanoserver` |
| Expiration | 建议 365 天（到期前需更换） |
| Scopes | **Push new packages and package versions** |
| Glob pattern | `NanoServer` |

> **Glob pattern 只填 `NanoServer`**，不要用 `*`——限制该 key 只能推这一个包，泄露时影响面最小。

4. 创建后**立刻复制** key（形如 `oy2abc...`，只显示一次）

### 2. 在 GitHub 仓库添加 Secret

1. 打开 https://github.com/cnefan31/lite-server/settings/secrets/actions
2. **New repository secret**
3. Name 填 `NUGET_API_KEY`（大小写必须完全一致）
4. Secret 粘贴上一步的 key

> workflow 里引用的是 `${{ secrets.NUGET_API_KEY }}`，名称不匹配会推送失败。

### 3. 确认包名可用性（建议定期复查）

```bash
# 200 = 已占用，404 = 可用
curl -sS -o /dev/null -w "%{http_code}\n" https://api.nuget.org/v3-flatcontainer/nanoserver/index.json
```

当前状态：**404（可用）**。若日后被占用，需改 `PackageId` 并重新配置。

---

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
> （NuGet 上未留下任何版本）。**配置好 `NUGET_API_KEY` 后即可正式发布。**

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

## 四、手动发布（workflow 出问题时的兜底）

```bash
dotnet nuget push artifacts/NanoServer.1.0.0.nupkg \
  --api-key <你的 key> \
  --source https://api.nuget.org/v3/index.json

# 符号包
dotnet nuget push artifacts/NanoServer.1.0.0.snupkg \
  --api-key <你的 key> \
  --source https://api.nuget.org/v3/index.json
```

`--skip-duplicate` 可加在末尾：版本已存在时不报错（用于幂等重跑）。

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
| `403 Forbidden` | API key 无权限或 Glob 不匹配 | 检查 key 的 Glob pattern 是否为 `NanoServer` |
| `409 Conflict` | 该版本号已存在 | NuGet 不允许覆盖，改版本号重发 |
| `401 Unauthorized` | secret 未配置或名称拼错 | 确认 Secrets 里有 `NUGET_API_KEY` |
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
