# Historical moderation quality

`moderation_quality_daily` stores correction-based estimates, not independently audited ground truth. Uncorrected decisions are assumed correct; missed spam that nobody reports remains invisible.

## Populations

| Cohort | System | Meaning |
| --- | --- | --- |
| `all_scored` | `pipeline` | Actual ML + LLM automatic deletion, once per ML-scored message |
| `all_scored` | `ml` | Counterfactual ML alone, SPAM when score > 0, HAM otherwise |
| `llm_triaged` | `ml` | Counterfactual ML on the same messages as the paired LLM row |
| `llm_triaged` | `llm` | Recorded LLM verdict, grouped by recorded deployment name |

Filter both cohort and system before summing. The populations overlap: adding systems or cohorts double-counts messages. In the paired cohort, `llm_model` identifies the partner LLM deployment for both systems; NULL means the historical event has no model attribution. ML model versions are not recorded.

The paired cohort requires an LLM event and its preceding ML score in `[-0.5, 1.5)`; 1.5 is the direct-ML spam boundary. It includes cached LLM verdicts. SKIP contributes `abstained` to both paired rows, keeping the precision/recall populations identical.

The UTC day comes from the first ML score. Edits do not create a second message. The latest score and LLM verdict before the latest human correction are evaluated. A text change between evaluation and correction excludes that comparison.

Latest decisive human correction wins across message labels, reviewer actions, and human unbans. An unban corrects only the message targeted by that user's preceding ban. Without a correction, automatic deletion implies SPAM, otherwise the current LLM verdict supplies the reference, otherwise HAM is assumed.

Whole-pipeline rows measure automatic deletions, so human-caught spam without an automatic deletion is FN. Unreviewed SKIP is unresolved; reviewed SKIP can contribute to pipeline counts. Non-ML/LLM deletion reasons are excluded from whole-pipeline comparisons. Messages without an ML score, including deterministic prefilters and older uninstrumented history, are outside these populations.

## Scheduling and backfill

The existing scheduler runs the job at `CLEANUP_SCHEDULED_HOUR_UTC`. `QUALITY_SETTLING_DAYS` defaults to 14 full UTC days after a day ends; the latest eligible day is therefore today minus 15 days.

The job fills missing eligible days from the first recorded ML score, oldest first. `QUALITY_BACKFILL_DAYS` defaults to 7 and is capped at 31 per invocation; once caught up, one day becomes eligible each day. These settings are read from `bot_setting` on each run.

Authenticated `POST /quality-history` processes another bounded batch. Repeat until `completedDays` is zero to backfill without waiting for the daily schedule. `POST /quality-history?day=YYYY-MM-DD` replaces one eligible day, including corrections that arrived after its previous calculation.

No settling period guarantees that corrections are final. A correction older than the settling window requires rebuilding its original scoring day. Increasing the window does not remove already-computed rows; dashboard eligibility should use the same window.

Each day uses one repeatable-read transaction and atomically replaces its rows. Completed days survive interruption, empty days get zero rows, and retries cannot multiply counts. A database session advisory lock serializes scheduled and manual runs across pods; the dedicated unpooled connection releases it on disposal.

The job reads indexed event ranges and batches message streams. Dashboard reads use the aggregate table's day-leading unique index; no event replay occurs in a dashboard query. The existing event-type/time and stream indexes support backfill.

## Dashboard SQL

Use half-open UTC dates, `$1` inclusive and `$2` exclusive. Parameters are bound by the dashboard/query client. Return NULL for empty denominators.

```sql
SELECT day,
       sum(tp) AS tp, sum(tn) AS tn, sum(fp) AS fp, sum(fn) AS fn,
       100.0 * sum(tp) / nullif(sum(tp + fp), 0) AS precision_pct,
       100.0 * sum(tp) / nullif(sum(tp + fn), 0) AS recall_pct,
       sum(abstained) AS abstained, sum(unresolved) AS unresolved,
       sum(excluded) AS excluded
FROM moderation_quality_daily
WHERE day >= $1::date AND day < $2::date
  AND cohort = 'all_scored' AND system = 'pipeline'
GROUP BY day
ORDER BY day;
```

For an interval, remove `day` from SELECT and remove GROUP BY/ORDER BY. Sum counts first; do not average daily percentages or multiply ML and LLM precision.

```sql
SELECT system, llm_model,
       sum(tp) AS tp, sum(tn) AS tn, sum(fp) AS fp, sum(fn) AS fn,
       100.0 * sum(tp) / nullif(sum(tp + fp), 0) AS precision_pct,
       100.0 * sum(tp) / nullif(sum(tp + fn), 0) AS recall_pct,
       sum(abstained) AS abstained, sum(unresolved) AS unresolved,
       sum(excluded) AS excluded
FROM moderation_quality_daily
WHERE day >= $1::date AND day < $2::date AND cohort = 'llm_triaged'
GROUP BY system, llm_model
ORDER BY llm_model, system;
```

For the latest 30 settled days at the default window, bind `$1 = CURRENT_DATE - 44` and `$2 = CURRENT_DATE - 14` with the database session in UTC. Show the covered date range and missing days alongside the metrics while backfill is catching up.
