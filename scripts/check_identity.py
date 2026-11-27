"""Verify the isolated Keycloak realm actually issues scoped integration claims."""

import base64
import json
import re
import time
from pathlib import Path
from urllib.error import URLError
from urllib.parse import urlencode
from urllib.request import Request, build_opener, ProxyHandler

ISSUER = 'http://localhost:5192/realms/dockflow'
AUDIENCE = 'dockflow-api'
OPENER = build_opener(ProxyHandler({}))
TENANTS = ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222')


def configuration():
    settings = Path(__file__).resolve().parents[1] / '.env'
    return {key: value for key, value in (line.split('=', 1) for line in settings.read_text().splitlines())}


def response(request):
    with OPENER.open(request, timeout=5) as handle:
        return json.load(handle)


def main():
    discovery = None
    for attempt in range(35):
        try:
            discovery = response(ISSUER + '/.well-known/openid-configuration')
            break
        except (URLError, TimeoutError, ConnectionError):
            if attempt == 34:
                raise RuntimeError('Local Keycloak discovery did not become available.') from None
            time.sleep(2)
    if discovery is None:
        raise RuntimeError('Local Keycloak discovery did not return a document.')
    assert discovery.get('issuer') == ISSUER
    assert discovery.get('token_endpoint') == ISSUER + '/protocol/openid-connect/token'
    assert 'S256' in discovery.get('code_challenge_methods_supported', [])
    env = configuration()
    for code, tenant in zip('abc', (TENANTS[0], TENANTS[0], TENANTS[1]), strict=True):
        secret = env[f'DOCKFLOW_CARRIER_{code.upper()}_SECRET']
        assert re.fullmatch('[A-Za-z0-9_-]{32,96}', secret)
        data = urlencode({'client_id': f'carrier-{code}', 'client_secret': secret,
                          'grant_type': 'client_credentials'}).encode('utf-8')
        token = response(Request(discovery['token_endpoint'], method='POST', data=data))['access_token']
        payload = token.split('.')[1]
        claims = json.loads(base64.urlsafe_b64decode(payload + '=' * (-len(payload) % 4)))
        assert claims['iss'] == ISSUER
        assert AUDIENCE in (claims['aud'] if isinstance(claims['aud'], list) else [claims['aud']])
        assert claims['tenant_id'] == tenant
        assert claims['carrier_id'] == f'{code*8}-{code*4}-{code*4}-{code*4}-{code*12}'
        assert 'Carrier' in claims['roles']
    print('PASS: issuer, PKCE metadata and scoped integration claims for three lab clients.')


if __name__ == '__main__':
    main()
