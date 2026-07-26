namespace CouponHubBot.RealTests

open System
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Coverage: the `/feedback` flow, admin-only `/debug` and `/undo`, and the bot's short
/// command aliases (CommandHandler.fs's Dispatch, :390-499).
///
/// Admin rights: FEEDBACK_ADMINS is seeded with the logged-in MTProto test user's own id
/// at fixture startup (RealAssemblyFixture.fs's InitializeAsync -> DbSeed.applyAsync,
/// RealAssemblyFixture.fs:157-165) — there is no COUPON_CI_FEEDBACK_ADMIN_ID and none
/// should be added. This also means every `/feedback` forward
/// (BotService.fs:129-132, `ForwardMessage.Make(adminId, msg.Chat.Id, msg.MessageId)`
/// with `adminId` = this account's own id) lands back in THIS SAME private chat
/// (fx.BotChatId) — that's what makes the forward observable with only one Telegram
/// account.
///
/// GitHub issue creation (BotService.fs:135-145) is gated on GitHubService.IsConfigured
/// (GitHubService.fs:46-48), which requires a non-empty GITHUB_TOKEN — deliberately
/// absent from the CI test pod (contract). No test here adds a token or asserts on issue
/// creation; `/feedback` coverage below stops at the DB write + admin forward +
/// user-facing "Спасибо!" reply.
type AdminAndFeedbackRealTests(fx: RealAssemblyFixture) =

    /// `user_feedback` has no DbSeed.fs helper (adding one is out of this file's scope —
    /// contract: "write it as a private function inside your own file" when a helper is
    /// missing). Scoped by user_id + most recent row rather than telegram_message_id:
    /// this suite drives exactly one MTProto account/user_id and runs serialized, so "our
    /// own latest feedback row" is unambiguous without needing the MTProto message id <->
    /// Bot API message id correspondence.
    let getLatestFeedbackTextAsync (userId: int64) : Task<string> =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            do! conn.OpenAsync()
            return!
                conn.QuerySingleAsync<string>(
                    "SELECT feedback_text FROM user_feedback WHERE user_id = @user_id ORDER BY id DESC LIMIT 1",
                    {| user_id = userId |})
        }

    /// Covers `/feedback` (CommandHandler.fs:434-436 -> handleFeedback :370-379): arms
    /// the pending flag, the next non-command message is saved (DbService.SaveUserFeedback),
    /// forwarded to every FeedbackAdminIds admin (here: this account itself), and answered
    /// with the thank-you text.
    [<Fact>]
    member _.``feedback happy path: DM saved, forwarded to admin (self), thanked``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            // Unique marker PER ATTEMPT — regenerated because this whole thunk re-runs on
            // TestRetry's one retry, so a retried attempt's forward-search can never match
            // attempt 1's already-forwarded copy (contract: "unique marker per attempt").
            let marker = $"real-test feedback marker {Guid.NewGuid():N}"

            let! armSentId = fx.UserClient.SendText(fx.BotChatId, "/feedback")
            let! _armed = fx.UserClient.AwaitTextContaining(fx.BotChatId, armSentId, "Следующее твоё сообщение", TimeSpan.FromSeconds 60.)

            let! markerSentId = fx.UserClient.SendText(fx.BotChatId, marker)

            let! thanks = fx.UserClient.AwaitTextContaining(fx.BotChatId, markerSentId, "Спасибо! Сообщение отправлено авторам.", TimeSpan.FromSeconds 60.)
            Assert.Contains("Спасибо!", thanks.message)

            // The admin forward (BotService.fs:129-132) is a SEPARATE message from the
            // bot carrying the original marker text verbatim — distinguished from our own
            // outgoing `marker` message by afterMsgId gating (m.id > markerSentId, and our
            // own send's id is markerSentId itself, never greater than it).
            let! forwarded = fx.UserClient.AwaitTextContaining(fx.BotChatId, markerSentId, marker, TimeSpan.FromSeconds 60.)
            Assert.Contains(marker, forwarded.message)

            let! savedText = getLatestFeedbackTextAsync fx.UserClient.Me.id
            Assert.Equal(marker, savedText)
        })

    /// Covers `/debug <id>` (CommandHandler.fs:480-484 -> handleDebug :40-45): admin-only,
    /// dumps the coupon's full event-history table (BotHelpers.formatEventHistoryTable,
    /// columns date/user/event_type — no coupon id column). Sets up history by adding then
    /// taking the coupon, then asserts both event types show up in the dump.
    [<Fact>]
    member _.``debug shows the coupon's event history``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "10_50_01-14_01-23_2706658654210.jpg" "10" "50" None
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! debugSentId = fx.UserClient.SendText(fx.BotChatId, $"/debug {couponId}")
            // sendCouponHistory (CommandHandler.fs:26-38) renders header=None for /debug, so
            // the reply is just the <pre> table with no coupon id printed anywhere in it.
            // "event_type" is the table's own column header (BotHelpers.fs:169) — an English
            // DB-column name that never collides with Russian UI text — so it's a safe marker
            // that this reply IS the history dump, not some other message racing in.
            let! history = fx.UserClient.AwaitTextContaining(fx.BotChatId, debugSentId, "event_type", TimeSpan.FromSeconds 60.)
            Assert.Contains("added", history.message)
            Assert.Contains("taken", history.message)
        })

    /// Covers `/undo <id>` (CommandHandler.fs:485-489 -> handleUndo :49-77). Sets up a
    /// coupon whose last live event is well-defined (add then take), so UndoLastEvent
    /// (DbService.fs:1010-1117) rewinds the "taken" event specifically
    /// (DbService.fs:1078-1081: status 'taken' -> 'available', taken_by/taken_at cleared —
    /// "back to the common pool", no prior holder restored). Asserts the confirmation text
    /// and the DB rollback via DbSeed.getCouponStatusAsync.
    [<Fact>]
    member _.``undo rewinds the last live event (taken -> available)``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            let! couponId = RealTestHelpers.addCouponViaCaptionAsync fx "10_50_01-19_01-28_2706613152454.jpg" "10" "50" None
            let! takeSentId = fx.UserClient.SendText(fx.BotChatId, $"/take {couponId}")
            let! _taken = fx.UserClient.AwaitPhotoCaptionContaining(fx.BotChatId, takeSentId, "теперь твой", TimeSpan.FromSeconds 60.)

            let! undoSentId = fx.UserClient.SendText(fx.BotChatId, $"/undo {couponId}")
            // handleUndo's header (CommandHandler.fs:71-74) renders as
            // "Откат купона ID:<id>: taken → available.\n...", which the coupon id makes
            // unique per attempt/coupon.
            let! undone = fx.UserClient.AwaitTextContaining(fx.BotChatId, undoSentId, $"Откат купона ID:{couponId}", TimeSpan.FromSeconds 60.)
            Assert.Contains("taken", undone.message)
            Assert.Contains("available", undone.message)

            let! status = DbSeed.getCouponStatusAsync fx.DbConnectionString couponId
            Assert.Equal("available", status.status)
            Assert.False(status.taken_by.HasValue, "Expected taken_by to be cleared by the undo")
        })

    /// Covers the short aliases confirmed against CommandHandler.fs's Dispatch
    /// (:390-499): /l, /coupons, bare /take (-> handleCoupons, :404-415); /m (-> handleMy,
    /// :416-421); /ad (-> handleAdded, :422-427); /s (-> handleStats, :428-433); /a (->
    /// HandleAddWizardStart, :440-445); /f (-> handleFeedback, :434-439). No further
    /// aliases exist in Dispatch beyond these.
    ///
    /// Kept cheap: one command per alias, tight per-command timeout, state-independent
    /// markers wherever the canonical reply has one (so this test doesn't depend on what
    /// earlier tests/runs left behind), and explicit, deliberate cleanup of both stateful
    /// aliases (/a's add-wizard row, /f's pending-feedback flag) so this test can never
    /// corrupt a later test class in the same serial run (contract, "Isolation model").
    [<Fact>]
    member _.``short aliases behave as their canonical commands``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            try
                // /l, /coupons, bare /take all alias handleCoupons (CommandHandler.fs:404-415).
                // "Всего доступно купонов:" (CommandHandler.fs:93) is sent on both the
                // empty and non-empty branches, so it's a state-independent marker.
                for cmd in [ "/l"; "/coupons"; "/take" ] do
                    let! sentId = fx.UserClient.SendText(fx.BotChatId, cmd)
                    let! reply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sentId, "Всего доступно купонов:", TimeSpan.FromSeconds 30.)
                    Assert.Contains("Всего доступно купонов:", reply.message)

                // /m -> handleMy (CommandHandler.fs:419-421). "Мои купоны" is the taken-
                // coupons section's own fixed leading text (CommandHandler.fs:210,221),
                // present whether or not this account currently holds any coupon.
                let! mSentId = fx.UserClient.SendText(fx.BotChatId, "/m")
                let! mReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, mSentId, "Мои купоны", TimeSpan.FromSeconds 30.)
                Assert.Contains("Мои купоны", mReply.message)

                // /ad -> handleAdded (CommandHandler.fs:425-427). Unlike /my, handleAdded's
                // empty and non-empty replies share no common substring
                // ("У тебя нет активных добавленных купонов." vs "Мои добавленные
                // купоны:") — add a coupon first to force the non-empty branch
                // deterministically instead of depending on leftover state.
                let! addedCouponId = RealTestHelpers.addCouponViaCaptionAsync fx "10_50_01-21_01-30_2706616470579.jpg" "10" "50" None
                let! adSentId = fx.UserClient.SendText(fx.BotChatId, "/ad")
                let! adReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, adSentId, "Мои добавленные купоны:", TimeSpan.FromSeconds 30.)
                Assert.Contains($"ID:{addedCouponId}", adReply.message)

                // /s -> handleStats (CommandHandler.fs:431-433). "Статистика:" is the
                // reply's fixed leading text (CommandHandler.fs:162-163).
                let! sSentId = fx.UserClient.SendText(fx.BotChatId, "/s")
                let! sReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, sSentId, "Статистика:", TimeSpan.FromSeconds 30.)
                Assert.Contains("Статистика:", sReply.message)

                // /a -> HandleAddWizardStart (CommandHandler.fs:443-445), which arms
                // stage='awaiting_photo' in `pending_add`. Cleared explicitly right away
                // (contract: "clear it with DbSeed.deletePendingAddFlowAsync, or cancel the
                // flow") rather than relying on the /f command right after to also clear it
                // as a side effect (it would — BotService.fs:92-94, "any command except
                // /add cancels add wizard" — but being explicit here doesn't depend on
                // alias-ordering staying the same later).
                let! aSentId = fx.UserClient.SendText(fx.BotChatId, "/a")
                let! aReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, aSentId, "Пришли фото купона", TimeSpan.FromSeconds 30.)
                Assert.Contains("Пришли фото купона", aReply.message)
                do! DbSeed.deletePendingAddFlowAsync fx.DbConnectionString fx.UserClient.Me.id

                // /f -> handleFeedback (CommandHandler.fs:437-439), which arms
                // SetPendingFeedback. Full /feedback coverage (forward, DB write,
                // thank-you) lives in its own test above; here we only confirm the alias
                // reaches handleFeedback — the `finally` below disarms it.
                let! fSentId = fx.UserClient.SendText(fx.BotChatId, "/f")
                let! fReply = fx.UserClient.AwaitTextContaining(fx.BotChatId, fSentId, "Следующее твоё сообщение", TimeSpan.FromSeconds 30.)
                Assert.Contains("Следующее твоё сообщение", fReply.message)
            finally
                // Runs even if an assertion above threw. Both cleanups are idempotent
                // no-ops if their respective state was already cleared (deletePendingAddFlowAsync
                // is a plain DELETE; any command unconditionally clears pending feedback —
                // BotService.fs:89-91), so calling both unconditionally here is safe. Waits
                // for the disarming /help's own reply (rather than firing-and-forgetting the
                // send) so the flag is guaranteed cleared server-side before this test method
                // returns and a later test class can send its own first message.
                DbSeed.deletePendingAddFlowAsync fx.DbConnectionString fx.UserClient.Me.id
                |> fun t -> t.GetAwaiter().GetResult()

                let helpSentId =
                    fx.UserClient.SendText(fx.BotChatId, "/help")
                    |> fun t -> t.GetAwaiter().GetResult()

                fx.UserClient.AwaitTextContaining(fx.BotChatId, helpSentId, "Команды", TimeSpan.FromSeconds 30.)
                |> fun t -> t.GetAwaiter().GetResult()
                |> ignore
        })
