module VahterBanBot.Tests.UpdateChatAdminsTests

open System
open VahterBanBot
open VahterBanBot.Tests.ContainerTestBase
open BotTestInfra
open Npgsql
open Dapper
open Xunit

/// Postgres-backed chat_admin table (V46) + interval lease (UpdateChatAdmins.fs, DB.fs's
/// TryAcquireIntervalJob) multi-pod integration tests. Uses `DbService` directly against the
/// shared container's Postgres (same construction pattern as MessageTests/SnapshotTests) rather
/// than the app HTTP surface -- there is no HTTP endpoint for this internal refresh mechanism.
type UpdateChatAdminsTests(fixture: MlEnabledVahterTestContainers, _unused: MlAwaitFixture) =

    /// "One pod fetches, all pods read" -- SaveChatAdmins fully replaces the table contents
    /// (delete-then-insert), and GetChatAdminIds (the reader snapshot every pod's local timer
    /// reloads from) reflects it, including removal of a stale admin, not just accumulation.
    [<Fact>]
    let ``SaveChatAdmins replaces table contents; GetChatAdminIds reflects it (shared across pods)`` () = task {
        let db = DbService(fixture.DbConnectionString, TimeProvider.System)
        let chatA, chatB = -9001L, -9002L
        let userX, userY = 555001L, 555002L

        do! db.SaveChatAdmins([| chatA, userX; chatB, userY |])
        let! idsFirst = db.GetChatAdminIds()
        Assert.Contains(userX, idsFirst)
        Assert.Contains(userY, idsFirst)

        // A later fetch (simulating the next lease window, possibly won by a different pod) fully
        // replaces the table -- a stale admin must disappear, not just accumulate.
        do! db.SaveChatAdmins([| chatA, userX |])
        let! idsSecond = db.GetChatAdminIds()
        Assert.Contains(userX, idsSecond)
        Assert.DoesNotContain(userY, idsSecond)
    }

    /// Uses a dedicated job_name (not 'chat_admins_refresh') so this doesn't race the live
    /// UpdateChatAdmins hosted service already ticking against the shared container.
    [<Fact>]
    let ``TryAcquireIntervalJob: one pod wins the lease, a second is blocked until minInterval elapses`` () = task {
        let jobName = $"test_interval_job_{Guid.NewGuid():N}"
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let! _ = conn.ExecuteAsync("INSERT INTO scheduled_job (job_name) VALUES (@jobName)", {| jobName = jobName |})

        let db = DbService(fixture.DbConnectionString, TimeProvider.System)
        let minInterval = TimeSpan.FromHours 1.0

        let! podAAcquired = db.TryAcquireIntervalJob(jobName, minInterval, "pod-a")
        Assert.True(podAAcquired, "First pod should acquire the never-run lease")

        let! podBAcquired = db.TryAcquireIntervalJob(jobName, minInterval, "pod-b")
        Assert.False(podBAcquired, "A second pod must not acquire the lease while pod-a holds it")

        do! db.CompleteScheduledJob(jobName)

        let! podBRightAfterComplete = db.TryAcquireIntervalJob(jobName, minInterval, "pod-b")
        Assert.False(podBRightAfterComplete, "Within minInterval of completion, no pod should re-acquire")

        // Backdate completion past minInterval (same time-travel convention as
        // LlmVerdictCacheGlobalFlagTests's AgeLlmVerdictCache) instead of sleeping for real.
        let! _ =
            conn.ExecuteAsync(
                "UPDATE scheduled_job SET last_completed_at = last_completed_at - make_interval(mins => 61) WHERE job_name = @jobName",
                {| jobName = jobName |})
        let! podBAfterIntervalElapsed = db.TryAcquireIntervalJob(jobName, minInterval, "pod-b")
        Assert.True(podBAfterIntervalElapsed, "After minInterval elapses, the lease should be acquirable again")
    }

    interface IClassFixture<MlAwaitFixture>
