# CLAUDE.md

Read `AGENTS.md` first. The public C# API is pinned by `PublicAPI.*.txt` baselines; see the Public C# API
section in `AGENTS.md` before adding, changing or removing a public type or member.

## Tests

No tautological tests. Every test must be able to fail when the behaviour it names breaks, and only then.

- Do not assert that a mock or stub returns what the test configured it to return, or that a value
  the test just set reads back the same.
- Do not compute the expected value with the code under test, or with a copy of its logic. Write the
  expected value out, or derive it from an independent source (the fixture's known keys, a recorded
  response, Umbraco's own output).
- Do not test constants, records, auto-properties or DI registrations against themselves.
- Test behaviour through the seam a site uses (a Delivery API request, a publish, a ledger event), not
  private steps whose only check is that they ran.
- Before calling a new test done, break the code it covers (or revert the fix) and watch it fail. If it
  still passes, it is tautological: rewrite or delete it.
