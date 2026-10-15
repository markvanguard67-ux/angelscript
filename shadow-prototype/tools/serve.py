#!/usr/bin/env python3
"""Cache-paranoid static server for the shadow prototype.

Plain `python3 -m http.server` sends no Cache-Control/Expires headers, so
browsers apply *heuristic freshness* to the ES modules and may keep running
stale code for hours after a file changes — without ever revalidating. This
wrapper serves the same directory but forbids caching outright, so a plain
reload always picks up the current sources.

    python3 tools/serve.py            # serves the prototype on :8080
"""
import http.server
import os
import socketserver

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')


class Handler(http.server.SimpleHTTPRequestHandler):
    def _version(self):
        import hashlib
        h = hashlib.md5()
        roots = [os.path.join(ROOT, 'src'), ROOT]
        names = []
        for d in os.listdir(os.path.join(ROOT, 'src')):
            names.append(os.path.join('src', d))
        for n in ('index.html', 'styles.css'):
            names.append(n)
        for n in sorted(names):
            st = os.stat(os.path.join(ROOT, n))
            h.update(f'{n}:{st.st_mtime_ns}:{st.st_size}'.encode())
        return h.hexdigest()[:12]

    def do_GET(self):
        if self.path.split('?')[0] == '/__version':
            body = self._version().encode()
            self.send_response(200)
            self.send_header('Content-Type', 'text/plain')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        super().do_GET()

    def end_headers(self):
        self.send_header('Cache-Control', 'no-store, must-revalidate, max-age=0')
        self.send_header('Pragma', 'no-cache')
        self.send_header('Expires', '0')
        super().end_headers()


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


if __name__ == '__main__':
    os.chdir(ROOT)
    with Server(('0.0.0.0', 8080), Handler) as httpd:
        print('serving', os.getcwd(), 'on 0.0.0.0:8080 (no-store)')
        httpd.serve_forever()
