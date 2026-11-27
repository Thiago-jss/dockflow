import http from 'k6/http';
import { check } from 'k6';
import { Rate } from 'k6/metrics';

// Fixed authorized target. No login, redirects, writes, or credential logging.
const target = 'https://localhost:5191/api/v1/appointments';
const badShape = new Rate('appointments_invalid_response');
const bearer = __ENV.DOCKFLOW_LOAD_BEARER;
const verifyLabTLS = __ENV.DOCKFLOW_LOAD_INSECURE_LAB !== '1';
const expected200 = http.expectedStatuses(200);
const rate = Number(__ENV.DOCKFLOW_LOAD_RPS || '1');
if (!Number.isInteger(rate) || rate < 1 || rate > 20) {
  throw new Error('DOCKFLOW_LOAD_RPS must be an integer between 1 and 20');
}

export const options = {
  insecureSkipTLSVerify: !verifyLabTLS,
  maxRedirects: 0,
  summaryTrendStats: ['med', 'p(95)', 'p(99)', 'max'],
  systemTags: ['status', 'method', 'name', 'scenario', 'expected_response'],
  scenarios: {
    appointment_reads: {
      executor: 'constant-arrival-rate', rate, timeUnit: '1s', duration: '30s',
      preAllocatedVUs: 10, maxVUs: 30, gracefulStop: '5s',
    },
  },
  thresholds: {
    'http_req_duration{name:appointment_list,expected_response:true}': ['p(95)<250', 'p(99)<500'],
    'http_req_failed{name:appointment_list}': ['rate<0.01'],
    appointments_invalid_response: ['rate<0.01'],
    dropped_iterations: ['count==0'],
    iterations: [`count>=${rate * 30}`],
  },
};

function readList(name = 'appointment_list') {
  return http.get(target, {
    headers: { Authorization: `Bearer ${bearer}`, Accept: 'application/json' },
    tags: { name }, redirects: 0, timeout: '3s',
    responseCallback: expected200,
  });
}

function validList(response) {
  if (response.status !== 200) return false;
  try {
    const value = response.json();
    return Array.isArray(value) && value.every((row) => row !== null && typeof row.id === 'string');
  } catch (_) {
    return false;
  }
}

export function setup() {
  if (!bearer || !/^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(bearer)) {
    throw new Error('Export a local Keycloak JWT as DOCKFLOW_LOAD_BEARER without printing it');
  }
  const probe = readList('appointment_list_preflight');
  if (!validList(probe)) throw new Error(`Appointment list preflight failed (status=${probe.status}); verify lab, TLS, JWT and API shape`);
}

export default function () {
  const valid = validList(readList());
  badShape.add(!valid);
  check(valid, { 'authenticated response is an appointment list': (value) => value });
}
