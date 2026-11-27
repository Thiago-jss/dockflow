"""Invoke local QA tools with only the generated credentials they need."""

import argparse
import os
import subprocess
import sys
from pathlib import Path

from bootstrap import read_existing

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('scenario', choices=['api', 'browser', 'load'])
    args, extra = parser.parse_known_args()
    values = read_existing(ROOT / '.env')
    env = {key: val for key, val in os.environ.items() if not key.startswith('DOCKFLOW_')}
    if args.scenario == 'load':
        sys.path.insert(0, str(ROOT / 'tests/e2e'))
        from api_lab import token
        key = 'DOCKFLOW_CARRIER_C_SECRET'
        old = os.environ.get(key)
        os.environ[key] = values[key]
        try:
            env['DOCKFLOW_LOAD_BEARER'] = token('carrier-c', key)
        finally:
            if old is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = old
        env['DOCKFLOW_LOAD_INSECURE_LAB'] = '1'
        return subprocess.run(['docker', 'run', '--rm', '--network', 'host', '-e', 'DOCKFLOW_LOAD_BEARER',
                               '-e', 'DOCKFLOW_LOAD_INSECURE_LAB', '-v', f'{ROOT}/tests/load:/suite:ro',
                               'grafana/k6:1.5.0@sha256:2072ea9eafa596532d9aee0cc0e0a50cfb0e7fb703981a46179af5f4f22c5ea4',
                               'run', '/suite/appointments.js', *extra], cwd=ROOT, env=env).returncode
    keyset = ([f'DOCKFLOW_CARRIER_{k}_SECRET' for k in 'ABC'] if args.scenario == 'api'
              else ['DOCKFLOW_OPERATOR_PASSWORD'])
    env.update({k: values[k] for k in keyset})
    if args.scenario == 'api':
        extra = ['--ca', str(ROOT / '.local/tls.crt'), *extra]
    return subprocess.run([sys.executable, '-B', f'tests/e2e/{args.scenario}_lab.py', *extra],
                          cwd=ROOT, env=env).returncode


if __name__ == '__main__':
    raise SystemExit(main())
