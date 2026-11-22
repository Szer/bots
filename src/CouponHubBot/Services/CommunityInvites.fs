namespace CouponHubBot.Services

open System
open Dapper
open Npgsql
open Microsoft.Extensions.Options
open BotInfra
open CouponHubBot
open Funogram.Telegram

type CommunityInvites(connectionString: string, tg: ITelegramApi, options: IOptions<BotConfiguration>) =
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
            """UPDATE "user" SET invite_link=@link WHERE id=@sponsor;
               SELECT append_community_invite_event(@chat,@sponsor,@link,'invite_link_created','{"actorKind":"bot"}',clock_timestamp())""",
            {| link = invite.InviteLink; chat = chatId; sponsor = sponsor |}, tx) |> taskIgnore
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
            {| current = current; message = messageId; stream = $"coupon-invite:{chatId}:{sponsor}" |}, tx)
        if replay.HasValue then
            return if replay.Value then Option.ofObj current else None
        else
            let! link =
                if isNull current then createLink conn tx chatId sponsor
                else System.Threading.Tasks.Task.FromResult current
            do! conn.ExecuteAsync(
                """SELECT append_community_invite_event(@chat,@sponsor,@link,'invite_link_obtained',
                       jsonb_build_object('actorKind','user','actorId',@sponsor,'messageId',@message), @date)""",
                {| link = link; chat = chatId; sponsor = sponsor; message = messageId; date = date.ToUniversalTime() |}, tx) |> taskIgnore
            do! tx.CommitAsync()
            return Some link
    }

    member _.Joined(chatId: int64, link: string, applicant: int64, date: DateTime, updateId: int64) = task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        let key = {| chat = chatId; link = link; applicant = applicant; date = date.ToUniversalTime(); update = updateId |}
        let! owner = conn.QuerySingleOrDefaultAsync<Nullable<int64>>(
            """SELECT u.id FROM community_invite_event e JOIN "user" u ON u.id=(e.data->>'sponsorId')::bigint
               WHERE e.event_type='invite_link_created' AND e.data->>'inviteId'=community_invite_key(@link)
                 AND (e.data->>'chatId')::bigint=@chat FOR NO KEY UPDATE OF u""", key, tx)
        if owner.HasValue then
            let! recorded = conn.QuerySingleAsync<bool>(
                """SELECT append_community_invite_event(@chat,@sponsor,@link,'invite_link_redeemed',
                       jsonb_build_object('actorKind','user','actorId',@applicant,'updateId',@update),@date)""",
                {| chat = chatId; sponsor = owner.Value; link = link; applicant = applicant; update = updateId; date = key.date |}, tx)
            if recorded then
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
