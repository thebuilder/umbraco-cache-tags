#!/usr/bin/env bash
# Runs the example site as two Umbraco servers on one SQL Server database:
#
#   samples/two-servers/run.sh        start SQL Server, publish the site twice, start A then B
#   samples/two-servers/run.sh stop   stop both servers and SQL Server
#
# A serves http://localhost:5320 and B http://localhost:5321. Edit on B and read the Delivery API
# from A, and a purge must wait until A has applied the change: `UMBRACO_URL=http://localhost:5321
# pnpm e2e` in samples/nextjs does exactly that, with the Next.js site reading from A.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/out"
password='LocalOnly-Sql-42!'
connection="Server=localhost,14330;Database=CacheTags;User Id=sa;Password=$password;TrustServerCertificate=True"

stop() {
  for server in a b; do
    if [[ -f "$out/$server.pid" ]]; then
      kill "$(cat "$out/$server.pid")" 2>/dev/null || true
      rm -f "$out/$server.pid"
    fi
  done
  docker compose -f "$here/compose.yaml" down
}

start() {
  local server="$1" port="$2"
  (
    cd "$out/$server"
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="http://localhost:$port" \
    ConnectionStrings__umbracoDbDSN="$connection" \
    ConnectionStrings__umbracoDbDSN_ProviderName=Microsoft.Data.SqlClient \
    Umbraco__CMS__WebRouting__UmbracoApplicationUrl="http://localhost:$port" \
      nohup dotnet TheBuilder.CacheTags.Example.dll >"$out/$server.log" 2>&1 &
    echo $! >"$out/$server.pid"
  )
  echo "Waiting for $server on http://localhost:$port (log: $out/$server.log)"
  until curl -sf -o /dev/null "http://localhost:$port/umbraco/delivery/api/v2/content"; do
    if ! kill -0 "$(cat "$out/$server.pid")" 2>/dev/null; then
      echo "$server stopped; see $out/$server.log" >&2
      exit 1
    fi
    sleep 2
  done
}

if [[ "${1:-}" == "stop" ]]; then
  stop
  exit 0
fi

docker compose -f "$here/compose.yaml" up -d --wait
docker compose -f "$here/compose.yaml" exec -T sql /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$password" \
  -Q "IF DB_ID('CacheTags') IS NULL CREATE DATABASE CacheTags"

# Two copies, so each server has its own content root: its own Examine index, logs and MainDom.
rm -rf "$out/a" "$out/b"
dotnet publish "$here/../TheBuilder.CacheTags.Example" -c Release -o "$out/a" --nologo -v q
cp -R "$out/a" "$out/b"

# A installs and seeds the database on its first start; B joins once it is up.
start a 5320
start b 5321
echo "Both servers are up."
