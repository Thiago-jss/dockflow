#!/usr/bin/env python3
"""Live checks for the isolated DockFlow lab, not a fixture server."""
import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
import http.client
import json
import os
import secrets
import signal
import ssl
import sys
import threading
from time import monotonic
from urllib.parse import urlencode
from uuid import UUID, uuid4

API = '/api/v1/appointments'
TOKEN = '/realms/dockflow/protocol/openid-connect/token'
GENERAL_DOCK = '11111111-1111-1111-1111-111111111101'  # G-01
REFRIGERATED_DOCK = '11111111-1111-1111-1111-111111111103'  # R-01
COUNT = 100
IO_TIMEOUT = 15


class Failure(Exception):
    pass


def require(condition, message):
    if not condition:
        raise Failure(message)


def request(path, method='GET', *, headers=None, body=None, context=None, identity=False):
    conn = (http.client.HTTPConnection('localhost', 5192, timeout=IO_TIMEOUT) if identity else
            http.client.HTTPSConnection('localhost', 5191, timeout=IO_TIMEOUT, context=context))
    try:
        conn.request(method, path, body=body, headers=headers or {})
        response = conn.getresponse()
        raw = response.read(2_000_001)
        require(len(raw) <= 2_000_000, f'{method} {path}: response exceeded limit')
        return response.status, raw  # Never follow redirects or log response bodies.
    finally:
        conn.close()


def json_body(raw, label):
    try:
        return json.loads(raw)
    except (ValueError, UnicodeError):
        raise Failure(f'{label}: expected JSON') from None


def token(client, key):
    require(bool(os.environ.get(key)), f'export {key} (do not print its value)')
    form = urlencode({'grant_type': 'client_credentials', 'client_id': client,
                      'client_secret': os.environ[key]})
    status, raw = request(TOKEN, 'POST', headers={'Content-Type': 'application/x-www-form-urlencoded'},
                          body=form, identity=True)
    require(status == 200, f'Keycloak {client}: expected 200, got {status}')
    data = json_body(raw, f'Keycloak {client}')
    value = data.get('access_token') if isinstance(data, dict) else None
    require(isinstance(value, str) and bool(value), f'Keycloak {client}: no access_token')
    return value


def appointment_id(raw, label):
    data = json_body(raw, label)
    try:
        return str(UUID(data['id']))
    except (KeyError, TypeError, ValueError, AttributeError):
        raise Failure(f'{label}: expected appointment UUID in id') from None


def appointment_response(data, label):
    fields = {'id', 'dockId', 'dockCode', 'vehicle', 'cargo', 'weightKg', 'start', 'end', 'status', 'version'}
    require(isinstance(data, dict) and set(data) == fields, f'{label}: appointment response fields changed')
    return data


def stamp(value):
    return value.isoformat(timespec='seconds').replace('+00:00', 'Z')


def slot(value):
    now = datetime.now(timezone.utc)
    if not value:
        # Randomize within the next two weeks, leaving time for setup and nearby cases.
        return now.replace(second=0, microsecond=0) + timedelta(days=1, minutes=secrets.randbelow(18_720))
    try:
        anchor = datetime.fromisoformat(value.replace('Z', '+00:00'))
    except ValueError:
        raise Failure('anchor must be an ISO-8601 future UTC minute') from None
    require(anchor.utcoffset() == timedelta(0) and not anchor.second and not anchor.microsecond and anchor > now,
            'anchor must be an ISO-8601 future UTC minute')
    return anchor


def status_is(result, expected, label):
    require(result[0] in expected, f'{label}: expected {sorted(expected)}, got {result[0]}')
    return result[1]


def conflict_is(result, code, label):
    body = json_body(status_is(result, {409}, label), label)
    require(isinstance(body, dict) and body.get('code') == code, f'{label}: expected conflict code {code}')


def listing(raw):
    data = json_body(raw, 'list appointments')
    require(isinstance(data, list), 'list appointments: expected an array')
    return Counter(str(appointment_response(row, 'list item')['id']).lower() for row in data)


def run(args):
    context = ssl.create_default_context(cafile=args.ca)
    if args.insecure_lab:
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
    anchor = slot(args.anchor)
    run_id = uuid4().hex
    print(f'run={run_id} anchor={stamp(anchor)}', flush=True)
    for endpoint in ('/health/live', '/health/ready'):
        status_is(request(endpoint, context=context), {200}, endpoint)
    status_is(request(API, context=context), {401}, 'anonymous appointment list')
    a, b, c = (token(f'carrier-{name.lower()}', f'DOCKFLOW_CARRIER_{name}_SECRET') for name in 'ABC')

    def get(bearer, path=API):
        return request(path, headers={'Authorization': f'Bearer {bearer}'}, context=context)

    def post(bearer, payload, key):
        return request(API, 'POST', headers={'Authorization': f'Bearer {bearer}', 'Content-Type': 'application/json',
                                              'Idempotency-Key': key}, body=json.dumps(payload), context=context)

    plate_letters = ''.join(chr(65 + int(run_id[i:i + 2], 16) % 26) for i in range(0, 8, 2))

    def payload(i, offset=0, dock=GENERAL_DOCK, cargo=None):
        start = anchor + timedelta(minutes=offset)
        # Synthetic plate in Brazilian Mercosul format (LLLDLDD).
        return {'vehicle': f'{plate_letters[:3]}{i // 100}{plate_letters[3]}{i % 100:02d}',
                'cargo': args.cargo if cargo is None else cargo, 'weightKg': 1200, 'start': stamp(start),
                'end': stamp(start + timedelta(minutes=30)), 'dockId': dock}

    barrier = threading.Barrier(COUNT, timeout=8)

    def contender(i):
        key = f'{run_id}-{i}'
        candidate = payload(i)
        # Same tenant and same dock; split the burst across two real carriers to
        # respect the API's independent 60-request buckets.
        bearer = a if i % 2 == 0 else b
        barrier.wait()
        return bearer, key, candidate, post(bearer, candidate, key)

    before = monotonic()
    with ThreadPoolExecutor(max_workers=COUNT) as pool:
        results = list(pool.map(contender, range(COUNT), timeout=35))
    counts = Counter(result[0] for _, _, _, result in results)
    print(f'contention statuses={dict(sorted(counts.items()))} elapsed_s={monotonic() - before:.2f}', flush=True)
    require(counts == {201: 1, 409: 99}, 'same dock and slot: expected exactly one 201 and 99 conflicts')
    for _, _, _, result in results:
        if result[0] == 409:
            conflict_is(result, 'reservation_overlap', 'losing contender')
    owner, key, winner, (_, raw) = next(item for item in results if item[3][0] == 201)
    peer = b if owner == a else a
    winner_id = appointment_id(raw, 'create winner')
    winning_visit = appointment_response(json_body(raw, 'create winner'), 'create winner')
    require(winning_visit['status'] == 'Scheduled' and winning_visit['dockId'] == GENERAL_DOCK and
            winning_visit['cargo'] == winner['cargo'] and type(winning_visit['version']) is int,
            'winner must be scheduled on G-01')
    uri = f'{API}/{winner_id}'
    detail = status_is(get(owner, uri), {200}, 'owner reads winner')
    require(appointment_response(json_body(detail, 'owner detail'), 'owner detail') == winning_visit,
            'owner read differs from the created appointment')
    for label, bearer in [('same tenant, other carrier', peer), ('other tenant', c)]:
        status_is(get(bearer, uri), {404}, f'{label} detail concealed')
        require(winner_id not in listing(status_is(get(bearer), {200}, f'{label} list')),
                f'{label} list exposed winner')

    # Change the slot so a 409 cannot be mistaken for ordinary dock contention.
    replay = status_is(post(owner, winner, key), {200, 201}, 'identical replay')
    require(appointment_response(json_body(replay, 'identical replay'), 'identical replay') == winning_visit,
            'replay changed the original appointment response')
    conflict_is(post(owner, payload(101, 30), key), 'idempotency_mismatch', 'different input with same key')
    adjacent = status_is(post(owner, payload(102, 30), f'{run_id}-adjacent'), {201}, 'adjacent slot')
    require(appointment_id(adjacent, 'adjacent slot') != winner_id, 'adjacent slot reused the winner ID')
    conflict_is(post(owner, payload(103, 60, GENERAL_DOCK, args.refrigerated_cargo), f'{run_id}-incompatible'),
                'dock_unavailable', 'incompatible dock')
    # Positive control rules out an unavailable or unseeded refrigerated dock.
    cold = status_is(post(owner, payload(104, 60, REFRIGERATED_DOCK, args.refrigerated_cargo), f'{run_id}-compatible'),
                     {201}, 'compatible refrigerated cargo on R-01')
    cold_visit = appointment_response(json_body(cold, 'compatible R-01 request'), 'compatible R-01 request')
    require(cold_visit['dockId'] == REFRIGERATED_DOCK,
            'compatible R-01 request was not assigned to R-01')
    cancelled = request(f'{API}/{cold_visit["id"]}/cancel', 'POST',
                        headers={'Authorization': f'Bearer {owner}', 'Content-Type': 'application/json'},
                        body=json.dumps({'version': cold_visit['version']}), context=context)
    cancelled_raw = status_is(cancelled, {200}, 'cancel compatible appointment')
    cancelled_visit = appointment_response(json_body(cancelled_raw, 'cancel response'), 'cancel response')
    require(cancelled_visit == {**cold_visit, 'status': 'Cancelled', 'version': cold_visit['version'] + 1},
            'cancel response changed fields other than status and version')
    require(listing(status_is(get(owner), {200}, 'owner list'))[winner_id] == 1,
            'owner list must contain winner exactly once')
    print(f'winner={winner_id} vehicle={winner["vehicle"]}')
    print('PASS health, auth, contention, owner visibility, carrier/tenant isolation, replay, mismatch, adjacent, compatibility, cancellation and response contract')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    tls = parser.add_mutually_exclusive_group(required=True)
    tls.add_argument('--ca', help='trusted CA/certificate PEM for this lab only')
    tls.add_argument('--insecure-lab', action='store_true', help='skip TLS checks only for fixed localhost:5191')
    parser.add_argument('--anchor', default=os.environ.get('DOCKFLOW_E2E_ANCHOR'))
    parser.add_argument('--cargo', type=json.loads, default=os.environ.get('DOCKFLOW_E2E_CARGO', '"General"'),
                        help='JSON cargo enum (e.g. \'"General"\' or 0)')
    parser.add_argument('--refrigerated-cargo', type=json.loads, default='"Refrigerated"',
                        help='JSON refrigerated cargo enum for the positive control')
    args = parser.parse_args()
    # A system-level deadline also bounds DNS/connect/read edge cases outside socket idle timeouts.
    signal.signal(signal.SIGALRM, lambda *_: os._exit(124))
    signal.alarm(120)
    try:
        run(args)
    except Failure as exc:
        sys.exit(f'FAIL: {exc}')
    except (OSError, http.client.HTTPException, TimeoutError, threading.BrokenBarrierError) as exc:
        sys.exit(f'FAIL: local transport/concurrency error ({type(exc).__name__}); check both lab origins and TLS')
    finally:
        signal.alarm(0)


if __name__ == '__main__':
    main()
