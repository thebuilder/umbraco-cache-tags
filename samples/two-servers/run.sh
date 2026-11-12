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

# Each server applies the other's changes when it next syncs Umbraco's cache instructions: every 5
# to 10 seconds by default, which every edit on B would then wait for. Syncing every second keeps
# the suite waiting on the package rather than on Umbraco's timer. SQL Server only: on SQLite this
# sync can deadlock with a save (see the one-server job in .github/workflows/verify.yml).
start() {
  local server="$1" port="$2"
  (
    cd "$out/$server"
    ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS="http://localhost:$port" \
    ConnectionStrings__umbracoDbDSN="$connection" \
    ConnectionStrings__umbracoDbDSN_ProviderName=Microsoft.Data.SqlClient \
    Umbraco__CMS__WebRouting__UmbracoApplicationUrl="http://localhost:$port" \
    Umbraco__CMS__Global__DatabaseServerMessenger__TimeBetweenSyncOperations="00:00:01" \
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

# Winter market is the last item the example seeders create and publish, so once a server serves it
# the seeding is done. A first start on a CI runner seeds in about a minute.
seeded() {
  local url="$1" server="$2" deadline=$((SECONDS + 240))
  echo "Waiting for $server to seed the example site"
  until [[ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 -H 'Accept-Language: en-US' \
    "$url/umbraco/delivery/api/v2/content/item/e0000000-0000-0000-0000-000000000026" || true)" == 200 ]]; do
    if ! kill -0 "$(cat "$out/$server.pid")" 2>/dev/null; then
      echo "$server stopped; see $out/$server.log" >&2
      exit 1
    fi
    if (( SECONDS >= deadline )); then
      echo "$server did not seed the example site within 240 seconds; see $out/$server.log" >&2
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

# A installs and seeds the database on its first start; B joins once A has finished seeding. A
# answers before its seeding is done (the seeders run once the application has started), and a B
# that started then would find Winter market missing and seed the same items alongside A: one of
# them then fails on a duplicate key or an unpublished parent, and that server stalls or stops.
start a 5320
seeded "http://localhost:5320" a
start b 5321
echo "Both servers are up."
