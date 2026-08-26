module VahterBanBot.Tests.SettingsNotifyTests

open System
open System.Threading
open System.Threading.Tasks
open BotInfra
open DotNet.Testcontainers.Builders
open Testcontainers.PostgreSql
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open Xunit

/// SettingsNotify/SettingsListenerHostedService only need a live LISTEN/NOTIFY channel — no
/// migrations, no bot_setting table — so this skips the full BotContainerBase stack (no
/// bot app, no flyway, no fakes) and owns a bare Postgres container instead.
type SettingsNotifyFixture() =
    let container = PostgreSqlBuilder("postgres:17.10").Build()
    let mutable connString = null

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! container.StartAsync()
                connString <- container.GetConnectionString()
            } :> Task)
    interface IAsyncDisposable with
        member _.DisposeAsync() = container.DisposeAsync()

    member _.ConnectionString = connString

/// Polls `check` until it returns true or `attempts * delayMs` elapses.
let private waitUntil (attempts: int) (delayMs: int) (check: unit -> bool) : Task<bool> =
    task {
        let mutable tries = 0
        while not (check()) && tries < attempts do
            tries <- tries + 1
            do! Task.Delay delayMs
        return check()
    }

/// Kills the backend still executing `LISTEN` (idle-in-listen state) to simulate a
/// connection drop, forcing SettingsListenerHostedService's reconnect path.
let private terminateListenerBackend (connString: string) : Task =
    task {
        use conn = new NpgsqlConnection(connString)
        do! conn.OpenAsync()
        use cmd = new NpgsqlCommand(
            """
SELECT pg_terminate_backend(pid)
FROM pg_stat_activity
WHERE query ILIKE 'LISTEN %' AND pid <> pg_backend_pid()
            """, conn)
        let! _ = cmd.ExecuteNonQueryAsync()
        ()
    }
    :> Task

type SettingsNotifyTests(fixture: SettingsNotifyFixture) =

    [<Fact>]
    let ``NOTIFY on one connection triggers the listener's reload callback`` () = task {
        let mutable reloadCount = 0
        let reload () : Task = task { Interlocked.Increment(&reloadCount) |> ignore } :> Task
        // `use`: Dispose() cancels the hosted service's stopping token even if an
        // assertion below throws, so the background LISTEN loop always winds down.
        use listener =
            new SettingsListenerHostedService(
                fixture.ConnectionString, reload, NullLogger<SettingsListenerHostedService>.Instance)
        do! listener.StartAsync(CancellationToken.None)

        // Reload-on-connect fires once before any NOTIFY.
        let! connected = waitUntil 40 50 (fun () -> reloadCount >= 1)
        Assert.True(connected, "listener should reload once right after connecting")

        let before = reloadCount
        do! SettingsNotify.notifySettingsChanged fixture.ConnectionString

        let! notified = waitUntil 40 50 (fun () -> reloadCount > before)
        Assert.True(notified, "listener should reload after receiving a NOTIFY")

        do! listener.StopAsync(CancellationToken.None)
    }

    [<Fact>]
    let ``listener reloads again after reconnecting following a dropped connection`` () = task {
        let mutable reloadCount = 0
        let reload () : Task = task { Interlocked.Increment(&reloadCount) |> ignore } :> Task
        use listener =
            new SettingsListenerHostedService(
                fixture.ConnectionString, reload, NullLogger<SettingsListenerHostedService>.Instance,
                minBackoff = TimeSpan.FromMilliseconds 100.0, maxBackoff = TimeSpan.FromMilliseconds 500.0)
        do! listener.StartAsync(CancellationToken.None)

        let! connected = waitUntil 40 50 (fun () -> reloadCount >= 1)
        Assert.True(connected, "listener should reload once right after connecting")

        let before = reloadCount
        do! terminateListenerBackend fixture.ConnectionString

        // No NOTIFY is sent — the reload after reconnect must come from the
        // reconnect-closes-the-missed-notification-window behavior itself.
        let! reconnected = waitUntil 100 50 (fun () -> reloadCount > before)
        Assert.True(reconnected, "listener should reload again after reconnecting")

        do! listener.StopAsync(CancellationToken.None)
    }

    interface IClassFixture<SettingsNotifyFixture>
