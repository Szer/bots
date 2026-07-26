# CouponHubBot.RealTests — authoring guide

Real-Telegram (MTProto), real-DB tests for CouponHubBot. **CI-dispatch only** — this
suite talks to a real Telegram account and a real bot deployment, costs money, and can
post to real Telegram. Never run it locally or from an agent; the CI workflow that
dispatches it is the only sanctioned runner. Local `dotnet build` (not `dotnet test`) is
fine and is how you validate a new file compiles.

## RULE: every coupon add MUST be idempotent

The suite runs SERIALLY over ONE shared transient Postgres and ONE Telegram account, with
no per-test cleanup by default, and **test class execution order is NOT guaranteed** (not
`.fsproj` order — proven empirically by CI run `30193635437`, which failed 4/32 tests
this way). Combined with `coupon_barcode_active_uniq` (the partial unique index `/add`'s
OCR-extracted barcode is checked against since merge #269), this means: if ANY two
coupon-adds anywhere in this assembly can use the same fixture's barcode, and one of them
doesn't delete-first, then whichever one runs SECOND — in whatever order THIS run happens
to pick — gets rejected with «Купон с таким штрихкодом уже есть в базе…» instead of
«Добавлен купон ID:», and its `Await*` call times out waiting for text that will never
arrive.

**"The other test always runs first" is not a safety argument.** There is no guaranteed
order, so any reasoning of that shape is wrong by construction — this is exactly the bug
this rule exists to close. The only correct fix is that every add is safe regardless of
what ran before it, including a retried attempt of itself (see "Retry contract" below).

Two sanctioned patterns, pick whichever fits the test:
1. **Default**: route the add through `RealTestHelpers.addCouponViaCaptionAsync`, which
   deletes any pre-existing row for the fixture's barcode FIRST, then sends the photo.
   Use this whenever the add is just setup for something else under test.
2. **Bespoke add** (the test's own subject IS the add reply, or it inserts via raw SQL):
   call `DbSeed.deleteCouponsByBarcodeAsync` yourself, immediately before the add, and
   keep asserting on the add's own reply/DB row as before. If the row has no barcode
   (`NULL`/synthetic `barcode_text`, e.g. a direct SQL insert), you cannot clean up by
   barcode — instead delete the row by id (`DbSeed.deleteCouponByIdAsync`) once your
   assertions are done, on BOTH the success and the failure path (`try`/`with` +
   re-raise, since F#'s `task { }` can't `do!`/`let!` inside `finally`).

Never hand-roll `SendPhoto` + an `/add` caption (or a raw `INSERT INTO coupon`) without
one of the two patterns above — that reintroduces the exact flake this rule exists to
close, even if the fixture "looks" unshared today; a later PR reusing the same fixture
elsewhere in this assembly won't know that assumption existed.

## New-test skeleton

```fsharp
namespace CouponHubBot.RealTests

open System
open Xunit

type MyNewRealTests(fx: RealAssemblyFixture) =

    [<Fact>]
    member _.``some behavior, in plain English``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "10_50_01-04_01-13_2706602781191.jpg" "10" "50" None

            let! sentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _reply = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, sentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("taken", status.status)
        })
```

Every `[<Fact>]` body is the sole content of one `TestRetry.withTimeoutRetry` thunk, and
starts with `fx.SkipUnlessUserClient()`.

## Retry contract — why assertion failures are NOT retried

`TestRetry.withTimeoutRetry` (`TestRetry.fs`) retries the whole test body **exactly
once**, and **only** when it raises `AwaitTimeoutException` (`TgUserClient.fs`) — the
"no message matching X within Ns" family. `Assert.*` failures, and any `failwith`, are a
different, non-flaky failure mode and propagate uncaught on the first attempt. This is
deliberate: retrying a real assertion failure would silently hide a real bug behind a
second, expensive, real-Telegram round trip.

Consequence for helper authors and test authors alike: **be deliberate about which
exception type a failure is.** "The bot didn't reply in time" -> `AwaitTimeoutException`
(retryable). "The bot replied, but not with what we expected" (wrong text, missing
button, wrong DB state) -> `Assert.*` / plain exception (not retryable). Never make an
assertion-class failure throw `AwaitTimeoutException` just to get a free retry, and never
wrap a genuinely await-shaped helper in something that swallows `AwaitTimeoutException`
into a different exception type — either mistake defeats `TestRetry`'s whole design.

Because retry means **re-running the whole body**, a test that adds a coupon inside a
retried body will try to add the SAME fixture photo twice if attempt 1 actually succeeded
before timing out. That is exactly the problem `addCouponViaCaptionAsync` /
`deleteCouponsByBarcodeAsync` solve — see below.

## Fixture reuse rule + the public-repo constraint

Photo fixtures live in `tests/CouponHubBot.Ocr.Tests/Images/` — reuse them, **do not add
new ones**. This repo is public; a fixture with a live, currently-usable barcode would
publish that barcode. Every fixture in that folder is deliberately dated in the past, so
every `/add` through one must carry an explicit future date in its caption (`/add <value>
<minCheck> <futureDate>`) — OCR reads the barcode from the photo, but the app uses the
caption's explicit value/min-check/date, not whatever OCR would infer from the (past)
printed date. `RealTestHelpers.futureExpiry` and `addCouponViaCaptionAsync`'s default
`expiryDate` already do this for you.

As of merge #269, `/add`'s OCR-extracted barcode is subject to the partial unique index
`coupon_barcode_active_uniq (barcode_text, expires_at) WHERE status IN ('available',
'taken','reported')`. Reusing a fixture whose barcode already has a live row (from an
earlier test, or from a prior attempt of the SAME test that TestRetry retried) makes the
`/add` fail with "Купон с таким штрихкодом уже есть в базе…" — an assertion-class
failure, not retried. **Always add coupons through `RealTestHelpers.addCouponViaCaptionAsync`**,
which deletes any pre-existing row for that fixture's barcode FIRST — this is what makes
retry safe and fixture reuse safe across many more tests than there are distinct
fixtures. Do not hand-roll `SendPhoto` + `/add` caption without also deleting by barcode
first, or you will reintroduce the flake this task exists to close.

## Helper inventory

### DB (`DbSeed.fs`) — all take `connectionString: string` as the first arg

| Signature | What it does |
|---|---|
| `deleteCouponsByBarcodeAsync (connectionString) (barcodeText: string) : Task` | Deletes every `coupon` row for that barcode (all expiries/statuses); `coupon_event` cascades automatically (`ON DELETE CASCADE`, the only FK onto `coupon`). Call BEFORE sending a fixture photo. |
| `deletePendingAddFlowAsync (connectionString) (userId: int64) : Task` | Clears the interactive `/add` wizard's `pending_add` row for a user (F# type `PendingAddFlow` in `DbService.fs` — the table itself is `pending_add`, not `PendingAddFlow`). |
| `deletePendingBatchesAsync (connectionString) (userId: int64) : Task` | Clears `pending_add_batch` rows (all statuses) for a user; `pending_add_batch_item` cascades automatically. |
| `getCouponStatusAsync (connectionString) (couponId: int) : Task<CouponStatusRow>` | `{ status; owner_id; taken_by: Nullable<int64> }` for one coupon id. |
| `getLatestOwnedCouponIdAsync (connectionString) (ownerId: int64) : Task<int>` | Newest coupon id for an owner (`ORDER BY id DESC LIMIT 1`) — the pattern several existing tests inline. |
| `setCouponExpiryAsync (connectionString) (couponId: int) (expiresAt: DateOnly) : Task` | Overwrites `coupon.expires_at` for one coupon id via direct SQL. |
| `truncateCouponsAsync` / `truncateBatchesAsync` / `getSettingAsync` / `setSettingAsync` / `applyAsync` | Pre-existing — unchanged. |

### Telegram (`TgUserClient.fs`) — new members alongside the existing `SendText` / `SendPhoto` / `AwaitTextContaining` / `FindCallbackData` / `PressCallbackButton` etc.

| Signature | What it does |
|---|---|
| `PressCallbackButtonMatching(chatId, msg, predicate, description) : Task` | `FindCallbackData` + `PressCallbackButton` in one call. On a miss, fails with the searched-for `description` AND the full list of callback data actually present on `msg` — not an `AwaitTimeoutException` (the message already arrived; a missing button is a real behavioral assertion). |
| `AwaitAndPressButton(chatId, afterMsgId, textMarker, timeout, buttonPredicate, buttonDescription) : Task<TL.Message>` | `AwaitTextContaining` + `PressCallbackButtonMatching` in one call; returns the awaited message. The await half is retryable, the press half is not — see above. |

### Cross-cutting (`RealTestHelpers.fs`)

| Signature | What it does |
|---|---|
| `fixtureBarcodes: Map<string, string>` | Fixture filename -> its ground-truth barcode, read off the filename itself (`[value]_[minCheck]_[validFrom]_[validTo]_[barcode].jpg`, per `CouponHubBot.Ocr.Tests/OcrTests.fs`'s own parsing/assertion). Two "low quality" entries are barcode-unproven — see the doc comment in the file. |
| `futureExpiry (daysFromNow: float) : string` | `yyyy-MM-dd` date that many days from now (UTC). |
| `addCouponViaCaptionAsync (fx) (fixtureImage: string) (value: string) (minCheck: string) (expiryDate: string option) : Task<int>` | **The add helper.** Deletes any existing coupon for the fixture's barcode, sends the photo with an explicit `/add` caption, awaits the confirmation, parses and returns the new coupon id. Pass `None` for the default (365 days out). Use this for every new `/add`. |

## Adding a new file

Stub files already exist and are already registered in the `.fsproj`, in F#-compile-order
position, after `RealTestHelpers.fs`/`RealAssemblyFixture.fs` and before `Program.fs`:

- `AddWizardRealTests.fs`
- `ReportButtonFlowRealTests.fs`
- `MyAndAddedRealTests.fs`
- `AdminAndFeedbackRealTests.fs`

**Do not touch the `.fsproj`, `DbSeed.fs`, `TgUserClient.fs`, `RealTestHelpers.fs`, or any
other author's stub file.** Replace only your own file's placeholder `[<Fact>]` with real
`[<Fact>]` methods. If you need a helper that doesn't exist yet, add it as a `private`
function inside your own file rather than editing shared plumbing — if it turns out to be
genuinely shared, flag it back to whoever owns this scaffolding instead of editing shared
files yourself.

## Isolation model (no automatic per-test cleanup)

There is still no assembly-wide per-test truncation. Tests isolate by:
(a) querying their own newest coupon/state via helpers above, scoped by owner id;
(b) fixture reuse now being safe (see above) instead of needing a distinct fixture per
test;
(c) `afterMsgId` gating in every `Await*` call — always pass the message id returned by
the `Send*` call that triggered the expected reply, never `0`, or an earlier test's
identical reply text can satisfy your `Await*` immediately.
