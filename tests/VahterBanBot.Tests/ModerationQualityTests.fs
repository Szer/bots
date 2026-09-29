module VahterBanBot.Tests.ModerationQualityTests

open System
open System.Threading
open Dapper
open Npgsql
open VahterBanBot
open VahterBanBot.Types
open VahterBanBot.Tests.ContainerTestBase
open Xunit

[<CLIMutable>]
type QualityRow =
    { cohort: string; system: string; llm_model: string | null
      tp: int64; tn: int64; fp: int64; fn: int64; abstained: int64; unresolved: int64; excluded: int64 }

type ModerationQualityTests(fixture: MlDisabledVahterTestContainers) =
    let day = DateTime(2001, 2, 3, 0, 0, 0, DateTimeKind.Utc)
    let chat = -900010L
    let job = ModerationQualityHistory(fixture.DbConnectionString, TimeProvider.System)
    let add id score verdict model = task {
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:{id}", 1,
            MessageReceived {| chatId = chat; messageId = id; userId = id; text = Some "example"; rawMessage = "{}" |}, day.AddMinutes 1.0)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:{id}", 1,
            MlScoredMessage {| chatId = chat; messageId = id; score = score; isSpam = score > 0.0 |}, day.AddMinutes 2.0)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:{id}", 2,
            LlmClassified {| chatId = chat; messageId = id; verdict = verdict; reason = None; modelName = model
                             promptTokens = 1; completionTokens = 1; latencyMs = 1; promptHash = None |}, day.AddMinutes 3.0)
        if verdict = "SPAM" then
            do! fixture.InsertRawEvent<ModerationEvent>($"moderation:{chat}:{id}", 1,
                BotAutoDeleted {| chatId = chat; messageId = id; userId = id
                                  reason = LlmSpam {| score = score; modelName = defaultArg model "unknown"; reason = None; cacheScope = None |} |}, day.AddMinutes 4.0)
    }
    let rows () = task {
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let! result = conn.QueryAsync<QualityRow>("SELECT * FROM moderation_quality_daily WHERE day = @day", {| day = day |})
        return Seq.toList result
    }

    [<Fact>]
    member _.``Historical counts compare identical messages and replace corrected days`` () = task {
        do! add 91001L 0.7 "SPAM" (Some "sol")
        do! add 91002L -0.2 "SPAM" (Some "sol")
        do! add 91003L 0.0 "NOT_SPAM" (Some "sol")
        do! add 91004L 0.4 "NOT_SPAM" (Some "sol")
        do! add 91005L 0.5 "SKIP" (Some "sol")
        do! add 91006L 0.7 "SPAM" (Some "mini")
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:91006", 2,
            MessageMarkedHam {| chatId = chat; messageId = 91006L; text = "example"; markedBy = Some 1L |}, day.AddDays 2.0)
        let! completed = job.Run(Some day, CancellationToken.None)
        Assert.Equal(1, completed)
        let! first = rows ()
        let row cohort system model = first |> List.find (fun r -> r.cohort = cohort && r.system = system && r.llm_model = model)
        let ml = row "llm_triaged" "ml" "sol"
        Assert.Equal((1L, 1L, 1L, 1L, 1L), (ml.tp, ml.tn, ml.fp, ml.fn, ml.abstained))
        let llm = row "llm_triaged" "llm" "sol"
        Assert.Equal((2L, 2L, 0L, 0L, 1L), (llm.tp, llm.tn, llm.fp, llm.fn, llm.abstained))
        Assert.Equal(1L, (row "llm_triaged" "llm" "mini").fp)
        let pipeline = row "all_scored" "pipeline" null
        Assert.Equal((2L, 2L, 1L, 0L, 1L), (pipeline.tp, pipeline.tn, pipeline.fp, pipeline.fn, pipeline.unresolved))
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:91006", 3,
            MessageMarkedSpam {| chatId = chat; messageId = 91006L; markedBy = Some 1L |}, day.AddDays 20.0)
        let! _ = job.Run(Some day, CancellationToken.None)
        let! second = rows ()
        let mini = second |> List.find (fun r -> r.cohort = "llm_triaged" && r.system = "llm" && r.llm_model = "mini")
        Assert.Equal((1L, 0L), (mini.tp, mini.fp))
        let! _ = job.Run(Some day, CancellationToken.None)
        let! third = rows ()
        Assert.Equal<QualityRow list>(List.sortBy (fun r -> r.cohort, r.system, r.llm_model) second,
                                     List.sortBy (fun r -> r.cohort, r.system, r.llm_model) third)
    }

    [<Fact>]
    member _.``Unban corrects its ban target and empty days are persisted`` () = task {
        let targetDay = day.AddDays 1.0
        let id = 92001L
        do! fixture.InsertRawEvent<MessageEvent>($"message:{chat}:{id}", 1,
            MessageReceived {| chatId = chat; messageId = id; userId = id; text = Some "example"; rawMessage = "{}" |}, targetDay)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:{id}", 1,
            MlScoredMessage {| chatId = chat; messageId = id; score = 3.0; isSpam = true |}, targetDay.AddMinutes 1.0)
        do! fixture.InsertRawEvent<ModerationEvent>($"moderation:{chat}:{id}", 1,
            BotAutoDeleted {| chatId = chat; messageId = id; userId = id; reason = MlSpam {| score = 3.0 |} |}, targetDay.AddMinutes 2.0)
        do! fixture.InsertRawEvent<UserEvent>($"user:{id}", 1,
            UserBanned {| userId = id; bannedBy = None; actor = Some Actor.ML; chatId = Some chat; messageId = Some id
                          messageText = None; bannedAt = targetDay.AddMinutes 2.0 |}, targetDay.AddMinutes 2.0)
        do! fixture.InsertRawEvent<UserEvent>($"user:{id}", 2,
            UserUnbanned {| userId = id; unbannedBy = Some 1L; actor = Some (Actor.User {| userId = 1L; username = None |}) |}, targetDay.AddDays 3.0)
        let! _ = job.Run(Some targetDay, CancellationToken.None)
        use conn = new NpgsqlConnection(fixture.DbConnectionString)
        let! fp = conn.QuerySingleAsync<int64>("SELECT fp FROM moderation_quality_daily WHERE day = @day AND system = 'pipeline'", {| day = targetDay |})
        Assert.Equal(1L, fp)
        let! _ = job.Run(Some (day.AddDays 2.0), CancellationToken.None)
        let! n = conn.QuerySingleAsync<int64>("SELECT count(*) FROM moderation_quality_daily WHERE day = @day AND tp + tn + fp + fn = 0", {| day = day.AddDays 2.0 |})
        Assert.Equal(2L, n)
    }

    [<Fact>]
    member _.``Unsettled dates are rejected`` () = task {
        let! _ = Assert.ThrowsAsync<ArgumentException>(fun () -> job.Run(Some DateTime.UtcNow.Date, CancellationToken.None))
        ()
    }

    [<Fact>]
    member _.``Backfill resumes missing UTC days and respects the maturity boundary`` () = task {
        let start = DateTime(1999, 2, 1, 0, 0, 0, DateTimeKind.Utc)
        let clock = { new TimeProvider() with override _.GetUtcNow() = DateTimeOffset(start.AddDays 18.0) }
        let history = ModerationQualityHistory(fixture.DbConnectionString, clock)
        do! fixture.InsertRawEvent<DetectionEvent>($"detection:{chat}:93001", 1,
            MlScoredMessage {| chatId = chat; messageId = 93001L; score = -2.0; isSpam = false |}, start.AddHours 1.0)
        let! count = history.Run(None, CancellationToken.None)
        Assert.Equal(4, count)
        let! resumed = history.Run(None, CancellationToken.None)
        Assert.Equal(0, resumed)
        let! _ = Assert.ThrowsAsync<ArgumentException>(fun () -> history.Run(Some (start.AddDays 4.0), CancellationToken.None))
        let! lastMature = history.Run(Some (start.AddDays 3.0), CancellationToken.None)
        Assert.Equal(1, lastMature)
    }

    [<Fact>]
    member _.``History endpoint validates dates and requires authentication`` () = task {
        use payload = new System.Net.Http.StringContent("")
        use! invalid = fixture.BotHttp.PostAsync("/quality-history?day=invalid", payload)
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode)
        use! immature = fixture.BotHttp.PostAsync("/quality-history?day=9999-01-01", payload)
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, immature.StatusCode)
        use client = new System.Net.Http.HttpClient(BaseAddress = fixture.BotHttp.BaseAddress)
        use! unauthorized = client.PostAsync("/quality-history", payload)
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode)
    }
