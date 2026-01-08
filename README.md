# XP VM AI Controller (VMware + XP guest)

This repo is a minimal host+guest setup:
- Host (Linux): runs a local LLM server and a small HTTP service that receives XP screenshots and returns action commands.
- Guest (Windows XP): runs an EXE that captures the full screen, sends it to the host, and executes returned actions.

## Layout
- `host/server.py` - HTTP server + LLM prompt -> action lines
- `host/send_cmd.py` - Send commands to XP agent over TCP
- `guest/XPAgent.cs` - Windows XP agent (screen capture + input)
- `data/last.png` - latest frame saved by the host

## Protocol
Guest POSTs a multipart form to `http://HOST:PORT/frame` with field `frame` containing a PNG.
Host responds with JSON:
```
{"actions":[
  {"cmd":"move","x":100,"y":120},
  {"cmd":"click","button":"left"},
  {"cmd":"type","text":"hello"},
  {"cmd":"key","key":"ENTER"},
  {"cmd":"focus","text":"notepad"},
  {"cmd":"sleep","ms":300}
]}
```
LLM can also return macro lines (host expands them):
```
macro open_run
macro save_as filename.txt
```

## Host setup (Linux)
1) Start your local LLM server (example: Ollama):
```
OLLAMA_MODEL=llava
```
2) Run the host controller:
```
cd /home/alen/PROJECT-XP
ENABLE_ACTIONS=1 USE_VISION=1 OLLAMA_MODEL=llava TASK_GOAL="open notepad" \
  python3 host/server.py
```
Notes:
- `ENABLE_ACTIONS=1` is required to let the LLM return real actions.
- If you use a text-only model, set `USE_VISION=0`.
- `TASK_GOAL` provides a simple task loop context (last actions are remembered).
- `ENABLE_OCR=1` will run `tesseract` (if installed) on each frame and feed text to the model.
- `STOP_ON_GOAL=1` will stop sending actions after a save when the goal includes \"stop\".

## Guest setup (Windows XP)
1) Install .NET Framework 2.0 or 3.5.
2) Build `guest/XPAgent.cs` with Visual Studio 2008 (C# console app) or csc:
```
csc /target:exe /out:XPAgent.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll XPAgent.cs
```
3) Run the agent in XP:
```
set XP_HOST=192.168.232.1
set XP_PORT=5000
set XP_INTERVAL_MS=1000
set XP_CMD_PORT=6001
XPAgent.exe
```
Hotkey:
- Press `F12` to toggle pause/resume.
Keys:
- Supported `key` values include `ENTER`, `TAB`, `ESC`, `ALT+F4`, `CTRL+L`, `WIN+R`.
Focus:
- Use `focus <substring>` to bring a window to the foreground by title substring.

## Command socket (XP agent)
The agent also listens for plain-text commands on `XP_CMD_PORT` (default 6001).
Each line is a single action (same as the text protocol), for example:
```
move 200 200
click left
type notepad
key ENTER
```
From Linux host you can send a single line using netcat:
```
printf "key WIN+R\n" | nc <xp-ip> 6001
```
Helper (recommended):
```
python3 host/send_cmd.py <xp-ip> send "key WIN+R"
python3 host/send_cmd.py <xp-ip> clipboard "hello from host"
python3 host/send_cmd.py <xp-ip> cmd "dir C:\\"
python3 host/send_cmd.py <xp-ip> file /path/to/file.txt
```

## Streaming
Host serves the latest frame and an MJPEG stream:
- `http://HOST:5000/last.png`
- `http://HOST:5000/stream`

## File transfer
Host → XP via `send_cmd.py file`, saved to `C:\\Temp` by default (set `XP_DROP_DIR` to change).

## VMware NAT notes
- With VMware NAT, the guest can usually reach the host via the NAT gateway IP
  (often `192.168.232.1`, but it can differ). Use `ipconfig` in the guest to find
  the default gateway and use that as `XP_HOST`.

## Safety
- The host only sends `noop` unless `ENABLE_ACTIONS=1`.
- The action parser ignores unknown commands.

## Next steps
- Add a task loop and memory on the host.
- Add UI element detection or OCR for more reliable targeting.
- Add a "pause" hotkey in the guest agent.
