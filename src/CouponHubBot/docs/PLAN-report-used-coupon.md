# Plan: report an externally-used coupon (`reported` status)

Status: **design approved by owner, not yet implemented**
Issue: [#266](https://github.com/Szer/bots/issues/266)
Author: design decisions below are BINDING unless marked OPEN.

## Problem

A coupon is added to the pool, the adder later redeems it externally (Dunnes app/site) and
forgets to run `/void`. It stays `available`, another user takes it, it fails at checkout.
The taker has no good lever: marking it `used` is a lie that corrupts stats, and returning it
to the pool feeds the next victim — historically the same dead coupon changed hands ~3×.
Today the only real recourse is nagging an admin.

Goal: let the community self-heal without admin involvement.

## Design

### 1. New coupon status: `reported`

Semantics: "a holder reported this coupon as already used externally; it is out of the pool
and back on the adder's plate."

`coupon.status` has **no CHECK constraint and no enum** (confirmed live; `V9__app_coupon_and_void.sql:2`
documents the same for `voided`), so the new value itself needs **no migration**.

**Transition** — only `taken -> reported`:

```
taken -> reported : /report <id> or the report button, by the CURRENT HOLDER only
```

On report:
- `status := 'reported'`
- `taken_by := NULL`, `taken_at := NULL` — mirrors `VoidCoupon` (`DbService.fs:833-838`)
- append `coupon_event` row `event_type = 'reported'`, `user_id = <reporter>` via the shared
  `insertEvent` helper (`DbService.fs:132-139`)

**BINDING: do NOT set `taken_by := owner_id`.** It is tempting (it would make the coupon appear
in the adder's `/my` for free) but `taken_by` is read by the `/my` query (`DbService.fs:351-365`),
the overdue-taken nag (`DbService.fs:575-603`), `/added`'s `(взят)` suffix
(`CommandHandler.fs:239-258`) and the take-limit check. Overloading it to mean "holder" when
nobody holds it would make all four lie. Use a dedicated query instead (§4).

The reporter is recoverable from `coupon_event` (`user_id` on the `reported` row) — do not add a
column for it. Coupon rows mutate in place; the event stream is the history.

### 2. Authorization

Only the **current holder** may report: `status = 'taken' AND taken_by = <actor>`. This is a
single guarded UPDATE (follow the `TryTakeCoupon` atomic pattern, `DbService.fs:478-486`) so a
concurrent `/void` or `/used` can't race it. Rejections:

- not held by actor → «Этот купон не у тебя.»
- not in `taken` status → «Купон уже не активен.»
- unknown id → «Купон не найден.»

No admin-only path is needed. Admins already have `/void` and `/undo`.

### 3. Entry points (two, both required)

**(a) `/report <id>` command** — mirrors `/void <id>` exactly
(`CommandHandler.fs:392-398`): a `Some t when t.StartsWith("/report ")` branch in
`CommandHandler.Dispatch`, arg parsed with `BotHelpers.parseInt`, malformed input →
«Формат: /report <id>».

**(b) One bottom row in `/my`** — the discoverable path. Chosen over a third per-coupon button
because Telegram splits width *within a row*: a third button shrinks the two useful ones, while
its own row costs zero width and exactly one row regardless of coupon count.

```
[  Вернуть ID:1394  ][ Использован ID:1394 ]
[  Вернуть ID:1393  ][ Использован ID:1393 ]
[      ⚠️ Купон уже использован            ]   <- new, callback "report"
[         Мои добавленные                  ]
```

Tap → bot replies «Какой купон уже использован?» with one button per held coupon
(`ID:1394` → `report:1394`) plus «Отмена». Tap → confirmation step
(«Купон ID:1394 уйдёт владельцу @X как использованный. Подтвердить?» → `report:1394:confirm`).

Three taps, no ID typing, no typos. The confirmation step is deliberate: a report accuses
another member, and misclicks should not be cheap. Rejected alternative: intercepting
«Использован» with a "did it work?" prompt — best discovery, but it taxes the ~95% happy path
with an extra tap forever.

Callback naming follows the existing colon convention and the `:del` suffix meaning
"delete the triggering message" (`CallbackHandler.fs:348-375`).

### 4. Where a reported coupon shows up

**Adder's `/my`** — a new section below their taken coupons, via a new
`GetReportedCouponsByOwner` (`owner_id = @user_id AND status = 'reported'`):

```
Мои купоны:
1. Купон ID:1394 на 10€ из 50€, до 25 июля, суббота · добавил @petrov
[Вернуть ID:1394][Использован ID:1394]

⚠️ Отмечены как использованные вне бота:
2. Купон ID:1409 на 5€ из 25€, до 28 июля, вторник
[Использован ID:1409]

[⚠️ Купон уже использован]
[Мои добавленные]
```

Only «Использован» — **never «Вернуть»**. Returning a known-dead coupon to the pool is the
exact harm this feature exists to stop.

`MarkUsed` (`DbService.fs:514-521`) currently only accepts `taken -> used` by the taker. Add a
separate owner path rather than entangling the authorization branch: new callback
`reportedUsed:{id}`, guarded on `owner_id = @actor AND status = 'reported'`, writing a `used`
event. Keep `used:{id}` untouched.

**Adder's `/added`** — add `'reported'` to `GetVoidableCouponsByOwner`'s status list
(`DbService.fs:953`) so the adder can still `/void` it there, with a
«(отмечен использованным)» suffix alongside the existing `(взят)`.

**Pool `/list`** — no change needed. `GetAvailableCoupons` (`DbService.fs:300-316`) is a single
`status = 'available'` equality, so `reported` is excluded by construction.

**Overdue-taken nag** — no change needed. Gated on `status = 'taken'`
(`DbService.fs:575-603`), so a reported coupon stops nagging automatically. Nagging the *adder*
about unresolved reported coupons is a plausible follow-up but is OUT OF SCOPE here.

### 5. Adder identity exposed to the taker

Append `· добавил @username` to each coupon line in `/my`. `GetCouponsTakenBy` gains a join to
`"user"` on `owner_id`. Render with the existing precedent (`ReminderService.fs:21-27`):
`@username`, else `first_name`, else numeric id.

**BINDING: use plain `@username`, not a `tg://user?id=` mention.** Telegram auto-links `@handles`
with no parse-mode change; HTML/Markdown mentions are unused in CouponHubBot (only in
VahterBanBot) and would mean escaping every interpolated string on that path.

Note `GetUserById` (`DbService.fs:327-334`) already exists but is dead code — this feature is
the first cross-user identity render in this bot.

### 6. Notifications

**To the adder**, on report (unsolicited DM, pattern per `NotificationService.fs:13-30`
including its single-retry-and-report-failure shape):

> Пользователь @petrov сообщил, что купон ID:1409 уже был использован.
> Если вы использовали его вне бота — нажмите «Использован» в /my или аннулируйте в /added.

**To the reporter**, confirmation:

> Спасибо! Купон ID:1409 отправлен владельцу @ivanov. Он больше не в общем пуле.

Failure to DM the adder must not fail the report — log and append a warning to the reporter's
confirmation, exactly as `NotifyTakerCouponVoided` does.

### 7. Discoverability note in `/my`

Commands are near-invisible in Telegram, so `/my` carries a one-line hint (matching the existing
`ℹ️` convention in `/stats`, `CommandHandler.fs:176`):

> ℹ️ Встретил уже использованный купон? Нажми «Купон уже использован» или вызови /report ID —
> купон вернётся владельцу.

Also add `/report` to `BotCommandsSetup.fs:16-24` (the `/`-autocomplete menu) and to the
`/start` + `/help` text (`CommandHandler.fs:79-85`).

### 8. Stats

Personal, in «Судьба моих купонов» — this is the accountability signal ("minus karma"):

> Отмечено использованными вне бота: N

Global, in «Сообщество (всего)» — note the global block currently shows *neither* voided nor
reported:

> Аннулировано: Y · Отмечено использованными: X

Both come from adding a 5th bucket to `GetPersonalCouponOutcomes` (`DbService.fs:394-411`) and
`GetGlobalCouponStats` (`DbService.fs:413-429`):

```sql
COUNT(*) FILTER (WHERE status = 'reported')::bigint AS reported_count
```

**This also fixes a real bug the new status would otherwise introduce**: those queries bucket by
`status = 'used'` / `IN ('available','taken')` / `= 'voided'`, and the four buckets are expected
to sum to `total_count`. A `reported` coupon would fall into none of them and silently break the
reconciliation.

**BINDING:** the event-count line («Добавлено · Взято · Возвращено · Использовано · Аннулировано»,
`GetUserStats`, `DbService.fs:367-392`) also gains «· Пожаловался: N» — reports *filed*, a
good-citizen counter distinct from reports *received*. Nearly free: `GetUserStats` already groups
every event type and nets `*_reverted`, so this is one more field plus one more rendered token.

Final personal `/stats` shape:

```
Статистика:
Добавлено: 12 · Взято: 30 · Возвращено: 4 · Использовано: 25 · Аннулировано: 2 · Пожаловался: 3

Судьба моих купонов:
Использовано: 8
Истекло неиспользованными: 1
Сейчас активны: 2
Аннулировано: 1
Отмечено использованными вне бота: 1
Утилизация: 73%
```

### 8a. Periodic community stats leaderboard (social pressure)

The bot posts a per-user stats leaderboard to the community chat from `ReminderService`
(`ReminderService.fs:77-78` calling `GetUserEventCounts` for `"used"` and `"added"`, rendered by
`formatCombinedStats`, `ReminderService.fs:29-60`, as `"{n}. {who} — {usedCount}/{addedCount}"`).

**BINDING: add a reported count to this leaderboard**, so the community can see who repeatedly
forgets to void. Intent is social pressure, per owner.

**BINDING — the trap: this must count reports RECEIVED, keyed on the coupon's `owner_id`, NOT
`coupon_event.user_id`.** A `reported` event's `user_id` is the **reporter**. Reusing
`GetUserEventCounts("reported")` would therefore put the count against the good citizen who
filed the report and leave the negligent adder looking clean — the exact inverse of the goal.
Requires a new query joining the event to its coupon:

```sql
SELECT c.owner_id AS user_id, COUNT(*)::int AS cnt
FROM coupon_event e
JOIN coupon c ON c.id = e.coupon_id
WHERE e.event_type = 'reported'
  AND e.created_at >= @since AND e.created_at < @until
GROUP BY c.owner_id;
```

Net out `reported_reverted` the same way `GetUserEventCounts` already nets `*_reverted`
(`DbService.fs:605-642`) — an admin-undone report must not keep shaming someone.

Rendering: keep the line compact and show the marker **only when the count is non-zero**, so the
leaderboard doesn't grow a column of zeros:

```
1. @ivanov — 25/12
2. @petrov — 18/9 ⚠️2
```

**Cadence: verify before implementing.** Recon described this run as weekly; the owner refers to
it as monthly. Determine the actual schedule in `ReminderService` and add the count to whichever
periodic community-stats post exists (if both a weekly and a monthly post exist, add it to both).
Do not change any existing cadence.

### 9. Admin `/undo`

Add a `"reported"` case to `UndoLastEvent` (`DbService.fs:848-941`) restoring
`status := 'taken'` and `taken_by := <reporter>` (recover the reporter from the `reported`
event's `user_id`, same way the `"returned"` case recovers `taken_at` from the last `taken`
event, `DbService.fs:925-931`), appending `reported_reverted`. Without this, a mistaken report
is unfixable.

### 10. Migration (one, required)

`coupon_barcode_active_uniq` is a partial unique index with predicate
`WHERE status = ANY(ARRAY['available','taken'])`. If `reported` is not added, the same barcode
can be re-added while the reported coupon is still in flight — recreating the duplicate-dead-coupon
problem this feature exists to prevent.

`V18__coupon_reported_status.sql`: drop and recreate that index including `'reported'`, and
update `TryAddCoupon`'s 23505 race-recovery lookup (`DbService.fs:257-266`) in lockstep — its
`status IN ('available','taken')` must match the index predicate or the recovery path returns
the wrong coupon id under a concurrent insert race.

No feature flag. The behaviour is additive and low-risk; a `bot_setting` flag would be dead
config (and `bot_setting` has no DELETE grant, so flags can only be overwritten, not removed).

## Test plan

Hermetic Testcontainers tests in `tests/CouponHubBot.Tests/` (new `ReportFlowTests.fs`,
modelled on `VoidFlowTests.fs:23-44` for the command case and `:134-154` for the callback case):

1. Holder reports via `/report <id>` → status `reported`, `taken_by` NULL, `reported` event written.
2. Holder reports via button flow (`report` → `report:<id>` → `report:<id>:confirm`).
3. Non-holder cannot report (status unchanged).
4. Reported coupon absent from `/list`.
5. Reported coupon appears in adder's `/my` with «Использован» and **no** «Вернуть» button.
6. Adder marks reported coupon used → status `used`, `used` event.
7. Adder can `/void` a reported coupon from `/added`.
8. Adder receives the DM naming the reporter and the coupon id.
9. Reported coupon does not trigger the overdue-taken nag.
10. `/stats` reported bucket increments; the four-bucket sum still reconciles with `total_count`.
11. Admin `/undo` of a report restores `taken` + original `taken_by`.
12. Same barcode cannot be re-added while a reported coupon holds that slot.
13. Community stats leaderboard attributes the report to the coupon's **owner**, not the
    reporter — seed a report by user B on user A's coupon, assert A's line carries the marker and
    B's does not. This is the §8a inversion trap; the test exists specifically to catch it.
14. An admin-undone report no longer counts in the leaderboard (`reported_reverted` netting).

## Resolved decisions (were open, now BINDING)

1. **Stats show both directions** — reports received (§8, «Судьба моих купонов») *and* reports
   filed (§8, «Пожаловался: N» on the event line).
2. **No daily nag for unresolved reported coupons.** One DM at report time is the whole
   notification story. Rationale: the coupon is already out of the pool, so an ignored report
   harms nobody — it just sits in the adder's `/my` until they act or it expires. Adding a second
   nag path also means re-solving the expiry-filtering problem that caused #263. Revisit only if
   adders demonstrably ignore the DM.

## Explicitly out of scope

- Nagging adders about unresolved reported coupons (see above).
- Defending against a malicious chain (added by 1 → taken by 2 → used by 2 → returned by 2 →
  taken by 3, so 3's report lands on innocent 1). Owner's call: social enforcement is cheaper
  than code here, and `coupon_event` already makes the full chain reconstructible if adjudication
  is ever needed. Do not build detection for this.
- Any reputation/karma score. No such concept exists in the codebase today and none is being
  introduced — the stats counters are the accountability signal.
