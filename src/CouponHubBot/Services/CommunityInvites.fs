namespace CouponHubBot.Services

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Dapper
open Npgsql
open Microsoft.Extensions.Options
open BotInfra
open CouponHubBot
open Funogram.Telegram

type CommunityInvites(connectionString: string, tg: ITelegramApi, options: IOptions<BotConfiguration>) =
    let writer = TransactionalEventWriter("community_invite_event", JsonSerializerOptions.Default)
    let streamId (chatId: int64) (sponsor: int64) = $"coupon-invite:{chatId}:{sponsor}"
    let inviteId (link: string) = Encoding.UTF8.GetBytes(link) |> SHA256.HashData |> Convert.ToHexStringLower

    let revokeLink (chatId: int64) (link: string) = task {
        let! result = tg.Call(Req.RevokeChatInviteLink.Make(chatId, link))
        match result with
        | Ok _ -> ()
        | Error err when err.Description.Contains("INVITE_HASH_EXPIRED") -> ()
        | Error _ -> failwith "Telegram invite revocation failed"
    }

    let createLink (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (chatId: int64) (sponsor: int64) = task {
        let! invite = tg.CallExn(Req.CreateChatInviteLink.Make(chatId, memberLimit = 1L))
        do! conn.ExecuteAsync(
            """UPDATE "user" SET invite_link=@link WHERE id=@sponsor""",
            {| link = invite.InviteLink; sponsor = sponsor |}, tx) |> taskIgnore
        let event = {| Case = "invite_link_created"; actorKind = "bot"; chatId = chatId
                       sponsorId = sponsor; inviteId = inviteId invite.InviteLink |}
        do! writer.AppendToLockedStream(tx, streamId chatId sponsor, event, DateTime.UtcNow)
        return invite.InviteLink
    }

    member _.GetLink(chatId: int64, sponsor: int64, messageId: int64, date: DateTime) = task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        let! current = conn.QuerySingleAsync<string>(
            """SELECT invite_link FROM "user" WHERE id=@sponsor FOR UPDATE""", {| sponsor = sponsor |}, tx)
        let! replay = conn.QuerySingleOrDefaultAsync<Nullable<bool>>(
            """SELECT COALESCE(data->>'inviteId'=community_invite_key(@current), false)
               FROM community_invite_event WHERE stream_id=@stream AND event_type='invite_link_obtained'
                 AND (data->>'messageId')::bigint=@message""",
            {| current = current; message = messageId; stream = streamId chatId sponsor |}, tx)
        if replay.HasValue then
            return if replay.Value then Option.ofObj current else None
        else
            let! link =
                if isNull current then createLink conn tx chatId sponsor
                else System.Threading.Tasks.Task.FromResult current
            let event = {| Case = "invite_link_obtained"; actorKind = "user"; chatId = chatId; sponsorId = sponsor
                           inviteId = inviteId link; actorId = sponsor; messageId = messageId |}
            do! writer.AppendToLockedStream(tx, streamId chatId sponsor, event, date.ToUniversalTime())
            do! tx.CommitAsync()
            return Some link
    }

    member _.Joined(chatId: int64, link: string, applicant: int64, date: DateTime, updateId: int64) = task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        let key = {| chat = chatId; inviteId = inviteId link; applicant = applicant; date = date.ToUniversalTime() |}
        let! owner = conn.QuerySingleOrDefaultAsync<Nullable<int64>>(
            """SELECT u.id FROM community_invite_event e JOIN "user" u ON u.id=(e.data->>'sponsorId')::bigint
               WHERE e.event_type='invite_link_created' AND e.data->>'inviteId'=@inviteId
                 AND (e.data->>'chatId')::bigint=@chat FOR NO KEY UPDATE OF u""", key, tx)
        if owner.HasValue then
            let stream = streamId chatId owner.Value
            let! replay = conn.QuerySingleAsync<bool>(
                """SELECT EXISTS (SELECT 1 FROM community_invite_event
                   WHERE stream_id=@stream AND event_type='invite_link_redeemed' AND data->>'inviteId'=@inviteId
                     AND (data->>'updateId')::bigint=@update AND created_at=@date)""",
                {| stream = stream; inviteId = key.inviteId; update = updateId; date = key.date |}, tx)
            if not replay then
                let event = {| Case = "invite_link_redeemed"; actorKind = "user"; chatId = chatId; sponsorId = owner.Value
                               inviteId = key.inviteId; actorId = applicant; updateId = updateId |}
                do! writer.AppendToLockedStream(tx, stream, event, key.date)
                let! _ = conn.QuerySingleAsync<int64>("""SELECT id FROM "user" WHERE id=@applicant FOR NO KEY UPDATE""", key, tx)
                do! conn.ExecuteAsync(
                    """UPDATE "user" SET invited_by=(
                           SELECT (data->>'sponsorId')::bigint FROM community_invite_event
                           WHERE event_type='invite_link_redeemed' AND (data->>'actorId')::bigint=@applicant
                             AND (data->>'chatId')::bigint=@chat
                           ORDER BY created_at DESC, (data->>'updateId')::bigint DESC LIMIT 1)
                       WHERE id=@applicant""", key, tx) |> taskIgnore
                let! consumed = conn.ExecuteAsync(
                    """UPDATE "user" SET invite_link=NULL WHERE id=@sponsor AND invite_link=@link""",
                    {| sponsor = owner.Value; link = link |}, tx)
                if consumed = 1 then
                    do! revokeLink chatId link
                    if options.Value.ManagedInvitesEnabled then
                        do! createLink conn tx chatId owner.Value |> taskIgnore
        do! tx.CommitAsync()
    }
