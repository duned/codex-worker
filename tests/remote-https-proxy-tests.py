#!/usr/bin/env python3
"""Exercise the checked-in proxy with disposable TLS and an HTTP stub, never system services."""
import http.client
import http.server
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import time


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def main():
    nginx = os.environ.get('NGINX_EXECUTABLE') or shutil.which('nginx')
    if not nginx or not shutil.which('openssl'):
        print('SKIP: nginx and openssl are required for isolated proxy validation')
        return 77
    source = Path(__file__).resolve().parents[1] / 'packaging/linux/nginx'
    seen = []

    class Backend(http.server.BaseHTTPRequestHandler):
        def handle_request(self):
            seen.append((self.command, self.path, dict(self.headers)))
            self.send_response(200)
            self.send_header('Cache-Control', 'public, max-age=3600')
            self.send_header('ETag', 'stub-validator')
            self.end_headers()
            self.wfile.write(b'synthetic-response')

        do_GET = do_POST = do_PUT = handle_request

        def log_message(self, *args):
            pass

    try:
        backend = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Backend)
    except PermissionError:
        print('SKIP: environment prohibits loopback sockets')
        return 77
    thread = threading.Thread(target=backend.serve_forever, daemon=True)
    thread.start()
    try:
        with tempfile.TemporaryDirectory(prefix='codex-proxy-') as directory:
            root = Path(directory)
            cert, key = root / 'cert.pem', root / 'key.pem'
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
                            '-keyout', str(key), '-out', str(cert), '-days', '1',
                            '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost'],
                           check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            tls_port, http_port = free_port(), free_port()
            original = (source / 'codex-server.conf').read_text()

            def render(text):
                # Change only deployment addresses/paths; retain actual routing/security directives.
                return (text.replace('codex-server.example.com', 'localhost')
                        .replace('listen [::]:80', f'listen [::1]:{http_port}')
                        .replace('listen 80', f'listen 127.0.0.1:{http_port}')
                        .replace('listen [::]:443', f'listen [::1]:{tls_port}')
                        .replace('listen 443', f'listen 127.0.0.1:{tls_port}')
                        .replace('/etc/letsencrypt/live/localhost/fullchain.pem', str(cert))
                        .replace('/etc/letsencrypt/live/localhost/privkey.pem', str(key))
                        .replace('http://127.0.0.1:5090', f'http://127.0.0.1:{backend.server_port}')
                        .replace('/var/lib/letsencrypt', str(root)))

            config = root / 'nginx.conf'

            def write_config(text):
                config.write_text(f'pid {root}/nginx.pid;\nerror_log {root}/error.log;\n'
                                  'events {}\nhttp {\naccess_log off;\n'
                                  + ''.join(f'{kind}_temp_path {root}/{kind};\n'
                                            for kind in ['client_body', 'proxy', 'fastcgi', 'uwsgi', 'scgi'])
                                  + render(text) + '\n}\n')
                subprocess.run([nginx, '-p', directory + '/', '-c', str(config), '-t'], check=True)

            write_config((source / 'acme-bootstrap.conf').read_text())
            write_config(original)
            process = subprocess.Popen([nginx, '-p', directory + '/', '-c', str(config),
                                        '-g', 'daemon off;'])
            try:
                context = ssl.create_default_context(cafile=str(cert))

                def request(method, path, headers=None):
                    connection = http.client.HTTPSConnection('localhost', tls_port, context=context, timeout=3)
                    try:
                        connection.request(method, path, headers=headers or {})
                        response = connection.getresponse()
                        result = response.status, dict(response.getheaders()), response.read()
                        return result
                    finally:
                        connection.close()

                # Bounded startup coordination, not a fixed sleep assumption.
                deadline = time.monotonic() + 5
                while True:
                    try:
                        assert request('GET', '/')[0] == 403
                        break
                    except ConnectionRefusedError:
                        if time.monotonic() >= deadline:
                            raise
                        time.sleep(0.02)
                public = [
                    ('PUT', '/api/v1/workers/w'), ('POST', '/api/v1/workers/w/heartbeat'),
                    ('GET', '/api/v1/workers/w/configuration'),
                    ('POST', '/api/v1/workers/w/assignments/request'),
                    ('POST', '/api/v1/workers/w/provisioning/request'),
                    ('POST', '/api/v1/workers/w/provisioning/p/report'),
                    ('POST', '/api/v1/workers/w/provisioning/commands/request'),
                    ('POST', '/api/v1/workers/w/provisioning/commands/c/report'),
                    ('POST', '/api/v1/workers/w/executions/e/report'),
                    ('POST', '/api/v1/workers/w/executions/e/lease/renew'),
                    ('GET', '/api/v1/workers/w/credentials/c'),
                    ('POST', '/api/v1/workers/register'),
                ]
                headers = {'Authorization': 'synthetic-api', 'X-Codex-Worker-Token': 'synthetic-bootstrap',
                           'X-Worker-Credential-Token': 'synthetic-delivery',
                           'X-Forwarded-For': '10.77.0.1', 'Forwarded': 'for=10.77.0.1',
                           'X-Forwarded-Host': 'evil.invalid', 'X-Forwarded-Proto': 'https',
                           'X-Real-IP': '10.77.0.1', 'If-None-Match': 'stub-validator',
                           'If-Modified-Since': 'Thu, 01 Jan 1970 00:00:00 GMT'}
                for method, path in public:
                    status, response_headers, body = request(method, path, headers)
                    assert status == 200, (method, path, status)
                    assert body == b'synthetic-response'
                    assert response_headers['Cache-Control'] == 'no-store'
                    assert response_headers['Pragma'] == 'no-cache'
                    assert response_headers['Expires'] == '0'
                    assert 'ETag' not in response_headers
                    upstream = seen[-1][2]
                    for name in ['Forwarded', 'X-Forwarded-For', 'X-Forwarded-Host',
                                 'X-Forwarded-Proto', 'X-Real-IP', 'If-None-Match', 'If-Modified-Since']:
                        assert name not in upstream, name
                    for name in ['Authorization', 'X-Codex-Worker-Token', 'X-Worker-Credential-Token']:
                        assert upstream[name] == headers[name]
                before = len(seen)
                for method, path in [('GET', '/'), ('GET', '/home'),
                                     ('GET', '/projects/project-a?label=review'),
                                     ('GET', '/workers/worker-a?step=preparation'),
                                     ('GET', '/executions/request-a?offset=50'),
                                     ('GET', '/settings/credential-a'),
                                     ('GET', '/api/v1/administration/session'),
                                     ('POST', '/api/v1/administration/session'),
                                     ('DELETE', '/api/v1/administration/session'), ('GET', '/api/v1/projects'),
                                     ('GET', '/api/v1/workers/w'),
                                     ('PUT', '/api/v1/workers/w/credential-access'),
                                     ('POST', '/api/v1/workers/w/authentication/revoke'),
                                     ('GET', '/api/v1/workers/w/heartbeat'),
                                     ('GET', '/api/v1/workers/register'),
                                     ('POST', '/api/v1/workers/w/unknown')]:
                    status, response_headers, _ = request(method, path, headers)
                    assert status == 403, (method, path, status)
                    assert response_headers['Cache-Control'] == 'no-store'
                assert len(seen) == before
                assert request('GET', '/api/v1/workers/w/configuration', {'Host': 'evil.invalid'})[0] == 421
                # Repeated conditional requests still reach upstream, rather than a cache.
                for _ in range(2):
                    assert request('GET', '/api/v1/workers/w/credentials/c', headers)[0] == 200
                assert len(seen) == before + 2
                assert request('POST', '/api/v1/workers/w/heartbeat',
                               {'Content-Length': str(4 * 1024 * 1024 + 1)})[0] == 413
                # One enrollment already occurred; the separate enrollment zone
                # rejects a small burst without exhausting the general poll zone.
                enrollment_statuses = [request('POST', '/api/v1/workers/register')[0]
                                       for _ in range(7)]
                assert 429 in enrollment_statuses
                assert request('GET', '/api/v1/workers/w/configuration')[0] == 200
                connection = http.client.HTTPConnection('127.0.0.1', http_port, timeout=3)
                connection.request('POST', '/api/v1/workers/register', headers={'Host': 'localhost'})
                assert connection.getresponse().status == 404
                connection.close()
                try:
                    connection = http.client.HTTPSConnection('localhost', tls_port, timeout=3)
                    connection.request('GET', '/')
                except ssl.SSLCertVerificationError:
                    pass
                else:
                    raise AssertionError('Untrusted certificate accepted')
                finally:
                    connection.close()
            finally:
                process.terminate()
                process.wait(timeout=5)
            # Model an allowed VPN peer without changing the system's network.
            write_config(original.replace('10.77.0.0/24', '127.0.0.1/32'))
            process = subprocess.Popen([nginx, '-p', directory + '/', '-c', str(config),
                                        '-g', 'daemon off;'])
            try:
                deadline = time.monotonic() + 5
                while True:
                    try:
                        assert request('GET', '/api/v1/projects')[0] == 200
                        break
                    except ConnectionRefusedError:
                        if time.monotonic() >= deadline:
                            raise
                        time.sleep(0.02)
                assert request('GET', '/')[0] == 200
                for path in ['/home', '/projects/project-a?label=review', '/workers/worker-a?step=preparation', '/executions/request-a?offset=50', '/settings/credential-a']:
                    assert request('GET', path)[0] == 200
            finally:
                process.terminate()
                process.wait(timeout=5)
            for log in root.glob('*.log'):
                assert 'synthetic-' not in log.read_text(), log
        print('PASS: native syntax, public method/routes, VPN restriction, host policy, headers, forwarding, no cache, limits, TLS trust')
        return 0
    finally:
        backend.shutdown()
        backend.server_close()
        thread.join(timeout=5)


if __name__ == '__main__':
    raise SystemExit(main())
