#!/usr/bin/env bash
#
# LiteServer 性能压测脚本。
#
# 前置：安装 hey
#   macOS:  brew install hey
#   其它:   go install github.com/rakyll/hey@latest
#
# 用法：
#   ./tools/bench.sh                              # 默认 http://127.0.0.1:8090
#   ./tools/bench.sh http://127.0.0.1:8090 3000 100
#   ./tools/bench.sh http://127.0.0.1:8090 3000 100 5   # 5 轮取中位数
#
# ⚠️ 关于可信度（重要）：
#   性能数字受「机器负载」影响远大于受代码影响。同一构建连测两次，
#   中位数可能相差 30% 以上。因此本脚本默认多轮采样取中位数，
#   并明确打印每轮原始值——请以中位数为准，不要采信单轮峰值。
#
# ⚠️ 本脚本只测量、不断言。绝对值没有跨机可比性，只能用于同机 A/B 对照。
set -uo pipefail

BASE="${1:-http://127.0.0.1:8090}"
N="${2:-3000}"
C="${3:-100}"
ROUNDS="${4:-5}"

command -v hey >/dev/null 2>&1 || { echo "需要 hey：brew install hey"; exit 1; }

API_KEY="${LITE_BENCH_API_KEY:-lite-sample-secret}"
TOKEN=$(curl -sS -X POST "$BASE/api/login" -d "{\"apiKey\":\"$API_KEY\"}" 2>/dev/null \
        | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')

line() { printf '%s\n' "============================================================================"; }

# 采集 ROUNDS 轮 Requests/sec，输出「中位数 (min~max)」
# 用法: measure <目标 URL> [额外 hey 参数...]
measure() {
  local target="$1"; shift
  local -a vals=()
  local v
  for _ in $(seq 1 "$ROUNDS"); do
    v=$(hey "$@" -n "$N" -c "$C" "$target" 2>&1 | awk '/Requests\/sec/{print $2}')
    [ -n "$v" ] && vals+=("$v")
  done
  if [ "${#vals[@]}" -eq 0 ]; then echo "    测量失败"; return; fi
  printf '%s\n' "${vals[@]}" | sort -n | awk '
    {a[NR]=$1}
    END{ printf "  %8.0f req/s   (min %.0f, max %.0f, n=%d)\n", a[int((NR+1)/2)], a[1], a[NR], NR }'
}

row() { printf '  %-26s' "$1"; }

line
echo "LiteServer 压测   目标 $BASE   每轮 $N 请求 / 并发 $C / $ROUNDS 轮取中位数"
line
printf '  %-26s %s\n' "负载(压测前)" "$(uptime | sed 's/.*load averages: //')"

row "health 基线";              measure "$BASE/api/health"

if [ -n "$TOKEN" ]; then
  row "404 路由未命中";         measure "$BASE/__bench_not_found__" -H "Authorization: Bearer $TOKEN"
fi

row "static 小文件";            measure "$BASE/tiny.txt"

if [ -n "$TOKEN" ]; then
  row "JWT 校验 /api/me";       measure "$BASE/api/me" -H "Authorization: Bearer $TOKEN"
  row "DI 作用域 /api/di";      measure "$BASE/api/di"
fi

row "health 禁用keepalive";     measure "$BASE/api/health" -disable-keepalive

line
printf '  %-26s %s\n' "负载(压测后)" "$(uptime | sed 's/.*load averages: //')"
echo
echo "解读要点："
echo "  · health 与 404 的差值 ≈ 路由匹配成本；两者相近说明查找开销可忽略。"
echo "  · static 比 health 慢是正常的：要 open/read 文件并做 stat。"
echo "  · JWT 比 health 慢的部分 ≈ HMAC-SHA256 验签成本。"
echo "  · keepalive 关闭后吞吐腰斩，说明瓶颈在 TCP 建连而非应用逻辑。"
