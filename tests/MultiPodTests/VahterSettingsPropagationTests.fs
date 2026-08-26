namespace MultiPodTests

open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open BotTestInfra
open Npgsql
open Dapper
open Xunit

/// Owner's exact acceptance scenario for PR #425 (Postgres LISTEN/NOTIFY cross-pod settings
/// propagation): a `bot_setting` row changed directly in the DB and picked up by ONE
/// instance's /reload-settings must reach every OTHER instance's live BotConfiguration too,
/// without that instance ever calling /reload-settings itself — the gap the single-pod
/// suite's /reload-settings never had to close.
type VahterSettingsPropagationTests(fixture: VahterMultiPodContainers) =

    [<Fact>]
    let ``reload-settings on instance 0 propagates STATS_SCHEDULED_HOUR_UTC to instance 1 within 5s`` () = task {
        let newHour = 13
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        do! conn.OpenAsync()
        let! _ =
            conn.ExecuteAsync(
                "INSERT INTO bot_setting(key,value,type,feature_group) VALUES('STATS_SCHEDULED_HOUR_UTC', @v, 'FREE_FORM', 'CORE') \
                 ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value",
                {| v = string newHour |})

        use content = new StringContent("", Encoding.UTF8, "application/json")
        let! reloadResp = fixture.BotHttpAt(0).PostAsync("/reload-settings", content)
        Assert.Equal(HttpStatusCode.OK, reloadResp.StatusCode)

        // Sanity: the instance that actually called /reload-settings sees it immediately.
        let! dump0 = fixture.GetSettingsDump 0
        use doc0 = JsonDocument.Parse dump0
        Assert.Equal(newHour, doc0.RootElement.GetProperty("StatsScheduledHour").GetInt32())

        let! reached, lastDump1 =
            SettingsPollHelpers.waitForField (fun () -> fixture.GetSettingsDump 1) "StatsScheduledHour" (string newHour)
        Assert.True(
            reached,
            $"Instance 1 never observed STATS_SCHEDULED_HOUR_UTC={newHour} within 5s via LISTEN/NOTIFY.\n"
            + $"Instance 0 dump: {dump0}\nInstance 1 last-seen dump: {lastDump1}")
    }
