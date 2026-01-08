#!/usr/bin/env python3
import base64
import cgi
import io
import json
import os
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.request import Request, urlopen

HOST = os.environ.get("XP_HOST", "0.0.0.0")
PORT = int(os.environ.get("XP_PORT", "5000"))
DATA_DIR = os.environ.get("XP_DATA_DIR", os.path.join(os.path.dirname(__file__), "..", "data"))
OLLAMA_URL = os.environ.get("OLLAMA_URL", "http://127.0.0.1:11434")
OLLAMA_MODEL = os.environ.get("OLLAMA_MODEL", "llava")
ENABLE_ACTIONS = os.environ.get("ENABLE_ACTIONS", "0") == "1"
USE_VISION = os.environ.get("USE_VISION", "1") == "1"
TASK_GOAL = os.environ.get("TASK_GOAL", "").strip()
MAX_HISTORY = int(os.environ.get("MAX_HISTORY", "6"))

ALLOWED_ACTIONS = {"move", "click", "dblclick", "rclick", "type", "key", "sleep", "noop", "macro", "focus"}
ACTION_HISTORY = []
LAST_FRAME_SIZE = None
LAST_ACTIVE_TITLE = ""


def _log(msg):
    sys.stdout.write(msg + "\n")
    sys.stdout.flush()


def _save_frame(file_bytes):
    os.makedirs(DATA_DIR, exist_ok=True)
    path = os.path.join(DATA_DIR, "last.png")
    with open(path, "wb") as f:
        f.write(file_bytes)
    return path


def _ollama_plan(image_bytes):
    history = "\n".join(ACTION_HISTORY[-MAX_HISTORY:])
    goal = TASK_GOAL if TASK_GOAL else "None"
    prompt = (
        "You are controlling a Windows XP VM via screen and input. "
        "Goal: %s\n"
        "Recent actions:\n%s\n"
        "Active window title: %s\n"
        "Windows XP tips: Start button is bottom-left. Taskbar is along the bottom. "
        "Start menu opens from Start button. Use Run by Start -> Run when needed. "
        "Desktop icons are usually on the left. "
        "Use the current screen to decide the next small step toward the goal. "
        "Avoid right-click unless explicitly needed. "
        "Always move the mouse before any click. "
        "Use integer pixel coordinates for move within the screen bounds. "
        "Prefer macro open_run, then type the app name and press ENTER. "
        "You may also use macro: macro open_run. "
        "Return only action lines, one per line, in this format:\n"
        "move X Y\n"
        "click left|right\n"
        "dblclick left\n"
        "rclick\n"
        "type TEXT\n"
        "key ENTER|TAB|ESC|ALT+F4|CTRL+L\n"
        "focus WINDOW_TITLE_SUBSTRING\n"
        "sleep MS\n"
        "noop\n"
        "No extra text. If unsure, output 'noop'."
    ) % (goal, history if history else "(none)", LAST_ACTIVE_TITLE or "(unknown)")

    payload = {
        "model": OLLAMA_MODEL,
        "prompt": prompt,
        "stream": False,
    }
    if USE_VISION:
        payload["images"] = [base64.b64encode(image_bytes).decode("ascii")]

    req = Request(
        OLLAMA_URL.rstrip("/") + "/api/generate",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
    )
    with urlopen(req, timeout=120) as resp:
        raw = resp.read().decode("utf-8", errors="replace")
        data = json.loads(raw)
        return data.get("response", "noop")


def _start_coords():
    if LAST_FRAME_SIZE:
        w, h = LAST_FRAME_SIZE
    else:
        w, h = 1024, 768
    return max(0, min(w - 1, 20)), max(0, min(h - 1, h - 10))


def _expand_macro(name):
    macro = name.strip().lower()
    actions = []
    if macro == "open_run":
        actions.extend([
            "key WIN+R",
            "sleep 300",
        ])
    return actions


def _parse_actions(text):
    actions = []
    for line in text.splitlines():
        line = line.strip()
        if not line:
            continue
        parts = line.split(" ")
        if not parts:
            continue
        cmd = parts[0].lower()
        if cmd not in ALLOWED_ACTIONS:
            continue
        if cmd == "macro":
            macro_name = line[len("macro"):].strip()
            actions.extend(_expand_macro(macro_name))
        else:
            if cmd == "type":
                text = line[len("type"):].strip()
                if text.startswith("\"") and text.endswith("\"") and len(text) >= 2:
                    text = text[1:-1]
                if text.startswith("'") and text.endswith("'") and len(text) >= 2:
                    text = text[1:-1]
                actions.append("type " + text)
            else:
                actions.append(line)
    if not actions:
        actions = ["noop"]
    # Ensure Run is used for app launch goals.
    if TASK_GOAL.lower().startswith("open ") and not any(
        a.startswith(("macro open_run", "key WIN+R")) for a in actions
    ):
        actions = _expand_macro("open_run") + actions
    # If goal includes typing, click center after launch to focus the app window.
    if "type" in TASK_GOAL.lower() and LAST_FRAME_SIZE:
        cx = LAST_FRAME_SIZE[0] // 2
        cy = LAST_FRAME_SIZE[1] // 2
        if not any(a.startswith(("move ", "click")) for a in actions):
            actions.extend(["move %d %d" % (cx, cy), "click left", "sleep 200"])
    # Require a move before any click to avoid random corner clicks.
    has_move = any(a.startswith("move ") for a in actions)
    if not has_move:
        filtered = []
        for a in actions:
            if a.startswith(("click", "dblclick", "rclick")):
                continue
            filtered.append(a)
        actions = filtered if filtered else ["noop"]
    return actions


def _actions_to_json(actions):
    items = []
    for line in actions:
        parts = line.split(" ", 1)
        cmd = parts[0].lower()
        arg = parts[1] if len(parts) > 1 else ""
        item = {"cmd": cmd}
        if cmd == "move":
            xy = arg.split(" ")
            if len(xy) >= 2:
                try:
                    x = int(float(xy[0]))
                    y = int(float(xy[1]))
                except ValueError:
                    continue
                if LAST_FRAME_SIZE:
                    max_x = max(0, LAST_FRAME_SIZE[0] - 1)
                    max_y = max(0, LAST_FRAME_SIZE[1] - 1)
                    x = max(0, min(max_x, x))
                    y = max(0, min(max_y, y))
                item["x"] = x
                item["y"] = y
        elif cmd == "click":
            item["button"] = arg.strip().lower() or "left"
        elif cmd == "dblclick":
            item["button"] = "left"
        elif cmd == "type":
            item["text"] = arg
        elif cmd == "key":
            item["key"] = arg.strip()
        elif cmd == "focus":
            item["text"] = arg.strip()
        elif cmd == "sleep":
            try:
                item["ms"] = int(arg.strip())
            except ValueError:
                item["ms"] = 0
        items.append(item)
    return {"actions": items}


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/frame":
            self.send_response(404)
            self.end_headers()
            return

        ctype, pdict = cgi.parse_header(self.headers.get("content-type"))
        if ctype != "multipart/form-data":
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b"expected multipart/form-data")
            return

        pdict["boundary"] = pdict["boundary"].encode("utf-8")
        pdict["CONTENT-LENGTH"] = int(self.headers.get("content-length", "0"))
        fields = cgi.parse_multipart(self.rfile, pdict)
        if "frame" not in fields:
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b"missing frame")
            return

        global LAST_FRAME_SIZE
        global LAST_ACTIVE_TITLE
        if "width" in fields and "height" in fields:
            try:
                w = int(fields["width"][0])
                h = int(fields["height"][0])
                if w > 0 and h > 0:
                    LAST_FRAME_SIZE = (w, h)
            except ValueError:
                LAST_FRAME_SIZE = None
        if "active_title" in fields:
            try:
                LAST_ACTIVE_TITLE = fields["active_title"][0]
            except Exception:
                LAST_ACTIVE_TITLE = ""

        image_bytes = fields["frame"][0]
        _save_frame(image_bytes)

        if not ENABLE_ACTIONS:
            actions = ["noop"]
        else:
            try:
                llm_text = _ollama_plan(image_bytes)
            except Exception as exc:
                _log("LLM error: %s" % exc)
                llm_text = "noop"
            actions = _parse_actions(llm_text)
            ACTION_HISTORY.extend(actions)
            if len(ACTION_HISTORY) > MAX_HISTORY * 2:
                del ACTION_HISTORY[: len(ACTION_HISTORY) - MAX_HISTORY * 2]

        body = json.dumps(_actions_to_json(actions)).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        _log("%s - %s" % (self.address_string(), fmt % args))


if __name__ == "__main__":
    _log("Starting XP control host on %s:%d" % (HOST, PORT))
    _log("ENABLE_ACTIONS=%s USE_VISION=%s OLLAMA_MODEL=%s" % (ENABLE_ACTIONS, USE_VISION, OLLAMA_MODEL))
    _log("TASK_GOAL=%s" % (TASK_GOAL if TASK_GOAL else "(none)"))
    HTTPServer((HOST, PORT), Handler).serve_forever()
