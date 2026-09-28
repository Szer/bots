module VahterBanBot.Tests.CallbackCleanupTests

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open BotInfra
open VahterBanBot
open VahterBanBot.Tests.ContainerTestBase
open Xunit

type CallbackCleanupTests(fixture: MlDisabledVahterTestContainers) =
    let now = DateTimeOffset.UtcNow
    let clock = { new TimeProvider() with member _.GetUtcNow() = now }
    let db = DbService(fixture.DbConnectionString, clock)

    let append (id: Guid) (version: int) (eventType: string) (channelId: int64) (createdAt: DateTime) = task {
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let sql = """
INSERT INTO event(stream_id, stream_version, data, created_at)
VALUES (@sid, @version,
        jsonb_build_object('Case', @eventType::text, 'data', 'test', 'targetUserId', 0,
                           'actionChannelId', @channelId::bigint, 'actionMessageId', 12345), @createdAt)
"""
        let! result = conn.ExecuteAsync(sql,
            {| sid = $"callback:{id}"; version = version; eventType = eventType
               channelId = channelId; createdAt = createdAt |})
        %result
    }

    let activeCount (id: Guid) = task {
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        return! conn.QuerySingleAsync<int>(
            "SELECT count(*)::int FROM active_callback WHERE stream_id = @sid", {| sid = $"callback:{id}" |})
    }

    [<Fact>]
    let ``Cleanup selects only eligible active callbacks and honors the exact cutoff`` () = task {
        let channel = -int64 (Random.Shared.Next(100000, Int32.MaxValue))
        let pending, posted, expired, resolved, recent, boundary, other =
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
        let old = now.UtcDateTime.AddDays(-2)
        for id in [pending; posted; expired; resolved] do
            do! append id 1 "CallbackCreated" channel old
        do! append recent 1 "CallbackCreated" channel now.UtcDateTime
        do! append boundary 1 "CallbackCreated" channel (now.UtcDateTime.AddDays(-1))
        do! append other 1 "CallbackCreated" (channel - 1L) old
        do! db.RecordCallbackMessagePosted(posted, 4321L)
        do! db.ExpireCallback expired
        let! resolution = db.ResolveCallback resolved
        Assert.True(resolution.IsSome)

        let! failed = db.GetFailedCallbackPosts(TimeSpan.FromDays 1)
        Assert.Contains(pending, failed)
        Assert.Contains(other, failed)
        for id in [posted; expired; resolved; recent; boundary] do
            Assert.DoesNotContain(id, failed)

        let! channelRows = db.GetOldCallbacksInChannel(TimeSpan.FromDays 1, channel)
        Assert.Equal<Set<Guid>>(set [pending; posted], channelRows |> Array.map _.id |> Set.ofArray)
        Assert.Equal(Some 4321L, (channelRows |> Array.find (fun row -> row.id = posted)).action_message_id)
        Assert.Equal(None, (channelRows |> Array.find (fun row -> row.id = pending)).action_message_id)

        let! _ = db.ExpireOrphanedCallbacks(TimeSpan.FromDays 1)
        for id in [pending; posted; other] do
            let! terminal = fixture.HasCallbackExpired id
            Assert.True terminal
            let! count = activeCount id
            Assert.Equal(0, count)
        for id in [recent; boundary] do
            let! count = activeCount id
            Assert.Equal(1, count)
    }

    [<Fact>]
    let ``Terminal callbacks stay absent after late posts and repeated creation`` () = task {
        let id = Guid.NewGuid()
        do! append id 1 "CallbackCreated" 0L now.UtcDateTime
        do! append id 2 "CallbackExpired" 0L now.UtcDateTime
        do! append id 3 "CallbackMessagePosted" 0L now.UtcDateTime
        do! append id 4 "CallbackCreated" 0L now.UtcDateTime
        let! count = activeCount id
        Assert.Equal(0, count)
    }

    [<Fact>]
    let ``Repeated creation preserves the source query row multiplicity`` () = task {
        let id = Guid.NewGuid()
        do! append id 1 "CallbackCreated" 0L now.UtcDateTime
        do! append id 2 "CallbackCreated" 0L now.UtcDateTime
        let! count = activeCount id
        Assert.Equal(2, count)
        do! db.ExpireCallback id
        let! afterExpiry = activeCount id
        Assert.Equal(0, afterExpiry)
    }

    [<Fact>]
    let ``Event rollback also rolls back the active callback projection`` () = task {
        let id = Guid.NewGuid()
        do! append id 1 "CallbackCreated" 0L now.UtcDateTime
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        let sql = """
INSERT INTO event(stream_id, stream_version, data)
VALUES (@sid, 2, '{"Case":"CallbackResolved"}'::jsonb)
"""
        let! inserted = conn.ExecuteAsync(sql, {| sid = $"callback:{id}" |}, tx)
        %inserted
        let! during = conn.QuerySingleAsync<int>(
            "SELECT count(*)::int FROM active_callback WHERE stream_id = @sid", {| sid = $"callback:{id}" |}, tx)
        Assert.Equal(0, during)
        do! tx.RollbackAsync()
        let! afterRollback = activeCount id
        Assert.Equal(1, afterRollback)
    }

    [<Fact>]
    let ``Concurrent resolution and expiry leave no active callback`` () = task {
        let id = Guid.NewGuid()
        do! append id 1 "CallbackCreated" 0L now.UtcDateTime
        let otherDb = DbService(fixture.DbConnectionString, TimeProvider.System)
        do! Task.WhenAll [| db.ResolveCallback(id) :> Task; otherDb.ExpireCallback(id) :> Task |]
        let! count = activeCount id
        Assert.Equal(0, count)
    }

    [<Fact>]
    let ``Event corrections refresh both old and new callback streams`` () = task {
        let oldId, newId = Guid.NewGuid(), Guid.NewGuid()
        do! append oldId 1 "CallbackCreated" 0L now.UtcDateTime
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let! moved = conn.ExecuteAsync(
            "UPDATE event SET stream_id = @newSid WHERE stream_id = @oldSid",
            {| oldSid = $"callback:{oldId}"; newSid = $"callback:{newId}" |})
        %moved
        let! oldCount = activeCount oldId
        let! newCount = activeCount newId
        Assert.Equal(0, oldCount)
        Assert.Equal(1, newCount)
        let! deleted = conn.ExecuteAsync("DELETE FROM event WHERE stream_id = @sid", {| sid = $"callback:{newId}" |})
        %deleted
        let! remaining = activeCount newId
        Assert.Equal(0, remaining)
    }
