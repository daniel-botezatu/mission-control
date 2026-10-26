# Benchmarks

## October 21 real recap comparison: quality gate failed

Installation note, October 25: the user subsequently requested and received 0.4.10. The measured quality limitations below remain unresolved; installation does not change these evaluation results.

All three completed native Claude runs used Opus 5.5, private Slack allowed, the explicit Amsterdam calendar day October 21, 2026, and all **233 messages across seven conversations**. No outline calls occurred in the new runs. The independent scorer rescored all answers against the same 24 component-level criteria; the pricing disagreement was excluded and legitimate newer live states were allowed.

| Run | Total tokens | Full / partial / missing | Seconds | CLI USD estimate |
|---|---:|---:|---:|---:|
| Supplied 0.4.8 | 10,197,409 | 16 / 7 / 1 | Not supplied | Not supplied |
| Installed 0.4.9 control | 1,696,504 | 8 / 11 / 5 | 878.84 | 3.668471 |
| Bounded-reader candidate | 2,832,486 | 10 / 11 / 3 | 499.83 | 5.3039394 |
| Final source-audit candidate | 3,686,857 | 13 / 9 / 2 | 868.06 | 6.608178 |

The final candidate used about **64% fewer tokens** than the supplied baseline, but omitted Corrections synchronization and the PHP security follow-up and left nine items partial. Its original 16-item coverage improved, while the eight added items regressed. **0.4.10 is held; 0.4.9 remains installed.** Full message delivery did not guarantee full semantic coverage. The proposed 1M-token estimate was not achieved.

Totals include input, cache creation, cache read and output. Complete CLI modelUsage is authoritative because streamed child output telemetry is incomplete; all input categories reconcile exactly. New totals include coordinator and readers; the supplied baseline excludes its main launcher. CLI costs are list-price estimates, not invoices. These are individual runs with cache/live-state/model variability, not general savings guarantees. Old cost and timing are unavailable. An earlier timed-out attempt has no complete usage total and is excluded from this table. The full evaluation, accounting caveats and blind scores stay with local evaluation artifacts and are not part of the source package.

## Historical 0.4.9 partition delivery and synthetic model overhead

A read-only comparison on October 21 (Europe/Amsterdam) reconstructed all **233 messages** identically through the full stream and nine partition assignments. Full stream: 9 message pages, 404086 wire characters; partitioned: 15 pages, 420582 characters. Summing growing response character prefixes yielded 2125896 versus 803201 (62% lower), but this excludes model prompts, ledger/merge work and caching: **it is not a token or billing measurement**. The body fingerprint matches between paths. No private message bodies were logged. October 23 returned zero retained messages in the available store.

Native Claude comparison on the same **18-message synthetic fixture**, external servers disabled:

| Metric | 0.4.8 full reader | Initial 0.4.9 three readers + merge |
|---|---:|---:|
| Uncached input tokens | 20 | 40 |
| Cache creation input tokens | 73951 | 116623 |
| Cached input tokens | 361366 | 284764 |
| Output tokens | 10439 | 20560 |
| CLI-reported cost, USD | 0.6508882 | 1.1770978 |
| Wall seconds | 98.30 | 112.45 |
| Blind fact recall | 18/18 | 18/18 |

Installed 0.4.9 small-window routing was also exercised natively: one named restricted reader, all 18 messages, five full-message pages, no outline. CLI usage: 24 uncached input tokens, 86215 cache-creation input tokens, 256104 cached input tokens, 16611 output tokens, **$0.8802578**, 166.03 seconds. It corrected the unsupported push recommendation and retained all 18 facts on manual review. This remains more expensive than the synthetic 0.4.8 baseline; real-day dollar savings remain unproven. See `routed-model.json`.

The small split run cost more despite lower aggregate input because of helper setup and ledger output. Installed 0.4.9 therefore uses one full reader for windows at or below 320000 body bytes and splits larger windows; this is a heuristic, not a proven break-even point. The held candidate lowers that threshold to 80000 and adds source/action auditing, with the real-day results above. Blind synthetic review also found an unsupported push recommendation in the split answer; workflow guidance distinguishes an unfinished artifact from readiness/intention to publish it. Neither the synthetic results nor the real-day comparison establishes perfect recall or a fixed billing reduction.

Artifacts: `artifacts/recap-049/oct21-delivery-final.json`, `old-model.json`, `new-model.json`, `blind/`. Synthetic model probes use the same default host model, as recorded in modelUsage; no model override was introduced.

## 0.4.8 single-copy delivery

The same live date window retained **233 messages across seven conversations**. Both readers produced the identical content fingerprint `faf0781261c71b299db12db469c01009a63e095058ca0baacd8b964253d43ea2`; there were no missing/duplicated body continuations. Coverage appeared once in each stable message stream. A regression separately refreshes source sync timestamps between pages and confirms coverage stays omitted until a meaningful gap change, for both full and compact formats.

| Measurement | 0.4.7 defaults | 0.4.8 defaults |
| --- | ---: | ---: |
| Message pages | 20 | 9 |
| Total wire characters, including discovery | 832,167 | 421,298 |
| Maximum response characters | 47,359 | 47,831 |
| Complete retained messages | 233 | 233 |
| Local elapsed seconds | 1.432 | 1.743 |

This is **55% fewer message calls and 49.4% less wire text**, not a measured reduction in billed/model input tokens. Warm local delivery timing varies; no speedup is claimed from these two timings. The first pass used 12,000 body characters, the new pass 48,000, with the same 48,000 wire cap. Single-copy MCP text and precise partial-body filling account for the improvement. Original timestamps, full middle paragraphs and approval boundaries are preserved. Archived source-reference coverage remains partial independently of complete retained-message delivery. Evidence: `artifacts/recap-048/before-window.json`, `after-window.json`.

A bounded actual Claude skill run over a synthetic multi-day conversation read all five in-day messages, retained the HelpDesk drafting test and prevention-before-backfill prerequisite, included 00:30 local work, and correctly displayed 13:39 UTC as 15:39 CEST. The actual answer was inspected, not only keyword-tested. It distinguishes the passed connection smoke test from the unexecuted drafting test and an assistant completion claim from independent verification. This fixture cost $0.164 and used two local history calls; it is not a general recap-quality guarantee. Evidence: `artifacts/recap-048/model-report.json`, `model-answer.txt`.

## 0.4.7 recap and dashboard repair

On the same live database, three-read averages improved from 391 ms to 19 ms for statistics and 1,157 ms to 64 ms for snapshots. A 20-second baseline backend sample consumed 55% of one core. The native minimise/restore test observed zero statistics/snapshot RPCs for 4.5 seconds while minimised, then resumed automatically. The installed repaired backend measured 2.21% of one core over 60 seconds overlapping validation and collection, then 0 CPU seconds over a quiet 30.02-second minimised interval. The latter is below the counter's measured resolution, not a guarantee of zero ongoing CPU use. These samples measure the backend, not the whole desktop process tree; the five-minute synthetic process-tree measurement below is separate. Evidence: `artifacts/recap-fixes/installed-idle-samples.jsonl` and `installed-quiet-idle.json`.

A complete read of 233 messages from seven sessions produced identical per-message content hashes. The previous skill defaults (16,000 output / 4,000 body characters) required 172 pages, 2,398,799 wire characters and 23.087 seconds; compact mode with the new defaults (48,000 / 12,000) required 20 pages, 832,580 characters and 1.522 seconds. At the same 48,000 / 12,000 budget, the old version needed 26 pages, 1,168,390 characters and 4.089 seconds: compact format reduced wire volume by 28.7%. Metadata appeared once in the compact stream. These are local retrieval measurements, not billed-token or model-reasoning measurements. Evidence: `artifacts/recap-fixes/*queries.json`, `window-*.json`, `native-visibility.json`.

## Historical memory plugin 0.4.5 date-window delivery

The installed read-only server delivered **233 complete messages across seven sessions**, in **26 bounded pages / 4.336 seconds**, maximum observed JSON-RPC response **47,794 characters** under a selected 48,000-character budget. This includes long-body continuations and incremental content fingerprints. The warm-local-machine measurement covers protocol delivery, not model reading/reasoning time. Existing unavailable references still produce partial source coverage; delivery of the retained window was complete. Evidence: `artifacts/memory-window/installed-live-window-045.json`.

## 0.4.3 real-data indexing repair

An isolated SQLite backup with 1,102 entries and **884,552,199 unread source bytes** was replayed against the configured local sources. The debug collector found today's HelpDesk activity after **7.62 seconds**, recovered **197 entries** for that day, and reached **zero pending files / zero unread bytes in 64.39 seconds** (2,653 retained entries). No source transcript or installed database was changed by the test. A representative approved Claude batch fell from 54,278 ms to 849 ms of scan time after eliminating redundant repository filesystem checks.

These are warm-cache measurements on this machine, not a general cold-start guarantee. The original collector and test workload were running concurrently. Later warning-classification changes were regression-tested separately. Raw private replay evidence is in ignored `artifacts/indexing-fix/replay-boundary-result.json`. No new quiet-resource result is claimed; the older measurements below remain historical.

Executed on 2026-10-21. Figures below are new refactor measurements, not results from the removed AI design.

## Quiet release application

A fresh synthetic 30-record demo, release Tauri shell, its collector and every discovered owned descendant were measured for **301.0 seconds** after the dashboard/WebView closed and settled. The external sampler/driver is excluded. PID/start-time pairs guard against PID reuse; CPU is normalized against one logical core, not divided by processor count.

| Metric | Measured |
| --- | --- |
| Quiet CPU | **0.010% of one logical core** |
| Average summed private bytes | **44.8 MiB** |
| Peak summed private bytes | 46.4 MiB |
| Average summed working set | **110.2 MiB** |
| Peak summed working set | 111.6 MiB |
| Samples | 246 |
| WebViews exit on close / same collector survives | Passed |
| Single-instance reopen / complete process-tree quit | Passed |

Both approximate background targets were met on this fixture. Private memory and working set are distinct counters; summed working sets can count shared pages more than once. This does not establish resource usage for large initial catch-up, visible dashboards, arbitrary real histories or external AI hosts. MCP runs separately on demand and was not left running during quiet collection.

[Raw process-tree summary](artifacts/memory-performance/benchmark-20261021-141329-3727c79d/summary.json), [samples](artifacts/memory-performance/benchmark-20261021-141329-3727c79d/samples.json). Reproduce with `scripts/benchmark-tauri.ps1 -Executable <release executable> -QuietSeconds 300` in a normal Windows session. The measured backend preceded final on-demand MCP metadata hardening (numeric offsets, generic cross-project diagnostics and repository context). A final drawer request-cancellation fix also runs only in the open dashboard. Neither path executes during quiet collection. Final package hashes are recorded separately.

## Lexical retrieval

`fixtures/retrieval-benchmark.json` contains **15 synthetic messages and 12 realistic work questions** with explicit relevance labels and concrete host-style lexical queries. Each search uses the real FTS5/history-tool implementation with a fresh read-only connection and a five-hit limit.

| Metric | Result |
| --- | --- |
| Mean recall@5 | **0.917** |
| Mean reciprocal rank | **0.917** |
| Median search/read latency | **16.9 ms** |
| p95 search/read latency | **142.7 ms** |
| Index construction | 302.5 ms |
| Benchmark process working set | 54.4 MiB |
| Benchmark process private memory | 23.6 MiB |

The OAuth question deliberately misses because its source only says ?login? and ?PKCE?; this demonstrates the lexical vocabulary limit. The host skill requests alternate terms. This is not an embedding comparison, blind answer-quality evaluation or evidence of real multi-server reasoning. The OS disk cache was not flushed; p95 includes the first tool read. [Per-question results](artifacts/memory-retrieval-benchmark-v2/retrieval.json).

```powershell
dotnet run --project src/MissionControl.Cli -c Release -- benchmark-history --data-dir C:\fresh\disposable-benchmark
```

Use a new empty directory each time. `stress [count]` and `seed-dashboard [count]` create explicitly approved synthetic records for larger indexing/UI exercises; no large-dataset performance result is claimed for this release.
