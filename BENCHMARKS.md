# Benchmarks

Resource use and recap quality depend on the dataset, model, hardware and process state. The figures below are historical reference measurements, not current-release guarantees. Run-specific reports and private evaluation material remain under ignored `artifacts/`.

## Reproduce measurements

- `scripts/benchmark-tauri.ps1 -Executable <release executable> -QuietSeconds 300` measures a complete hidden desktop process tree on Windows using synthetic data.
- `dotnet run --project src/MissionControl.Cli -c Release -- benchmark-history --data-dir <new-empty-directory>` exercises the real FTS/history read path with `fixtures/retrieval-benchmark.json`.
- `integrations/tests/recap_delivery_probe.py` verifies full-message reconstruction without logging bodies. `recap_model_probe.py` runs a bounded native Claude evaluation on synthetic data; it is a separately authorized model run.
- Use fresh disposable directories for stress/recovery fixtures. Label warm/cold cache, visible/hidden state, collection backlog and whether all child processes are included.

## Historical reference results

A five-minute synthetic hidden Windows release run on 21 October 2026 met the approximate 150 MB background-memory and 1% of one-core targets. These are not guarantees for initial catch-up, large real histories, a visible WebView or separately running AI hosts. Summed working sets can count shared pages repeatedly; report private memory separately.

The synthetic lexical benchmark used 15 messages and 12 questions with explicit relevance labels: recall@5 and mean reciprocal rank were both 0.917, median search/read latency 16.9 ms and p95 142.7 ms. The disk cache was not flushed; the first read contributes to p95. Synonym or embedding changes require a fresh run rather than reusing this baseline.

A real-day recap comparison used 233 messages across seven conversations and the same 24-item rubric. The old full-stream recap scored 16 full / 7 partial / 1 missing using 10.20M total tokens. The later bounded-reader candidate scored 13 / 9 / 2 using 3.69M. Full delivery reduced repeated context but did not preserve all useful information or meet the proposed 1M-token target. Model/cache/live-state variation and absent baseline billing prevent a general dollar-saving claim.

A separate small synthetic comparison retained all 18 facts, but splitting work across readers cost more than the single-reader baseline. Partition size is therefore a heuristic, not a measured universal break-even point. No automatic persistent daily ledger or incremental weekly cache is implemented.

Token totals must include input, cache creation, cache reads and output for the coordinator and every reader. Character counts are not tokens; a helper's final-call telemetry is not necessarily its whole run. Report completed coverage, semantic score and cost independently.
