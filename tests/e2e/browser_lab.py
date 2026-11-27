#!/usr/bin/env python3
"""Drive the real Keycloak sign-in and authenticated DockFlow MVC view."""
import argparse
from datetime import date, timedelta
import os
from pathlib import Path
import re
import sys
from urllib.parse import urlsplit
from uuid import uuid4

APP = 'https://localhost:5191'
IDP = 'http://localhost:5192'
ALLOWED = {APP, IDP}
EVIDENCE = Path(__file__).resolve().parent / 'artifacts'


def origin(url):
    parts = urlsplit(url)
    return f'{parts.scheme}://{parts.netloc}'


def run(args, password, sync_playwright, expect):
    step = 'launch Chromium'
    with sync_playwright() as pw:
        # Browser-only TLS exception; external requests also fail closed at the
        # proxy layer, including redirects not surfaced to route handlers.
        with pw.chromium.launch(args=['--proxy-server=http://127.0.0.1:1',
                                       '--proxy-bypass-list=<-loopback>;localhost:5191;localhost:5192',
                                       '--disable-background-networking']) as browser:
            context = browser.new_context(ignore_https_errors=True, service_workers='block',
                                          accept_downloads=False, viewport={'width': 1440, 'height': 960})
            context.route('**/*', lambda route: route.continue_() if origin(route.request.url) in ALLOWED
                          else route.abort('blockedbyclient'))
            context.set_default_timeout(10_000)
            context.set_default_navigation_timeout(15_000)
            context.add_init_script("""window.dockFlowInvalidations = 0;
                const LiveSource = window.EventSource;
                window.EventSource = class extends LiveSource {
                    constructor(url, options) {
                        super(url, options);
                        this.addEventListener('message', e => {
                            if (new URL(url, location.href).pathname === '/hubs/board' &&
                                e.data.includes('"target":"Invalidate"')) window.dockFlowInvalidations++;
                        });
                    }
                };""")
            page = context.new_page()
            captures = None
            target = APP + args.path + (f'?day={args.day.isoformat()}' if args.day else '')
            try:
                step = 'protected page redirects to the local Keycloak realm'
                page.goto(target, wait_until='domcontentloaded')
                expect(page).to_have_url(re.compile(r'^http://localhost:5192/realms/dockflow/protocol/openid-connect/auth\?'))
                page.locator('#username').fill('manager-a')
                # Do not expose the fill argument through Call Logs, tracing, videos or HARs.
                step = 'sign in to Keycloak as manager-a'
                page.locator('#password').fill(password)
                page.locator('#kc-login').click()
                step = 'authenticated appointment view loaded at the same protected URL'
                expect(page).to_have_url(target)
                expect(page.get_by_role('heading', name=args.heading, exact=True)).to_be_visible()
                if args.completed is not None:
                    step = 'the authenticated analytics count matches PostgreSQL'
                    expect(page.get_by_text(re.compile(rf'^{args.completed} descarga\(s\) concluída\(s\) hoje'))).to_be_visible()
                # The rowheader contains plate plus cargo/weight text.
                vehicle = (page.get_by_role('rowheader', name=re.compile(r'^' + re.escape(args.vehicle) + r'(?:\s|$)'))
                           if args.vehicle else None)
                if vehicle is not None:
                    step = 'synthetic carrier appointment visible to tenant operator'
                    expect(vehicle).to_be_visible()
                step = 'session survives reloading the protected page'
                result = page.reload(wait_until='domcontentloaded')
                if result is None or result.status != 200:
                    raise RuntimeError('reload did not return HTTP 200')
                expect(page).to_have_url(target)
                expect(page.get_by_role('heading', name=args.heading, exact=True)).to_be_visible()
                step = 'day navigation and mobile width'
                today_key = page.locator('#docas time').get_attribute('datetime')
                if args.day and today_key != args.day.isoformat():
                    raise RuntimeError('requested UTC day not rendered')
                page.get_by_role('link', name='Próximo dia').click()
                next_key = (date.fromisoformat(today_key) + timedelta(days=1)).isoformat()
                expect(page.locator('#docas time')).to_have_attribute('datetime', next_key)
                page.goto(target, wait_until='domcontentloaded')
                page.set_viewport_size({'width': 390, 'height': 844})
                if not page.evaluate('document.documentElement.scrollWidth === document.documentElement.clientWidth'):
                    raise RuntimeError('mobile page has horizontal overflow')
                if args.demo_captures:
                    EVIDENCE.mkdir(mode=0o700, exist_ok=True)
                    page.screenshot(path=str(EVIDENCE / 'dispatch-mobile.png'), full_page=True)
                page.set_viewport_size({'width': 1440, 'height': 960})
                if vehicle is not None:
                    expect(vehicle).to_be_visible()
                    step = 'tenant board receives a live invalidation while an operator moves the visit'
                    observer = context.new_page()
                    observer.goto(target, wait_until='domcontentloaded')
                    expect(observer.get_by_text('Conectado · ao vivo', exact=True)).to_be_visible()
                    row = page.get_by_role('row').filter(has=vehicle)
                    csrfless = context.request.post(APP + row.locator('form').first.get_attribute('action'),
                                                    form={'action':'CheckIn', 'version':'1'})
                    if csrfless.status != 400:
                        raise RuntimeError('MVC accepted an unsafe request without antiforgery token')
                    page.bring_to_front()
                    if args.demo_captures:
                        captures = EVIDENCE / f'demo-{uuid4().hex}'
                        captures.mkdir(mode=0o700, parents=True)
                        page.screenshot(path=str(captures / 'frame-00.png'))
                    for i, (button, status) in enumerate([('Registrar chegada','Na fila'), ('Alocar','Alocada'),
                                                          ('Iniciar','Em operação'), ('Concluir','Concluída')], 1):
                        step = f'yard transition: {button}'
                        row.get_by_role('button', name=button, exact=True).click()
                        expect(row.get_by_text(status, exact=True)).to_be_visible()
                        expect(page.locator('#docas time')).to_have_attribute('datetime', today_key)
                        if status == 'Em operação':
                            expect(page.get_by_role('complementary', name='Ocorrências').get_by_text('Ocupada', exact=True).first).to_be_visible()
                        if captures is not None:
                            page.screenshot(path=str(captures / f'frame-{i:02d}.png'))
                    step = 'verify actual SSE invalidation, not only the polling fallback'
                    observer.wait_for_function('window.dockFlowInvalidations > 0', timeout=10_000)
                    observer.close()
                step = 'save authenticated-only screenshot'
                EVIDENCE.mkdir(mode=0o700, exist_ok=True)
                evidence = EVIDENCE / f'operator-{uuid4().hex}.png'
                page.screenshot(path=str(evidence), full_page=True, timeout=10_000)
                step = 'sign out locally and remove the MVC session cookie'
                page.get_by_role('button', name='Sair do painel', exact=True).click()
                expect(page).to_have_url(APP + '/sair')
                expect(page.get_by_role('heading', name='Você saiu do painel.', exact=True)).to_be_visible()
                if any(cookie['name'] == '__Host-DockFlow' for cookie in context.cookies(APP)):
                    raise RuntimeError('MVC session cookie was retained')
                print(f'PASS Keycloak login, protected MVC view, reload, app logout; evidence={evidence}')
                if vehicle is not None:
                    print('PASS MVC CSRF guard, complete yard lifecycle and live SignalR SSE invalidation')
                    if captures is not None:
                        print(f'Authenticated-only demo frames: {captures}')
            except Exception as exc:
                codes = sorted(set(re.findall(r'\bERR_[A-Z_]+\b', str(exc))))[:3]
                category = ': ' + ', '.join(codes) if codes else ''
                return f'FAIL: {step} ({type(exc).__name__}{category}); browser call logs suppressed'
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--path', default='/', help='protected MVC path, not an absolute URL')
    parser.add_argument('--heading', default='Painel de docas', help='exact visible appointment heading')
    parser.add_argument('--vehicle', help='optional synthetic plate already seeded in tenant A')
    parser.add_argument('--demo-captures', action='store_true', help='save authenticated progress frames for a short demo')
    parser.add_argument('--completed', type=int, help='expected completed-today counter')
    parser.add_argument('--day', type=date.fromisoformat, help='UTC agenda day (YYYY-MM-DD)')
    args = parser.parse_args()
    if not args.path.startswith('/') or any(c in args.path for c in '\\?#\r\n') or args.path.startswith('//'):
        parser.error('--path must be an unambiguous absolute path on the fixed app origin')
    if args.demo_captures and not args.vehicle:
        parser.error('--demo-captures requires --vehicle')
    password = os.environ.get('DOCKFLOW_OPERATOR_PASSWORD')
    if not password:
        sys.exit('Export DOCKFLOW_OPERATOR_PASSWORD (do not print it)')
    # Playwright's driver inherits these switches; disable user-configured debug logs.
    for key in ('DEBUG', 'PWDEBUG'):
        os.environ.pop(key, None)
    try:
        from playwright.sync_api import expect, sync_playwright
    except ImportError:
        sys.exit('Install the Playwright Python dependency and Chromium; see README')
    try:
        error = run(args, password, sync_playwright, expect)
    except Exception as exc:
        error = f'FAIL: browser setup/teardown ({type(exc).__name__}); check Playwright Chromium install'
    if error:
        sys.exit(error)


if __name__ == '__main__':
    main()
