#!/usr/bin/env python3
"""Checks TheBuilder.CacheTags (Milestone 2) against the spike fixture: the response header, what the
mapping records for each kind of request, and what the ledger plans for each kind of edit. Expected
keys come from the fixture (spike/TheBuilder.CacheTags.Spike/Seeding/FixtureSeeder.cs), never from
the package.

Usage: python3 spike/check-package.py [base-url]   (default http://localhost:5330)
Reads the site's SQLite database directly; needs the sqlite3 CLI.
The last check prunes every mapping row, and the running site remembers for an hour which renders it
wrote, so restart the site before running this again.
"""
import json
import os
import subprocess
import sys
import time
import urllib.parse
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5330"
API = BASE + "/umbraco/delivery/api/v2"
DB = os.path.join(os.path.dirname(__file__), "TheBuilder.CacheTags.Spike", "umbraco", "Data", "Umbraco.sqlite.db")
failures = []


def call(url, headers=None):
    request = urllib.request.Request(url, headers=headers or {})
    with urllib.request.urlopen(request) as response:
        return dict(response.headers), response.read()


def sql(query):
    out = subprocess.run(["sqlite3", DB, query], capture_output=True, text=True, check=True).stdout
    return [line for line in out.splitlines() if line]


def check(name, condition, detail=""):
    print(("ok   " if condition else "FAIL ") + name + ("" if condition else f": {detail}"))
    if not condition:
        failures.append(name)


def op(name):
    return json.loads(call(BASE + "/spike/op?name=" + name)[1])


def key_of(url, headers=None):
    response_headers, _ = call(url, headers)
    return response_headers.get("X-Umbraco-Cache-Keys")


def recorded(response_key):
    time.sleep(1.5)  # the writer batches in the background
    return set(sql(f"SELECT CacheKey FROM theBuilderCacheTagsResponseKey WHERE ResponseKey = '{response_key}'"))


def latest_plan(kind):
    rows = sql(f"SELECT Plan FROM theBuilderCacheTagsEvent WHERE Kind = '{kind}' ORDER BY CreatedUtc DESC LIMIT 1")
    return json.loads(rows[0]) if rows else None


fx = json.loads(call(BASE + "/spike/fixture")[1])
c = lambda key: "content-" + key
m = lambda key: "media-" + key
page_keys = {k.replace("c:", "content-").replace("m:", "media-") for k in fx["expectedPage"]}

op("reset")

# Requests: the header and the mapping.
page_key = key_of(API + "/content/item/fixture/")
check("an item response carries one response- key", page_key is not None and page_key.startswith("response-"), page_key)
check("the item mapping is exactly the fixture's expected set", recorded(page_key) == page_keys, sorted(recorded(page_key) ^ page_keys))
check("a repeat request gets the same key", key_of(API + "/content/item/fixture/") == page_key)

expanded = key_of(API + f"/content/item/{fx['page']}?expand=properties[picker]")
check("an expanded picker adds what the picked item references", recorded(expanded) == page_keys | {c(fx["g"])}, sorted(recorded(expanded) ^ (page_keys | {c(fx['g'])})))

da = key_of(API + f"/content/item/{fx['variant']}", {"Accept-Language": "da-DK"})
en = key_of(API + f"/content/item/{fx['variant']}", {"Accept-Language": "en-US"})
check("each culture has its own key", da != en)
check("da-DK records only the da-DK picker", recorded(da) == {c(fx["variant"]), c(fx["c"])}, sorted(recorded(da)))
check("en-US records only the en-US picker", recorded(en) == {c(fx["variant"]), c(fx["b"])}, sorted(recorded(en)))

listing = key_of(API + "/content?filter=contentType:spikePage&take=50")
check("a content type listing records its type selector", "type-spikepage" in recorded(listing), sorted(recorded(listing)))
children = key_of(API + "/content?fetch=children:/")
check("the root listing records the children of the page routed at /", "children-" + fx["site"] in recorded(children), sorted(recorded(children)))
cultures = dict(row.split("|") for row in sql(f"SELECT ResponseKey, Culture FROM theBuilderCacheTagsResponse WHERE ResponseKey IN ('{page_key}', '{da}')"))
check("the mapping stores the culture served, not the header", cultures == {page_key: "en-us", da: "da-dk"}, cultures)

preview_headers, _ = call(API + f"/content/item/{fx['page']}", {"Preview": "true", "Api-Key": "spike-local-preview-key"})
check("preview gets no key", "X-Umbraco-Cache-Keys" not in preview_headers)
check("preview gets no-store", preview_headers.get("Cache-Control") == "no-store", preview_headers.get("Cache-Control"))

media_key = key_of(API + f"/media/item/{fx['m1']}")
check("a media response records the media item", recorded(media_key) == {m(fx["m1"])}, sorted(recorded(media_key)))

# Edits: the ledger plans, and the responses they reach.
events = lambda: int(sql("SELECT COUNT(*) FROM theBuilderCacheTagsEvent")[0])
before = events()
op("save-a")
check("a draft save plans nothing", events() == before, f"{events() - before} events")

op("publish-a")
plan = latest_plan("Published")
check("publishing A plans A, its parent's listings, its type and the index",
      plan and {c(fx["a"]), "children-" + fx["site"], "type-spikepage", "search-deliveryapicontentindex"} <= set(plan["keys"]), plan)
reached = set(sql("SELECT DISTINCT ResponseKey FROM theBuilderCacheTagsResponseKey WHERE CacheKey IN ('" + "','".join(plan["keys"]) + "')"))
check("that plan reaches the fixture page's response", page_key in reached, sorted(reached))
check("that plan reaches the type listing", listing in reached)
check("that plan reaches the root listing", children in reached)
check("that plan does not reach the variant's responses", da not in reached and en not in reached)

op("rename-c")
plan = latest_plan("Published")
check("renaming C reaches its child's route", plan and c(fx["childOfC"]) in plan["keys"], plan)

op("sort-site")
plan = latest_plan("Sorted")
check("a sort plans only the parent's listings", plan and plan["keys"] == ["children-" + fx["site"]], plan)

op("move-mover-under-c")
plan = latest_plan("Moved")
check("a move plans both parents' listings", plan and {"children-" + fx["site"], "children-" + fx["c"]} <= set(plan["keys"]), plan)

op("publish-variant-da")
plan = latest_plan("Published")
check("a one-culture publish is limited to that culture", plan and plan["cultures"] == ["da-DK"], plan)

op("unpublish-b")
plan = latest_plan("Unpublished")
check("an unpublish plans the item", plan and c(fx["b"]) in plan["keys"], plan)

op("save-m1")
plan = latest_plan("MediaSaved")
check("a media save plans the media item", plan and m(fx["m1"]) in plan["keys"], plan)

# The admin lookup API (Milestone 5 builds the backoffice view on it).
token_request = urllib.request.Request(
    BASE + "/umbraco/management/api/v1/security/back-office/token",
    data=urllib.parse.urlencode({
        "grant_type": "client_credentials",
        "client_id": "umbraco-back-office-spike-check",
        "client_secret": "spike-local-check-client-secret",
    }).encode(),
    headers={"Content-Type": "application/x-www-form-urlencoded"})
with urllib.request.urlopen(token_request) as response:
    bearer = {"Authorization": "Bearer " + json.loads(response.read())["access_token"]}
lookup = lambda key: json.loads(call(BASE + "/umbraco/management/api/v1/cache-tags/lookup?key=" + key, bearer)[1])

described = lookup(page_key)
check("the lookup describes a response key's request", described["request"]["path"] == "/umbraco/delivery/api/v2/content/item/fixture/", described)
check("the lookup lists the keys a response covers, with names",
      {k["key"] for k in described["keys"]} == page_keys and any(k["name"] == "Target A" for k in described["keys"]), described["keys"])
covering = lookup(c(fx["a"]))
check("the lookup lists the responses that cover an item", page_key in {r["key"] for r in covering["responses"]}, covering)
changes = json.loads(call(BASE + "/umbraco/management/api/v1/cache-tags/changes?take=5", bearer)[1])
check("the change list shows the latest change first", changes and changes[0]["kind"] == "MediaSaved", [c_["kind"] for c_ in changes])

removed = int(op("prune-everything")["outcome"])
left = int(sql("SELECT COUNT(*) FROM theBuilderCacheTagsResponse")[0]) + int(sql("SELECT COUNT(*) FROM theBuilderCacheTagsResponseKey")[0])
check("pruning runs on Umbraco's database provider and removes rows past the cutoff", removed > 0 and left == 0, (removed, left))

print(f"\n{len(failures)} failed" if failures else "\nall passed")
sys.exit(1 if failures else 0)
