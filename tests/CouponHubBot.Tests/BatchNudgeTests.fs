namespace CouponHubBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Xunit
open FakeCallHelpers
open BatchTestHelpers

/// Nudge tests (in-memory throttles, ConcurrentDictionary in CouponFlowHandler):
///   - /add command nudge: append "просто прислать фото без команды /add" once
///     per UTC day per user
///   - Rapid-singles nudge: send "выделить несколько фото сразу и отправить
///     альбомом" on the 2nd single (non-album) photo within 5s, once per day
type BatchNudgeTests(fixture: OcrCouponHubTestContainers) =

    let setupBatchTest () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.TruncateCoupons()
            do! fixture.TruncateBatches()
            do! resetOcrFakes fixture
        }

    let goodFile = "10_50_2026-01-17_2026-01-26_2706688198845.jpg"

    [<Fact>]
    let ``/add nudge: first invocation appends the tip, second invocation does not`` () =
        task {
            do! setupBatchTest ()
            // Use a user that hasn't been nudged today (high unique id).
            let user = Tg.user(id = 7600L, username = "addnudge_first", firstName = "AddNudge")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            let! _ = fixture.Execute("DELETE FROM pending_add WHERE user_id=@u", {| u = user.Id |})

            // First /add — expect the nudge text in the reply.
            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            do! waitForSendMessageMatching fixture user.Id
                    (fun t -> t.Contains "просто прислать фото без команды") 3000

            // Cancel the wizard so a second /add is a fresh start.
            let! _ = fixture.Execute("DELETE FROM pending_add WHERE user_id=@u", {| u = user.Id |})
            do! fixture.ClearFakeCalls()

            // Second /add same UTC day — should NOT include the nudge text.
            let! _ = fixture.SendUpdate(Tg.dmMessage("/add", user))
            // Wait for the reply ("Пришли фото купона…") to land.
            do! waitForSendMessageMatching fixture user.Id (fun t -> t.Contains "Пришли фото купона") 3000

            let! calls = fixture.GetFakeCalls("sendMessage")
            let nudged =
                calls
                |> Array.exists (fun call ->
                    match parseCallBody call.Body with
                    | Some p when p.ChatId = Some user.Id ->
                        match p.Text with
                        | Some t -> t.Contains "просто прислать фото без команды"
                        | _ -> false
                    | _ -> false)
            Assert.False(nudged, "Second /add same day should not re-nudge")
        }

    [<Fact>]
    let ``Rapid-singles nudge: fires after 2nd single photo within 5s, not on 3rd same day`` () =
        task {
            do! setupBatchTest ()
            let user = Tg.user(id = 7601L, username = "rapid_singles", firstName = "Rapid")
            do! fixture.SetChatMemberStatus(user.Id, "member")
            do! fixture.SetTelegramFile("rs-1", readImageBytes goodFile)
            do! fixture.SetTelegramFile("rs-2", readImageBytes goodFile)
            do! fixture.SetTelegramFile("rs-3", readImageBytes goodFile)
            do! fixture.SetAzureOcrResponse(200, readAzureCacheJson goodFile)

            let deleteWizard () =
                fixture.Execute("DELETE FROM pending_add WHERE user_id=@u", {| u = user.Id |})

            // The rapid-singles nudge fires inside HandleAddWizardPhoto, which is
            // only invoked when there's no in-progress pending_add (or it's at
            // awaiting_photo). After the first photo the wizard is at
            // awaiting_discount_choice → the next photo would hit the "Сейчас идёт
            // добавление…" branch instead. We force-delete pending_add between
            // sends so each photo reaches HandleAddWizardPhoto and the nudge logic.
            let! _ = deleteWizard ()

            // 1st single (no MediaGroupId) — no previous timestamp, no nudge.
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "rs-1"))
            do! Task.Delay 200

            let! calls1 = fixture.GetFakeCalls("sendMessage")
            let nudged1 =
                calls1
                |> Array.exists (fun call ->
                    match parseCallBody call.Body with
                    | Some p when p.ChatId = Some user.Id ->
                        match p.Text with
                        | Some t -> t.Contains "выделить несколько фото"
                        | _ -> false
                    | _ -> false)
            Assert.False(nudged1, "1st single photo should not trigger album nudge")

            // Clear wizard so 2nd photo routes through HandleAddWizardPhoto again.
            let! _ = deleteWizard ()

            // 2nd single within 5s — SHOULD trigger the nudge.
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "rs-2"))
            do! waitForSendMessageMatching fixture user.Id
                    (fun t -> t.Contains "выделить несколько фото") 3000

            // 3rd single same day — should NOT re-nudge.
            do! fixture.ClearFakeCalls()
            let! _ = deleteWizard ()
            let! _ = fixture.SendUpdate(Tg.dmPhotoWithCaption("", user, fileId = "rs-3"))
            do! Task.Delay 500
            let! calls3 = fixture.GetFakeCalls("sendMessage")
            let nudged3 =
                calls3
                |> Array.exists (fun call ->
                    match parseCallBody call.Body with
                    | Some p when p.ChatId = Some user.Id ->
                        match p.Text with
                        | Some t -> t.Contains "выделить несколько фото"
                        | _ -> false
                    | _ -> false)
            Assert.False(nudged3, "3rd single photo same day should not re-nudge")
        }
