namespace AlitaBot.Tests

open System.Threading.Tasks
open Xunit

[<Collection("AlitaBot")>]
type SchedulerTests(fixture: DefaultAlitaTestContainers) =
    interface IClassFixture<DefaultAlitaTestContainers>

    [<Fact>]
    member _.``daily_dossier_update job runs via test endpoint``() = task {
        // Seed a user into message_log so the dossier job has something to process
        do! fixture.Execute(
                "INSERT INTO message_log(user_id, chat_id, message, sent_at) VALUES(2001, @chat, 'тест', NOW())",
                {| chat = fixture.TargetChatId |})
            :> Task

        // Also seed a person_dossier row so the job can update it
        do! fixture.Execute(
                """INSERT INTO person_dossier(user_id, username, display_name, summary)
                   VALUES(2001, 'testuser', 'Test User', NULL)
                   ON CONFLICT (user_id) DO NOTHING""",
                {| |})
            :> Task

        let! resp = fixture.Bot.PostAsync("/test/run-job?name=daily_dossier_update", null)
        resp.EnsureSuccessStatusCode() |> ignore

        // scheduled_job should show last_completed_at set
        let! completed =
            fixture.QuerySingleOrDefault<System.DateTime option>(
                "SELECT last_completed_at FROM scheduled_job WHERE job_name = 'daily_dossier_update'",
                {| |})
        // The job ran (even if no new facts were found); completed_at updated
        Assert.True(completed <> None)
    }

    [<Fact>]
    member _.``daily_news_fetch job runs via test endpoint``() = task {
        // NEWS_SOURCE_URLS is [] by default in test seeding — job should complete with 0 summaries
        let! resp = fixture.Bot.PostAsync("/test/run-job?name=daily_news_fetch", null)
        resp.EnsureSuccessStatusCode() |> ignore

        // Verify the endpoint returns ok
        let! body = resp.Content.ReadAsStringAsync()
        Assert.Contains("ok", body)
    }

    [<Fact>]
    member _.``daily_cleanup job runs via test endpoint``() = task {
        let! resp = fixture.Bot.PostAsync("/test/run-job?name=daily_cleanup", null)
        resp.EnsureSuccessStatusCode() |> ignore

        let! body = resp.Content.ReadAsStringAsync()
        Assert.Contains("ok", body)
    }

    [<Fact>]
    member _.``health endpoint returns OK``() = task {
        let! resp = fixture.Bot.GetAsync("/healthz")
        resp.EnsureSuccessStatusCode() |> ignore
        let! body = resp.Content.ReadAsStringAsync()
        Assert.Equal("OK", body)
    }
