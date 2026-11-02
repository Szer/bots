# Community dashboard

`/dashboard` opens a Telegram Mini App with a sortable, filterable member table, personal comparisons, and pooled community averages. `DASHBOARD_URL` in `bot_setting` supplies the HTTPS button URL; `/start dashboard` opens the same button.

The public page contains no member statistics. A normal browser shows a link to the bot; Telegram launches authenticate with the original `Telegram.WebApp.initData` string.

## Access

`DASHBOARD_ENABLED` is a DB feature flag, seeded `false`. Members listed in `FEEDBACK_ADMINS` bypass the flag; everyone else needs it enabled. Changes take effect through `/reload-settings` and propagate to every replica without restarting.

The flag gates bot launch buttons, new sessions, and every statistics request, including existing sessions and cached snapshots. The public login shell remains reachable so administrators can authenticate; administrators still need verified Telegram identity and community membership.

`POST /dashboard/api/session` accepts `text/plain` init data. The server verifies Telegram's HMAC-SHA256 signature, rejects duplicate fields, and requires `auth_date` within five minutes, allowing thirty seconds of forward clock skew.

The verified user must pass `getChatMember` for the configured community. The bot must be a community administrator for reliable checks; owners, administrators, and members are accepted, matching the bot's existing membership policy.

Sessions use random 256-bit bearer tokens, expire after fifteen minutes, and are bound to the community. PostgreSQL stores only token hashes so either replica can validate them; expired records are pruned when a session is created.

Every statistics request validates the session and membership before reading cached data. Membership results last at most one minute; membership updates also update the receiving replica's cache. Changing the configured community invalidates existing sessions for subsequent requests.

The browser keeps the bearer token in memory and clears private content on expiry or access denial. Session tokens never go in URLs or browser storage; dashboard responses use `Cache-Control: no-store`. Loaded data cannot be recalled after someone leaves the community.

## Statistics

`GET /dashboard/api/stats?days=0|30|90|365` requires `Authorization: Bearer …`. Zero selects all history; other values select actions in the last N days.

| Metric | Calculation |
| --- | --- |
| Take expiry remaining | Mean calendar days remaining when a coupon was taken |
| Hold duration | Mean hours from taking to the same holder's use, return, or report |
| Return share | Returned reservations divided by all reservations, with both counts shown |
| Added expiry remaining | Mean calendar days remaining when a coupon was added |

Dates use Europe/Dublin. Repeated reservations count separately, admin reversals are removed in stack order, and a later owner's use after a report does not extend the original holder's reservation.

Open reservations and reservations closed by voiding are excluded from hold duration and displayed separately. Expired open reservations are flagged; adding or returning with at most one day remaining means expiry today or tomorrow, including already-expired records if present.

The selected period applies to the take or add timestamp, with outcomes included through the snapshot time. The table includes historical registered users, including former members; pooled averages weight actions rather than averaging users' averages.

Four fixed period snapshots are cached for fifteen minutes per replica. Concurrent refreshes share a single query; sorting and filtering happen locally and never change the baseline. There is no automatic statistics refresh.

## Color signals

Mean and percentage cells require ten relevant observations. For lower-is-better metrics, green is at least 25% below the baseline, amber at least 25% above, and red at least 50% above.

Added expiry remaining is green at least 10% above the baseline, amber at least 25% below, and red at least 50% below. Null values, zero baselines, and small samples remain neutral; elevated return share also stays neutral when mean return time is under an hour.

Holding for at least 24 hours then returning with at most one day left is amber for one or two cases and red from three. Expired unresolved reservations are red independently of sample size.

## Verification

`dotnet test tests/CouponHubBot.Tests -c Release` exercises forged and stale login data, nonmember access, two-replica sessions, membership revocation, expiry, community binding, snapshot caching, launch buttons, and analytics with synthetic event histories.

## Phone layout

Phones open on the member list, with separate personal and community views. Sorting selects the metric shown beside every member; tapping a member expands averages and return details. Filters are collapsible, and all list operations use the loaded snapshot. Wider screens show the comparison table.
