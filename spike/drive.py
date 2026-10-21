#!/usr/bin/env python3
"""Drives the spike site through the request matrix and the content operations, then prints how
each collection strategy did against the expected keys of each request.

Usage: python3 spike/drive.py <run-name>   (site on http://localhost:5317)
Raw logs land in spike/results/raw/<run-name>.json.
"""
import json
import os
import sys
import urllib.request

BASE = "http://localhost:5317"
API = BASE + "/umbraco/delivery/api/v2"
RUN = sys.argv[1] if len(sys.argv) > 1 else "run"


def call(path, label=None, headers=None):
    request = urllib.request.Request(path, headers={**(headers or {}), **({"X-Spike-Label": label} if label else {})})
    try:
        with urllib.request.urlopen(request) as response:
            return response.status, dict(response.headers), response.read()
    except urllib.error.HTTPError as error:
        return error.code, dict(error.headers), error.read()


def spike(path):
    return json.loads(call(BASE + "/spike/" + path)[2])


def op(name):
    return spike("op?name=" + name)


fx = spike("fixture")
c = lambda key: "c:" + key
m = lambda key: "m:" + key
page_set = set(fx["expectedPage"])
preview_headers = {"Preview": "true", "Api-Key": "spike-local-preview-key"}

# label -> (url, headers, expected keys or None to skip the check, compare extras)
requests = {
    "route": (API + "/content/item/fixture/", None, page_set),
    "id": (API + f"/content/item/{fx['page']}", None, page_set),
    # A is rendered with its own properties, so its picker (G) is rendered too.
    "ids": (API + f"/content/items?id={fx['page']}&id={fx['a']}", None, page_set | {c(fx["g"])}),
    # A listing renders every page; only the fixture page's keys are checked.
    "query": (API + "/content?filter=contentType:spikePage&take=50", None, page_set),
    "expand": (API + f"/content/item/{fx['page']}?expand=properties[picker]", None, page_set | {c(fx["g"])}),
    "preview": (API + f"/content/item/{fx['page']}", preview_headers, page_set),
    "media": (API + f"/media/item/{fx['m1']}", None, {m(fx["m1"])}),
    "variant-da": (API + f"/content/item/{fx['variant']}", {"Accept-Language": "da-DK"}, {c(fx["variant"]), c(fx["c"])}),
    "variant-en": (API + f"/content/item/{fx['variant']}", {"Accept-Language": "en-US"}, {c(fx["variant"]), c(fx["b"])}),
}
exact = {"route", "id", "expand", "preview", "media", "variant-da", "variant-en"}

op("reset")
spike("clear")

# Cold: republish the page before each request, which drops the converted values cached on it.
for name, (url, headers, _) in requests.items():
    op("touch-page")
    call(url, f"{name}-cold", headers)
for name, (url, headers, _) in requests.items():
    call(url, f"{name}-warm", headers)

for change in ("publish-a", "publish-f", "save-m1"):
    op(change)
    for name in ("route", "id"):
        url, headers, _ = requests[name]
        call(url, f"{name}-after-{change}", headers)

# Renaming C changes the route of its child. Request the child warm, rename C, request it again.
child_url = API + f"/content/item/{fx['childOfC']}"
routes = {}
for label in ("child-before-rename", "child-warm"):
    routes[label] = json.loads(call(child_url, label)[2])["route"]["path"]
op("rename-c")
routes["child-after-rename"] = json.loads(call(child_url, "child-after-rename")[2])["route"]["path"]
true_route = json.loads(call(child_url, "child-truth", {"Preview": "true", "Api-Key": "spike-local-preview-key"})[2])["route"]["path"]

# Operations whose notifications and refresher payloads the ledger design depends on.
for change in ("save-a", "publish-a", "unpublish-b", "republish-b", "move-mover-under-c",
               "sort-site", "publish-variant-da", "unpublish-variant-da", "trash-d"):
    op(change)

log = spike("log")
os.makedirs(os.path.join(os.path.dirname(__file__), "results", "raw"), exist_ok=True)
with open(os.path.join(os.path.dirname(__file__), "results", "raw", RUN + ".json"), "w") as handle:
    json.dump({"routes": routes, "trueRoute": true_route, **log}, handle, indent=2)

strategies = ("render", "objects", "json", "refs", "refsCulture")


def keys(entry, strategy):
    return {k for k in entry["sets"].get(strategy, []) if k[:2] in ("c:", "m:")}


def cell(entry, strategy, want, compare_extras):
    got = keys(entry, strategy)
    missing, extra = want - got, got - want
    text = "all" if not missing else f"missing {len(missing)}/{len(want)}"
    if compare_extras and extra:
        text += f", +{len(extra)}"
    return text


print(f"## {RUN}\n")
print("Cells: keys missing from the expected set, then `+n` extra keys (exact requests only).")
print("`resolved` and `refs2` list counts. Rich text shape is read from the body.\n")
print("| request | ran | rich text | " + " | ".join(strategies) + " | resolved | refs2 |")
print("|---|---|---|" + "---|" * (len(strategies) + 2))
details = []
for entry in log["requests"]:
    label = entry["label"]
    base = label.rsplit("-", 1)[0] if label.endswith(("-cold", "-warm")) else label.split("-after-")[0]
    if base not in requests:
        continue
    want = requests[base][2]
    compare = base in exact
    cells = [cell(entry, s, want, compare) for s in strategies]
    print(f"| {label} | {entry['controllerRan']} | {entry['richText']} | " + " | ".join(cells)
          + f" | {len(keys(entry, 'resolved'))} | {len(keys(entry, 'refs2'))} |")
    for strategy in strategies:
        got = keys(entry, strategy)
        if want - got or (compare and got - want):
            details.append(f"- {label} {strategy}: missing {sorted(want - got)}" + (f", extra {sorted(got - want)}" if compare and got - want else ""))

print("\n### Child of C, renamed parent\n")
print(f"- route before rename: `{routes['child-before-rename']}`, warm: `{routes['child-warm']}`")
print(f"- route served after renaming C: `{routes['child-after-rename']}`; current route (preview): `{true_route}`")
ran = {e["label"]: e["controllerRan"] for e in log["requests"]}
print(f"- controller ran after rename: {ran.get('child-after-rename')}")

print("\n### Headers\n")
for entry in log["requests"]:
    if entry["label"] in ("route-cold", "preview-cold", "variant-da-cold", "route-warm"):
        print(f"- {entry['label']}: {json.dumps(entry['headers'])}")

print("\n### Differences per strategy\n")
print("\n".join(details) if details else "None.")
