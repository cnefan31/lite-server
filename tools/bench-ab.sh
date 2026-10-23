#!/usr/bin/env bash
#
# JIT vs NativeAOT 对照压测。
#
# 用法：./tools/bench-ab.sh [请求数] [并发] [轮数]
#
# 做法：依次启动两种构建、各压一轮、打印对照表。
# 注意两次测量之间机器负载会漂移，故务必用多轮中位数，不要看单次峰值。
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1

N="${1:-2000}"; C="${2:-50}"; R="${3:-3}"
PORT=8090
BASE="http://127.0.0.1:$PORT"
PROJ=samples/LiteServer.Sample

command -v hey >/dev/null 2>&1 || { echo "需要 hey：brew install hey"; exit 1; }

echo "编译两种构建…"
dotnet build "$PROJ" -c Release -v q 2>&1 | grep -E "error" && exit 1
dotnet publish "$PROJ" -c Release -r "$(uname -m | sed 's/arm64/osx-arm64/;s/x86_64/osx-x64/')" -o "$PROJ/dist" -v q 2>&1 | grep -E "error" && exit 1
mkdir -p "$PROJ/dist/wwwroot" && cp "$PROJ"/bin/Release/net10.0/wwwroot/* "$PROJ/dist/wwwroot/" 2>/dev/null

run_one() {
  local label="$1" cmd="$2"
  pkill -f "lite-sample" 2>/dev/null; sleep 1
  eval "$cmd" >/tmp/bench-ab.log 2>&1 &
  for _ in $(seq 1 40); do
    curl -sS -o /dev/null "$BASE/api/health" 2>/dev/null && break
    sleep 0.25
  done

  local rps avg
  rps=$(for _ in $(seq 1 "$R"); do
          hey -n "$N" -c "$C" "$BASE/api/health" 2>&1 | awk '/Requests\/sec/{print $2}'
        done | sort -n | awk '{a[NR]=$1} END{printf "%.0f", a[int((NR+1)/2)]}')
  avg=$(hey -n "$N" -c "$C" "$BASE/api/health" 2>&1 | awk '/^  Average:/{print $2}')
  printf '  %-12s %10s req/s   avg %s\n' "$label" "$rps" "$avg"
  LAST_RPS="$rps"
  pkill -f "lite-sample" 2>/dev/null; sleep 1
}

echo
echo "JIT vs AOT（每轮 $N 请求 / 并发 $C / $R 轮取中位数）"
echo "--------------------------------------------------------"
run_one "JIT" "dotnet $PROJ/bin/Release/net10.0/lite-sample.dll"
JIT_RPS="$LAST_RPS"
run_one "NativeAOT" "./$PROJ/dist/lite-sample"
AOT_RPS="$LAST_RPS"
echo "--------------------------------------------------------"
awk -v j="$JIT_RPS" -v a="$AOT_RPS" 'BEGIN{
  if (j+0>0) printf "  AOT 相对 JIT 提升: %+.1f%%\n", (a-j)/j*100;
}'
echo
echo "说明：AOT 消除了 JIT 编译与预热开销，稳态吞吐通常更高；"
echo "      但差异会随机型与负载浮动，请以同机多次测量的中位数为准。"
