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
/// propagation) — CouponHubBot side. See VahterSettingsPropagationTests for the full rationale;
/// this mirrors it against CouponHubBot's own bot_setting/BotConfiguration.
type CouponSettingsPropagationTests(fixture: CouponMultiPodContainers) =

    [<Fact>]
    let ``reload-settings on instance 0 propagates REMINDER_HOUR_DUBLIN to instance 1 within 5s`` () = task {
        let newHour = 15
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        do! conn.OpenAsync()
        let! _ =
            conn.ExecuteAsync(
                "INSERT INTO bot_setting(key,value,type,feature_group) VALUES('REMINDER_HOUR_DUBLIN', @v, 'FREE_FORM', 'REMINDER') \
                 ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value",
                {| v = string newHour |})

        use content = new StringContent("", Encoding.UTF8, "application/json")
        let! reloadResp = fixture.BotHttpAt(0).PostAsync("/reload-settings", content)
        Assert.Equal(HttpStatusCode.OK, reloadResp.StatusCode)

        // Sanity: the instance that actually called /reload-settings sees it immediately.
        let! dump0 = fixture.GetSettingsDump 0
        use doc0 = JsonDocument.Parse dump0
        Assert.Equal(newHour, doc0.RootElement.GetProperty("ReminderHourDublin").GetInt32())

        let! reached, lastDump1 =
            SettingsPollHelpers.waitForField (fun () -> fixture.GetSettingsDump 1) "ReminderHourDublin" (string newHour)
        Assert.True(
            reached,
            $"Instance 1 never observed REMINDER_HOUR_DUBLIN={newHour} within 5s via LISTEN/NOTIFY.\n"
            + $"Instance 0 dump: {dump0}\nInstance 1 last-seen dump: {lastDump1}")
    }
