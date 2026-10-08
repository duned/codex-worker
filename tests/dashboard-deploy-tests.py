import hashlib
import sys
sys.dont_write_bytecode = True
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

REPO = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('deploy', REPO / 'tools/dashboard-deploy.py')
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)


def assets(root, content='new'):
    (root / 'assets').mkdir(parents=True)
    manifest = []
    for name in ('app.js', 'app.css'):
        data = content.encode()
        (root / 'assets' / name).write_bytes(data)
        manifest.append({'path': 'assets/' + name, 'sha256': hashlib.sha256(data).hexdigest()})
    (root / 'assets.json').write_text(json.dumps(manifest))
    (root / 'index.html').write_text('<script src="/dashboard-assets/preview/assets/app.js"></script><link href="/dashboard-assets/preview/assets/app.css">')


class DeploymentTests(unittest.TestCase):
    def test_integrity_and_paths(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            assets(root)
            deploy.validate(root)
            (root / 'assets/app.js').write_text('corrupt')
            with self.assertRaisesRegex(ValueError, 'hash'):
                deploy.validate(root)
            (root / 'assets.json').write_text('[{"path":"assets/../escape"}]')
            with self.assertRaisesRegex(ValueError, 'path'):
                deploy.validate(root)

    def test_unsafe_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'link').symlink_to(root, target_is_directory=True)
            for value in ('relative', str(root / 'link'), str(root / '..')):
                with self.assertRaises(ValueError):
                    deploy.safe_directory(value)

    def test_restart_failure_restores_old_assets(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            repo, target = base / 'repo', base / 'target'
            repo.mkdir()
            target.mkdir()
            assets(repo / 'src/CodexServer/obj/worker-poc/preview')
            assets(target / 'current', 'old')
            executable = base / 'CodexServer'
            executable.touch()
            restarts = []

            def fake_run(*args, **kwargs):
                output = ''
                if '--property=Environment' in args:
                    output = f'DOTNET_ENVIRONMENT=Development CODEX_SERVER_DEVELOPMENT_DASHBOARD_DIR={target}'
                if '--property=ExecStart' in args:
                    output = '{ path=/opt/codex-server/current/CodexServer ; }'
                if args[:4] == ('sudo', '-n', 'systemctl', 'restart'):
                    restarts.append(args)
                    if len(restarts) == 1:
                        raise subprocess.CalledProcessError(1, args)
                return subprocess.CompletedProcess(args, 0, output)

            with patch.object(deploy, 'run', fake_run), patch.object(deploy, 'INSTALLED_SERVER', executable):
                with self.assertRaisesRegex(ValueError, 'restored'):
                    deploy.deploy(repo)
            self.assertEqual('old', (target / 'current/assets/app.js').read_text())
            self.assertEqual(2, len(restarts))
            self.assertTrue(all(args[-1] == 'codex-server' for args in restarts))

    def test_cli_alias_from_unrelated_directory_through_symlink(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / 'cw').symlink_to(REPO / 'cw')
            (root / 'systemctl').write_text('#!/bin/sh\nprintf "DOTNET_ENVIRONMENT=Production\\n"\n')
            (root / 'systemctl').chmod(0o755)
            import os
            env = dict(os.environ, CW_REPO_DIR=str(REPO), PATH=str(root) + ':' + os.environ['PATH'])
            # The prerequisite probe must succeed without elevated actions.
            (root / 'sudo').write_text('#!/bin/sh\nexit 99\n')
            (root / 'sudo').chmod(0o755)
            for command in (['dashboard', 'deploy'], ['dd']):
                result = subprocess.run([str(root / 'cw'), *command], cwd=root, env=env, capture_output=True, text=True, check=False)
                self.assertEqual(1, result.returncode)
                self.assertIn('explicitly opt in', result.stderr)


if __name__ == '__main__':
    unittest.main()
