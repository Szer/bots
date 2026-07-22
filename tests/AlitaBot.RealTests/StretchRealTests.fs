namespace AlitaBot.RealTests

open System
open System.Net.Http
open System.Text.RegularExpressions
open System.Threading.Tasks
open Dapper
open Npgsql
open Xunit

/// Slice 9 (stretch) real-Telegram tests: `/say` voice replies (real alita-tts), admin-
/// gated `/sql` analytics (real Azure AI Foundry generating the SQL), and the LLM-
/// responder cost footer. Same idioms as PersonaRealTests.fs/CommandRealTests.fs.
type StretchRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    let requireLlmMode () =
        if env.ResponderMode <> "llm" then
            Assert.Skip "RESPONDER_MODE=llm required — run `RESPONDER_MODE=llm make real-test`"

    let queryOne (sql: string) (param: obj) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            let! rows = conn.QueryAsync<LogRow>(sql, param)
            return rows |> Seq.tryHead
        }

    /// Same shape as PersonaRealTests'/ProactiveRealTests' own copy — upserts a
    /// `bot_setting` row directly, mirroring the fake suite's `fixture.SetBotSetting`.
    let setBotSetting (key: string) (value: string) =
        task {
            use conn = new NpgsqlConnection(fx.DbConnectionString)
            do! conn.OpenAsync()
            //language=postgresql
            let sql =
                """
INSERT INTO bot_setting(key, value, type, feature_group)
VALUES(@key, @value, 'FREE_FORM', 'RUNTIME')
ON CONFLICT (key) DO UPDATE SET value = @value
"""
            let! _ = conn.ExecuteAsync(sql, {| key = key; value = value |})
            ()
        }

    let reloadSettings () =
        task {
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 10.)
            http.DefaultRequestHeaders.Add("X-Telegram-Bot-Api-Secret-Token", env.WebhookSecret)
            let! resp = http.PostAsync(env.ReloadSettingsUrl, null)
            resp.EnsureSuccessStatusCode() |> ignore
        }

    /// Polls for the most recent `[<prefix>%` command row logged for this chat — same
    /// MTProto-vs-Bot-API id-domain reasoning as CommandRealTests.fs's awaitCommandRow.
    let awaitCommandRow (textPrefix: string) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = false AND text LIKE @prefix
ORDER BY message_id DESC LIMIT 1;
"""
                        {| chat_id = env.TestChatId; prefix = textPrefix + "%" |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    let awaitBotReplyRow (userMessageId: int64) =
        task {
            let deadline = DateTime.UtcNow + Timeouts.dbSettle
            let mutable found = None

            while found.IsNone && DateTime.UtcNow < deadline do
                let! row =
                    queryOne
                        """
SELECT message_id, user_id, is_bot, reply_to_message_id, text
FROM message_log
WHERE chat_id = @chat_id AND is_bot = true AND reply_to_message_id = @rid
ORDER BY message_id LIMIT 1;
"""
                        {| chat_id = env.TestChatId; rid = userMessageId |}

                found <- row
                if found.IsNone then do! Task.Delay 500

            return found
        }

    // ── /say ─────────────────────────────────────────────────────────────────

    [<Fact>]
    member _.``real /say привет arrives as a genuine voice note``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()

            if String.IsNullOrWhiteSpace env.TtsDeployment then
                Assert.Skip "ALITA_TTS_DEPLOYMENT missing in ~/.alita-test/env"

            // A longer phrase than a bare "привет" — a single short word's TTS clip can
            // legitimately round to 0 whole seconds (Telegram's voice-note duration is an
            // integer), which would make the fuzzy duration check below meaningless.
            let! msgId = fx.UserClient.SendText(env.TestChatId, "/say привет, это тестовое голосовое сообщение")
            let! duration, byteSize = fx.UserClient.AwaitVoiceReplyTo(env.TestChatId, msgId, VoiceRealTimeouts.transcriptionReply)

            // Fuzzy on purpose (plan §6) — a real TTS call's exact duration/size vary by
            // voice/model; what matters is that SOMETHING playable came back.
            Assert.True(duration > 0, $"expected a positive voice-note duration, got {duration}")
            Assert.True(byteSize > 0L, $"expected non-empty voice-note bytes, got {byteSize}")

            match! awaitCommandRow "[say-cmd]" with
            | None -> Assert.Fail "no '[say-cmd] ...' row landed in message_log"
            | Some cmdRow ->
                match! awaitBotReplyRow cmdRow.message_id with
                | None ->
                    Assert.Fail
                        $"no message_log row replying (reply_to_message_id={cmdRow.message_id}) to the /say command"
                | Some botRow ->
                    Assert.True botRow.is_bot
                    Assert.StartsWith("[voice]", botRow.text)
        })

    // ── /sql ─────────────────────────────────────────────────────────────────

    [<Fact>]
    member _.``real /sql as an admin answers a natural-language question with a number``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()

            if String.IsNullOrWhiteSpace env.LlmDeployment then
                Assert.Skip "ALITA_LLM_DEPLOYMENT missing in ~/.alita-test/env"

            try
                do! setBotSetting "ADMIN_USER_IDS" $"[{fx.UserClient.Me.id}]"
                do! reloadSettings ()

                let question = "сколько сообщений в этом чате за сегодня?"
                let! msgId = fx.UserClient.SendText(env.TestChatId, $"/sql {question}")
                let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)

                Assert.False(String.IsNullOrWhiteSpace reply.message)
                Assert.True(
                    Regex.IsMatch(reply.message, @"\d"),
                    $"expected the /sql answer to contain a number: {reply.message}")

                printfn "[real /sql] question: %s" question
                printfn "[real /sql] reply: %s" reply.message
            finally
                setBotSetting "ADMIN_USER_IDS" "[]" |> ignore
                reloadSettings () |> ignore
        })

    // ── Cost footer ──────────────────────────────────────────────────────────

    [<Fact>]
    member _.``real cost footer appears on an LLM reply when enabled``() =
        TestRetry.withTimeoutRetry (fun () -> task {
            fx.SkipUnlessUserClient()
            requireLlmMode ()

            try
                do! setBotSetting "COST_FOOTER_ENABLED" "true"
                do! reloadSettings ()

                let marker = Guid.NewGuid().ToString "N"
                let! msgId = fx.UserClient.SendText(env.TestChatId, $"алита, привет! маркер {marker}")
                let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, Timeouts.reply)
                // The cost footer lands via an EXTRA edit AFTER the streaming renderer's
                // own final edit (ResponderService.maybeAppendCostFooter) — read the
                // SETTLED text, not the first partial send AwaitReplyTo itself observed
                // (edits arrive as UpdateEditMessage, invisible to AwaitReplyTo's snapshot
                // of new messages — same reasoning as PersonaRealTests' MDV2 real-test).
                let! finalText = fx.UserClient.AwaitEditsSettled(env.TestChatId, reply.id, Timeouts.editQuiet)

                Assert.False(String.IsNullOrWhiteSpace finalText)
                Assert.True(
                    Regex.IsMatch(finalText, @"⛽ \$\d+\.\d{4}"),
                    $"expected a '⛽ $X.XXXX' cost footer in: {finalText}")
            finally
                setBotSetting "COST_FOOTER_ENABLED" "false" |> ignore
                reloadSettings () |> ignore
        })
