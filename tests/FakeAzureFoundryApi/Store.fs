namespace FakeAzureFoundryApi

open System
open System.Collections.Concurrent

module Store =
    let calls = ConcurrentQueue<ApiCallLog>()

    /// Per-deployment canned completion response.
    let completionResponses = ConcurrentDictionary<string, string>()

    let defaultCompletion = "test response"

    let getCompletion (deployment: string) =
        match completionResponses.TryGetValue(deployment) with
        | true, v -> v
        | _       -> defaultCompletion

    let setCompletion (deployment: string) (content: string) =
        completionResponses.[deployment] <- content

    let logCall (method: string) (url: string) (body: string) =
        calls.Enqueue(
            { Method    = method
              Url       = url
              Body      = body
              Timestamp = DateTime.UtcNow })

    let clearCalls () =
        let mutable item = Unchecked.defaultof<ApiCallLog>
        while calls.TryDequeue(&item) do ()
