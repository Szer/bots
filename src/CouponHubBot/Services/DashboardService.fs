namespace CouponHubBot.Services

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open BotInfra
open CouponHubBot.Utils
open Dapper
open Npgsql

[<CLIMutable>]
type DashboardRow =
    { Community: bool
      Id: string
      Name: string
      Username: string | null
      Takes: int
      TakeDays: Nullable<float>
      Resolved: int
      HoldHours: Nullable<float>
      Returns: int
      ReturnPct: Nullable<float>
      ReturnHoldHours: Nullable<float>
      ReturnDays: Nullable<float>
      LateReturns: int
      HeldDayLateReturns: int
      Unresolved: int
      UnresolvedExpired: int
      VoidClosed: int
      Adds: int
      AddDays: Nullable<float>
      LateAdds: int
      LateAddPct: Nullable<float> }

type DashboardSnapshot =
    { AsOf: DateTimeOffset
      RefreshAfter: DateTimeOffset
      PeriodDays: int
      Community: DashboardRow
      Members: DashboardRow array }

type DashboardService(connString: string, time: TimeProvider, tg: ITelegramApi) =
    let snapshots = Dictionary<int, DashboardSnapshot>()
    let snapshotGate = new SemaphoreSlim(1, 1)
    let infoGate = new SemaphoreSlim(1, 1)
    let mutable botLink: (string option * DateTimeOffset) option = None
    let statsSql =
        use stream = typeof<DashboardRow>.Assembly.GetManifestResourceStream("CouponHubBot.DashboardStats.sql")
        use reader = new StreamReader(stream)
        reader.ReadToEnd()

    let hashToken (token: string) = SHA256.HashData(Text.Encoding.UTF8.GetBytes token)

    member _.CreateSession(userId: int64, communityId: int64) =
        task {
            let token = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes 32)
            let now = time.GetUtcNow()
            let expiresAt = now.AddMinutes 15.0
            use conn = new NpgsqlConnection(connString)
            do! conn.ExecuteAsync(
                """
DELETE FROM dashboard_session WHERE expires_at <= @now;
INSERT INTO dashboard_session (token_hash, user_id, community_id, expires_at)
VALUES (@hash, @userId, @communityId, @expiresAt);
""", {| hash = hashToken token; userId = userId; communityId = communityId; now = now; expiresAt = expiresAt |}) :> Task
            return {| token = token; expiresAt = expiresAt; viewerId = string userId |}
        }

    member _.FindSession(token: string, communityId: int64) =
        task {
            if token.Length <> 43 then return None
            else
                use conn = new NpgsqlConnection(connString)
                let! userId = conn.QuerySingleOrDefaultAsync<Nullable<int64>>(
                    """
SELECT user_id FROM dashboard_session
WHERE token_hash = @hash AND expires_at > @now AND community_id = @communityId
                    """, {| hash = hashToken token; now = time.GetUtcNow(); communityId = communityId |})
                return if userId.HasValue then Some userId.Value else None
        }

    member _.GetSnapshot(days: int) =
        task {
            do! snapshotGate.WaitAsync()
            try
                let now = time.GetUtcNow()
                match snapshots.TryGetValue days with
                | true, cached when cached.RefreshAfter > now -> return cached
                | _ ->
                    let startAt = if days = 0 then DateTimeOffset.UnixEpoch else now.AddDays(-float days)
                    use conn = new NpgsqlConnection(connString)
                    let! rows = conn.QueryAsync<DashboardRow>(statsSql, {| start_at = startAt; end_at = now |}, commandTimeout = 10)
                    let rows = Seq.toArray rows
                    let snapshot =
                        { AsOf = now
                          RefreshAfter = now.AddMinutes 15.0
                          PeriodDays = days
                          Community = rows |> Array.find (fun r -> r.Community)
                          Members = rows |> Array.filter (fun r -> not r.Community) }
                    snapshots[days] <- snapshot
                    return snapshot
            finally
                %snapshotGate.Release()
        }

    member _.GetBotLink() =
        task {
            do! infoGate.WaitAsync()
            try
                let now = time.GetUtcNow()
                match botLink with
                | Some (link, expiresAt) when expiresAt > now -> return link
                | _ ->
                    let! me = tg.Call(Funogram.Telegram.Req.GetMe.Make())
                    let link =
                        match me with
                        | Ok bot -> bot.Username |> Option.map (fun username -> "https://t.me/" + username + "?start=dashboard")
                        | Error _ -> None
                    botLink <- Some (link, now.AddMinutes 15.0)
                    return link
            finally
                %infoGate.Release()
        }

    interface IDisposable with
        member _.Dispose() =
            snapshotGate.Dispose()
            infoGate.Dispose()
