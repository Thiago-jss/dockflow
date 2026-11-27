import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[2] / 'scripts' / 'bootstrap.py'


class LabProvisioning(unittest.TestCase):
    def test_credentials_are_private_stable_and_only_on_disk(self):
        with tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR')) as directory:
            root = Path(directory)
            command = [sys.executable, str(SCRIPT), '--directory', directory]
            first = subprocess.run(command, capture_output=True, check=True)
            env = root / '.env'
            private = root / '.local'
            self.assertEqual(env.stat().st_mode & 0o777, 0o600)
            self.assertEqual(private.stat().st_mode & 0o777, 0o700)
            self.assertEqual((private / 'tls.key').stat().st_mode & 0o777, 0o600)
            pairs = dict(line.split('=', 1) for line in env.read_text().splitlines())
            self.assertEqual(len(set(pairs.values())), len(pairs))
            realm = json.loads((private / 'dockflow-realm.json').read_text())
            web = next(c for c in realm['clients'] if c['clientId'] == 'dockflow-web')
            self.assertEqual(web['secret'], pairs['DOCKFLOW_WEB_SECRET'])
            self.assertFalse(web['directAccessGrantsEnabled'])
            self.assertEqual(web['redirectUris'], ['https://localhost:5191/signin-oidc'])
            for credential in pairs.values():
                self.assertNotIn(credential.encode(), first.stdout + first.stderr)
            before = env.read_bytes()
            subprocess.run(command, capture_output=True, check=True)
            self.assertEqual(env.read_bytes(), before)

    def test_existing_unknown_file_is_not_replaced(self):
        with tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR')) as directory:
            env = Path(directory) / '.env'
            env.write_text('UNRELATED_VALUE=preserve-me\n')
            out = subprocess.run([sys.executable, str(SCRIPT), '--directory', directory], capture_output=True)
            self.assertNotEqual(out.returncode, 0)
            self.assertEqual(env.read_text(), 'UNRELATED_VALUE=preserve-me\n')


if __name__ == '__main__':
    unittest.main()
