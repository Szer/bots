namespace FakeAzureFoundryApi

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http

module Handlers =

    let readBody (ctx: HttpContext) = task {
        if ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value = 0L then
            return ""
        else
            use reader = new IO.StreamReader(ctx.Request.Body, Encoding.UTF8)
            return! reader.ReadToEndAsync()
    }

    let respondJson (ctx: HttpContext) (status: int) (json: string) = task {
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json"
        let bytes = Encoding.UTF8.GetBytes(json)
        do! ctx.Response.Body.WriteAsync(bytes.AsMemory(0, bytes.Length))
    }

    let handleChatCompletions (deployment: string) (ctx: HttpContext) = task {
        let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
        let! body = readBody ctx
        Console.WriteLine($"FAKE FOUNDRY COMPLETIONS {deployment} bodyLen={body.Length}")
        Store.logCall ctx.Request.Method url body

        let content = Store.getCompletion deployment

        let responseJson =
            $"""{{
  "choices": [{{
    "finish_reason": "stop",
    "index": 0,
    "message": {{
      "content": {JsonSerializer.Serialize(content)},
      "role": "assistant"
    }}
  }}],
  "created": 1774736361,
  "id": "chatcmpl-fake",
  "model": "{deployment}",
  "object": "chat.completion",
  "usage": {{
    "completion_tokens": 10,
    "prompt_tokens": 100,
    "total_tokens": 110
  }}
}}"""

        do! respondJson ctx 200 responseJson
    }

    /// Returns a deterministic zero-vector of 1536 dimensions.
    let handleEmbeddings (deployment: string) (ctx: HttpContext) = task {
        let url = ctx.Request.Path.ToString() + ctx.Request.QueryString.ToString()
        let! body = readBody ctx
        Console.WriteLine($"FAKE FOUNDRY EMBEDDINGS {deployment} bodyLen={body.Length}")
        Store.logCall ctx.Request.Method url body

        let zeros = Array.replicate 1536 0.0
        let vectorJson = JsonSerializer.Serialize(zeros)
        let responseJson =
            $"""{{
  "data": [{{
    "embedding": {vectorJson},
    "index": 0,
    "object": "embedding"
  }}],
  "model": "{deployment}",
  "object": "list",
  "usage": {{ "prompt_tokens": 5, "total_tokens": 5 }}
}}"""

        do! respondJson ctx 200 responseJson
    }

    let getCalls (ctx: HttpContext) = task {
        let calls = Store.calls |> Seq.toArray
        let json = JsonSerializer.Serialize(calls, JsonSerializerOptions(JsonSerializerDefaults.Web))
        do! respondJson ctx 200 json
    }

    let clearCalls (ctx: HttpContext) = task {
        Store.clearCalls()
        do! respondJson ctx 200 """{"ok":true}"""
    }

    let setCompletion (ctx: HttpContext) = task {
        let! body = readBody ctx
        try
            let payload =
                JsonSerializer.Deserialize<CompletionMockDto>(body, JsonSerializerOptions(JsonSerializerDefaults.Web))
            match payload with
            | null ->
                do! respondJson ctx 400 """{"ok":false}"""
            | dto ->
                Store.setCompletion dto.deployment dto.content
                do! respondJson ctx 200 """{"ok":true}"""
        with _ ->
            do! respondJson ctx 400 """{"ok":false}"""
    }
