namespace FakeAzureFoundryApi

open System

[<CLIMutable>]
type ApiCallLog =
    { Method:    string
      Url:       string
      Body:      string
      Timestamp: DateTime }

[<CLIMutable>]
type CompletionMockDto =
    { deployment: string
      content:    string }
