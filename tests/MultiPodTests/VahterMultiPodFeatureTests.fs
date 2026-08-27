namespace MultiPodTests

open System
open System.Threading.Tasks
open BotTestInfra
open Npgsql
open Dapper
open Xunit

/// Minimal direct-SQL assertions against the `event` table -- same queries
/// VahterBanBot.Tests/ContainerTestBase.fs uses (MessageIsAutoDeleted/UserBanned/GetMlScore),
/// not shared via a project reference (see FakeCallHelpers.fs's note: a test project referencing
/// another test project is unusual here).
module private VahterEventAssertions =
    let messageIsAutoDeleted (connString: string) (chatId: int64) (messageId: int64) = task {
        use conn = new NpgsqlConnection(connString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE event_type = 'BotAutoDeleted'
  AND (data->>'chatId')::BIGINT = @chatId
  AND (data->>'messageId')::INT  = @messageId
            """
        let! count = conn.QuerySingleAsync<int>(sql, {| chatId = chatId; messageId = messageId |})
        return count > 0
    }

    /// Absence proves the deletion short-circuited before ML ran (spam-text cache hit), same
    /// attribution VahterBanBot.Tests/SpamTextCacheTests.fs uses for the single-pod case.
    let getMlScore (connString: string) (chatId: int64) (messageId: int64) = task {
        use conn = new NpgsqlConnection(connString)
        //language=postgresql
        let sql =
            """
SELECT (data->>'score')::DOUBLE PRECISION FROM event
WHERE event_type = 'MlScoredMessage'
  AND (data->>'chatId')::BIGINT = @chatId
  AND (data->>'messageId')::INT  = @messageId
            """
        let! scores = conn.QueryAsync<float>(sql, {| chatId = chatId; messageId = messageId |})
        return scores |> Seq.tryHead
    }

    let userBanned (connString: string) (userId: int64) = task {
        use conn = new NpgsqlConnection(connString)
        //language=postgresql
        let sql =
            """
SELECT COUNT(*) FROM event
WHERE stream_id  = 'user:' || @userId
  AND event_type = 'UserBanned'
  AND (data->'actor'->>'Case' = 'User' OR data->'bannedBy'->>'Case' = 'BannedByVahter')
            """
        let! count = conn.QuerySingleAsync<int>(sql, {| userId = userId |})
        return count > 0
    }

/// Feature-specific multi-pod behavior stacking on VahterMultiPodSmokeTests.fs's proof that the
/// harness itself works -- covers two DB-shared-state fixes: the spam-text cache (spam_text_seed)
/// and the chat-admin snapshot (chat_admin). The ML fallback guard (SaveTrainedModel WHERE clause)
/// is covered at DB-integration level in VahterBanBot.Tests/MLFallbackGuardTests.fs -- a real
/// two-pod cold-start race is impractical here too (SDCA training takes minutes).
type VahterMultiPodFeatureTests(fixture: VahterMultiPodContainers) =

    /// A manual /ban delivered to instance 0 seeds spam_text_seed (V45); the
    /// exact same normalized text arriving as a FRESH message on instance 1 -- which never saw
    /// the /ban -- is still auto-deleted, because the cache is Postgres-backed, not a per-pod
    /// ConcurrentDictionary. Requires SPAM_TEXT_CACHE_MODE=enforce + ML_SPAM_DELETION_ENABLED=true
    /// (seeded in VahterMultiPodContainers.SeedDatabase).
    [<Fact>]
    let ``Cross-pod spam-text cache: a ban seeded via instance 0 is enforced by instance 1`` () = task {
        let spamText = $"click this link right now to claim your huge prize before it expires forever {Guid.NewGuid()}"
        let originalMsg = Tg.quickMsg(text = spamText, chat = fixture.ChatsToMonitor)
        let! originalResp = fixture.SendUpdateTo(0, originalMsg)
        Assert.Equal(System.Net.HttpStatusCode.OK, originalResp.StatusCode)

        let banReply = Tg.replyMsg(originalMsg.Message.Value, text = "/ban", from = fixture.Vahter)
        let! banResp = fixture.SendUpdateTo(0, banReply)
        Assert.Equal(System.Net.HttpStatusCode.OK, banResp.StatusCode)

        // Sanity: the /ban actually landed (same UserBanned shape VahterBanBot.Tests asserts).
        let! banned = VahterEventAssertions.userBanned fixture.DbConnectionString originalMsg.Message.Value.From.Value.Id
        Assert.True(banned, "Sanity: manual /ban via instance 0 should ban the spammer")

        // A DIFFERENT user repeats the EXACT SAME text as a fresh message, delivered to
        // instance 1 -- the pod that never processed the /ban.
        let repeatUser = Tg.user()
        let repeatMsg = Tg.quickMsg(text = spamText, chat = fixture.ChatsToMonitor, from = repeatUser)
        let! repeatResp = fixture.SendUpdateTo(1, repeatMsg)
        Assert.Equal(System.Net.HttpStatusCode.OK, repeatResp.StatusCode)

        let repeatChatId = repeatMsg.Message.Value.Chat.Id
        let repeatMessageId = repeatMsg.Message.Value.MessageId

        // Small bounded retry as network-hop insurance (container-to-container HTTP, unlike the
        // in-process single-pod suite) -- the webhook handler itself awaits the full pipeline.
        let mutable deleted = false
        let mutable attempts = 0
        while not deleted && attempts < 10 do
            let! d = VahterEventAssertions.messageIsAutoDeleted fixture.DbConnectionString repeatChatId repeatMessageId
            deleted <- d
            if not deleted then
                attempts <- attempts + 1
                do! Task.Delay 300
        Assert.True(deleted, "Instance 1 should auto-delete the exact repeat of a message banned via instance 0")

        // Attribution: no MlScoredMessage event for the repeat -- the spam-text cache
        // short-circuited before ML ever ran on instance 1.
        let! mlScore = VahterEventAssertions.getMlScore fixture.DbConnectionString repeatChatId repeatMessageId
        Assert.True(mlScore.IsNone, "Cache hit should short-circuit before ML scoring on instance 1")
    }

    /// Chat-admin snapshot convergence: the lease-gated Telegram fetch (FakeTgApi always answers
    /// getChatAdministrators with a fixed admin, id 42) runs on a short interval here (see
    /// VahterMultiPodContainers.SeedDatabase's UPDATE_CHAT_ADMINS_INTERVAL_SEC=3, vs. production's
    /// 5-minute default) so convergence is a bounded few-second poll, not a multi-minute wait --
    /// no SQL-seed shortcut needed. Proof is behavioral (same admin-immunity mechanism Bot.fs uses,
    /// `AllowedUsers` OR `UpdateChatAdmins.Admins`, which single-pod MLBanTests.fs exercises too):
    /// id 42's spam-scoring message stops being ML-scored/auto-deleted on EACH instance once its
    /// local snapshot reloads chat_admin, including instance 1, which never itself won the fetch
    /// lease.
    [<Fact>]
    let ``Cross-pod chat-admin snapshot: both instances converge to the fetched admin set`` () = task {
        let admin = Tg.user(id = 42L, username = "just_admin")

        // Baseline: a fresh non-admin's spam message is deleted on instance 0 -- confirms the ML
        // deletion pipeline itself works, so a later "not deleted" result for id 42 is
        // attributable to admin immunity, not a broken pipeline.
        let nonAdminMsg = Tg.quickMsg(text = "2222222", chat = fixture.ChatsToMonitor)
        let! _ = fixture.SendUpdateTo(0, nonAdminMsg)
        let! nonAdminDeleted =
            VahterEventAssertions.messageIsAutoDeleted
                fixture.DbConnectionString nonAdminMsg.Message.Value.Chat.Id nonAdminMsg.Message.Value.MessageId
        Assert.True(nonAdminDeleted, "Sanity: a non-admin's spam message should be auto-deleted")

        let pollForAdminImmunity (instance: int) = task {
            let mutable immune = false
            let mutable attempts = 0
            while not immune && attempts < 20 do
                let probeMsg = Tg.quickMsg(text = "2222222", chat = fixture.ChatsToMonitor, from = admin)
                let! _ = fixture.SendUpdateTo(instance, probeMsg)
                let! deleted =
                    VahterEventAssertions.messageIsAutoDeleted
                        fixture.DbConnectionString probeMsg.Message.Value.Chat.Id probeMsg.Message.Value.MessageId
                if not deleted then
                    immune <- true
                else
                    attempts <- attempts + 1
                    do! Task.Delay 1000
            return immune
        }

        let! immune0 = pollForAdminImmunity 0
        Assert.True(immune0, "Instance 0 should eventually treat id 42 (FakeTgApi's fixed admin) as a chat admin")

        let! immune1 = pollForAdminImmunity 1
        Assert.True(immune1, "Instance 1 should ALSO treat id 42 as a chat admin -- proof it read chat_admin, not just its own fetch")
    }
