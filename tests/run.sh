#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
run_dir=$(mktemp -d)
container="s3bulk-tests-${RANDOM}-$$"
moto_pid=""
cleanup() {
  if [[ -n "$moto_pid" ]]; then kill "$moto_pid" 2>/dev/null || true; wait "$moto_pid" 2>/dev/null || true; fi
  docker rm -f "$container" >/dev/null 2>&1 || true
  rm -rf "$run_dir"
}
trap cleanup EXIT
# These fixed loopback ports must be free; the runner does not reset existing services.
python - <<'CHECK_PORTS'
import socket
for port in (15432, 19000):
    with socket.socket() as s:
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        s.bind(('127.0.0.1', port))
CHECK_PORTS
python -m venv "$run_dir/venv"
"$run_dir/venv/bin/pip" --disable-pip-version-check -q install 'moto[server]==5.1.14'
docker run -d --name "$container" -e POSTGRES_HOST_AUTH_METHOD=trust -p 127.0.0.1:15432:5432 postgres:17-alpine >/dev/null
ready=false
for ((i=0;i<30;i++)); do
  if docker exec "$container" pg_isready -U postgres >/dev/null 2>&1; then ready=true; break; fi
  sleep 1
done
[[ "$ready" == true ]]
"$run_dir/venv/bin/moto_server" -H 127.0.0.1 -p 19000 >"$run_dir/moto.log" 2>&1 &
moto_pid=$!
ready=false
for ((i=0;i<30;i++)); do
  kill -0 "$moto_pid"
  if curl -fsS http://127.0.0.1:19000/ >/dev/null; then ready=true; break; fi
  sleep 1
done
[[ "$ready" == true ]]
dotnet restore --locked-mode
dotnet build --no-restore
dotnet restore tests/Integration.csproj --locked-mode
dotnet run --no-restore --project tests/Integration.csproj -- "$PWD/bin/Debug/net10.0/S3BulkDelete.dll"
