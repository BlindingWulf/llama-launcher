"""ollama_shim.py - a tiny Ollama-compatible server in front of LlamaLauncher (PolyStrata or llama-server).

Apps that only speak Ollama talk to http://127.0.0.1:11434; each request is translated to the OpenAI API of the
server LlamaLauncher is running.  Its port and API key are read from the launcher's settings.json, re-read when that
file changes, so a new key, port or model is picked up without restarting this.  Whatever model name an app asks for
goes to the model that is loaded (names the server lists go to that model), so this never makes the server switch.

Standard library only: no pip install, no models or engine of its own.

    pythonw ollama_shim.py                      # in the background: 127.0.0.1:11434 -> the launcher's server
    python  ollama_shim.py                      # the same with the log in this window
    python  ollama_shim.py --lan                # also other devices on the network (no API key, like Ollama)
    python  ollama_shim.py --upstream http://127.0.0.1:8081 --api-key KEY      # any other OpenAI-compatible server
"""
from __future__ import annotations

import argparse
import base64
import fnmatch
import hashlib
import hmac
import http.client
import json
import logging
import logging.handlers
import os
import re
import sys
import time
import urllib.parse
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

OLLAMA_VERSION = "0.13.0"           # what /api/version reports: new enough for apps that check for tools/thinking
SETTINGS = os.path.join(os.environ.get("LOCALAPPDATA", ""), "LlamaLauncher", "settings.json")
HERE = os.path.dirname(os.path.abspath(__file__))
log = logging.getLogger("ollama-shim")


def now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


STARTED = now()


class UpstreamDown(Exception):
    """The launcher's server could not be reached (nothing is loaded, or it is starting)."""


class UpstreamError(Exception):
    """The server failed a request after its answer had started."""


class BadRequest(Exception):
    pass


class Upstream:
    """The OpenAI-compatible server: from the flags, else LlamaLauncher's settings.json (Port, ApiKey)."""

    def __init__(self, url: str | None = None, key: str | None = None):
        self.url, self.key, self.settings = url, key, SETTINGS
        self.client_key_flag: str | None = None     # --require-key
        self.debug_flag = False                     # --debug
        self._mtime, self._settings = None, {}
        self._models, self._models_at = [], -1e9

    def _read(self) -> dict:
        try:
            mtime = os.path.getmtime(self.settings)
            if mtime != self._mtime:
                with open(self.settings, encoding="utf-8-sig") as f:
                    self._settings = json.load(f)
                self._mtime = mtime
        except (OSError, ValueError) as e:          # mid-replacement or no launcher: keep what was read last
            if self._mtime is None:
                log.warning("could not read %s (%s); using port 8080 without a key", self.settings, e)
                self._mtime = 0
        return self._settings

    def target(self) -> tuple[str, int, str]:
        s = self._read() if self.url is None or self.key is None else {}
        u = urllib.parse.urlsplit(self.url or f"http://127.0.0.1:{s.get('Port') or 8080}")
        key = self.key if self.key is not None else s.get("ApiKey") or ""
        return u.hostname or "127.0.0.1", u.port or 80, key

    def client_key(self) -> str:
        """The key Ollama clients must send ("" for none): --require-key, else the launcher's OllamaApiKey while its
        OllamaRequireKey is on, re-read when settings.json changes."""
        if self.client_key_flag is not None:
            return self.client_key_flag
        s = self._read()
        return str(s.get("OllamaApiKey") or "") if s.get("OllamaRequireKey") else ""

    def debug(self) -> bool:
        """The debug log (--debug, or the launcher's OllamaDebugLog): one line of request details per request, for
        troubleshooting. Never message text: no prompts, answers, images, tool arguments or upstream error text."""
        return self.debug_flag or bool(self._read().get("OllamaDebugLog"))

    def request(self, method: str, path: str, body=None, timeout: float = 3600):
        host, port, key = self.target()
        headers = {"Accept": "application/json, text/event-stream"}
        if key:
            headers["Authorization"] = f"Bearer {key}"
        if body is not None:
            body = body if isinstance(body, bytes) else json.dumps(body, ensure_ascii=False).encode()
            headers["Content-Type"] = "application/json"
        conn = http.client.HTTPConnection(host, port, timeout=timeout)
        try:
            conn.request(method, path, body, headers)
            return conn, conn.getresponse()
        except OSError as e:
            conn.close()
            raise UpstreamDown(f"LlamaLauncher's server is not reachable at http://{host}:{port} ({e}). "
                               "Start a model in LlamaLauncher.") from e

    def get_json(self, path: str) -> dict:
        try:
            conn, r = self.request("GET", path, timeout=5)
        except UpstreamDown:
            return {}
        try:
            data = json.loads(r.read() or b"{}") if r.status == 200 else {}
            return data if isinstance(data, dict) else {}
        except (OSError, ValueError):
            return {}
        finally:
            conn.close()

    def models(self) -> list[dict]:
        """The loaded models, cached for a few seconds: [{"id", "n_ctx", "size", "params", "vision"}]."""
        if time.monotonic() - self._models_at < 5:
            return self._models
        out, props = [], None
        for m in self.get_json("/v1/models").get("data") or []:
            status = m.get("status")
            if isinstance(status, dict) and status.get("value") not in (None, "loaded"):
                continue                            # a router's other models: asking for them would load them
            meta = m.get("meta") or {}
            mods = (m.get("architecture") or {}).get("input_modalities")
            n_ctx = meta.get("n_ctx")
            if mods is None or not n_ctx:           # llama-server says these in /props
                props = self.get_json("/props") if props is None else props
                if mods is None:
                    mods = ["image"] if (props.get("modalities") or {}).get("vision") else []
                n_ctx = n_ctx or (props.get("default_generation_settings") or {}).get("n_ctx") or meta.get("n_ctx_train")
            out.append({"id": m["id"], "n_ctx": n_ctx or 0, "size": meta.get("size") or 0,
                        "params": meta.get("n_params") or 0, "vision": "image" in mods})
        self._models, self._models_at = out, time.monotonic()
        return out

    def resolve(self, name) -> dict:
        """The loaded model a request runs on: the one it names (with or without ":latest"), else the first."""
        models = self.models()
        bare = name.removesuffix(":latest") if isinstance(name, str) else ""
        for m in models:
            if bare == m["id"] or name == m["id"]:
                return m
        return models[0] if models else {"id": bare, "n_ctx": 0, "size": 0, "params": 0, "vision": False}


UP = Upstream()


# ---- model metadata in Ollama's shapes ----

FAMILIES = (("qwen", "qwen3"), ("gemma", "gemma3"), ("llama", "llama"), ("mistral", "mistral"), ("phi", "phi3"),
            ("glm", "glm4"), ("deepseek", "deepseek2"), ("gpt-oss", "gptoss"), ("lfm", "lfm2"))
QUANT = re.compile(r"(?i)(?:^|[-_.])((?:ud-)?(?:iq\d_[a-z]+|q\d_k(?:_[a-z]+)?|q\d_\d|bf16|f16|f32|nvfp4|mxfp4))(?=$|[-_.])")
PARAMS = re.compile(r"(?i)(?<![\d.])(\d+(?:\.\d+)?)b(?![a-z])")


def tag(model_id: str) -> str:
    return model_id if ":" in model_id else model_id + ":latest"


def family(model_id: str) -> str:
    n = model_id.lower()
    return next((fam for key, fam in FAMILIES if key in n), "llama")


def details(m: dict) -> dict:
    fam, q, p = family(m["id"]), QUANT.search(m["id"]), PARAMS.search(m["id"])
    size = f"{m['params'] / 1e9:.1f}B" if m["params"] else (p.group(1) + "B" if p else "")
    return {"parent_model": "", "format": "gguf", "family": fam, "families": [fam], "parameter_size": size,
            "quantization_level": q.group(1).upper() if q else ""}


def listing(m: dict) -> dict:
    return {"name": tag(m["id"]), "model": tag(m["id"]), "modified_at": STARTED, "size": m["size"],
            "digest": hashlib.sha256(m["id"].encode()).hexdigest(), "details": details(m)}


# ---- requests: Ollama -> OpenAI ----

MIME = (("iVBOR", "image/png"), ("/9j/", "image/jpeg"), ("R0lGOD", "image/gif"), ("UklGR", "image/webp"), ("Qk", "image/bmp"))
OPTIONS = ("temperature", "top_p", "top_k", "min_p", "seed", "stop", "repeat_penalty", "presence_penalty",
           "frequency_penalty")


def image_part(img: str) -> dict:
    url = img if img.startswith("data:") else \
        "data:%s;base64,%s" % (next((t for p, t in MIME if img.startswith(p)), "image/png"), img)
    return {"type": "image_url", "image_url": {"url": url}}


def openai_messages(msgs) -> list[dict]:
    """Ollama's messages (images beside the text, tool-call arguments as objects, tool results without ids)."""
    out, unanswered, n = [], [], 0
    for m in msgs or []:
        if not isinstance(m, dict):
            raise BadRequest("each message must be an object")
        role, text, images = m.get("role") or "user", m.get("content") or "", m.get("images") or []
        o = {"role": role,
             "content": ([{"type": "text", "text": text}] if text else []) + [image_part(i) for i in images]
             if images else text}
        if m.get("thinking"):
            o["reasoning_content"] = m["thinking"]
        if m.get("tool_calls"):
            o["tool_calls"] = []
            for tc in m["tool_calls"]:
                f, n = tc.get("function") or {}, n + 1
                args, cid = f.get("arguments", {}), tc.get("id") or f"call_{n}"
                o["tool_calls"].append({"id": cid, "type": "function", "function": {
                    "name": f.get("name", ""), "arguments": args if isinstance(args, str) else json.dumps(args)}})
                unanswered.append((cid, f.get("name", "")))
        if role == "tool":                          # pair it with the oldest unanswered call (of that name)
            name = m.get("tool_name") or m.get("name") or ""
            i = next((k for k, (_, nm) in enumerate(unanswered) if nm == name), 0 if unanswered else None)
            if m.get("tool_call_id"):
                o["tool_call_id"] = m["tool_call_id"]
                unanswered = [u for u in unanswered if u[0] != m["tool_call_id"]]
            elif i is not None:
                o["tool_call_id"] = unanswered.pop(i)[0]
            else:
                n += 1
                o["tool_call_id"] = f"call_{n}"
            if name:
                o["name"] = name
        out.append(o)
    return out


def openai_request(req: dict, model_id: str, messages: list[dict]) -> dict:
    body = {"model": model_id, "messages": messages, "stream": True}
    opts = req.get("options") if isinstance(req.get("options"), dict) else {}
    body.update({k: opts[k] for k in OPTIONS if opts.get(k) is not None})
    if isinstance(opts.get("num_predict"), int) and opts["num_predict"] > 0:
        body["max_tokens"] = opts["num_predict"]
    if req.get("tools"):
        body["tools"] = req["tools"]
    fmt = req.get("format")
    if fmt == "json":
        body["response_format"] = {"type": "json_object"}
    elif isinstance(fmt, dict):
        body["response_format"] = {"type": "json_schema", "json_schema": {"name": "response", "schema": fmt}}
    think = req.get("think")                        # unset or true: the server's own thinking setting
    if think is False:
        body["chat_template_kwargs"] = {"enable_thinking": False}
    elif isinstance(think, str) and think:
        body["reasoning_effort"] = think            # "low", "medium", "high"
    return body


def error_text(raw: bytes) -> str:
    try:
        e = json.loads(raw).get("error")
        return (e.get("message") if isinstance(e, dict) else e) or raw.decode(errors="replace")
    except (ValueError, AttributeError):
        return raw.decode(errors="replace").strip() or "upstream error"


def stream_events(resp):
    """The upstream's SSE answer as ("content" | "thinking", text) pieces, then ("end", (finish, usage, calls))."""
    calls, finish, usage = {}, "stop", {}
    try:
        for line in iter(resp.readline, b""):
            line = line.strip()
            if not line.startswith(b"data:"):
                continue                            # blank lines and ": keep-alive" comments
            data = line[5:].strip()
            if data == b"[DONE]":
                break
            try:
                chunk = json.loads(data)
            except ValueError:
                continue
            if chunk.get("error"):
                raise UpstreamError(error_text(data))
            usage = chunk.get("usage") or usage
            if not chunk.get("usage") and isinstance(chunk.get("timings"), dict):    # llama-server's counts
                t = chunk["timings"]
                usage = {"prompt_tokens": t.get("prompt_n") or 0, "completion_tokens": t.get("predicted_n") or 0}
            for ch in chunk.get("choices") or []:
                d = ch.get("delta") or {}
                if d.get("reasoning_content") or d.get("reasoning"):
                    yield "thinking", d.get("reasoning_content") or d.get("reasoning")
                if d.get("content"):
                    yield "content", d["content"]
                for tc in d.get("tool_calls") or []:    # arguments arrive in pieces: merged by index
                    c, f = calls.setdefault(tc.get("index", len(calls)), {"name": "", "arguments": ""}), tc.get("function") or {}
                    c["name"] = c["name"] or f.get("name") or ""
                    c["arguments"] += f.get("arguments") or ""
                finish = ch.get("finish_reason") or finish
    except OSError as e:
        raise UpstreamError(f"the connection to LlamaLauncher's server was lost ({e})") from e
    yield "end", (finish, usage, [calls[i] for i in sorted(calls)])


def ollama_calls(calls: list[dict]) -> list[dict]:
    out = []
    for i, c in enumerate(calls):
        try:
            args = json.loads(c["arguments"] or "{}")
        except ValueError:
            if UP.debug():
                log.info("tool call %s had arguments that are not JSON (%d chars); sent as {}", c["name"], len(c["arguments"]))
            args = {}
        out.append({"function": {"index": i, "name": c["name"], "arguments": args}})
    return out


# ---- the HTTP server ----

LOCAL_ORIGIN = re.compile(r"(?i)^(?:https?://(?:localhost|127\.0\.0\.1|\[::1\]|0\.0\.0\.0)(?::\d+)?"
                          r"|(?:app|file|tauri|vscode-webview|vscode-file)://.*)$")
EXTRA_ORIGINS = [o.strip() for o in os.environ.get("OLLAMA_ORIGINS", "").split(",") if o.strip()]
# The /v1 paths Ollama itself serves. Everything else the launcher's server has under /v1 (PolyStrata's load, unload,
# VRAM and status controls) stays behind its own key. A model id is one plain segment: no "..", "%" or "\".
V1_ALLOWED = re.compile(r"^(?:POST /v1/(?:chat/completions|completions|embeddings)|GET /v1/models(?:/[\w.:+-]+)?)$")
CLIENT_GONE = (BrokenPipeError, ConnectionResetError, ConnectionAbortedError)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "ollama-shim"
    sys_version = ""
    timeout = 300                                   # idle keep-alive connections

    def version_string(self):
        return "ollama-shim/1"                      # how the launcher tells this from Ollama itself

    def log_message(self, fmt, *args):
        """Request lines only in the debug log, and never "GET /" (the launcher's check every few seconds)."""
        if UP.debug() and not (self.command == "GET" and self.path == "/"):
            log.info("%s %s", self.address_string(), fmt % args)

    # -- plumbing --

    def origin_ok(self) -> bool:
        """Ollama's rule: browsers only from local pages and app shells, plus OLLAMA_ORIGINS."""
        o = self.headers.get("Origin")
        return not o or bool(LOCAL_ORIGIN.match(o)) or any(fnmatch.fnmatchcase(o, p) for p in EXTRA_ORIGINS)

    def authorized(self) -> bool:
        """The optional API key: "Authorization: Bearer KEY", "X-API-Key: KEY", or Basic auth with KEY as the password
        (a URL like http://ollama:KEY@host:11434, for apps that only take a URL)."""
        key = UP.client_key()
        if not key:
            return True
        scheme, _, value = self.headers.get("Authorization", "").partition(" ")
        if scheme.lower() == "basic":
            try:
                value = base64.b64decode(value.strip()).decode().partition(":")[2]
            except (ValueError, UnicodeDecodeError):
                value = ""
        elif scheme.lower() != "bearer":
            value = self.headers.get("X-API-Key", "")
        return hmac.compare_digest(value.strip().encode(), key.encode())

    def cors(self):
        if self.headers.get("Origin"):
            self.send_header("Access-Control-Allow-Origin", self.headers["Origin"])
            self.send_header("Vary", "Origin")

    def body(self) -> bytes:
        if self.headers.get("Transfer-Encoding", "").lower() == "chunked":
            parts = []
            while size := int(self.rfile.readline().split(b";")[0].strip() or b"0", 16):
                parts.append(self.rfile.read(size))
                self.rfile.readline()
            while self.rfile.readline() not in (b"\r\n", b"\n", b""):
                pass
            return b"".join(parts)
        n = int(self.headers.get("Content-Length") or 0)
        return self.rfile.read(n) if n > 0 else b""

    def json_body(self) -> dict:
        raw = self.body()
        try:
            req = json.loads(raw) if raw.strip() else {}
        except ValueError:
            raise BadRequest("invalid JSON in the request body") from None
        if not isinstance(req, dict):
            raise BadRequest("the request body must be a JSON object")
        return req

    def reply(self, status: int, obj, ctype="application/json; charset=utf-8"):
        data = obj if isinstance(obj, bytes) else json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.cors()
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(data)

    def start_stream(self, status=200, ctype="application/x-ndjson"):
        self.send_response(status)
        self.send_header("Content-Type", ctype)
        self.send_header("Transfer-Encoding", "chunked")
        self.cors()
        self.end_headers()

    def write_chunk(self, data: bytes):
        if data:
            self.wfile.write(b"%x\r\n%s\r\n" % (len(data), data))
            self.wfile.flush()

    def ndjson(self, obj):
        self.write_chunk(json.dumps(obj, ensure_ascii=False).encode() + b"\n")

    def end_stream(self):
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()

    # -- routing --

    def do_GET(self):
        self.route("GET")

    def do_HEAD(self):
        self.route("HEAD")

    def do_POST(self):
        self.route("POST")

    def do_DELETE(self):
        self.route("DELETE")

    def do_OPTIONS(self):
        if not self.origin_ok():
            return self.reply(403, {"error": "origin not allowed"})
        self.send_response(204)
        self.cors()
        self.send_header("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, HEAD, OPTIONS")
        self.send_header("Access-Control-Allow-Headers", self.headers.get("Access-Control-Request-Headers") or
                         "Authorization, Content-Type, User-Agent, Accept, X-Requested-With")
        self.send_header("Content-Length", "0")
        self.end_headers()

    def route(self, method: str):
        path = urllib.parse.urlsplit(self.path).path.rstrip("/") or "/"
        try:
            if not self.origin_ok():
                self.body()
                return self.reply(403, {"error": "origin not allowed (add it to OLLAMA_ORIGINS)"})
            if path != "/" and not self.authorized():  # "Ollama is running" stays open: apps probe it
                self.body()
                return self.reply(401, {"error": "unauthorized: this Ollama API needs its API key "
                                                 "(Authorization: Bearer <key>)"})
            if path.startswith("/v1/") and method != "HEAD":
                if not V1_ALLOWED.match(f"{method} {path}"):  # not the server's load/unload/VRAM/status controls
                    self.body()
                    return self.reply(404, b"404 page not found", "text/plain")
                return self.proxy(method, path)
            req = self.json_body() if method in ("POST", "DELETE") else {}
            handler = ROUTES.get(("GET" if method == "HEAD" else method, path))
            if handler is None:
                return self.reply(404, b"404 page not found", "text/plain")
            handler(self, req)
        except BadRequest as e:
            self.reply(400, {"error": str(e)})
        except UpstreamDown as e:
            log.warning("%s", e)
            self.reply(503, {"error": str(e)})
        except CLIENT_GONE:
            if UP.debug():
                log.info("%s left before the answer was complete", self.address_string())
            self.close_connection = True

    # -- Ollama API --

    def root(self, req):
        self.reply(200, b"Ollama is running", "text/plain; charset=utf-8")

    def version(self, req):
        self.reply(200, {"version": OLLAMA_VERSION})

    def tags(self, req):
        self.reply(200, {"models": [listing(m) for m in UP.models()]})

    def ps(self, req):
        self.reply(200, {"models": [{**listing(m), "expires_at": "2099-12-31T23:59:59Z", "size_vram": m["size"],
                                     "context_length": m["n_ctx"]} for m in UP.models()]})

    def show(self, req):
        m = UP.resolve(req.get("model") or req.get("name"))
        if not m["id"]:
            return self.reply(404, {"error": "no model is loaded in LlamaLauncher"})
        fam = details(m)["family"]
        info = {"general.architecture": fam, "general.basename": m["id"], "general.parameter_count": m["params"]}
        if m["n_ctx"]:
            info[f"{fam}.context_length"] = m["n_ctx"]
        self.reply(200, {"modelfile": f"# served by ollama-shim from LlamaLauncher\nFROM {m['id']}\n",
                         "parameters": "", "template": "{{ .Prompt }}", "details": details(m), "model_info": info,
                         "capabilities": ["completion", "tools", "thinking"] + (["vision"] if m["vision"] else []),
                         "modified_at": STARTED})

    def chat(self, req, generate=False):
        m = UP.resolve(req.get("model"))
        name = req.get("model") or tag(m["id"])
        if generate:
            prompt, images = req.get("prompt") or "", req.get("images") or []
            msgs = ([{"role": "system", "content": req["system"]}] if req.get("system") else []) + \
                   [{"role": "user", "content": prompt, "images": images}]
            empty = not (prompt or images)
        else:
            msgs = req.get("messages") or []
            empty = not msgs

        def frame(content="", thinking="", calls=None, done=False):
            if generate:
                f = {"model": name, "created_at": now(), "response": content}
                if thinking:
                    f["thinking"] = thinking
            else:
                msg = {"role": "assistant", "content": content}
                if thinking:
                    msg["thinking"] = thinking
                if calls:
                    msg["tool_calls"] = calls
                f = {"model": name, "created_at": now(), "message": msg}
            f["done"] = done
            return f

        if empty:                                   # Ollama's "load the model" / "unload it" requests
            unload = req.get("keep_alive") in (0, "0", "0s", "0m")
            return self.reply(200, {**frame(done=True), "done_reason": "unload" if unload else "load"})

        stream, start = req.get("stream", True) is not False, time.time_ns()
        conn, resp = UP.request("POST", "/v1/chat/completions", openai_request(req, m["id"], openai_messages(msgs)))
        try:
            if resp.status != 200:
                msg = error_text(resp.read())
                if UP.debug():
                    log.info("/api/%s: the server answered %d", "generate" if generate else "chat", resp.status)
                return self.reply(resp.status, {"error": msg})
            if stream:
                self.start_stream()
            text, thought, first, pieces = [], [], None, 0
            try:
                for kind, val in stream_events(resp):
                    if kind == "end":
                        finish, usage, calls = val
                        break
                    first, pieces = first or time.time_ns(), pieces + 1
                    if stream:
                        self.ndjson(frame(val) if kind == "content" else frame(thinking=val))
                    else:
                        (text if kind == "content" else thought).append(val)
            except UpstreamError as e:                  # its text can quote the request: not logged
                log.warning("/api/%s: the server failed during an answer", "generate" if generate else "chat")
                if not stream:
                    return self.reply(500, {"error": str(e)})
                self.ndjson({"error": str(e)})
                return self.end_stream()
            end = time.time_ns()
            first = first or end
            calls = ollama_calls(calls) if not generate else []
            stats = {"done_reason": "length" if finish == "length" else "stop", "total_duration": end - start,
                     "load_duration": 0, "prompt_eval_count": usage.get("prompt_tokens") or 0,
                     "prompt_eval_duration": first - start, "eval_count": usage.get("completion_tokens") or pieces,
                     "eval_duration": max(end - first, 1)}
            if UP.debug():                              # counts and times only
                log.info("/api/%s model=%s stream=%s messages=%d images=%d tools=%d calls=%d prompt_tokens=%d "
                         "eval_tokens=%d total_ms=%d first_token_ms=%d done=%s", "generate" if generate else "chat",
                         m["id"], stream, len(msgs), sum(len(x.get("images") or []) for x in msgs if isinstance(x, dict)),
                         len(req.get("tools") or []), len(calls), stats["prompt_eval_count"], stats["eval_count"],
                         (end - start) // 1_000_000, (first - start) // 1_000_000, stats["done_reason"])
            if not stream:
                return self.reply(200, {**frame("".join(text), "".join(thought), calls, done=True), **stats})
            if calls:
                self.ndjson(frame(calls=calls))
            self.ndjson({**frame(done=True), **stats})
            self.end_stream()
        finally:
            conn.close()                            # a client that left stops the generation upstream too

    def generate(self, req):
        self.chat(req, generate=True)

    def embed(self, req, legacy=False):
        m, start = UP.resolve(req.get("model")), time.time_ns()
        conn, resp = UP.request("POST", "/v1/embeddings",
                                {"model": m["id"], "input": req.get("prompt") if legacy else req.get("input")})
        try:
            raw = resp.read()
        finally:
            conn.close()
        if resp.status != 200:
            return self.reply(resp.status, {"error": "the loaded server does not do embeddings: " + error_text(raw)})
        data = json.loads(raw or b"{}")
        vecs = [d["embedding"] for d in sorted(data.get("data") or [], key=lambda d: d.get("index", 0))]
        if legacy:
            return self.reply(200, {"embedding": vecs[0] if vecs else []})
        self.reply(200, {"model": req.get("model") or tag(m["id"]), "embeddings": vecs,
                         "total_duration": time.time_ns() - start, "load_duration": 0,
                         "prompt_eval_count": (data.get("usage") or {}).get("prompt_tokens") or 0})

    def embeddings(self, req):
        self.embed(req, legacy=True)

    def pull(self, req):                            # nothing to download: the launcher has the model
        if req.get("stream", True) is False:
            return self.reply(200, {"status": "success"})
        self.start_stream()
        self.ndjson({"status": "pulling manifest"})
        self.ndjson({"status": "success"})
        self.end_stream()

    def unsupported(self, req):
        self.reply(501, {"error": "models are managed in LlamaLauncher, not through this Ollama shim"})

    # -- Ollama's OpenAI-compatible /v1 API (V1_ALLOWED only): passed through with the launcher's key --

    def proxy(self, method: str, path: str):
        query = urllib.parse.urlsplit(self.path).query
        raw = self.body() if method == "POST" else None
        if raw:
            try:
                j = json.loads(raw)
                if isinstance(j, dict) and isinstance(j.get("model"), str):
                    j["model"] = UP.resolve(j["model"])["id"] or j["model"]
                    raw = json.dumps(j, ensure_ascii=False).encode()
            except ValueError:
                pass
        conn, resp = UP.request(method, path + ("?" + query if query else ""), raw)
        try:
            self.start_stream(resp.status, resp.getheader("Content-Type") or "application/json")
            while chunk := resp.read1(65536):
                self.write_chunk(chunk)
            self.end_stream()
        finally:
            conn.close()


ROUTES = {
    ("GET", "/"): Handler.root,
    ("GET", "/api/version"): Handler.version,
    ("GET", "/api/tags"): Handler.tags,
    ("GET", "/api/ps"): Handler.ps,
    ("POST", "/api/show"): Handler.show,
    ("POST", "/api/chat"): Handler.chat,
    ("POST", "/api/generate"): Handler.generate,
    ("POST", "/api/embed"): Handler.embed,
    ("POST", "/api/embeddings"): Handler.embeddings,
    ("POST", "/api/pull"): Handler.pull,
    ("POST", "/api/create"): Handler.unsupported,
    ("POST", "/api/copy"): Handler.unsupported,
    ("POST", "/api/push"): Handler.unsupported,
    ("DELETE", "/api/delete"): Handler.unsupported,
}


class Server(ThreadingHTTPServer):
    allow_reuse_address = False                     # on Windows it would let a second copy share the port
    daemon_threads = True

    def handle_error(self, request, client_address):
        if isinstance(sys.exc_info()[1], CLIENT_GONE):
            return                                  # a client dropped an idle connection (the launcher closing, say)
        log.exception("error while answering %s", client_address[0])


def main():
    ap = argparse.ArgumentParser(description="An Ollama-compatible server in front of LlamaLauncher.")
    ap.add_argument("--host", help="address to listen on (default 127.0.0.1, or 0.0.0.0 with --lan)")
    ap.add_argument("--port", type=int, default=11434, help="port to listen on (default 11434, Ollama's)")
    ap.add_argument("--lan", action="store_true", help="listen on every network interface (no API key needed!)")
    ap.add_argument("--upstream", help="OpenAI-compatible server URL (default: LlamaLauncher's port on 127.0.0.1)")
    ap.add_argument("--api-key", help="its API key (default: LlamaLauncher's)")
    ap.add_argument("--require-key", help="API key clients must send (default: the launcher's Ollama key if it is on)")
    ap.add_argument("--debug", action="store_true",
                    help="debug log: request details for troubleshooting, never message text (default: the launcher's "
                         "Ollama debug log switch)")
    ap.add_argument("--settings", default=SETTINGS, help="LlamaLauncher's settings.json (default: %(default)s)")
    ap.add_argument("--log", default=os.path.join(HERE, "ollama-shim.log"), help="log file (default: %(default)s)")
    a = ap.parse_args()

    os.makedirs(os.path.dirname(os.path.abspath(a.log)), exist_ok=True)
    handlers = [logging.handlers.RotatingFileHandler(a.log, maxBytes=512_000, backupCount=1, encoding="utf-8")]
    if sys.stderr is not None:                      # python.exe; pythonw.exe has no console
        handlers.append(logging.StreamHandler())
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s", handlers=handlers)

    UP.url, UP.key, UP.settings, UP.client_key_flag, UP.debug_flag = a.upstream, a.api_key, a.settings, a.require_key, a.debug
    host = a.host or ("0.0.0.0" if a.lan else "127.0.0.1")
    try:
        srv = Server((host, a.port), Handler)
    except OSError as e:
        log.error("cannot listen on %s:%d (%s) - is Ollama or this shim already running?", host, a.port, e)
        sys.exit(1)
    up_host, up_port, key = UP.target()
    log.info("Ollama shim on http://%s:%d -> http://%s:%d (%s; clients %s)", host, a.port, up_host, up_port,
             "with LlamaLauncher's API key" if key else "no API key",
             "need an API key" if UP.client_key() else "need no API key")
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
