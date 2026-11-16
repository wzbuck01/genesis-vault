"""
GMKtec seed HTTP client — bootstrap artifact for CCR sessions.
Wraps T2 HMAC auth to call the Genesis.Seed.Web MCP service on GMKtec.
Direct HTTP to gmktec.exponential-systems.net — no tunnel required (VERIFIED 2026-11-09).
T2 HMAC Bearer SID: genesis-gmktec-2026

Fetch in a new session:
  curl -fsSL https://raw.githubusercontent.com/wzbuck01/genesis-vault/main/gmktec_client.py \
    -o /home/claude/scratchpad/gmktec_client.py

SQL EXECUTION PATTERNS (ranked by reliability — 2026-11-16):

  PREFERRED — run_ps_sql(sql): PS @"..."@ here-string piped to sqlcmd stdin.
    Single exec_cmd call, no file I/O, handles SQL up to ~3KB reliably.

  AVOID — FileStream::Append via EncodedCommand: KNOWN UNRELIABLE on GMKtec.
    Append calls silently drop (return success signal but write 0 bytes).
    Use write_sql_b64() with chunk_size > total_b64_length to force single-chunk
    WriteAllBytes only — but EncodedCommand > ~3400 chars hangs the remote PS host,
    limiting reliable single-chunk writes to ~600 bytes of SQL content.
    Bottom line: for any SQL that fits in a PS here-string, use run_ps_sql().
"""
import hashlib, hmac, time, uuid, json, http.client, io, base64

SEED_SIGNING_SECRET = "0dd0f019b05c16924b2c943a2dce65c9e8c231f889ddb5cddd31f01fdf4d7d6e"
BEARER_SID         = "genesis-gmktec-2026"
GMKTEC_HOST        = "gmktec.exponential-systems.net"
GMKTEC_PORT        = 80
MCP_PATH           = f"/mcp/{BEARER_SID}"


def _t2_bearer(path: str, body_bytes: bytes) -> str:
    nonce = uuid.uuid4().hex
    ts    = str(int(time.time()))
    body_hash = hashlib.sha256(body_bytes).hexdigest()
    msg   = f"POST:{path}:{nonce}:{ts}:{body_hash}"
    key   = SEED_SIGNING_SECRET.encode("utf-8")
    sig   = hmac.new(key, msg.encode("utf-8"), hashlib.sha256).hexdigest()
    return f"{BEARER_SID}:{nonce}:{ts}:{sig}"


def _post(path: str, payload: dict) -> dict:
    body = json.dumps(payload).encode("utf-8")
    bearer = _t2_bearer(path, body)
    conn = http.client.HTTPConnection(GMKTEC_HOST, GMKTEC_PORT, timeout=45)
    conn.request("POST", path, body=body, headers={
        "Content-Type": "application/json",
        "Authorization": f"Bearer {bearer}",
    })
    resp = conn.getresponse()
    raw = b""
    # SSE chunked — drain until connection closes; tolerate IncompleteRead
    try:
        while True:
            chunk = resp.read(4096)
            if not chunk:
                break
            raw += chunk
    except Exception:
        pass
    finally:
        try:
            conn.close()
        except Exception:
            pass
    # SSE lines: "data: {...}\n"
    text = raw.decode("utf-8", errors="replace")
    for line in text.splitlines():
        line = line.strip()
        if line.startswith("data:"):
            try:
                return json.loads(line[5:].strip())
            except Exception:
                pass
    # Fallback: try raw JSON
    try:
        return json.loads(text)
    except Exception:
        return {"raw": text}


def call(tool: str, args: dict) -> dict:
    payload = {
        "jsonrpc": "2.0", "id": 1,
        "method": "tools/call",
        "params": {"name": tool, "arguments": args}
    }
    return _post(MCP_PATH, payload)


def _extract_output(r: dict) -> str:
    try:
        return r["result"]["structuredContent"]["output"]
    except (KeyError, TypeError):
        pass
    try:
        for c in r["result"]["content"]:
            if c.get("type") == "text":
                return c["text"]
    except (KeyError, TypeError):
        pass
    return json.dumps(r)


def exec_cmd(cmd: str, retries: int = 2, delay: float = 2.0) -> str:
    """Run a command on GMKtec via seed_exec with retry on empty/error responses.

    Default retries=2, delay=2.0 caps worst-case wait at ~92s (45+2+45).
    Use retries=1 for quick probes or when a 45s timeout is already acceptable.
    Empty-raw responses ({"raw": ""}) are retried; other non-empty responses
    (including error text) are returned immediately.
    """
    import time as _time
    last = ""
    for attempt in range(1, retries + 1):
        try:
            r = call("seed_exec", {"command": cmd})
            out = _extract_output(r)
            # {"raw": ""} means empty response — retry
            if out and out.strip() and out.strip() != '{"raw": ""}':
                return out
            last = out
        except Exception as e:
            last = f"exc:{e}"
        if attempt < retries:
            _time.sleep(delay * attempt)
    return last


def _esc_ps(s: str) -> str:
    """Escape a string for embedding in a PowerShell @"..."@ here-string.
    Only $ and ` need escaping inside double-quoted PS here-strings."""
    return str(s).replace('`', '``').replace('$', '`$')


def run_ps_sql(sql: str, db: str = "GenesisForensics",
               server: str = r"localhost\SQLEXPRESS",
               user: str = "genesis_seed",
               password: str = "G3n3s1s!S33d#2026",
               retries: int = 1) -> str:
    """Execute SQL inline via PowerShell here-string piped to sqlcmd stdin.

    Preferred pattern for GenesisForensics SQL execution — avoids file I/O
    entirely and is reliably resistant to GMKtec's FileStream::Append drop bug.
    Handles SQL up to ~3KB. For larger SQL, use write_sql_b64() with a
    chunk_size large enough to fit all base64 in a single WriteAllBytes call
    (EncodedCommand must stay under ~3400 chars; that limits content to ~600
    bytes — so for >600-byte SQL, run_ps_sql is the only reliable option).

    SQL may contain N'...' literals and JSON braces freely. Escape rules:
    only $ (-> `$) and backtick (-> ``) need escaping inside @"..."@.
    Use _esc_ps() for any dynamic string inserted into the SQL.

    Returns raw sqlcmd output. Check for 'exit:0' or '(N rows affected)'.
    """
    sql_ps = _esc_ps(sql)
    ps_script = (
        f'@"\n{sql_ps}\n"@ | sqlcmd -S {server} -U {user} '
        f'-P "{password}" -d {db} -h -1 -W\n'
        f'Write-Output "exit:$LASTEXITCODE"'
    )
    enc = base64.b64encode(ps_script.encode('utf-16-le')).decode()
    return exec_cmd(f'powershell -EncodedCommand {enc}', retries=retries)


def health() -> dict:
    """Check GMKtec seed health (no-auth)."""
    conn = http.client.HTTPConnection(GMKTEC_HOST, GMKTEC_PORT, timeout=15)
    conn.request("GET", "/health")
    resp = conn.getresponse()
    data = resp.read()
    conn.close()
    return json.loads(data)


if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1:
        cmd = " ".join(sys.argv[1:])
        print(exec_cmd(cmd))
    else:
        print(health())
