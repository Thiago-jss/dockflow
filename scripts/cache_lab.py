"""Exercise the PostgreSQL fallback after stopping Redis in an isolated lab."""
import argparse
import json
import os
import ssl
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import uuid4

from bootstrap import read_existing

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests/e2e'))
from api_lab import API, request, require, token


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', choices=['dockflow-qa','dockflow-ci'], required=True)
    args = parser.parse_args()
    compose = ['docker','compose','-p', args.project]

    def run(*argv, capture=False):
        return subprocess.run([*compose,*argv], cwd=ROOT, check=True, text=True,
                              stdout=subprocess.PIPE if capture else None, timeout=35).stdout

    running = set(run('ps','--services','--status','running', capture=True).splitlines())
    require({'gate','redis','db','web'} <= running, 'expected an isolated complete lab')
    query = """SELECT count(*) FROM public.audit_log WHERE tenant_id='11111111-1111-1111-1111-111111111111'
    AND kind='UnloadingCompleted' AND (occurred_at AT TIME ZONE 'UTC')::date=(now() AT TIME ZONE 'UTC')::date;"""
    expected = int(run('exec','-T','db','psql','-U','dockflow_admin','-d','dockflow','-Atc',query,capture=True).strip())
    require(expected >= 1, 'complete one test unloading before checking Redis fallback')
    os.environ['DOCKFLOW_CARRIER_C_SECRET'] = read_existing(ROOT/'.env')['DOCKFLOW_CARRIER_C_SECRET']
    bearer = token('carrier-c','DOCKFLOW_CARRIER_C_SECRET')
    context = ssl.create_default_context(cafile=str(ROOT/'.local/tls.crt'))
    key = uuid4().hex
    start = datetime.now(timezone.utc).replace(second=0,microsecond=0) + timedelta(days=17,minutes=int(key[:4],16)%1000)
    body = json.dumps({'vehicle':'CACHE-'+key[:8].upper(), 'cargo':'General','weightKg':1200,
                       'start':start.isoformat(),'end':(start+timedelta(minutes=3)).isoformat()})
    run('stop','-t','4','redis')
    try:
        code,_ = request(API,'POST',headers={'Authorization':f'Bearer {bearer}',
                         'Content-Type':'application/json','Idempotency-Key':key},body=body,context=context)
        require(code==201,f'booking without Redis expected 201, received {code}')
        subprocess.run([sys.executable,'scripts/lab.py','browser','--completed',str(expected)],
                       cwd=ROOT,check=True,timeout=65)
        print('PASS: Redis down; reservation confirmed and authenticated analytics matched PostgreSQL.')
    finally:
        run('start','redis')


if __name__ == '__main__':
    main()
