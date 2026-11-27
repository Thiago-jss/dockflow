#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
pg="dockflow-worker-test-pg-$$"
mq="dockflow-worker-test-mq-$$"
pg_id= mq_id=
cleanup() {
  if [[ -n $pg_id ]]; then docker rm -fv "$pg_id" >/dev/null; fi
  if [[ -n $mq_id ]]; then docker rm -fv "$mq_id" >/dev/null; fi
}
trap cleanup EXIT
password=$(openssl rand -hex 20)
pg_id=$(docker run -d --name "$pg" -p 127.0.0.1::5432 -e POSTGRES_PASSWORD="$password" \
  postgres:17-alpine@sha256:f9cf3e1f0066f0eb0c48707d864f0ac5941e9ff454a072077d97a4d278d07d73)
mq_id=$(docker run -d --name "$mq" --hostname dockflow-worker-test -p 127.0.0.1::5672 \
  -e RABBITMQ_DEFAULT_USER=dockflowtest -e RABBITMQ_DEFAULT_PASS="$password" \
  -e 'RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS=+S 2:2 +A 4' \
  rabbitmq:4.3-management-alpine@sha256:92d5e539a2f376b0c4ba13d03ec52c1e22a9df1a98be3739ccfb452ac83a4ede)
pg_port=$(docker port "$pg_id" 5432/tcp); pg_port=${pg_port##*:}
mq_port=$(docker port "$mq_id" 5672/tcp); mq_port=${mq_port##*:}
ready=false
for ((i=0; i<90; i++)); do
  if docker exec "$pg_id" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 &&
     timeout 2 docker exec "$mq_id" sh -c 'nc -z -w 1 127.0.0.1 5672' >/dev/null 2>&1 &&
     timeout 2 bash -c 'exec 9<>"/dev/tcp/127.0.0.1/$1"' _ "$mq_port" 2>/dev/null; then ready=true; break; fi
  sleep 1
done
if ! $ready; then
  printf 'Disposable PostgreSQL or RabbitMQ did not become reachable.\n' >&2
  docker inspect --format '{{.Name}}: {{.State.Status}} (exit {{.State.ExitCode}})' "$pg_id" "$mq_id" >&2
  docker exec "$pg_id" pg_isready -h 127.0.0.1 -U postgres >&2 || true
  docker exec "$mq_id" sh -c 'nc -z -w 1 127.0.0.1 5672 && printf "AMQP reachable inside container\n"' >&2 || true
  exit 1
fi
python3 -c 'from pathlib import Path; print(Path("src/DockFlow.Infrastructure/Migrations/InitialDockFlow.cs").read_text().split(chr(34)*3)[1])' |
  docker exec -i "$pg_id" psql -U postgres -v ON_ERROR_STOP=1 >/dev/null
docker run --rm --network host -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e TestDatabase="Host=127.0.0.1;Port=$pg_port;Database=postgres;Username=postgres;Password=$password" \
  -e TestBrokerPort="$mq_port" -e TestBrokerPassword="$password" -v "$PWD:/repo:ro" -w /tmp \
  mcr.microsoft.com/dotnet/sdk:10.0 sh -ec '
    cp /repo/global.json /repo/Directory.Build.props .
    mkdir src tests
    cp -r /repo/src/DockFlow.Worker src/
    cp -r /repo/tests/DockFlow.WorkerTests tests/
    dotnet build tests/DockFlow.WorkerTests --no-incremental
    dotnet run --project tests/DockFlow.WorkerTests --no-build -- --integration
  '
