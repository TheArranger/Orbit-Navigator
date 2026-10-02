"""Loopback-only release fixture; never connects to a remote page or account."""

import http.server
import json
import threading
from urllib.parse import urlsplit


MAIN = b"""<!doctype html><html><head><title>Orbit release checks</title>
<style>body{font:20px system-ui;background:#15222b;color:#eee;padding:30px}a,button{display:block;margin:16px;padding:16px;color:#eee;background:#24594e}iframe{width:90%;height:330px;border:2px solid #40ba9f}</style></head><body>
<h1>Local Orbit release checks</h1><p>No account, real stream or remote requests.</p>
<a href='/new-tab' target='_blank'>Open a second tab</a>
<a href='/download'>Download release-check.txt</a>
<iframe src='/frame' allow='fullscreen' allowfullscreen></iframe></body></html>"""
FRAME = b"""<!doctype html><html><head><title>Fullscreen test player</title>
<style>html,body{margin:0;background:#192a48;color:white;font:24px system-ui}#player{padding:24px;box-sizing:border-box;background:#192a48;width:100%;height:100%}button{font:24px system-ui;padding:20px;background:#356e56;color:white}#player:fullscreen{width:100vw;height:100vh}</style></head><body><div id='player'><h2>Local fullscreen player</h2><button id='toggle'>Enter player fullscreen</button><p id='status'>Windowed</p></div><script>
document.querySelector('#toggle').onclick=async()=>{if(document.fullscreenElement)await document.exitFullscreen();else await document.querySelector('#player').requestFullscreen();};
document.onfullscreenchange=()=>{let f=!!document.fullscreenElement;document.querySelector('#status').textContent=f?'Fullscreen active':'Windowed';document.querySelector('#toggle').textContent=f?'Exit player fullscreen':'Enter player fullscreen';};
</script></body></html>"""


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == '/download':
            data = b'Orbit Navigator local download verification.\n'
            mime = 'application/octet-stream'
        elif path == '/frame':
            data, mime = FRAME, 'text/html; charset=utf-8'
        elif path == '/new-tab':
            data, mime = b'<title>Second tab verified</title><h1>New tab routing works</h1>', 'text/html; charset=utf-8'
        elif path == '/':
            data, mime = MAIN, 'text/html; charset=utf-8'
        else:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header('Content-Type', mime)
        self.send_header('Content-Length', str(len(data)))
        self.send_header('Cache-Control', 'no-store')
        if path == '/download':
            self.send_header('Content-Disposition', 'attachment; filename="orbit-release-check.txt"')
        self.end_headers()
        self.wfile.write(data)


if __name__ == '__main__':
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    # A forgotten fixture cannot remain available indefinitely.
    timer = threading.Timer(1200, server.shutdown)
    timer.daemon = True
    timer.start()
    print(json.dumps({'port': server.server_port}), flush=True)
    try:
        server.serve_forever()
    finally:
        timer.cancel()
        server.server_close()
