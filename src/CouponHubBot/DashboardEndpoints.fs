namespace CouponHubBot

open System
open System.IO
open System.Threading.Tasks
open BotInfra
open CouponHubBot.Services
open CouponHubBot.Utils
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options

module DashboardEndpoints =
    let configure (builder: WebApplicationBuilder) =
        %builder.Services.AddRateLimiter(fun limiter ->
            limiter.RejectionStatusCode <- StatusCodes.Status429TooManyRequests
            %limiter.AddFixedWindowLimiter("dashboard-login", fun limit ->
                limit.PermitLimit <- 120
                limit.Window <- TimeSpan.FromMinutes 1.0
                limit.QueueLimit <- 0))

    let map (app: WebApplication) =
        %app.Use(Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
            task {
                if ctx.Request.Path.StartsWithSegments(PathString "/dashboard") then
                    ctx.Response.Headers.CacheControl <- "no-store"
                    ctx.Response.Headers["Referrer-Policy"] <- "no-referrer"
                    ctx.Response.Headers["X-Content-Type-Options"] <- "nosniff"
                    ctx.Response.Headers["Content-Security-Policy"] <-
                        "default-src 'none'; script-src 'self' https://telegram.org; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'self' https://web.telegram.org https://*.telegram.org"
                    ctx.Response.Headers["Permissions-Policy"] <- "camera=(), microphone=(), geolocation=()"
                do! next.Invoke ctx
            }))
        %app.UseRateLimiter()

        for route, file, contentType in
            [ "/dashboard", "index.html", "text/html; charset=utf-8"
              "/dashboard/app.js", "app.js", "text/javascript; charset=utf-8"
              "/dashboard/styles.css", "styles.css", "text/css; charset=utf-8" ] do
            %app.MapGet(route, Func<IResult>(fun () ->
                Results.File(Path.Combine(app.Environment.ContentRootPath, "wwwroot", "dashboard", file), contentType)))

        %app.MapGet("/dashboard/api/info", Func<DashboardService, Task<IResult>>(fun service ->
            task {
                let! link = service.GetBotLink()
                return Results.Json({| botUrl = link |> Option.toObj |})
            }))

        %app.MapPost("/dashboard/api/session", Func<HttpContext, DashboardService, TelegramMembershipService, Task<IResult>>(fun ctx service membership ->
            task {
                if not (ctx.Request.HasFormContentType) && ctx.Request.ContentType <> null
                   && ctx.Request.ContentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) then
                    use reader = new StreamReader(ctx.Request.Body)
                    let buffer = Array.zeroCreate<char> (DashboardAuth.maxInitDataLength + 1)
                    let! length = reader.ReadBlockAsync(buffer, 0, buffer.Length)
                    let options = ctx.RequestServices.GetRequiredService<IOptions<BotConfiguration>>()
                    let time = ctx.RequestServices.GetRequiredService<TimeProvider>()
                    let communityId = options.Value.CommunityChatId
                    match DashboardAuth.validate options.Value.BotToken (time.GetUtcNow()) (String(buffer, 0, length)) with
                    | None -> return Results.Unauthorized()
                    | Some userId ->
                        let! isMember = membership.IsMember(userId, TimeSpan.FromMinutes 1.0)
                        if not isMember || communityId <> options.Value.CommunityChatId then
                            return Results.StatusCode(StatusCodes.Status403Forbidden)
                        else
                            let! session = service.CreateSession(userId, communityId)
                            return Results.Json session
                else
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType)
            })).RequireRateLimiting("dashboard-login")

        %app.MapGet("/dashboard/api/stats", Func<HttpContext, DashboardService, TelegramMembershipService, Task<IResult>>(fun ctx service membership ->
            task {
                let options = ctx.RequestServices.GetRequiredService<IOptions<BotConfiguration>>()
                let communityId = options.Value.CommunityChatId
                let authorization = string ctx.Request.Headers.Authorization
                let! userId =
                    if authorization.StartsWith("Bearer ", StringComparison.Ordinal) then
                        service.FindSession(authorization.Substring 7, communityId)
                    else Task.FromResult None
                match userId with
                | None -> return Results.Unauthorized()
                | Some userId ->
                    let! isMember = membership.IsMember(userId, TimeSpan.FromMinutes 1.0)
                    if not isMember then return Results.StatusCode(StatusCodes.Status403Forbidden)
                    else
                        match Int32.TryParse(string ctx.Request.Query["days"]) with
                        | true, days when List.contains days [ 0; 30; 90; 365 ] ->
                            let! snapshot = service.GetSnapshot days
                            if communityId <> options.Value.CommunityChatId then
                                return Results.StatusCode(StatusCodes.Status403Forbidden)
                            else
                                return Results.Json
                                    {| asOf = snapshot.AsOf; refreshAfter = snapshot.RefreshAfter; periodDays = snapshot.PeriodDays
                                       viewerId = string userId; community = snapshot.Community; members = snapshot.Members |}
                        | _ -> return Results.BadRequest()
            }))
