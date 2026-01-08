#!/usr/bin/env python3
import argparse
import base64
import os
import socket
import sys


def send_lines(host, port, lines):
    data = ("\n".join(lines) + "\n").encode("utf-8")
    with socket.create_connection((host, port), timeout=5) as s:
        s.sendall(data)
        s.shutdown(socket.SHUT_WR)
        resp = s.recv(65536).decode("utf-8", errors="replace")
        return resp.strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("host")
    ap.add_argument("--port", type=int, default=6001)
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("ping")

    p_clip = sub.add_parser("clipboard")
    p_clip.add_argument("text")

    p_cmd = sub.add_parser("cmd")
    p_cmd.add_argument("command")

    p_send = sub.add_parser("send")
    p_send.add_argument("line")

    p_file = sub.add_parser("file")
    p_file.add_argument("path")
    p_file.add_argument("--name", default=None)

    args = ap.parse_args()

    if args.cmd == "ping":
        resp = send_lines(args.host, args.port, ["noop"])
        print(resp)
    elif args.cmd == "clipboard":
        resp = send_lines(args.host, args.port, ["clipboard " + args.text])
        print(resp)
    elif args.cmd == "cmd":
        resp = send_lines(args.host, args.port, ["cmd " + args.command])
        print(resp.replace("\\n", "\n"))
    elif args.cmd == "send":
        resp = send_lines(args.host, args.port, [args.line])
        print(resp)
    elif args.cmd == "file":
        path = args.path
        name = args.name or os.path.basename(path)
        with open(path, "rb") as f:
            blob = f.read()
        b64 = base64.b64encode(blob).decode("ascii")
        lines = ["file_begin " + name]
        chunk = 4096
        for i in range(0, len(b64), chunk):
            lines.append("file_chunk " + b64[i : i + chunk])
        lines.append("file_end")
        resp = send_lines(args.host, args.port, lines)
        print(resp)


if __name__ == "__main__":
    main()
