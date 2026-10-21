#!/usr/bin/env bash
# Runs the spike matrix against the three site configurations. Site on http://localhost:5317.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
site="$here/TheBuilder.CacheTags.Spike"
mkdir -p "$here/results/raw"

run() {
  local name="$1"; shift
  (cd "$site" && env ASPNETCORE_ENVIRONMENT=Development "$@" dotnet run --no-build --urls http://localhost:5317 > "$here/results/raw/$name.site.log" 2>&1) &
  local pid=$!
  until curl -sf http://localhost:5317/spike/fixture > /dev/null; do sleep 2; done
  python3 "$here/drive.py" "$name" > "$here/results/$name.md"
  pkill -f TheBuilder.CacheTags.Spike || true
  wait "$pid" 2>/dev/null || true
}

run markup-nocache Umbraco__CMS__DeliveryApi__RichTextOutputAsJson=false Umbraco__CMS__DeliveryApi__OutputCache__Enabled=false
run json-nocache Umbraco__CMS__DeliveryApi__RichTextOutputAsJson=true Umbraco__CMS__DeliveryApi__OutputCache__Enabled=false
run markup-outputcache Umbraco__CMS__DeliveryApi__RichTextOutputAsJson=false Umbraco__CMS__DeliveryApi__OutputCache__Enabled=true
