module Fizruk.Tests.StatusFlowTests

open System
open System.Threading.Tasks
open Fizruk
open Fizruk.Tests.Fakes
open k8s
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Xunit

let private withApi (handler: HttpContext -> Task) (run: string -> Task<unit>) =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.UseUrls("http://127.0.0.1:0") |> ignore
        use app = builder.Build()
        app.Run(RequestDelegate handler)
        do! app.StartAsync()
        do! run (Seq.exactlyOne app.Urls)
        do! app.StopAsync()
    }

let private config (url: string) =
    Config.parse $$$$"""
    {
      "games": { "demo": {
        "displayName": "Demo", "namespace": "demo", "deployment": "demo",
        "podSelector": "app=demo", "container": "server", "nodeLabelSelector": "role=game",
        "address": "game.example.test:1234", "activityRegex": "JOIN",
        "idleGraceMinutes": 5, "idleWindowMinutes": 10, "startTimeoutMinutes": 1,
        "details": [{ "type": "factorio-ups", "prometheusUrl": "{{{{url}}}}/metrics",
                      "metricSelector": "simulation_ups{server=\"demo\"}" }]
      } }, "chats": { "-1": ["demo"] }
    }
    """

let private node (metadata: string) (status: string) =
    $$$$"""{"metadata": {"name":"game-node", {{{{metadata}}}} }, "status": { {{{{status}}}} }}"""

let private fullNode =
    node
        """"labels":{"topology.kubernetes.io/region":"test-region","topology.kubernetes.io/zone":"test-zone-2"}"""
        """"capacity":{"cpu":"4"},"allocatable":{"cpu":"3860m"},"conditions":[{"type":"Ready","status":"True"}]"""

let private runStatus nodeJson statusCode metricsBody ready desired check =
    let mutable metricsRequested = false
    let handler (ctx: HttpContext) =
        task {
            ctx.Response.ContentType <- "application/json"
            let body =
                match ctx.Request.Path.Value with
                | "/apis/apps/v1/namespaces/demo/deployments/demo" ->
                    $$$$"""{"apiVersion":"apps/v1","kind":"Deployment","spec":{"replicas":{{{{desired}}}}}}"""
                | "/api/v1/namespaces/demo/pods" ->
                    let condition = if ready then "True" else "False"
                    $$$$"""{"apiVersion":"v1","kind":"PodList","items":[{"metadata":{"name":"demo-pod"},"spec":{"nodeName":"game-node"},"status":{"phase":"Running","conditions":[{"type":"Ready","status":"{{{{condition}}}}"}]}}]}"""
                | "/api/v1/nodes" ->
                    $$$$"""{"apiVersion":"v1","kind":"NodeList","items":[{"metadata":{"name":"unrelated-node"},"status":{"capacity":{"cpu":"64"}}},{{{{nodeJson}}}}]}"""
                | "/metrics/api/v1/query" ->
                    metricsRequested <- true
                    Assert.Equal(
                        "avg(avg_over_time(simulation_ups{server=\"demo\"}[5m]) and (time() - timestamp(simulation_ups{server=\"demo\"}) < 30))",
                        string ctx.Request.Query["query"])
                    ctx.Response.StatusCode <- statusCode
                    metricsBody
                | path -> failwith $"Unexpected API request: {path}"
            do! ctx.Response.WriteAsync body
        }
    withApi (fun ctx -> handler ctx :> Task) (fun url ->
        task {
            use client = new Kubernetes(KubernetesClientConfiguration(Host = url))
            let core = GameCore(config url, KubernetesGateway client, FakeNotifier(), TimeProvider.System, TimeSpan.FromSeconds 30.0, NullLogger<GameCore>.Instance)
            let! reply = core.Status "demo"
            check reply metricsRequested
        })

let private result value =
    $$$$"""{"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[123,"{{{{value}}}}"]}]}}"""

[<Theory>]
[<InlineData("59.94", "59.9")>]
[<InlineData("0", "0.0")>]
let ``running status reports the hosting node capacity and location with measured UPS`` (value, expected) =
    runStatus fullNode 200 (result value) true 1 (fun reply requested ->
        Assert.True requested
        Assert.Contains("Demo: running.", reply)
        Assert.Contains("Node: game-node (Ready, age unknown; 4 vCPU, region test-region, zone test-zone-2)", reply)
        Assert.Contains($"Avg UPS (last 5m, active): {expected}.", reply)
        Assert.DoesNotContain("64 vCPU", reply)
        Assert.Contains("Address: game.example.test:1234", reply))

[<Theory>]
[<InlineData(503, "unavailable")>]
[<InlineData(200, "not json")>]
[<InlineData(200, "{}")>]
[<InlineData(200, "{\"status\":\"error\",\"error\":\"bad query\"}")>]
[<InlineData(200, "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[]}}")>]
[<InlineData(200, "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{},{}]}}")>]
let ``unavailable or invalid metrics do not hide running server status`` (code, body) =
    runStatus fullNode code body true 1 (fun reply _ ->
        Assert.Contains("Demo: running.", reply)
        Assert.Contains("Avg UPS (last 5m, active): unavailable.", reply)
        Assert.Contains("4 vCPU", reply))

[<Theory>]
[<InlineData("NaN")>]
[<InlineData("+Inf")>]
[<InlineData("-Inf")>]
[<InlineData("-1")>]
let ``invalid UPS values are unavailable rather than misleading numbers`` value =
    runStatus fullNode 200 (result value) true 1 (fun reply _ ->
        Assert.Contains("Avg UPS (last 5m, active): unavailable.", reply))

[<Fact>]
let ``missing node capacity and topology still allow status`` () =
    let minimal = node """"labels":{}""" """"conditions":[]"""
    runStatus minimal 200 (result "60") true 1 (fun reply _ ->
        Assert.Contains("Node: game-node (NotReady, age unknown)", reply)
        Assert.DoesNotContain("vCPU", reply)
        Assert.Contains("Avg UPS (last 5m, active): 60.0.", reply))

[<Theory>]
[<InlineData(false, 1, "Demo: starting")>]
[<InlineData(true, 0, "Demo: stopped.")>]
let ``stopped and starting games do not query or display UPS`` (ready, desired, expected) =
    runStatus fullNode 200 (result "60") ready desired (fun reply requested ->
        Assert.False requested
        Assert.StartsWith(expected, reply)
        Assert.DoesNotContain("UPS", reply))

[<Fact>]
let ``metrics request timeout returns unavailable`` () =
    withApi
        (fun _ -> Task.Delay 500)
        (fun url ->
            task {
                let metrics = { PrometheusUrl = Uri url; MetricSelector = "simulation_ups" }
                let! line = FactorioUps.fetch metrics (TimeSpan.FromMilliseconds 50.0)
                Assert.Equal("Avg UPS (last 5m, active): unavailable.", line)
            })
