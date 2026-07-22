namespace AlitaBot.Tests

open System
open System.Threading.Tasks
open BotTestInfra
open Funogram.Telegram.Types
open Xunit

open CommandsTestHelpers

/// `/song` (Gemini/Lyria music generation) — see BotService.handleSongCommand and
/// GeminiProvider.fs's GeminiMusicGen. The fake-test container ships no `ffmpeg`
/// (StretchTests.fs's `/say` doc comment notes the same), so `tryConvertToMp3` always
/// falls back to the raw scripted bytes — these tests assert on THAT path, same as
/// StretchTests' `/say` tests do for `tryConvertToOggOpus`.
type SongTests(fixture: AlitaTestContainers) =

    let botReplyRows (replyToMessageId: int64) =
        fixture.Query<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND is_bot = TRUE AND reply_to_message_id = @mid
ORDER BY message_id
""",
            {| cid = fixture.TargetChatId; mid = replyToMessageId |})

    let senderRow (chatId: int64) (messageId: int64) =
        fixture.QuerySingleOrDefault<MessageLogRow>(
            """
SELECT chat_id, message_id, user_id, username, display_name, is_bot, reply_to_message_id, text
FROM message_log WHERE chat_id = @cid AND message_id = @mid
""",
            {| cid = chatId; mid = messageId |})

    [<Fact>]
    let ``song generates audio, sends it, logs the bot reply, and records llm_usage kind=music`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetGeminiMusicScript [||]

            let user = Tg.user(id = 9401L, username = "song_alice", firstName = "Alice")
            let update = Tg.groupMessage("/song (рок-баллада) про баги в проде", user, fixture.TargetChatId)
            let msgId = update.Message.Value.MessageId

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! audioSends = fixture.GetFakeCalls("sendAudio")
            Assert.NotEmpty(audioSends |> Array.filter (isToChat fixture.TargetChatId))

            let! geminiCalls = fixture.GetGeminiCalls()
            Assert.Contains(geminiCalls, fun (c: FakeCall) -> c.Url.Contains "generateContent")

            let! cmdRow = senderRow fixture.TargetChatId msgId
            Assert.NotNull(box cmdRow)
            Assert.Equal("[song-cmd] (рок-баллада) про баги в проде", cmdRow.text)

            let! replyRows = botReplyRows msgId
            Assert.NotEmpty replyRows
            Assert.Contains(replyRows, fun (r: MessageLogRow) -> r.text.StartsWith "[song] " && r.text.Contains "про баги в проде")

            let! sawMusicUsage = awaitUsageRow fixture "music" fixture.TargetChatId
            Assert.True(sawMusicUsage, "expected an llm_usage row with kind='music' after /song")
        }

    [<Fact>]
    let ``song on cooldown refuses without calling Gemini again`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()
            do! fixture.SetGeminiMusicScript [||]

            let user = Tg.user(id = 9402L, username = "song_bob", firstName = "Bob")

            let! firstResp = fixture.SendUpdate(Tg.groupMessage("/song первая песня для истории", user, fixture.TargetChatId))
            firstResp.EnsureSuccessStatusCode() |> ignore

            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let! secondResp = fixture.SendUpdate(Tg.groupMessage("/song вторая песня сразу же", user, fixture.TargetChatId))
            secondResp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let toChat = sends |> Array.filter (isToChat fixture.TargetChatId)
            Assert.Contains(toChat, fun c -> (jsonString c "text").Contains "рано")

            let! geminiCalls = fixture.GetGeminiCalls()
            Assert.Empty geminiCalls

            let! audioSends = fixture.GetFakeCalls("sendAudio")
            Assert.Empty(audioSends |> Array.filter (isToChat fixture.TargetChatId))
        }

    [<Fact>]
    let ``song over SONG_MAX_CHARS refuses without calling Gemini`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9403L, username = "song_carol", firstName = "Carol")
            let longText = String.replicate 1001 "a"
            let update = Tg.groupMessage($"/song {longText}", user, fixture.TargetChatId)

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let toChat = sends |> Array.filter (isToChat fixture.TargetChatId)
            Assert.Contains(toChat, fun c -> (jsonString c "text").Contains "максимум")

            let! geminiCalls = fixture.GetGeminiCalls()
            Assert.Empty geminiCalls

            let! audioSends = fixture.GetFakeCalls("sendAudio")
            Assert.Empty(audioSends |> Array.filter (isToChat fixture.TargetChatId))
        }

    [<Fact>]
    let ``song with an empty prompt replies with a usage hint and never calls Gemini`` () =
        task {
            do! fixture.ClearFakeCalls()
            do! fixture.ClearAzureOcrCalls()

            let user = Tg.user(id = 9404L, username = "song_dave", firstName = "Dave")
            let update = Tg.groupMessage("/song", user, fixture.TargetChatId)

            let! resp = fixture.SendUpdate(update)
            resp.EnsureSuccessStatusCode() |> ignore

            let! sends = fixture.GetFakeCalls("sendMessage")
            let toChat = sends |> Array.filter (isToChat fixture.TargetChatId)
            Assert.Contains(toChat, fun c -> (jsonString c "text").Contains "/song")

            let! geminiCalls = fixture.GetGeminiCalls()
            Assert.Empty geminiCalls
        }

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(task {
                do! fixture.SetGeminiMusicScript [||]
            } :> Task)

        member _.DisposeAsync() =
            ValueTask(task {
                do! fixture.SetGeminiMusicScript [||]
            } :> Task)
