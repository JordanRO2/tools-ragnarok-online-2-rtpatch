#!/usr/bin/env python3
"""Minimal HTTP/1.1 patch server for the RO2 private-server harness.

Serves a www tree (built by make-www.ps1) to the repointed RO2 updater + launcher.
Usage:  python patchsrv.py <www-root> [port]

Notes:
  - HTTP/1.1 is REQUIRED. Over HTTP/1.0 the launcher's body read comes back empty
    and it wrongly decides "already up to date".
  - Real files -> 200 + Content-Length; missing -> 404. Empty 0-byte files (the four
    /Patch/Global/Launcher/MD5 manifests) are served as 200 + Content-Length: 0, which
    is exactly what the updater needs to skip its launcher self-update and spawn.
  - The updater's versioned MD5 URL is built with a literal backslash (.../MD5\\1/...);
    WinINET normally canonicalizes it to .../MD5/1/..., but we also accept the
    backslash form defensively.
"""
import http.server, socketserver, sys, os, datetime

ROOT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else "."
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 8080
LOG  = os.path.join(os.path.dirname(os.path.abspath(__file__)), "patchsrv.log")


class Handler(http.server.SimpleHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def __init__(self, *a, **k):
        super().__init__(*a, directory=ROOT, **k)

    def _norm(self):
        if "\\" in self.path:
            self.path = self.path.replace("\\", "/")

    def _log(self, code):
        line = "%s  %-4s %-3s  %s" % (
            datetime.datetime.now().strftime("%H:%M:%S"), self.command, code, self.path)
        print(line, flush=True)
        open(LOG, "a").write(line + "\n")

    def _status(self):
        p = self.translate_path(self.path)
        if os.path.isdir(p):  # a directory serves its index.html (e.g. "/" = the big-news page)
            return 200 if any(os.path.isfile(os.path.join(p, i)) for i in ("index.html", "index.htm")) else 404
        return 200 if os.path.isfile(p) else 404

    def do_GET(self):
        self._norm()
        self._log(self._status())
        super().do_GET()

    def do_HEAD(self):
        self._norm()
        self._log(self._status())
        super().do_HEAD()

    def log_message(self, *a):
        pass  # suppress the default stderr log; we keep our own


socketserver.ThreadingTCPServer.allow_reuse_address = True
with socketserver.ThreadingTCPServer(("127.0.0.1", PORT), Handler) as httpd:
    print("serving %s on 127.0.0.1:%d (HTTP/1.1)  log -> %s" % (ROOT, PORT, LOG), flush=True)
    httpd.serve_forever()
