namespace CouponHubBot.Services

open System
open Dapper
open Npgsql
open Microsoft.Extensions.Options
open BotInfra
open CouponHubBot
open Funogram.Telegram.Types
open Funogram.Telegram

type CommunityInvites(connectionString: string, tg: ITelegramApi, options: IOptions<BotConfiguration>) =
    member _.GetLink(chatId: int64, sponsor: int64) = task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        let key = {| sponsor = sponsor; chat = chatId |}
        let! _ = conn.QuerySingleAsync<int64>("""SELECT id FROM "user" WHERE id=@sponsor FOR UPDATE""", key, tx)
        let! existing = conn.QuerySingleOrDefaultAsync<string>(
            "SELECT link FROM community_invite WHERE chat_id=@chat AND sponsor=@sponsor AND applicant IS NULL", key, tx)
        if not (isNull existing) then return existing
        else
            let! invite = tg.CallExn(Req.CreateChatInviteLink.Make(chatId, createsJoinRequest = true))
            do! conn.ExecuteAsync(
                "INSERT INTO community_invite(link, chat_id, sponsor) VALUES (@link,@chat,@sponsor)",
                {| link = invite.InviteLink; chat = chatId; sponsor = sponsor |}, tx) |> taskIgnore
            do! tx.CommitAsync()
            return invite.InviteLink
    }

    member private _.Claim(chatId: int64, link: string, applicant: int64, updateId: int64) = task {
        use conn = new NpgsqlConnection(connectionString)
        let key = {| chat = chatId; link = link; applicant = applicant; update = updateId |}
        // Commit the winner before approval so even an ambiguous Telegram timeout cannot admit a second person.
        return! conn.QuerySingleOrDefaultAsync<Nullable<int64>>(
            """UPDATE community_invite SET applicant=@applicant, request_update=@update,
                   new_user=CASE WHEN applicant IS NULL THEN NOT EXISTS
                       (SELECT 1 FROM "user" WHERE id=@applicant) ELSE new_user END
               WHERE chat_id=@chat AND link=@link AND sponsor<>@applicant AND NOT retired
                 AND (applicant IS NULL OR (applicant=@applicant AND request_update=@update))
               RETURNING sponsor""", key)
    }

    member private _.Approve(chatId: int64, link: string, owner: int64, request: ChatJoinRequest) = task {
        use conn = new NpgsqlConnection(connectionString)
        do! conn.OpenAsync()
        use! tx = conn.BeginTransactionAsync()
        let! finished = conn.QuerySingleAsync<bool>(
            "SELECT finished FROM community_invite WHERE link=@link FOR UPDATE", {| link = link |}, tx)
        if not finished then
            let! sponsor = tg.CallExn(Req.GetChatMember.Make(chatId, owner))
            let eligible = TelegramMembershipService.IsAdmitted sponsor
            if eligible then
                let! result =
                    match request.QueryId with
                    | Some query -> tg.Call(Req.AnswerChatJoinRequestQuery.Make(query, "approve"))
                    | None -> tg.Call(Req.ApproveChatJoinRequest.Make(chatId, request.From.Id))
                let admitted =
                    match result with
                    | Ok true -> true
                    | Error err when err.Description.Contains("USER_ALREADY_PARTICIPANT") -> false
                    | Error err when err.Description.Contains("HIDE_REQUESTER_MISSING") -> false
                    | _ -> failwith "Telegram join approval failed"
                if admitted then
                    do! conn.ExecuteAsync(
                        """INSERT INTO "user"(id, username, first_name, last_name, invited_by)
                           VALUES (@id,@username,@first,@last,@sponsor)
                           ON CONFLICT (id) DO UPDATE SET invited_by=EXCLUDED.invited_by
                           WHERE "user".invited_by IS NULL AND (SELECT new_user FROM community_invite WHERE link=@link)""",
                        {| id = request.From.Id; username = Option.toObj request.From.Username
                           link = link; first = request.From.FirstName; last = Option.toObj request.From.LastName; sponsor = owner |}, tx) |> taskIgnore
            do! conn.ExecuteAsync("UPDATE community_invite SET finished=true WHERE link=@link", {| link = link |}, tx) |> taskIgnore
        do! tx.CommitAsync()
    }

    member this.Handle(updateId: int64, request: ChatJoinRequest) = task {
        let chatId = request.Chat.Id
        match request.InviteLink with
        | Some invite when options.Value.ManagedInvitesEnabled && chatId = options.Value.CommunityChatId ->
            let! owner = this.Claim(chatId, invite.InviteLink, request.From.Id, updateId)
            if owner.HasValue then
                do! this.Approve(chatId, invite.InviteLink, owner.Value, request)
                let! revoked = tg.Call(Req.RevokeChatInviteLink.Make(request.Chat.Id, invite.InviteLink))
                match revoked with
                | Ok _ -> ()
                | Error err when err.Description.Contains("INVITE_HASH_EXPIRED") -> ()
                | Error _ -> failwith "Telegram invite revocation failed"
                let! _ = this.GetLink(request.Chat.Id, owner.Value)
                use conn = new NpgsqlConnection(connectionString)
                do! conn.ExecuteAsync("UPDATE community_invite SET retired=true WHERE link=@link",
                                      {| link = invite.InviteLink |}) |> taskIgnore
            else
                match request.QueryId with
                | Some query -> do! tg.CallExn(Req.AnswerChatJoinRequestQuery.Make(query, "queue")) |> taskIgnore
                | None -> ()
        | _ -> ()
    }
