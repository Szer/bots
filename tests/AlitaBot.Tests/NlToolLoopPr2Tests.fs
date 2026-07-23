namespace AlitaBot.Tests

open System
open System.Text.Json
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

open CommandsTestHelpers

/// S10 PR2: the remaining natural-language tools (generate_song, speak_text, sql_query
/// [AdminOnly], and the read-only thin wrappers — ask_chat_history, summarize_chat,
/// show_dossier, roast_user, show_awards, show_quote, show_karma, switch_model,
/// show_usage). One test per tool: a scripted tool_calls round asserts the SAME core the
/// command handler uses actually ran (DB write / Telegram send / rendered text), and that
/// the tool's result is threaded back into the next round as a role=tool message — same
/// idioms as NlToolLoopTests.fs (PR1).
module NlToolLoopPr2TestHelpers =
    /// Strict JSON array of {title,user,evidence_quote} — the /awards LLM contract (same
    /// shape as SocialTests.fs's awardsJson).
    let awardsJson (awards: (string * string * string) list) =
        awards
        |> List.map (fun (title, user, quote) -> {| title = title; user = user; evidence_quote = quote |})
        |> JsonSerializer.Serialize

    /// Strict JSON object {author,quote,comment} — the /quote LLM contract.
    let quoteJson (author: string) (quote: string) (comment: string) =
        JsonSerializer.Serialize {| author = author; quote = quote; comment = comment |}

    /// A tiny buffer starting with the Ogg container magic ("OggS") — enough for
    /// MediaActions.isOggContainer's 4-byte check to pass, exercising the sendVoice fast
    /// path (never ffmpeg, which the fake-test container doesn't ship). Same helper as
    /// StretchTests.fs's oggBytesBase64.
    let oggBytesBase64 () =
        Convert.ToBase64String [| 0x4Fuy; 0x67uy; 0x67uy; 0x53uy; 1uy; 2uy; 3uy; 4uy; 5uy |]

open NlToolLoopPr2TestHelpers

type NlToolLoopPr2Tests(fixture: AlitaTestContainers) =

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    [<Fact>]
    let ``generate_song tool executes MediaActions.generateSong, sends audio titled with the composed caption, and records llm_usage kind=music`` () =
        task {
            do! fixture.SetGeminiMusicScript [||]
            let scriptedCaption = "держи трек"
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted
                           200
                           (NlToolLoopTestHelpers.toolCallsCompletionBody
                               [ "call_song", "generate_song", """{"lyrics_or_description":"тестовая песня для инструмента"}""" ])
                       LlmTestHelpers.scripted 200 (nonStreamCompletionBody scriptedCaption)
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "готово") |]

            let user = Tg.user(id = 9601L, username = "nl_songwriter", firstName = "Songwriter")
            let update = Tg.groupMessage("алита, сочини песню про тесты", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! audioSends = fixture.GetFakeCalls("sendAudio")
            let toChat = audioSends |> Array.filter (isToChat fixture.TargetChatId)
            Assert.Contains(toChat, fun c -> (jsonString c "title") = scriptedCaption)

            let! replies = botReplyRows msgId
            Assert.Contains(replies, fun (r: MessageLogRow) -> r.text = $"[song] {scriptedCaption}")

            let! sawMusicUsage = awaitUsageRow fixture "music" fixture.TargetChatId
            Assert.True(sawMusicUsage, "expected an llm_usage row with kind='music' after generate_song")

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(3, llmCalls.Length)
            Assert.True(
                NlToolLoopTestHelpers.requestHasToolResultFor llmCalls[2] "call_song",
                "expected round 3's request to include a role=tool message with tool_call_id=call_song")
        }

    [<Fact>]
    let ``speak_text tool executes MediaActions.speakText and sends a voice note logged with the exact spoken text`` () =
        task {
            do! fixture.SetAzureTtsScript [| LlmTestHelpers.scripted 200 (oggBytesBase64 ()) |]
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted
                           200
                           (NlToolLoopTestHelpers.toolCallsCompletionBody
                               [ "call_speak", "speak_text", """{"text":"привет из инструмента"}""" ])
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "сказано") |]

            let user = Tg.user(id = 9602L, username = "nl_speaker", firstName = "Speaker")
            let update = Tg.groupMessage("алита, скажи голосом привет из инструмента", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! voiceSends = fixture.GetFakeCalls("sendVoice")
            Assert.NotEmpty(voiceSends |> Array.filter (isToChat fixture.TargetChatId))

            let! replies = botReplyRows msgId
            Assert.Contains(replies, fun (r: MessageLogRow) -> r.text = "[voice] привет из инструмента")

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)
            Assert.True(NlToolLoopTestHelpers.requestHasToolResultFor llmCalls[1] "call_speak")
        }

    [<Fact>]
    let ``sql_query tool is never offered to a non-admin but IS offered to an admin`` () =
        task {
            do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "ладно") |]
            let nonAdmin = Tg.user(id = 9603L, username = "nl_noadmin", firstName = "NoAdmin")
            let! resp1 = fixture.SendUpdate(Tg.groupMessage("алита привет", nonAdmin, fixture.TargetChatId))
            resp1.EnsureSuccessStatusCode() |> ignore
            let! calls1 = fixture.GetAzureLlmCalls()
            Assert.DoesNotContain("sql_query", NlToolLoopTestHelpers.requestToolNames calls1[0])

            try
                do! fixture.SetBotSetting("ADMIN_USER_IDS", $"[{AlitaTestConfig.adminUserId}]")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript [| LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "ладно") |]
                let admin = Tg.user(id = AlitaTestConfig.adminUserId, username = "nl_admincheck", firstName = "Admin")
                let! resp2 = fixture.SendUpdate(Tg.groupMessage("алита привет", admin, fixture.TargetChatId))
                resp2.EnsureSuccessStatusCode() |> ignore
                let! calls2 = fixture.GetAzureLlmCalls()
                Assert.Contains("sql_query", NlToolLoopTestHelpers.requestToolNames calls2[calls2.Length - 1])
            finally
                fixture.SetBotSetting("ADMIN_USER_IDS", "[]") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``sql_query tool (real, admin) executes CommandCores.sqlCore and threads the result table back`` () =
        task {
            try
                do! fixture.SetBotSetting("ADMIN_USER_IDS", $"[{AlitaTestConfig.adminUserId}]")
                do! fixture.ReloadSettings()
                do! fixture.SetAzureLlmScript
                        [| LlmTestHelpers.scripted
                               200
                               (NlToolLoopTestHelpers.toolCallsCompletionBody
                                   [ "call_sql", "sql_query", """{"question":"тестовый маркер"}""" ])
                           LlmTestHelpers.scripted 200 (nonStreamCompletionBody (JsonSerializer.Serialize {| sql = "SELECT 'SQLQUERYMARKER' AS n" |}))
                           LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "вот результат") |]

                let admin = Tg.user(id = AlitaTestConfig.adminUserId, username = "nl_sqladmin", firstName = "Admin")
                let update = Tg.groupMessage("алита, узнай через базу тестовый маркер", admin, fixture.TargetChatId)
                let! resp = fixture.SendUpdate(update)
                resp.EnsureSuccessStatusCode() |> ignore

                let! llmCalls = fixture.GetAzureLlmCalls()
                Assert.Equal(3, llmCalls.Length)
                Assert.True(
                    requestMessagesContain llmCalls[2] "SQLQUERYMARKER",
                    "expected the rendered SQL result table threaded into round 3")
            finally
                fixture.SetBotSetting("ADMIN_USER_IDS", "[]") |> ignore
                fixture.ReloadSettings() |> ignore
        }

    [<Fact>]
    let ``ask_chat_history tool executes CommandCores.askCore and threads the no-match result back`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted
                           200
                           (NlToolLoopTestHelpers.toolCallsCompletionBody
                               [ "call_ask", "ask_chat_history", """{"question":"когда был запуск шаттла Индевор в 1994"}""" ])
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "не нашла, извини") |]

            let user = Tg.user(id = 9604L, username = "nl_asker", firstName = "Asker")
            let update = Tg.groupMessage("алита, поищи в истории про шаттл Индевор", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)
            Assert.True(
                requestMessagesContain llmCalls[1] "Ничего подходящего",
                "expected the no-match result threaded into round 2")
        }

    [<Fact>]
    let ``summarize_chat tool executes CommandCores.summaryCore and threads the summary back`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_sum", "summarize_chat", """{"count":10}""" ])
                       LlmTestHelpers.scripted 200 (nonStreamCompletionBody "сводка: обсуждали разное")
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "вот сводка") |]

            let user = Tg.user(id = 9605L, username = "nl_summarizer", firstName = "Summarizer")
            let update = Tg.groupMessage("алита, перескажи, что тут обсуждали", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(3, llmCalls.Length)
            Assert.True(
                requestMessagesContain llmCalls[2] "сводка: обсуждали разное",
                "expected the summary threaded into round 3")
        }

    [<Fact>]
    let ``show_dossier tool executes CommandCores.dossierCore and threads the no-dossier-yet result back`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_dos", "show_dossier", "{}" ])
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "пока пусто") |]

            let user = Tg.user(id = 9606L, username = "nl_freshdossier", firstName = "Fresh")
            let update = Tg.groupMessage("алита, что ты обо мне знаешь", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)
            Assert.True(
                requestMessagesContain llmCalls[1] "пусто, я тебя ещё не изучила",
                "expected NoDossierText threaded into round 2")
        }

    [<Fact>]
    let ``roast_user tool executes CommandCores.roastCore against a self-resolved target and threads the roast back`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_roast", "roast_user", "{}" ])
                       LlmTestHelpers.scripted 200 (nonStreamCompletionBody "текст прожарки без проблем")
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "вот, держи") |]

            let user = Tg.user(id = 9607L, username = "nl_roastfresh", firstName = "RoastFresh")
            let update = Tg.groupMessage("алита, прожарь меня", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(3, llmCalls.Length)
            Assert.True(
                requestMessagesContain llmCalls[2] "текст прожарки без проблем",
                "expected the roast threaded into round 3")
        }

    [<Fact>]
    let ``show_awards tool executes CommandCores.awardsCore, writes karma, and threads the announcement back`` () =
        task {
            let seedUser = Tg.user(id = 9608L, username = "nl_awardseed", firstName = "Seed")
            let! seedResp = fixture.SendUpdate(Tg.groupMessage("сообщение для истории show_awards", seedUser, fixture.TargetChatId))
            seedResp.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_awards", "show_awards", "{}" ])
                       LlmTestHelpers.scripted
                           200
                           (nonStreamCompletionBody (awardsJson [ "Душнила недели", "@nl_awardseed", "цитата для теста инструмента" ]))
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "вот награды") |]

            let user = Tg.user(id = 9609L, username = "nl_awarder", firstName = "Awarder")
            let update = Tg.groupMessage("алита, раздай награды недели", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(3, llmCalls.Length)
            Assert.True(requestMessagesContain llmCalls[2] "Душнила недели", "expected the rendered awards threaded into round 3")
        }

    [<Fact>]
    let ``show_quote tool executes CommandCores.quoteCore and threads the quote of the day back`` () =
        task {
            let seedUser = Tg.user(id = 9610L, username = "nl_quoteseed", firstName = "Seed")
            let! seedResp = fixture.SendUpdate(Tg.groupMessage("сообщение для истории show_quote", seedUser, fixture.TargetChatId))
            seedResp.EnsureSuccessStatusCode() |> ignore

            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_quote", "show_quote", "{}" ])
                       LlmTestHelpers.scripted 200 (nonStreamCompletionBody (quoteJson "TestUser" "тестовая цитата для инструмента" "воистину"))
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "вот цитата") |]

            let user = Tg.user(id = 9611L, username = "nl_quoter", firstName = "Quoter")
            let update = Tg.groupMessage("алита, какая сегодня цитата дня", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(3, llmCalls.Length)
            Assert.True(
                requestMessagesContain llmCalls[2] "тестовая цитата для инструмента",
                "expected the rendered quote threaded into round 3")
        }

    [<Fact>]
    let ``show_karma tool executes CommandCores.karmaCore and threads the no-karma-yet result back`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_karma", "show_karma", "{}" ])
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "пока нечем хвастаться") |]

            let user = Tg.user(id = 9612L, username = "nl_karmafresh", firstName = "KarmaFresh")
            let update = Tg.groupMessage("алита, сколько у меня кармы", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)
            Assert.True(requestMessagesContain llmCalls[1] "пока без наград", "expected NoKarmaText threaded into round 2")
        }

    [<Fact>]
    let ``switch_model tool executes CommandCores.modelCore and threads the current-model text back`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_model", "switch_model", "{}" ])
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "вот что у меня под капотом") |]

            let user = Tg.user(id = 9613L, username = "nl_modelasker", firstName = "ModelAsker")
            let update = Tg.groupMessage("алита, какая ты модель", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)
            Assert.True(requestMessagesContain llmCalls[1] "Текущая модель", "expected the model-catalog text threaded into round 2")
        }

    [<Fact>]
    let ``show_usage tool executes CommandCores.usageCore and threads the usage stats text back`` () =
        task {
            do! fixture.SetAzureLlmScript
                    [| LlmTestHelpers.scripted 200 (NlToolLoopTestHelpers.toolCallsCompletionBody [ "call_usage", "show_usage", "{}" ])
                       LlmTestHelpers.scripted 200 (LlmTestHelpers.completionBody "вот статистика") |]

            let user = Tg.user(id = 9614L, username = "nl_usageasker", firstName = "UsageAsker")
            let update = Tg.groupMessage("алита, сколько потратили на тебя", user, fixture.TargetChatId)
            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! llmCalls = fixture.GetAzureLlmCalls()
            Assert.Equal(2, llmCalls.Length)
            Assert.True(requestMessagesContain llmCalls[1] "Usage", "expected the usage stats text threaded into round 2")
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.ClearFakeCalls()
                do! fixture.ClearAzureOcrCalls()
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureImageScript [||]
                do! fixture.SetGeminiMusicScript [||]
                do! fixture.SetAzureTtsScript [||]
                do! fixture.SetAzureLlmStreamOptions(0, 0, 0)
                do! fixture.SetBotSetting("RESPONDER_MODE", "llm")
                do! fixture.SetBotSetting("NL_TOOLS_ENABLED", "true")
                do! fixture.SetBotSetting("ADMIN_USER_IDS", "[]")
                do! fixture.ReloadSettings()
            } :> Task)

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetAzureLlmScript [||]
                do! fixture.SetAzureImageScript [||]
                do! fixture.SetGeminiMusicScript [||]
                do! fixture.SetAzureTtsScript [||]
                do! fixture.SetAzureLlmStreamOptions(0, 0, 0)
                do! fixture.SetBotSetting("RESPONDER_MODE", "echo")
                do! fixture.SetBotSetting("NL_TOOLS_ENABLED", "false")
                do! fixture.SetBotSetting("ADMIN_USER_IDS", "[]")
                do! fixture.ReloadSettings()
            } :> Task)
