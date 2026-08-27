namespace MultiPodTests

open System.Net
open System.Net.Http
open System.Text
open BotInfra
open BotTestInfra
open Npgsql
open Xunit

/// API-level reconnect coverage for SettingsListenerHostedService, replacing the internal-level
/// tests deleted from VahterBanBot.Tests/SettingsNotifyTests.fs (PR #425 review). Picked the
/// Coupon fixture over Vahter's: no ML cold-start model file/advisory-lock race to wait out, so
/// the fixture boots faster and the reconnect scenario stays the only variable under test.
type CouponSettingsReconnectTests(fixture: CouponMultiPodContainers) =

    [<Fact>]
    let ``instance 1 reloads via reconnect after its LISTEN backend is killed`` () = task {
        // Harsher than targeting one instance's backend by id/start-time: killing every LISTEN
        // backend forces BOTH instances through SettingsListenerHostedService's reconnect path,
        // and the assertion below still only needs instance 1's convergence to hold.
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        do! conn.OpenAsync()
        use killCmd = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE query ILIKE 'LISTEN %' AND pid <> pg_backend_pid()", conn)
        let! _ = killCmd.ExecuteNonQueryAsync()

        let newHour = 21
        do! DbSettings.upsertBotSetting fixture.DbConnectionString "REMINDER_HOUR_DUBLIN" (string newHour) "FREE_FORM" "REMINDER"

        use content = new StringContent("", Encoding.UTF8, "application/json")
        let! reloadResp = fixture.BotHttpAt(0).PostAsync("/reload-settings", content)
        Assert.Equal(HttpStatusCode.OK, reloadResp.StatusCode)

        // Bound of 15s: reconnect backoff is 1s min / 30s max cap, first retry lands ~1s in;
        // SettingsListenerHostedService also reloads unconditionally on reconnect, so this
        // passes whether instance 1 recovers the NOTIFY or just the reconnect-triggered reload.
        let! reached, lastDump1 =
            SettingsPollHelpers.waitForFieldWithin 15.0 (fun () -> fixture.GetSettingsDump 1) "ReminderHourDublin" (string newHour)
        Assert.True(
            reached,
            $"Instance 1 never observed REMINDER_HOUR_DUBLIN={newHour} within 15s after its LISTEN backend was killed.\n"
            + $"Instance 1 last-seen dump: {lastDump1}")
    }
