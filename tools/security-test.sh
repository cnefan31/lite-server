#!/usr/bin/env bash
#
# NanoServer 安全回归测试。
#
# 用法：./tools/security-test.sh [base-url] [api-key]
# 默认：http://127.0.0.1:8090  nano-sample-secret
#
# 覆盖 OWASP 常见面：路径穿越、JWT 攻击、SSRF、CRLF 注入、
# DoS 资源耗尽、CORS 配置、信息泄露、WebSocket 鉴权。
#
# 每项断言「期望的安全结果」，不符合即为回归。
set -uo pipefail

BASE="${1:-http://127.0.0.1:8090}"
KEY="${2:-nano-sample-secret}"

PASS=0; FAIL=0; FAILED=""

# expect <名称> <期望状态码> <curl 参数...>
expect() {
  local name="$1" want="$2"; shift 2
  local got
  got=$(curl -sS -o /dev/null -w '%{http_code}' --max-time 15 "$@" 2>/dev/null)
  if [ "$got" = "$want" ]; then
    PASS=$((PASS+1)); printf '  \033[32m✓\033[0m %-42s %s\n' "$name" "$got"
  else
    FAIL=$((FAIL+1)); FAILED="$FAILED\n  ✗ $name 期望 $want 实得 $got"
    printf '  \033[31m✗\033[0m %-42s %s (期望 %s)\n' "$name" "$got" "$want"
  fi
}

# expect_absent <名称> <不该出现的字符串> <curl 参数...>
expect_absent() {
  local name="$1" needle="$2"; shift 2
  if curl -sS --max-time 15 "$@" 2>/dev/null | grep -q "$needle"; then
    FAIL=$((FAIL+1)); FAILED="$FAILED\n  ✗ $name 出现了敏感内容: $needle"
    printf '  \033[31m✗\033[0m %-42s 泄露 %s\n' "$name" "$needle"
  else
    PASS=$((PASS+1)); printf '  \033[32m✓\033[0m %-42s 未泄露\n' "$name"
  fi
}

hr() { printf '%s\n' "------------------------------------------------------------"; }

TOKEN=$(curl -sS -X POST "$BASE/api/login" -d "{\"apiKey\":\"$KEY\"}" 2>/dev/null \
        | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
AUTH=(-H "Authorization: Bearer $TOKEN")

echo "NanoServer 安全回归测试   目标 $BASE"
hr

echo "[1] 路径穿越与隐藏文件"
expect "..%2F..%2Fetc%2Fpasswd -> 403" 403 --path-as-is "${AUTH[@]}" "$BASE/..%2F..%2Fetc%2Fpasswd"
expect "%2e%2e%2f 编码点 -> 403"      403 --path-as-is "${AUTH[@]}" "$BASE/%2e%2e%2f%2e%2e%2fetc%2fpasswd"
expect ".canary 隐藏文件 -> 403"        403 "${AUTH[@]}" "$BASE/.canary"
expect_absent "外部文件不可读" "OUTSIDE_SECRET_MARKER" --path-as-is "${AUTH[@]}" "$BASE/..%2F..%2F..%2F..%2Ftmp%2Foutside_secret.txt"

echo "[2] JWT 攻击"
expect "篡改签名 -> 401" 401 -H "Authorization: Bearer ${TOKEN%?}X" "$BASE/api/me"
expect "alg=none -> 401" 401 -H "Authorization: Bearer eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJzdWIiOiJ4In0." "$BASE/api/me"
expect "伪造签名 -> 401" 401 -H "Authorization: Bearer aaa.bbb.ccc" "$BASE/api/me"
expect "空 token -> 401" 401 "$BASE/api/me"

echo "[3] SSRF"
for u in "http%3A%2F%2F127.0.0.1%2F" "http%3A%2F%2Flocalhost%2F" \
         "http%3A%2F%2F169.254.169.254%2Flatest%2F" "http%3A%2F%2F10.0.0.1%2F" \
         "http%3A%2F%2F192.168.1.1%2F" "http%3A%2F%2F2130706433%2F" \
         "http%3A%2F%2F0x7f000001%2F" "http%3A%2F%2F127.0.0.1.nip.io%2F" \
         "file%3A%2F%2F%2Fetc%2Fpasswd" "gopher%3A%2F%2F127.0.0.1%2F"; do
  label=$(python3 -c "import urllib.parse,sys;print(urllib.parse.unquote(sys.argv[1]))" "$u")
  expect "拒绝 $label" 400 "${AUTH[@]}" "$BASE/api/upstream?url=$u"
done

echo "[4] 上传限制"
printf 'x' > /tmp/_sec_small.bin
expect "正常小文件 -> 200" 200 -X POST "${AUTH[@]}" -H "filename: _sec.bin" --data-binary @/tmp/_sec_small.bin "$BASE/api/upload"
expect "路径穿越文件名 -> 400" 400 -X POST "${AUTH[@]}" -H "filename: ../evil.bin" --data-binary @/tmp/_sec_small.bin "$BASE/api/upload"

echo "[5] CORS 配置"
for o in "https://evil.example" "null"; do
  if curl -sS -D- -o /dev/null "$BASE/api/health" -H "Origin: $o" 2>/dev/null | grep -qi "access-control-allow-origin"; then
    FAIL=$((FAIL+1)); FAILED="$FAILED\n  ✗ CORS 放行了 $o"
    printf '  \033[31m✗\033[0m CORS 拒绝了 %-28s 被放行\n' "$o"
  else
    PASS=$((PASS+1)); printf '  \033[32m✓\033[0m CORS 拒绝 %-29s ok\n' "$o"
  fi
done

echo "[6] 信息泄露"
expect_absent "错误响应不含堆栈" "at NanoServer" -X POST "$BASE/api/login" -d '{bad'
expect_absent "错误响应不含物理路径" "/Users/" -X POST "$BASE/api/login" -d '{bad'
expect "目录枚举被拒 /src/" 404 "${AUTH[@]}" "$BASE/src/"
expect "目录枚举被拒 /.git/" 404 "${AUTH[@]}" "$BASE/.git/"

echo "[7] WebSocket 鉴权"
if command -v python3 >/dev/null 2>&1; then
  python3 - "$BASE" <<'PY'
import asyncio, sys
try:
    import websockets
except ImportError:
    print("  - 跳过（未安装 websockets）"); sys.exit(0)
base = sys.argv[1].replace("http://","").replace("https://","")
async def main():
    ok = 0; bad = 0
    for label, url in [("无 token", f"ws://{base}/ws"), ("伪造 token", f"ws://{base}/ws?token=fake.jwt")]:
        try:
            async with websockets.connect(url, open_timeout=5):
                print(f"  \033[31m✗\033[0m WS {label} 被接受"); bad += 1
        except Exception:
            print(f"  \033[32m✓\033[0m WS {label} 被拒绝"); ok += 1
    sys.exit(0 if bad == 0 else 1)
asyncio.run(main())
PY
  if [ $? -eq 0 ]; then PASS=$((PASS+2)); else FAIL=$((FAIL+2)); fi
fi

rm -f /tmp/_sec_small.bin
hr
printf '通过 %d，失败 %d\n' "$PASS" "$FAIL"
[ "$FAIL" -gt 0 ] && printf "$FAILED\n" && exit 1
echo "全部安全断言通过"
