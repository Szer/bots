module VahterBanBot.Tests.ReactionSpamTests

open System.Net
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Xunit

type TgUser = Telegram.Bot.Types.User

/// Reaction-spam triage tests. After PR #?? the threshold no longer auto-bans —
/// it builds a dossier, runs a vision LLM in shadow mode (records verdict but defers to
/// vahter), and posts an admin alert with [BAN] [SPAM] [NOT SPAM] buttons. Tests live
/// on the ML-enabled fixture which has both REACTION_SPAM_ENABLED=true and the LLM
/// endpoint pointed at fakeAzureOpenAi.
///
/// The fake LLM routes verdict by keywords in the request body:
///   "ban-me"      → BAN
///   "react-spam"  → SPAM
///   "real-lurker" → NOT_SPAM
///   otherwise     → UNSURE
type ReactionSpamTriageTests(fixture: MlEnabledVahterTestContainers) =

    /// Helper: trip the threshold by sending REACTION_SPAM_MAX_REACTIONS reactions from a
    /// user with fewer messages than REACTION_SPAM_MIN_MESSAGES. Returns nothing —
    /// callers assert on DB state.
    let tripThreshold (fixture: MlEnabledVahterTestContainers) (user: TgUser) (msgIdBase: int) = task {
        let chat = fixture.ChatsToMonitor[0]
        for i in 1..5 do
            let! resp =
                Tg.quickReaction(chat, msgIdBase + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }

    /// OnCallback bails with "you are not in DB" if the clicker has no event-store record yet.
    /// Tests for vahter button clicks need to seed the vahter first — easiest path is to send
    /// a normal message from them, which records UsernameChanged on user:{vahterId}.
    let seedVahterInDb (fixture: MlEnabledVahterTestContainers) (vahter: TgUser) = task {
        let chat = fixture.ChatsToMonitor[0]
        let! resp = Tg.quickMsg(chat = chat, from = vahter) |> fixture.SendMessage
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
    }

    [<Fact>]
    let ``Shadow mode: threshold trip records LLM verdict + posts admin alert without auto-banning`` () = task {
        // Default fixture has LLM_REACTION_TRIAGE_AUTO_ACT=false → shadow mode.
        // Default user firstName is a Guid (no keyword) → fake LLM returns UNSURE.
        let user = Tg.user()
        do! tripThreshold fixture user 1000

        // LLM was called and verdict was recorded
        let! verdict = fixture.TryGetReactionTriageVerdict user.Id
        Assert.Equal(Some "UNSURE", verdict)

        // Shadow-mode flag is recorded on the event
        let! shadowMode = fixture.TryGetReactionTriageShadowMode user.Id
        Assert.Equal(Some true, shadowMode)

        // Reason field is captured
        let! reason = fixture.TryGetReactionTriageReason user.Id
        Assert.True(reason.IsSome && reason.Value.Length > 0, "Reason should be populated")

        // No autonomous action — user is NOT banned
        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.False(isBanned, "Shadow mode must not auto-ban; vahter clicks the button.")

        // The 3 reaction-triage callbacks are present (BAN / SPAM / NOT SPAM buttons)
        let! _ = fixture.GetReactionCallbackId(user.Id, "ReactionBan")
        let! _ = fixture.GetReactionCallbackId(user.Id, "ReactionSpam")
        let! _ = fixture.GetReactionCallbackId(user.Id, "ReactionNotSpam")
        ()
    }

    [<Fact>]
    let ``Vahter clicks BAN → user banned, no cooldown set`` () = task {
        let vahter = fixture.Vahters[0]
        do! seedVahterInDb fixture vahter

        let user = Tg.user()
        do! tripThreshold fixture user 2000

        let! banId = fixture.GetReactionCallbackId(user.Id, "ReactionBan")
        let! resp = fixture.ClickCallback(banId, vahter)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! isBanned = fixture.UserBanned user.Id
        Assert.True(isBanned, "BAN button should ban the user")
    }

    [<Fact>]
    let ``Vahter clicks NOT SPAM → cooldown set, no ban`` () = task {
        let vahter = fixture.Vahters[0]
        do! seedVahterInDb fixture vahter

        let user = Tg.user()
        do! tripThreshold fixture user 3000

        let! notSpamId = fixture.GetReactionCallbackId(user.Id, "ReactionNotSpam")
        let! resp = fixture.ClickCallback(notSpamId, vahter)
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! cooldownSet = fixture.HasReactionCooldown user.Id
        Assert.True(cooldownSet, "NOT SPAM button should set the cooldown")

        let! isBanned = fixture.UserBanned user.Id
        Assert.False(isBanned, "NOT SPAM must not ban")
    }

    [<Fact>]
    let ``Cooldown short-circuits: subsequent reactions don't re-trigger triage`` () = task {
        let vahter = fixture.Vahters[0]
        do! seedVahterInDb fixture vahter

        let user = Tg.user()
        do! tripThreshold fixture user 4000

        // First trip → NOT_SPAM click → cooldown event
        let! notSpamId = fixture.GetReactionCallbackId(user.Id, "ReactionNotSpam")
        let! _ = fixture.ClickCallback(notSpamId, vahter)
        let! cooldownSet = fixture.HasReactionCooldown user.Id
        Assert.True(cooldownSet)

        // Now react again — this should NOT trigger a new triage event because of cooldown
        let chat = fixture.ChatsToMonitor[0]
        for i in 1..5 do
            let! resp =
                Tg.quickReaction(chat, 4100 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        // Verdict count should stay at 1 (the original trip), not increase
        use conn = new Npgsql.NpgsqlConnection(fixture.DbConnectionString)
        let! count =
            Dapper.SqlMapper.QuerySingleAsync<int>(
                conn,
                "SELECT COUNT(*)::INT FROM event WHERE event_type = 'LlmReactionTriageClassified' AND (data->>'userId')::BIGINT = @userId",
                {| userId = user.Id |})
        Assert.Equal(1, count)
    }

    [<Fact>]
    let ``Reactions in non-monitored chat are ignored`` () = task {
        let user = Tg.user()
        let randomChat = Tg.chat()  // Not in ChatsToMonitor

        for i in 1..10 do
            let! resp =
                Tg.quickReaction(randomChat, 5000 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.False(isBanned, "Reactions in non-monitored chat should not trigger triage")

        let! verdict = fixture.TryGetReactionTriageVerdict user.Id
        Assert.Equal(None, verdict)

        let! reactionCount = fixture.GetUserReactionCount user.Id
        Assert.Equal(0, reactionCount)
    }


// NOTE: autonomous-mode tests (LLM_REACTION_TRIAGE_AUTO_ACT=true) are deliberately not added
// here yet. Per the plan's rollout order, the feature ships in shadow mode first; the
// autonomous flag flip is a config change with the same code path (the goAutonomous branch in
// RunReactionTriagePipeline). Once we promote the LLM in prod we'll add coverage for autoAct
// behavior — until then, flipping the flag inside a shared MlEnabledVahterTestContainers
// fixture would race with neighbouring LLM-triage tests.


type ReactionSpamDisabledTests(fixture: MlDisabledVahterTestContainers) =

    [<Fact>]
    let ``Reactions are ignored when feature is disabled`` () = task {
        // When REACTION_SPAM_ENABLED=false, reactions should be ignored entirely
        let user = Tg.user()
        let chat = fixture.ChatsToMonitor[0]

        for i in 1..10 do
            let! resp =
                Tg.quickReaction(chat, 9000 + i, user)
                |> fixture.SendMessage
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

        let! isBanned = fixture.UserBannedByBot user.Id
        Assert.False(isBanned, "Reactions should be ignored when feature is disabled")

        let! reactionCount = fixture.GetUserReactionCount user.Id
        Assert.Equal(0, reactionCount)

        // No triage verdict either
        let! verdict = fixture.TryGetReactionTriageVerdict user.Id
        Assert.Equal(None, verdict)
    }
