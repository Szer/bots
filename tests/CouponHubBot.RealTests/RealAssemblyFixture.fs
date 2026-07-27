namespace CouponHubBot.RealTests

open System
open System.Net.Http
open System.Text
open System.Threading.Tasks
open Xunit

/// Assembly-wide fixture: log in the MTProto user (its id feeds the FEEDBACK_ADMINS
/// seed) -> seed bot_setting -> wait /healthz -> POST /reload-settings. Unlike
/// AlitaBot.RealTests' RealAssemblyFixture, there is no BotProcess/NgrokTunnel branch
/// here, and this project still never calls setWebhook/deleteWebhook itself — this
/// project has no bot-token env var (see RealEnv.fs's doc comment). The bot now
/// self-registers its OWN webhook at startup when WEBHOOK_URL is set
/// (src/CouponHubBot/Services/WebhookRegistration.fs), which is how CI wires this up;
/// teardown (deleteWebhook) is still the workflow's job, per the contract's
/// containment section. Local dev mode still follows README.dev.md's manual
/// setWebhook curl UNLESS WEBHOOK_URL is also set in that bot_setting/env. Both modes
/// run the exact same path here regardless.
type RealAssemblyFixture() =

    static let waitHealthyAsync (url: string) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 5.)
            let timeout = TimeSpan.FromSeconds 60.
            let deadline = DateTime.UtcNow + timeout
            let mutable healthy = false
            let mutable lastError = "no attempt made"

            while not healthy && DateTime.UtcNow < deadline do
                try
                    let! resp = http.GetAsync url
                    healthy <- resp.IsSuccessStatusCode
                    if not healthy then lastError <- $"HTTP {int resp.StatusCode}"
                with e ->
                    lastError <- e.Message

                if not healthy then
                    do! Task.Delay 1000

            if not healthy then
                failwith $"{url} did not become healthy within {timeout.TotalSeconds}s: {lastError}"
        }

    /// The pod may already be running (with stale/absent settings) before
    /// DbSeed.applyAsync upserts COMMUNITY_CHAT_ID/FEEDBACK_ADMINS/etc — this makes it
    /// pick those up without a restart. See DbSeed.fs for the one setting
    /// (TEST_MODE/FakeTimeProvider) this can NOT fix retroactively.
    static let reloadSettingsAsync (env: RealEnv) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 10.)
            http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.BotAuthToken)
            let! resp = http.PostAsync(env.ReloadSettingsUrl, null)

            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                failwith $"POST {env.ReloadSettingsUrl} -> {int resp.StatusCode}: {body}"
        }

    let env = RealEnv.load ()

    /// Explicit opt-in gate, checked BEFORE env.HasCore everywhere below — see
    /// RealEnv.realTestsOptedIn's doc comment. This is the one choke point: as long
    /// as InitializeAsync's real setup and every SkipUnlessCore/SkipUnlessUserClient
    /// call both branch on this same value, no test in this assembly can reach a real
    /// Telegram call without it.
    let optedIn = RealEnv.realTestsOptedIn ()

    let mutable userClient: TgUserClient option = None
    let mutable botChatId: int64 = 0L

    member _.Env = env

    /// Npgsql connection string for direct DB assertions/seeding (COUPON_TEST_DB_URL).
    member _.DbConnectionString = env.TestDbUrl

    member _.UserClient =
        match userClient with
        | Some c -> c
        | None -> failwith "user client is not up — call SkipUnlessUserClient() first"

    /// Bot-API-style chat id for the private DM with the test bot — resolved once at
    /// fixture init via ResolveUserByUsername(env.TestBotUsername). Every CouponHubBot
    /// command/callback in this suite is sent to this chat id.
    member _.BotChatId =
        if botChatId = 0L then
            failwith "bot chat id is not resolved — call SkipUnlessUserClient() first"
        botChatId

    member _.SkipUnlessCore() =
        if not optedIn then
            Assert.Skip
                "real-money tests opted out: set COUPON_REAL_TESTS=1 to run this project. It drives a real Telegram MTProto session against a live bot — reserved for deliberate feature work or the workflow_dispatch-only coupon-real-test.yml, never for an unattended `dotnet test`."
        elif not env.HasCore then
            Assert.Skip
                $"core config missing in {RealEnv.envFilePath} (bot username / chat id / base url / auth token / db url)"

    member this.SkipUnlessUserClient() =
        this.SkipUnlessCore()

        if not env.HasUserClient then
            Assert.Skip
                "MTProto user client unavailable — fill COUPON_TG_API_ID/HASH/PHONE in ~/.coupon-test/env and run `make coupon-tg-login`"

    /// Fires BatchDebounce's pending TimeProvider timer(s) — see DbSeed.fs's doc
    /// comment for why TEST_MODE must already have been true at pod boot for this to
    /// be anything other than a 400.
    member _.AdvanceClockAsync(ms: int) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 10.)
            http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.BotAuthToken)
            use content = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = http.PostAsync($"{env.ClockAdvanceUrl}?ms={ms}", content)

            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                failwith $"POST {env.ClockAdvanceUrl}?ms={ms} -> {int resp.StatusCode}: {body}"
        }

    /// Runs the overdue-taken / expiring-today reminder job immediately.
    /// `nowUtcIso` matches ReminderTests.fs's `?nowUtc=` query param (ISO-8601 UTC).
    member _.RunReminderAsync(nowUtcIso: string) =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 30.)
            http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.BotAuthToken)
            use content = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = http.PostAsync($"{env.RunReminderUrl}?nowUtc={nowUtcIso}", content)

            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                failwith $"POST {env.RunReminderUrl}?nowUtc={nowUtcIso} -> {int resp.StatusCode}: {body}"
        }

    /// Reapplies the currently-running pod's bot_setting into IOptions<BotConfiguration>
    /// — public wrapper around the same call InitializeAsync makes at fixture startup,
    /// exposed for MembershipGateRealTests' save-flip-assert-restore sequence.
    member _.ReloadSettingsAsync() = reloadSettingsAsync env

    /// Clears TelegramMembershipService's in-memory cache (Program.fs's
    /// /test/membership/invalidate — TEST_MODE-gated, same shape as the two hooks
    /// above). Required around MembershipGateRealTests' COMMUNITY_CHAT_ID flip: without
    /// this, a stale cached verdict (up to 1 day old) can mask both the flip itself and
    /// its restoration — see that test's doc comment.
    member _.InvalidateMembershipCacheAsync() =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 10.)
            http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.BotAuthToken)
            use content = new StringContent("", Encoding.UTF8, "application/json")
            let! resp = http.PostAsync(env.MembershipInvalidateUrl, content)

            if not resp.IsSuccessStatusCode then
                let! body = resp.Content.ReadAsStringAsync()
                failwith $"POST {env.MembershipInvalidateUrl} -> {int resp.StatusCode}: {body}"
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() : ValueTask =
            task {
                // optedIn gates ALL real setup below, ahead of (and independent of)
                // env.HasUserClient — this is what actually prevents a real Telegram
                // call from happening at all when COUPON_REAL_TESTS is unset, even
                // though credentials are fully present (~/.coupon-test/env). Every
                // individual test's own SkipUnlessCore/SkipUnlessUserClient check is
                // belt-and-braces on top of this, not the primary guard.
                if optedIn && env.HasUserClient then
                    let c = new TgUserClient(env.TgApiId, env.TgApiHash, env.TgSessionPath, env.TgPhone)
                    let! me = c.LoginAsync()
                    userClient <- Some c

                    let! resolvedBotId = c.ResolveUserByUsername env.TestBotUsername
                    botChatId <- resolvedBotId

                    // FEEDBACK_ADMINS (BotConfiguration.FeedbackAdminIds) is seeded with
                    // THIS logged-in MTProto user's own numeric id (`me.id`), discovered
                    // here at runtime rather than read from any secret/env var — there is
                    // no COUPON_CI_FEEDBACK_ADMIN_ID and none should be added. This keeps a
                    // real Telegram user id out of CI configuration entirely, and gives
                    // /void and /feedback admin-forward real-tests working admin rights
                    // with zero human secret setup: the workflow seeds FEEDBACK_ADMINS as
                    // "" and this UPSERTs the discovered id over it every run.
                    do! DbSeed.applyAsync env.TestDbUrl env.TestChatId me.id
                    do! waitHealthyAsync env.HealthUrl
                    do! reloadSettingsAsync env
            }
            |> ValueTask

        member _.DisposeAsync() : ValueTask =
            task {
                match userClient with
                | Some c ->
                    try
                        do! (c :> IAsyncDisposable).DisposeAsync()
                    with _ ->
                        ()
                | None -> ()
            }
            |> ValueTask
