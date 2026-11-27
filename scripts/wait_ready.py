"""Wait for the explicitly bound local API without accepting a different certificate."""

import ssl
import time
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import build_opener, HTTPSHandler, ProxyHandler


def main():
    certificate = Path(__file__).resolve().parents[1] / '.local/tls.crt'
    context = ssl.create_default_context(cafile=str(certificate))
    opener = build_opener(ProxyHandler({}), HTTPSHandler(context=context))
    deadline = time.monotonic() + 85
    while time.monotonic() < deadline:
        try:
            with opener.open('https://localhost:5191/health/ready', timeout=3) as response:
                if response.status == 200:
                    print('PASS: DockFlow is ready over the lab TLS certificate.')
                    return
        except (URLError, HTTPError, TimeoutError, ConnectionError):
            pass
        time.sleep(1)
    raise SystemExit('DockFlow local readiness timed out. Inspect migrate, web and gate logs.')


if __name__ == '__main__':
    main()
