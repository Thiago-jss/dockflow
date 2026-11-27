"""Generate credentials and a dedicated TLS certificate for the loopback lab."""

import argparse
import json
import os
import re
import secrets
import subprocess
import tempfile
from pathlib import Path

KEYS = (
    'DOCKFLOW_DB_PASSWORD', 'DOCKFLOW_DB_ADMIN_PASSWORD', 'DOCKFLOW_MQ_PASSWORD',
    'DOCKFLOW_WEB_SECRET', 'DOCKFLOW_CARRIER_A_SECRET', 'DOCKFLOW_CARRIER_B_SECRET',
    'DOCKFLOW_CARRIER_C_SECRET', 'DOCKFLOW_OPERATOR_PASSWORD', 'DOCKFLOW_REDIS_PASSWORD',
)
ROLES = ('Administrator', 'LogisticsManager', 'DockOperator', 'GateOperator', 'Carrier')
TENANT_A = '11111111-1111-1111-1111-111111111111'
TENANT_B = '22222222-2222-2222-2222-222222222222'


def mapper(name, provider, config):
    return {'name': name, 'protocol': 'openid-connect', 'protocolMapper': provider,
            'consentRequired': False, 'config': {'claim.name': name, **config}}


def attribute(name):
    return mapper(name, 'oidc-usermodel-attribute-mapper', {
        'user.attribute': name, 'jsonType.label': 'String', 'id.token.claim': 'true',
        'access.token.claim': 'true', 'userinfo.token.claim': 'false',
    })


def constant(name, value):
    return mapper(name, 'oidc-hardcoded-claim-mapper', {
        'claim.value': value, 'jsonType.label': 'String', 'access.token.claim': 'true',
        'id.token.claim': 'false',
    })


def realm(values):
    roles = mapper('roles', 'oidc-usermodel-realm-role-mapper', {
        'multivalued': 'true', 'jsonType.label': 'String', 'id.token.claim': 'true',
        'access.token.claim': 'true', 'userinfo.token.claim': 'false',
    })
    audience = mapper('api-audience', 'oidc-audience-mapper', {
        'included.client.audience': 'dockflow-api', 'access.token.claim': 'true', 'id.token.claim': 'false',
    })
    client = {'enabled': True, 'publicClient': False, 'protocol': 'openid-connect',
              'directAccessGrantsEnabled': False, 'implicitFlowEnabled': False,
              'clientAuthenticatorType': 'client-secret'}
    clients = [
        {**client, 'clientId': 'dockflow-web', 'secret': values['DOCKFLOW_WEB_SECRET'],
         'standardFlowEnabled': True, 'serviceAccountsEnabled': False,
         'redirectUris': ['https://localhost:5191/signin-oidc'],
         'attributes': {'pkce.code.challenge.method': 'S256',
                        'post.logout.redirect.uris': 'https://localhost:5191/signout-callback-oidc'},
         'fullScopeAllowed': True, 'protocolMappers': [roles, attribute('tenant_id')]},
        {**client, 'clientId': 'dockflow-api', 'bearerOnly': True,
         'standardFlowEnabled': False, 'serviceAccountsEnabled': False},
    ]
    users = []
    for key, tenant, char in [('A', TENANT_A, 'a'), ('B', TENANT_A, 'b'), ('C', TENANT_B, 'c')]:
        app = f'carrier-{char}'
        clients.append({**client, 'clientId': app, 'secret': values[f'DOCKFLOW_CARRIER_{key}_SECRET'],
                        'standardFlowEnabled': False, 'serviceAccountsEnabled': True,
                        'fullScopeAllowed': True, 'protocolMappers': [
                            roles, audience, constant('tenant_id', tenant),
                            constant('carrier_id', f'{char*8}-{char*4}-{char*4}-{char*4}-{char*12}'),
                        ]})
        users.append({'username': f'service-account-{app}', 'enabled': True,
                      'serviceAccountClientId': app, 'realmRoles': ['Carrier']})
    users.append({'username': 'manager-a', 'firstName': 'Gestão', 'lastName': 'Laboratório',
                  'email': 'manager-a@example.invalid', 'emailVerified': True, 'enabled': True,
                  'realmRoles': ['LogisticsManager', 'GateOperator', 'DockOperator'],
                  'attributes': {'tenant_id': [TENANT_A]}, 'credentials': [
                      {'type': 'password', 'value': values['DOCKFLOW_OPERATOR_PASSWORD'], 'temporary': False},
                  ]})
    return {'realm': 'dockflow', 'enabled': True, 'sslRequired': 'none', 'registrationAllowed': False,
            'resetPasswordAllowed': False, 'bruteForceProtected': True, 'accessTokenLifespan': 300,
            'ssoSessionIdleTimeout': 900, 'ssoSessionMaxLifespan': 3600,
            'defaultLocale': 'pt-BR', 'internationalizationEnabled': True, 'supportedLocales': ['pt-BR', 'en'],
            'roles': {'realm': [{'name': role} for role in ROLES]}, 'clients': clients, 'users': users}


def read_existing(path):
    if path.is_symlink() or path.stat().st_mode & 0o077:
        raise ValueError('Existing .env must be a private regular file.')
    pairs = dict(line.split('=', 1) for line in path.read_text().splitlines())
    if set(pairs) != set(KEYS) or not all(re.fullmatch('[A-Za-z0-9_-]{32,96}', x) for x in pairs.values()):
        raise ValueError('Existing .env is not a DockFlow-generated lab environment.')
    return pairs


def write_private(path, text, mode=0o600):
    fd, temp = tempfile.mkstemp(dir=path.parent)
    try:
        os.fchmod(fd, mode)
        with os.fdopen(fd, 'w', encoding='utf-8') as handle:
            handle.write(text)
        os.replace(temp, path)
    finally:
        Path(temp).unlink(missing_ok=True)


def provision(root):
    env, local = root / '.env', root / '.local'
    if env.exists() or env.is_symlink():
        values = read_existing(env)
    else:
        values = {key: secrets.token_urlsafe(36) for key in KEYS}
        fd = os.open(env, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w', encoding='utf-8') as handle:
            handle.writelines(f'{key}={value}\n' for key, value in values.items())
    local.mkdir(mode=0o700, exist_ok=True)
    if local.is_symlink() or local.stat().st_mode & 0o077:
        raise ValueError('Existing .local must be a private directory.')
    # The unprivileged Keycloak container reads the single bind-mounted file.
    # Its parent on the host is 0700, and neither path is a build input.
    write_private(local / 'dockflow-realm.json', json.dumps(realm(values), ensure_ascii=False), 0o644)
    redis = f"requirepass {values['DOCKFLOW_REDIS_PASSWORD']}\nmaxmemory 32mb\nmaxmemory-policy allkeys-lru\n"
    write_private(local / 'redis.conf', redis, 0o644)
    cert, key = local / 'tls.crt', local / 'tls.key'
    if not cert.exists() or not key.exists():
        with tempfile.TemporaryDirectory(dir=local) as temporary:
            t = Path(temporary)
            subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256',
                            '-days', '365', '-subj', '/CN=DockFlow local laboratory',
                            '-addext', 'subjectAltName=DNS:localhost,IP:127.0.0.1',
                            '-keyout', str(t / 'tls.key'), '-out', str(t / 'tls.crt')],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=True)
            (t / 'tls.key').chmod(0o600)
            os.replace(t / 'tls.key', key)
            os.replace(t / 'tls.crt', cert)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, default=Path(__file__).resolve().parents[1])
    args = parser.parse_args()
    try:
        provision(args.directory)
    except (ValueError, OSError, subprocess.SubprocessError):
        parser.exit(1, 'DockFlow lab setup refused; confirm directory ownership, .env and openssl.\n')
    print('DockFlow lab files ready; credentials are private and were not printed.')


if __name__ == '__main__':
    main()
