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
pg_id=$(docker run -d --name "$pg" -p 127.0.0.1::5432 -e POSTGRES_PASSWORD="$password" postgres:17-alpine)
mq_id=$(docker run -d --name "$mq" -p 127.0.0.1::5672 -e RABBITMQ_DEFAULT_USER=dockflowtest \
  -e RABBITMQ_DEFAULT_PASS="$password" rabbitmq:4.3-management-alpine)
ready=false
for ((i=0; i<45; i++)); do
  if docker exec "$pg_id" pg_isready -h 127.0.0.1 -U postgres >/dev/null &&
     docker exec "$mq_id" rabbitmq-diagnostics -q check_port_connectivity >/dev/null 2>&1; then ready=true; break; fi
  sleep 1
done
if ! $ready; then printf 'Disposable PostgreSQL or RabbitMQ failed to start.\n' >&2; exit 1; fi
pg_port=$(docker port "$pg_id" 5432/tcp); pg_port=${pg_port##*:}
mq_port=$(docker port "$mq_id" 5672/tcp); mq_port=${mq_port##*:}
python3 -c 'from pathlib import Path; print(Path("src/DockFlow.Infrastructure/InitialDockFlow.cs").read_text().split(chr(34)*3)[1])' |
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
