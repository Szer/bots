open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open FakeAzureFoundryApi.Handlers

let builder = WebApplication.CreateBuilder()
let app = builder.Build()

// Azure AI Foundry chat completions endpoint
app.MapPost(
    "/openai/deployments/{deployment}/chat/completions",
    Func<string, HttpContext, Threading.Tasks.Task>(fun deployment ctx ->
        handleChatCompletions deployment ctx))
|> ignore

// Azure AI Foundry embeddings endpoint
app.MapPost(
    "/openai/deployments/{deployment}/embeddings",
    Func<string, HttpContext, Threading.Tasks.Task>(fun deployment ctx ->
        handleEmbeddings deployment ctx))
|> ignore

// Test endpoints
app.MapPost("/test/mock/completion", Func<HttpContext, Threading.Tasks.Task>(fun ctx -> setCompletion ctx)) |> ignore
app.MapGet("/test/calls",           Func<HttpContext, Threading.Tasks.Task>(fun ctx -> getCalls ctx))      |> ignore
app.MapDelete("/test/calls",        Func<HttpContext, Threading.Tasks.Task>(fun ctx -> clearCalls ctx))    |> ignore
app.MapGet("/health",               Func<string>(fun () -> "OK"))                                          |> ignore

app.Run()
