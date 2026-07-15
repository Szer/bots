module SerializationCompat.Tests.FakeTgApiSpike

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Xunit
open BotInfra
open Funogram.Types
open Funogram.Telegram.Types

module Req = Funogram.Telegram.Req

/// Spike gate for the migration: proves Funogram's HTTP layer speaks to the existing
/// FakeTgApi implementation unchanged — URL shape ({base}/bot{token}/{method}), JSON
/// request bodies, and response parsing — by hosting FakeTgApi's real handlers in-proc.
type FakeTgApiSpikeTests() =

    let mutable app: WebApplication | null = null
    let mutable config = Unchecked.defaultof<BotConfig>

    interface IAsyncLifetime with
        member _.InitializeAsync() : ValueTask =
            ValueTask(task {
                let builder = WebApplication.CreateBuilder()
                %builder.Logging.ClearProviders()
                let a = builder.Build()
                %a.MapPost("/bot{token}/{method}", Func<HttpContext, Task>(fun ctx -> FakeTgApi.Handlers.handleTelegramMethod ctx))
                %a.MapPost("/bot{token}/{method}/{rest}", Func<HttpContext, Task>(fun ctx -> FakeTgApi.Handlers.handleTelegramMethod ctx))
                a.Urls.Add "http://127.0.0.1:0"
                do! a.StartAsync()
                let addr =
                    a.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses
                    |> Seq.head
                app <- a
                config <-
                    { IsTest = false
                      Token = "test-token"
                      Offset = None
                      Limit = None
                      Timeout = Some 10000L
                      AllowedUpdates = None
                      OnError = ignore
                      ApiEndpointUrl = Uri($"{addr}/bot")
                      Client = new HttpClient()
                      WebHook = None
                      RequestLogger = None }
            } :> Task)
        member _.DisposeAsync() : ValueTask =
            ValueTask(task {
                match app with
                | null -> ()
                | a -> do! a.StopAsync()
            } :> Task)

    [<Fact>]
    member _.``sendMessage: Funogram request hits FakeTgApi route and response parses`` () =
        task {
            let! result =
                Req.SendMessage.Make(123L, "hello from funogram")
                |> Funogram.Api.api config
                |> Async.StartAsTask
            match result with
            | Ok msg ->
                Assert.Equal(123L, msg.Chat.Id)
                Assert.True(msg.MessageId > 0L)
            | Error e -> Assert.Fail $"FakeTgApi rejected Funogram sendMessage: {e.ErrorCode} {e.Description}"
        }

    [<Fact>]
    member _.``deleteMessage: simple-ok methods work`` () =
        task {
            let! result =
                Req.DeleteMessage.Make(123L, 42L)
                |> Funogram.Api.api config
                |> Async.StartAsTask
            match result with
            | Ok ok -> Assert.True ok
            | Error e -> Assert.Fail $"FakeTgApi rejected Funogram deleteMessage: {e.ErrorCode} {e.Description}"
        }

    [<Fact>]
    member _.``getChatMember: response parses and carries the wire status`` () =
        task {
            let! result =
                Req.GetChatMember.Make(123L, 456L)
                |> Funogram.Api.api config
                |> Async.StartAsTask
            match result with
            | Ok member' ->
                // KNOWN Funogram 3.0.4 BUG: the DU converter picks ChatMember cases by
                // field-shape overlap, not by the "status" discriminator — {"status":"member"}
                // deserializes as ChatMember.Owner. The Status STRING is populated correctly
                // on whichever case gets chosen, so migrated code must branch on Status, not
                // on DU cases, until the converter is fixed upstream (Szer owns Funogram).
                let status, userId =
                    match member' with
                    | ChatMember.Owner m -> m.Status, m.User.Id
                    | ChatMember.Administrator m -> m.Status, m.User.Id
                    | ChatMember.Member m -> m.Status, m.User.Id
                    | ChatMember.Restricted m -> m.Status, m.User.Id
                    | ChatMember.Left m -> m.Status, m.User.Id
                    | ChatMember.Banned m -> m.Status, m.User.Id
                // FakeTgApi defaults unknown users to "member"
                Assert.Equal("member", status)
                Assert.Equal(456L, userId)
            | Error e -> Assert.Fail $"FakeTgApi rejected Funogram getChatMember: {e.ErrorCode} {e.Description}"
        }
