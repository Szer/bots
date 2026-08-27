module VahterBanBot.Tests.MLFallbackGuardTests

open System
open System.IO
open VahterBanBot
open VahterBanBot.Tests.ContainerTestBase
open Npgsql
open Dapper
open Xunit

/// DB-integration-level coverage of DB.fs's SaveTrainedModel WHERE guard (ML.fs's loser-timeout
/// fallback race). A full two-pod StartAsync race is impractical to test directly
/// (SDCA training takes minutes; timing two real HostedServices against the 5-minute poll window
/// would be slow and flaky) -- this exercises the exact SQL guard against a real Postgres instead,
/// which is what actually prevents the clobber.
///
/// Uses the ML-DISABLED container deliberately: ml_trained_model is untouched by app logic there
/// (MlEnabled=false short-circuits both StartAsync's load and CleanupService's TryReloadIfNewer),
/// so writing directly to the singleton row here can't corrupt the ML-enabled container's shared,
/// pinned model that other tests assert deterministic scores against.
type MLFallbackGuardTests(fixture: MlDisabledVahterTestContainers) =

    [<Fact>]
    let ``SaveTrainedModel does not overwrite a newer model already in DB`` () = task {
        let db = DbService(fixture.DbConnectionString, TimeProvider.System)
        use conn = new NpgsqlConnection(fixture.DbConnectionString)

        // Simulate the race winner: a model row timestamped in the future, so any subsequent
        // SaveTrainedModel call using the real clock is guaranteed to look older than it.
        let winnerBytes = [| 1uy; 2uy; 3uy |]
        let futureCreatedAt = DateTime.UtcNow.AddHours 1.0
        let! _ =
            conn.ExecuteAsync(
                "INSERT INTO ml_trained_model (id, model_data, created_at) VALUES (1, @data, @createdAt) \
                 ON CONFLICT (id) DO UPDATE SET model_data = EXCLUDED.model_data, created_at = EXCLUDED.created_at",
                {| data = winnerBytes; createdAt = futureCreatedAt |})

        // Losing pod's fallback retrain (ML.fs StartAsync timeout branch) attempts to save.
        let loserBytes = [| 9uy; 9uy; 9uy |]
        use loserStream = new MemoryStream(loserBytes)
        let! saved = db.SaveTrainedModel(loserStream)
        Assert.False(saved, "A write timestamped older than the existing row must report failure, not silently no-op")

        // DB must still hold the winner's bytes -- not clobbered by the loser.
        let! stillWinnerBytes = conn.QuerySingleAsync<byte[]>("SELECT model_data FROM ml_trained_model WHERE id = 1")
        Assert.Equal<byte[]>(winnerBytes, stillWinnerBytes)
    }

    [<Fact>]
    let ``SaveTrainedModel does overwrite when the new model is actually newer`` () = task {
        let db = DbService(fixture.DbConnectionString, TimeProvider.System)
        use conn = new NpgsqlConnection(fixture.DbConnectionString)

        let staleBytes = [| 4uy; 5uy; 6uy |]
        let pastCreatedAt = DateTime.UtcNow.AddHours -1.0
        let! _ =
            conn.ExecuteAsync(
                "INSERT INTO ml_trained_model (id, model_data, created_at) VALUES (1, @data, @createdAt) \
                 ON CONFLICT (id) DO UPDATE SET model_data = EXCLUDED.model_data, created_at = EXCLUDED.created_at",
                {| data = staleBytes; createdAt = pastCreatedAt |})

        let freshBytes = [| 7uy; 8uy; 9uy |]
        use freshStream = new MemoryStream(freshBytes)
        let! saved = db.SaveTrainedModel(freshStream)
        Assert.True(saved, "A write timestamped after the existing row must succeed")

        let! nowBytes = conn.QuerySingleAsync<byte[]>("SELECT model_data FROM ml_trained_model WHERE id = 1")
        Assert.Equal<byte[]>(freshBytes, nowBytes)
    }
