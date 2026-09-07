namespace CouponHubBot.Tests

open System
open System.Net.Http
open Microsoft.Extensions.Logging.Abstractions
open Microsoft.Extensions.Options
open CouponHubBot
open CouponHubBot.Services
open Xunit

// Pure config tests — no DB, no containers, no HTTP calls (IsConfigured never sends a request).
type GitHubServiceIsConfiguredTests() =

    let baseConf : BotConfiguration =
        { BotToken = "token"
          SecretToken = "secret"
          CommunityChatId = -1L
          TelegramApiBaseUrl = null
          ReminderHourDublin = 10
          ReminderRunOnStart = false
          OcrEnabled = false
          OcrMaxFileSizeBytes = 0L
          AzureOcrEndpoint = ""
          AzureOcrKey = ""
          FeedbackAdminIds = [||]
          GitHubToken = "gh-token"
          GitHubRepo = "Szer/bots"
          FeedbackGitHubIssues = false
          WebhookUrl = ""
          TestMode = true
          MaxTakenCoupons = 6
          BatchDebounceMs = 5000 }

    let makeService (conf: BotConfiguration) =
        new GitHubService(new HttpClient(), Options.Create conf, NullLogger<GitHubService>.Instance, TimeProvider.System)

    [<Fact>]
    let ``Flag off with valid repo and token is not configured`` () =
        let svc = makeService { baseConf with FeedbackGitHubIssues = false }
        Assert.False(svc.IsConfigured)

    [<Fact>]
    let ``Flag on with valid repo and token is configured`` () =
        let svc = makeService { baseConf with FeedbackGitHubIssues = true }
        Assert.True(svc.IsConfigured)

    [<Fact>]
    let ``Flag on with blank repo is not configured`` () =
        let svc = makeService { baseConf with FeedbackGitHubIssues = true; GitHubRepo = "" }
        Assert.False(svc.IsConfigured)
