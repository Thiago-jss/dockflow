"""Prove durable booking and eventual processing during a broker outage in the loopback lab."""

import argparse
import json
import os
import ssl
import subprocess
import sys
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import UUID, uuid4

from bootstrap import read_existing

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests/e2e'))
from api_lab import API, request, require, token


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', choices=['dockflow-qa', 'dockflow-ci'], required=True)
    args = parser.parse_args()
    compose = ['docker', 'compose', '-p', args.project]

    def run(*command, capture=False):
        return subprocess.run([*compose, *command], cwd=ROOT, check=True,
                              stdout=subprocess.PIPE if capture else None,
                              text=True, timeout=30).stdout

    running = set(run('ps', '--services', '--status', 'running', capture=True).splitlines())
    require({'gate', 'web', 'worker', 'db', 'broker'} <= running, 'expected a complete isolated lab')
    os.environ['DOCKFLOW_CARRIER_C_SECRET'] = read_existing(ROOT / '.env')['DOCKFLOW_CARRIER_C_SECRET']
    bearer = token('carrier-c', 'DOCKFLOW_CARRIER_C_SECRET')
    context = ssl.create_default_context(cafile=str(ROOT / '.local/tls.crt'))
    headers = {'Authorization': f'Bearer {bearer}', 'Content-Type': 'application/json', 'Idempotency-Key': uuid4().hex}
    offset = int(headers['Idempotency-Key'][:6], 16) % (9 * 24 * 60)
    start = datetime.now(timezone.utc).replace(second=0, microsecond=0) + timedelta(days=1, minutes=offset)
    end = start + timedelta(minutes=5)
    body = {'vehicle': 'QA-' + uuid4().hex[:8].upper(), 'cargo': 'General', 'weightKg': 1200,
            'start': start.isoformat(), 'end': end.isoformat()}

    def check(visit):
        sql = f"""SELECT o.id, (o.published_at IS NOT NULL)::int,
                    (i.id IS NOT NULL)::int,
                    (SELECT count(*) FROM public.audit_log a WHERE a.aggregate_id=v.id AND a.kind='AppointmentScheduled')
                  FROM public.visits v JOIN public.outbox o ON o.aggregate_id=v.id AND o.tenant_id=v.tenant_id
                  LEFT JOIN public.inbox i ON i.id=o.id
                  WHERE v.id='{visit}' AND o.kind='AppointmentScheduled';"""
        row = run('exec', '-T', 'db', 'psql', '-U', 'dockflow_admin', '-d', 'dockflow',
                  '-v', 'ON_ERROR_STOP=1', '-Atc', sql, capture=True).strip().split('|')
        require(len(row) == 4, 'expected one outbox row joined to the durable reservation')
        return str(UUID(row[0])), tuple(map(int, row[1:]))

    run('stop', '-t', '6', 'broker')
    try:
        result, raw = request(API, 'POST', headers=headers, body=json.dumps(body), context=context)
        require(result == 201, f'booking with broker stopped expected 201, got {result}')
        visit = str(UUID(json.loads(raw)['id']))
        event, state = check(visit)
        require(state == (0, 0, 1), 'booking, audit and unpublished outbox were not all durable')
    finally:
        run('start', 'broker')
    deadline = time.monotonic() + 75
    while time.monotonic() < deadline:
        if check(visit) == (event, (1, 1, 1)):
            print('PASS: broker outage preserved reservation, audit and outbox; restored worker published and consumed the same event.')
            return
        time.sleep(2)
    sys.exit('FAIL: the original event did not reach durable inbox before deadline.')


if __name__ == '__main__':
    main()
