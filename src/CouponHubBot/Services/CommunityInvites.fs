namespace CouponHubBot.Services

open System
open Dapper
open Npgsql
open Microsoft.Extensions.Options
open BotInfra
open CouponHubBot
open Funogram.Telegram

type CommunityInvites(connectionString: string, tg: ITelegramApi, options: IOptions<BotConfiguration>) =
    let createLink (conn: NpgsqlConnection) (tx: NpgsqlTransaction) (chatId: int64) (sponsor: int64) = task {
        let! invite = tg.CallExn(Req.CreateChatInviteLink.Make(chatId, memberLimit = 1L))
        do! conn.ExecuteAsync(
            "INSERT INTO community_invite(link, chat_id, sponsor) VALUES (@link,@chat,@sponsor)",
            {| link = invite.InviteLink; chat = chatId; sponsor = sponsor |}, tx) |> taskIgnore
        return invite.InviteLink
    }

    member _.GetLink(chatId: int64, sponsor: int64) = task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        let key = {| sponsor = sponsor; chat = chatId |}
        let! _ = conn.QuerySingleAsync<int64>("""SELECT id FROM "user" WHERE id=@sponsor FOR UPDATE""", key, tx)
        let! existing = conn.QuerySingleOrDefaultAsync<string>(
            "SELECT link FROM community_invite WHERE chat_id=@chat AND sponsor=@sponsor AND NOT consumed", key, tx)
        if not (isNull existing) then return existing
        else
            let! link = createLink conn tx chatId sponsor
            do! tx.CommitAsync()
            return link
    }

    member _.Joined(chatId: int64, link: string, applicant: int64, date: DateTime, updateId: int64) = task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        let key = {| chat = chatId; link = link; applicant = applicant; date = date.ToUniversalTime(); update = updateId |}
        let! owner = conn.QuerySingleOrDefaultAsync<Nullable<int64>>(
            """SELECT u.id FROM community_invite i JOIN "user" u ON u.id=i.sponsor
               WHERE i.chat_id=@chat AND i.link=@link FOR NO KEY UPDATE OF u""", key, tx)
        if owner.HasValue then
            let! recorded = conn.ExecuteAsync(
                """INSERT INTO community_invite_join(link, applicant, joined_at, update_id) VALUES (@link,@applicant,@date,@update)
                   ON CONFLICT DO NOTHING""", key, tx)
            if recorded = 1 then
                let! _ = conn.QuerySingleAsync<int64>("""SELECT id FROM "user" WHERE id=@applicant FOR NO KEY UPDATE""", key, tx)
                do! conn.ExecuteAsync(
                    """UPDATE "user" SET invited_by=(
                           SELECT i.sponsor FROM community_invite_join j JOIN community_invite i USING (link)
                           WHERE j.applicant=@applicant AND i.chat_id=@chat
                           ORDER BY j.joined_at DESC, j.update_id DESC LIMIT 1)
                       WHERE id=@applicant""", key, tx) |> taskIgnore
                let! consumed = conn.ExecuteAsync(
                    "UPDATE community_invite SET consumed=true WHERE link=@link AND NOT consumed", key, tx)
                if consumed = 1 then
                    let! revoked = tg.Call(Req.RevokeChatInviteLink.Make(chatId, link))
                    match revoked with
                    | Ok _ -> ()
                    | Error err when err.Description.Contains("INVITE_HASH_EXPIRED") -> ()
                    | Error _ -> failwith "Telegram invite revocation failed"
                    if options.Value.ManagedInvitesEnabled then
                        do! createLink conn tx chatId owner.Value |> taskIgnore
        do! tx.CommitAsync()
    }
