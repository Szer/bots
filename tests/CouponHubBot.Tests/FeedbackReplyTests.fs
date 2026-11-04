namespace CouponHubBot.Tests

open BotTestInfra
open System.Net
open Dapper
open Npgsql
open Xunit
open Funogram.Telegram.Types
open FakeCallHelpers
open BatchTestHelpers

[<CLIMutable>]
type FeedbackDeliveryRow =
    { feedback_id: int64
      admin_chat_id: int64
      admin_message_id: int64 }

[<CLIMutable>]
type FeedbackReplyRow =
    { feedback_id: int64
      admin_id: int64
      reply_text: string
      delivered: bool }

type FeedbackReplyTests(fixture: DefaultCouponHubTestContainers) =

    // Admin user IDs 900/901 are configured in FEEDBACK_ADMINS for the test container.
    let adminId = 900L
    let secondAdminId = 901L

    let getFeedbackDeliveries (feedbackId: int64) =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            let sql = "SELECT feedback_id, admin_chat_id, admin_message_id FROM feedback_delivery WHERE feedback_id = @fid ORDER BY admin_chat_id"
            let! rows = conn.QueryAsync<FeedbackDeliveryRow>(sql, {| fid = feedbackId |})
            return rows |> Seq.toArray
        }

    let getLatestFeedbackId (userId: int64) =
        task {
            use conn = new NpgsqlConnection(fixture.DbConnectionString)
            //language=postgresql
            let sql = "SELECT id FROM user_feedback WHERE user_id = @uid ORDER BY id DESC LIMIT 1"
            return! conn.QuerySingleAsync<int64>(sql, {| uid = userId |})
        }

    /// Runs the full /feedback wizard for `user` and returns the new feedback row id.
    let sendFeedback (user: User) (text: string) =
        task {
            let! _ = fixture.SendUpdate(Tg.dmMessage("/feedback", user))
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage(text, user))
            return! getLatestFeedbackId user.Id
        }

    [<Fact>]
    let ``Feedback forwarding records a delivery row per admin`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 5001L, username = "reply_flow1", firstName = "RF1")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! feedbackId = sendFeedback user "Please add dark mode"

            let! deliveries = getFeedbackDeliveries feedbackId
            Assert.Equal(2, deliveries.Length)
            let chatIds = deliveries |> Array.map (fun d -> d.admin_chat_id) |> Array.sort
            Assert.Equal<int64[]>([| adminId; secondAdminId |], chatIds)
        }

    [<Fact>]
    let ``Admin reply delivers to the user and notifies the other admin`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 5002L, username = "reply_flow2", firstName = "RF2")
            do! fixture.SetChatMemberStatus(user.Id, "member")

            let! feedbackId = sendFeedback user "Bug in balances page"
            let! deliveries = getFeedbackDeliveries feedbackId
            let adminDelivery = deliveries |> Array.find (fun d -> d.admin_chat_id = adminId)
            let otherDelivery = deliveries |> Array.find (fun d -> d.admin_chat_id = secondAdminId)

            let admin = Tg.user(id = adminId, username = "reply_admin1", firstName = "Admin1")
            do! fixture.ClearFakeCalls()
            let! resp =
                fixture.SendUpdate(
                    Tg.dmMessage("/reply Спасибо, починим", admin, replyToMessageId = adminDelivery.admin_message_id))
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)

            let! msgCalls = fixture.GetFakeCalls("sendMessage")

            let userCall =
                msgCalls
                |> Array.tryPick (fun call ->
                    match parseCallBody call.Body with
                    | Some p when p.ChatId = Some user.Id && p.Text.IsSome && p.Text.Value.Contains("Спасибо, починим") ->
                        Some(p.Text.Value, getReplyToMessageId call.Body)
                    | _ -> None)
            Assert.True(userCall.IsSome, "Expected the user to receive the admin's reply")
            let userText, userReplyTo = userCall.Value
            Assert.Contains("Ответ авторов на твой фидбэк", userText)

            let! feedbackMsgId =
                fixture.QuerySingleOrDefault<int64>(
                    "SELECT telegram_message_id FROM user_feedback WHERE id = @id",
                    {| id = feedbackId |})
            Assert.Equal(Some(int feedbackMsgId), userReplyTo)

            Assert.True(findCallWithText msgCalls adminId "Отправлено.", "Expected admin confirmation")

            let otherCall =
                msgCalls
                |> Array.tryPick (fun call ->
                    match parseCallBody call.Body with
                    | Some p when p.ChatId = Some secondAdminId && p.Text.IsSome && p.Text.Value.Contains("Ответ на фидбэк отправлен") ->
                        Some(getReplyToMessageId call.Body)
                    | _ -> None)
            Assert.True(otherCall.IsSome, "Expected the other admin to be notified")
            Assert.Equal(Some(int otherDelivery.admin_message_id), otherCall.Value)

            let! replyRow =
                fixture.QuerySingleOrDefault<FeedbackReplyRow>(
                    "SELECT feedback_id, admin_id, reply_text, delivered FROM feedback_reply WHERE feedback_id = @fid",
                    {| fid = feedbackId |})
            Assert.NotNull(replyRow)
            Assert.Equal(adminId, replyRow.admin_id)
            Assert.True(replyRow.delivered)
        }

    [<Fact>]
    let ``Reply without a reply target gets only a hint`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 5003L, username = "reply_flow3", firstName = "RF3")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! _ = sendFeedback user "Some feedback"

            let admin = Tg.user(id = adminId, username = "reply_admin2", firstName = "Admin2")
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/reply hello", admin))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls adminId "Ответь этой командой", "Expected the hint")
            Assert.False(findCallWithText msgCalls user.Id "Ответ авторов", "User must not receive a DM")
        }

    [<Fact>]
    let ``Non-admin reply produces zero sendMessage calls`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 5004L, username = "reply_flow4", firstName = "RF4")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! feedbackId = sendFeedback user "Feedback from non-admin test"
            let! deliveries = getFeedbackDeliveries feedbackId
            let adminDelivery = deliveries |> Array.find (fun d -> d.admin_chat_id = adminId)

            let nonAdmin = Tg.user(id = 5005L, username = "not_an_admin", firstName = "NotAdmin")
            do! fixture.SetChatMemberStatus(nonAdmin.Id, "member")

            do! fixture.ClearFakeCalls()
            let! _ =
                fixture.SendUpdate(
                    Tg.dmMessage("/reply I am not an admin", nonAdmin, replyToMessageId = adminDelivery.admin_message_id))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.Equal(0, msgCalls.Length)
        }

    [<Fact>]
    let ``slash-r alias works`` () =
        task {
            do! fixture.ClearFakeCalls()
            let user = Tg.user(id = 5006L, username = "reply_flow5", firstName = "RF5")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! feedbackId = sendFeedback user "Feedback via alias test"
            let! deliveries = getFeedbackDeliveries feedbackId
            let adminDelivery = deliveries |> Array.find (fun d -> d.admin_chat_id = adminId)

            let admin = Tg.user(id = adminId, username = "reply_admin3", firstName = "Admin3")
            do! fixture.ClearFakeCalls()
            let! _ = fixture.SendUpdate(Tg.dmMessage("/r via alias", admin, replyToMessageId = adminDelivery.admin_message_id))

            let! msgCalls = fixture.GetFakeCalls("sendMessage")
            Assert.True(findCallWithText msgCalls user.Id "via alias", "Expected reply text delivered via /r alias")
        }
