#!/usr/bin/env python3
"""Local opt-in development deployment; never invokes product build/release tooling."""
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
sys.dont_write_bytecode = True
import tempfile
import urllib.request
import urllib.error
from urllib.parse import urlsplit


INSTALLED_SERVER = Path("/opt/codex-server/current/CodexServer")
CAPABILITY = 'codex-server-development-dashboard-override-v1'


def check_capability(installed):
    try:
        result = run(str(installed), '--dashboard-override-capability', capture_output=True, text=True, timeout=10)
        if result.stdout.strip() == CAPABILITY:
            return
    except (subprocess.SubprocessError, OSError):
        pass
    raise ValueError('installed codex-server override support could not be established; upgrade the installed Server once to a build containing development dashboard override support')


def verification_url(value):
    url = urlsplit(value)
    if url.scheme not in ('http', 'https') or url.hostname not in ('127.0.0.1', '::1', 'localhost') or url.username or url.password or url.path not in ('', '/') or url.query or url.fragment:
        raise ValueError('dashboard verification URL must be a loopback HTTP/HTTPS origin')
    return value.rstrip('/')


def verify_served(directory, origin):
    # No proxy, credentials, redirects or TLS bypass. Compare every manifest asset
    # as well as the shell, so an embedded or partially served generation fails.
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            return None

    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    files = [('index.html', '/')]
    files.extend((item['path'], '/dashboard-assets/preview/' + item['path'])
                 for item in json.loads((directory / 'assets.json').read_text()))
    for name, route in files:
        expected = (directory / name).read_bytes()
        try:
            with opener.open(urllib.request.Request(origin + route, headers={'Cache-Control': 'no-cache'}), timeout=10) as response:
                actual = response.read(len(expected) + 1)
        except (OSError, urllib.error.URLError) as error:
            raise ValueError('dashboard verification endpoint unreachable or rejected the request') from error
        if actual != expected:
            raise ValueError('served dashboard generation does not match deployed assets')


def run(*args, **kwargs):
    return subprocess.run(args, check=True, timeout=kwargs.pop('timeout', 300), **kwargs)


def safe_directory(value):
    path = Path(value)
    if not path.is_absolute() or str(path) != value or path.resolve() != path or not path.is_dir():
        raise ValueError('destination must be an existing absolute directory without symlinks')
    return path


def validate(directory):
    assets = {}
    for item in json.loads((directory / 'assets.json').read_text()):
        name = item['path']
        if not re.fullmatch(r'assets/[A-Za-z0-9/_.-]+', name) or '..' in name or name in assets:
            raise ValueError('invalid or duplicate asset path')
        path = directory / name
        if path.resolve() != path or not path.is_file():
            raise ValueError('missing asset or symbolic link')
        if hashlib.sha256(path.read_bytes()).hexdigest() != item['sha256']:
            raise ValueError('asset hash mismatch')
        assets[name] = True
    if not all(any(name.endswith(ext) for name in assets) for ext in ('.js', '.css')):
        raise ValueError('JavaScript or CSS missing')
    shell = (directory / 'index.html')
    if shell.resolve() != shell:
        raise ValueError('symbolic shell link')
    references = re.findall(r'(?:src|href)="/dashboard-assets/preview/([^"]+)"', shell.read_text())
    if not all(any(name.endswith(ext) for name in references) for ext in ('.js', '.css')) or any(name not in assets for name in references):
        raise ValueError('shell references missing assets')


def deploy(repo):
    # Only explicit systemd drop-in Environment entries are accepted; do not read secret env files.
    env = dict(item.split('=', 1) for item in shlex.split(run('systemctl', 'show', 'codex-server', '--property=Environment', '--value', capture_output=True, text=True).stdout) if '=' in item)
    if env.get('DOTNET_ENVIRONMENT') != 'Development':
        raise ValueError('codex-server must explicitly opt in with DOTNET_ENVIRONMENT=Development')
    root = safe_directory(env.get('CODEX_SERVER_DEVELOPMENT_DASHBOARD_DIR', ''))
    installed = INSTALLED_SERVER
    start = run('systemctl', 'show', 'codex-server', '--property=ExecStart', '--value', capture_output=True, text=True).stdout
    if not installed.is_file() or 'path=/opt/codex-server/current/CodexServer ;' not in start:
        raise ValueError('local installed codex-server service was not found')
    check_capability(installed)
    origin = verification_url(env.get('CODEX_SERVER_DEVELOPMENT_DASHBOARD_VERIFY_URL', 'http://127.0.0.1:5090'))
    if root == Path('/') or root == repo or any(root.is_relative_to(Path(path)) for path in ('/opt', '/etc', '/usr', '/bin', '/sbin', '/boot', '/proc', '/sys', '/dev', '/run')) or root.is_relative_to(repo) or repo.is_relative_to(root):
        raise ValueError('unsafe dashboard destination')
    if (root / '.deploy.lock').is_symlink():
        raise ValueError('symbolic lock link')
    with (root / '.deploy.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        current, previous = root / 'current', root / 'previous'
        for path in (current, previous):
            if path.exists():
                safe_directory(str(path))
                validate(path)
        revision = run('git', '-C', str(repo), 'rev-parse', '--short', 'HEAD', capture_output=True, text=True).stdout.strip()
        print(f'Source: {repo} @ {revision} (including current checkout edits)', flush=True)
        frontend = repo / 'src/CodexServer/worker-poc'
        run('npm', 'ci', '--no-audit', '--no-fund', cwd=frontend)
        run('npm', 'run', 'check', cwd=frontend)
        print(f'Build/check passed. Destination: {root}. Restart required: codex-server only.', flush=True)
        with tempfile.TemporaryDirectory(prefix='.staging-', dir=root) as staging:
            candidate = Path(staging) / 'current'
            shutil.copytree(repo / 'src/CodexServer/obj/worker-poc/preview', candidate, symlinks=True)
            validate(candidate)
            if previous.exists():
                shutil.rmtree(previous)
            had_current = current.exists()
            if had_current:
                current.rename(previous)
            try:
                candidate.rename(current)
                run('sudo', '-n', 'systemctl', 'restart', 'codex-server')
                run('systemctl', 'is-active', '--quiet', 'codex-server')
                verify_served(current, origin)
            except (subprocess.SubprocessError, OSError, ValueError) as error:
                if current.exists():
                    current.rename(candidate)
                if had_current:
                    previous.rename(current)
                run('sudo', '-n', 'systemctl', 'restart', 'codex-server')
                run('systemctl', 'is-active', '--quiet', 'codex-server')
                raise ValueError(f'activation/verification failed ({error}); previous dashboard restored and Server restarted') from error
        print('Activation and served-generation verification succeeded; previous assets retained in previous. Worker unchanged.')


if __name__ == '__main__':
    try:
        deploy(Path(sys.argv[1]).resolve())
    except (ValueError, OSError, subprocess.SubprocessError, KeyError) as error:
        print(f'cw: dashboard deployment failed: {error}', file=sys.stderr)
        sys.exit(1)
