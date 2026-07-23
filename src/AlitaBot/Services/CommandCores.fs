namespace AlitaBot.Services

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Funogram.Telegram.Types
open AlitaBot
open AlitaBot.Llm
open BotInfra

// ── Social-engine / analytics module-level shapes ───────────────────────────
//
// Moved out of BotService (S10 PR2 prerequisite, mirroring PR1's MessageLog/Admin/
// MediaActions extraction) so the "core" of every read-only command (the DB reads + LLM
// call that actually produces the reply text) lives in a file compiled BEFORE
// ToolExecutor.fs — the NL tool-calling loop's read-only tools (ask_chat_history,
// summarize_chat, show_dossier, roast_user, show_awards, show_quote, show_karma,
// switch_model, show_usage) call the EXACT SAME functions BotService's command handlers
// call, so both paths share one implementation and one set of guardrails. BotService
// rebinds these locally where it already did (following the PR1 convention) and keeps its
// own "shell" (log the command row, send the reply, log the bot's row) — only the "what do
// we actually answer" logic moved here.

/// `/roast`'s resolved target: a user_id plus the display name shown in the LLM prompt.
type RoastTarget = { UserId: int64; DisplayName: string }

/// `/roast`'s gathered ammunition — see `CommandCores.gatherRoastAmmo`.
type RoastAmmo =
    { DossierSummary: string option
      Facts: string list
      Messages: string list }

/// One entry of the `/awards` LLM's JSON array — see `CommandCores.parseAwardsJson`.
type AwardEntry =
    { Title: string
      User: string
      EvidenceQuote: string }

/// The `/quote` LLM's JSON object — see `CommandCores.parseQuoteJson`.
type QuoteEntry =
    { Author: string
      Quote: string
      Comment: string }

/// One entry of the LLM_MODELS bot_setting (`/model`'s catalog).
type LlmModelEntry = { Model: string; Deployment: string }

/// Shared "core" logic for the read-only commands/tools (S10 PR2) — pure DB-read +
/// (mostly) LLM-call functions with no Telegram-sending/message_log-logging of their own,
/// so both a command handler's `reply` wrapper and ToolExecutorService's dispatch can call
/// the identical function and just do different things with the resulting (text, outcome).
module CommandCores =

    let displayNameOf (u: User) =
        match u.LastName with
        | Some last -> $"{u.FirstName} {last}"
        | None -> u.FirstName

    /// One line per message_log row: "[display_name]: text" — shared by /summary and the
    /// Slice 8 interjection LLM call's recent-context transcript.
    let buildTranscript (rows: MessageLogRow[]) : string =
        rows |> Array.map (fun r -> $"[{r.display_name}]: {r.text}") |> String.concat "\n"

    /// Calls `chat.Complete(request, ...)` and applies `tryParse` to the raw response
    /// text; on an LLM failure OR a malformed/unparseable response, retries ONCE with the
    /// exact same request before giving up. `/awards`/`/quote`/`/sql` all need a strict
    /// JSON shape out of a free-text-capable model — this is the one retry the plan asks
    /// for, shared so every JSON-contract command/tool gets identical behavior.
    let completeJsonWithRetry
        (chat: IChatCompletion)
        (logger: ILogger)
        (usageCtx: UsageContext)
        (request: ChatRequest)
        (tryParse: string -> 'a option)
        : Task<'a option> =
        task {
            let attempt () =
                task {
                    match! chat.Complete(request, usageCtx, CancellationToken.None) with
                    | Ok resp -> return tryParse resp.Text
                    | Error err ->
                        logger.LogWarning("Social JSON LLM call failed: {Error}", string err)
                        return None
                }
            match! attempt () with
            | Some v -> return Some v
            | None ->
                logger.LogWarning("Social JSON LLM response malformed or the call failed — retrying once")
                return! attempt ()
        }

    // ── /ask ─────────────────────────────────────────────────────────────────

    let buildAskContext (rows: AskMatchRow[]) : string =
        rows
        |> Array.map (fun r -> $"""[{r.display_name}, {r.sent_at.ToString("yyyy-MM-dd")}]: {r.text}""")
        |> String.concat "\n"

    let askRequest (conf: BotConfiguration) (question: string) (context: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.AskPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text $"Вопрос: {question}\n\nСообщения из истории чата:\n{context}" ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None
          ReasoningEffort = None }

    /// `/ask <question>` / `ask_chat_history` tool core: embeds the question, pulls the
    /// ASK_TOP_K nearest message_embedding rows for this chat above ASK_SIM_FLOOR cosine
    /// similarity, and answers via a non-stream LLM call grounded in that context.
    let askCore
        (db: DbService)
        (embeddings: IEmbeddings)
        (chat: IChatCompletion)
        (logger: ILogger)
        (conf: BotConfiguration)
        (chatId: int64)
        (usageCtx: UsageContext)
        (question: string)
        : Task<string * string> =
        task {
            if String.IsNullOrWhiteSpace question then
                return "Спроси что-нибудь про этот чат: `/ask когда договорились встретиться`.", "ask_empty_question"
            else
                match! embeddings.Embed(conf.EmbeddingDeployment, [ question ], usageCtx, CancellationToken.None) with
                | Error err ->
                    logger.LogWarning("/ask: failed to embed the question: {Error}", string err)
                    return "Не получилось разобрать вопрос 🙁", "ask_embed_failed"
                | Ok vectors when vectors.Length = 0 || vectors[0].Length = 0 ->
                    logger.LogWarning("/ask: embedding returned no vectors for the question")
                    return "Не получилось разобрать вопрос 🙁", "ask_embed_failed"
                | Ok vectors ->
                    let! matches = db.SemanticSearch(chatId, vectors[0], conf.AskTopK, conf.AskSimFloor)
                    if matches.Length = 0 then
                        return "Ничего подходящего в истории этого чата не нашла.", "ask_no_matches"
                    else
                        let context = buildAskContext matches
                        match! chat.Complete(askRequest conf question context, usageCtx, CancellationToken.None) with
                        | Ok resp when not (String.IsNullOrWhiteSpace resp.Text) -> return resp.Text, "ask_answered"
                        | Ok _ -> return "Модель промолчала.", "ask_empty_response"
                        | Error err ->
                            logger.LogWarning("/ask: LLM call failed: {Error}", string err)
                            return "Не получилось ответить 🙁", "ask_failed"
        }

    // ── /summary ─────────────────────────────────────────────────────────────

    [<Literal>]
    let SummaryDefaultCount = 200

    [<Literal>]
    let SummaryMaxCount = 500

    /// `/summary [count]` arg parsing: a positive integer arg is capped at
    /// SummaryMaxCount; anything else (missing, non-numeric, <= 0) falls back to
    /// SummaryDefaultCount.
    let parseSummaryCount (args: string) =
        match Int32.TryParse(args.Trim()) with
        | true, v when v > 0 -> min v SummaryMaxCount
        | _ -> SummaryDefaultCount

    let summaryRequest (conf: BotConfiguration) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.SummaryPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text transcript ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None
          ReasoningEffort = None }

    /// `/summary [count]` / `summarize_chat` tool core: speaker-attributed transcript of
    /// the last `count` (default SummaryDefaultCount, capped SummaryMaxCount) message_log
    /// rows for this chat, fed to a non-stream LLM call (SUMMARY_PROMPT).
    let summaryCore
        (db: DbService)
        (chat: IChatCompletion)
        (logger: ILogger)
        (conf: BotConfiguration)
        (chatId: int64)
        (usageCtx: UsageContext)
        (countArg: string)
        : Task<string * string> =
        task {
            let count = parseSummaryCount countArg
            let! rows = db.RecentContext(chatId, count)
            if rows.Length = 0 then
                return "Пока обсуждать нечего — история этого чата пуста.", "summary_empty_history"
            else
                let transcript = buildTranscript rows
                match! chat.Complete(summaryRequest conf transcript, usageCtx, CancellationToken.None) with
                | Ok resp when not (String.IsNullOrWhiteSpace resp.Text) -> return resp.Text, "summary_generated"
                | Ok _ -> return "Модель промолчала — не смогла подвести итоги.", "summary_empty_response"
                | Error err ->
                    logger.LogWarning("Summary generation failed: {Error}", string err)
                    return "Не получилось подвести итоги 🙁", "summary_failed"
        }

    // ── /dossier ─────────────────────────────────────────────────────────────

    [<Literal>]
    let NoDossierText = "пусто, я тебя ещё не изучила"

    [<Literal>]
    let DossierFactsShown = 5

    let renderDossier (summary: string) (facts: ActiveFactRow[]) : string =
        if facts.Length = 0 then
            summary
        else
            let factsText = facts |> Array.map (fun f -> $"- {f.content}") |> String.concat "\n"
            $"{summary}\n\nФакты:\n{factsText}"

    /// `/dossier [@username]` / `show_dossier` tool core: resolves the target (self when
    /// `args` is blank) and renders their cumulative summary plus newest DossierFactsShown
    /// active facts, or NoDossierText for anyone with nothing yet.
    let dossierCore (db: DbService) (requesterUserId: int64) (args: string) : Task<string * string> =
        task {
            let trimmedArg = args.Trim().TrimStart('@')
            let! targetIdOpt =
                if trimmedArg = "" then Task.FromResult(Some requesterUserId) else db.ResolveUserIdByUsername(trimmedArg)
            match targetIdOpt with
            | None -> return NoDossierText, "dossier_unknown_user"
            | Some targetId ->
                match! db.GetPersonDossier(targetId) with
                | None -> return NoDossierText, "dossier_empty"
                | Some dossier ->
                    let! facts = db.NewestActiveFacts(targetId, DossierFactsShown)
                    return renderDossier dossier.summary facts, "dossier_shown"
        }

    // ── /roast ───────────────────────────────────────────────────────────────

    [<Literal>]
    let RoastMessagesLimit = 50

    [<Literal>]
    let RoastFactsK = 8

    [<Literal>]
    let NoRoastDataText = "этого кадра я ещё не изучила"

    [<Literal>]
    let RoastCooldownText = "этого уже жарили, дай остыть"

    /// Resolves `/roast`'s target: an explicit `@username`/`username` arg (message_log
    /// lookup for user_id + display_name — `None` when nobody with that username has ever
    /// been logged); otherwise the message being replied to's author; otherwise the
    /// invoker themselves. Command-only (needs the full `Message` for the reply-to case) —
    /// the `roast_user` tool uses `resolveRoastTargetForTool` instead (no reply-to concept
    /// in a tool call).
    let resolveRoastTarget (db: DbService) (msg: Message) (from: User) (args: string) : Task<RoastTarget option> =
        task {
            let trimmed = args.Trim().TrimStart('@')
            if trimmed <> "" then
                match! db.ResolveUserByUsername(trimmed) with
                | Some(uid, name) -> return Some { UserId = uid; DisplayName = name }
                | None -> return None
            else
                match msg.ReplyToMessage |> Option.bind (fun r -> r.From) with
                | Some u -> return Some { UserId = u.Id; DisplayName = displayNameOf u }
                | None -> return Some { UserId = from.Id; DisplayName = displayNameOf from }
        }

    /// `roast_user` tool's target resolution: an explicit `@username`/`username` argument
    /// resolves via message_log the same way the command does; a blank/absent argument
    /// targets the invoker themselves (a tool call has no "message being replied to").
    let resolveRoastTargetForTool
        (db: DbService)
        (invokerId: int64)
        (invokerDisplayName: string)
        (argUsername: string option)
        : Task<RoastTarget option> =
        task {
            match argUsername |> Option.map (fun s -> s.Trim().TrimStart('@')) with
            | Some trimmed when trimmed <> "" ->
                match! db.ResolveUserByUsername(trimmed) with
                | Some(uid, name) -> return Some { UserId = uid; DisplayName = name }
                | None -> return None
            | _ -> return Some { UserId = invokerId; DisplayName = invokerDisplayName }
        }

    let roastAmmoIsEmpty (ammo: RoastAmmo) =
        ammo.DossierSummary.IsNone && ammo.Facts.IsEmpty && ammo.Messages.IsEmpty

    /// Gathers `/roast`'s ammunition for `target`: dossier summary + up to RoastFactsK
    /// newest active interaction_memory facts + up to RoastMessagesLimit of the target's
    /// own recent message_log texts. An opted-out target is roasted ONLY from their recent
    /// messages — dossier/facts are never read for them.
    let gatherRoastAmmo (db: DbService) (target: RoastTarget) : Task<RoastAmmo> =
        task {
            let! optedOut = db.IsOptedOut(target.UserId)
            let! recentRows = db.UserRecentMessages(target.UserId, RoastMessagesLimit)
            let messages = recentRows |> Array.map (fun r -> r.text) |> Array.toList
            if optedOut then
                return { DossierSummary = None; Facts = []; Messages = messages }
            else
                let! dossierOpt = db.GetPersonDossier(target.UserId)
                let! facts = db.NewestActiveFacts(target.UserId, RoastFactsK)
                return
                    { DossierSummary = dossierOpt |> Option.map (fun d -> d.summary)
                      Facts = facts |> Array.map (fun f -> f.content) |> Array.toList
                      Messages = messages }
        }

    let roastRequest (conf: BotConfiguration) (targetName: string) (ammo: RoastAmmo) : ChatRequest =
        let parts =
            [ ammo.DossierSummary |> Option.map (fun s -> $"Досье:\n{s}")
              (if ammo.Facts.IsEmpty then
                   None
               else
                   Some("Факты:\n" + (ammo.Facts |> List.map (fun f -> $"- {f}") |> String.concat "\n")))
              (if ammo.Messages.IsEmpty then
                   None
               else
                   Some("Сообщения:\n" + (ammo.Messages |> List.map (fun m -> $"- {m}") |> String.concat "\n"))) ]
            |> List.choose id
        let body = String.concat "\n\n" parts
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.RoastPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text $"Цель: {targetName}\n\n{body}" ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None
          ReasoningEffort = None }

    /// `/roast`/`roast_user` shared core once a target is already resolved: cooldown check
    /// (ROAST_COOLDOWN_SECONDS per target), ammo gathering, the ROAST_PROMPT LLM call, and
    /// — only on an actual successful delivery — `db.RecordRoast`.
    let roastCore
        (db: DbService)
        (chat: IChatCompletion)
        (logger: ILogger)
        (time: TimeProvider)
        (conf: BotConfiguration)
        (usageCtx: UsageContext)
        (target: RoastTarget)
        : Task<string * string> =
        task {
            let now = time.GetUtcNow().UtcDateTime
            let! lastRoasted = db.LastRoastedAt(target.UserId)
            match lastRoasted with
            | Some last when (now - last).TotalSeconds < float conf.RoastCooldownSeconds ->
                return RoastCooldownText, "roast_cooldown"
            | _ ->
                let! ammo = gatherRoastAmmo db target
                if roastAmmoIsEmpty ammo then
                    return NoRoastDataText, "roast_no_data"
                else
                    match! chat.Complete(roastRequest conf target.DisplayName ammo, usageCtx, CancellationToken.None) with
                    | Ok resp when not (String.IsNullOrWhiteSpace resp.Text) ->
                        do! db.RecordRoast(target.UserId, now)
                        return resp.Text, "roast_delivered"
                    | Ok _ -> return "Слов не нашлось — то ещё достижение.", "roast_empty_response"
                    | Error err ->
                        logger.LogWarning("Roast generation failed: {Error}", string err)
                        return "Не получилось прожарить 🙁", "roast_failed"
        }

    /// `/roast [@username | reply-to-target]` command-only end-to-end core (resolution +
    /// roastCore) — the `roast_user` tool calls `resolveRoastTargetForTool` + `roastCore`
    /// directly instead (no reply-to concept).
    let roastCommandCore
        (db: DbService)
        (chat: IChatCompletion)
        (logger: ILogger)
        (time: TimeProvider)
        (conf: BotConfiguration)
        (msg: Message)
        (from: User)
        (usageCtx: UsageContext)
        (args: string)
        : Task<string * string> =
        task {
            match! resolveRoastTarget db msg from args with
            | None -> return NoRoastDataText, "roast_unknown_target"
            | Some target -> return! roastCore db chat logger time conf usageCtx target
        }

    // ── /awards, /quote shared handle-attribution helpers ───────────────────

    /// The handle a `/awards`/`/quote` transcript line attributes a message to: `@username`
    /// when the sender has one, their `display_name` otherwise.
    let socialHandleOf (r: MessageLogRow) =
        if String.IsNullOrWhiteSpace(r.username: string) then r.display_name else $"@{r.username}"

    let buildHandleTranscript (rows: MessageLogRow[]) : string =
        rows |> Array.map (fun r -> $"[{socialHandleOf r}]: {r.text}") |> String.concat "\n"

    /// Defensive cleanup for a `/awards` "user" field: strips a stray bracketed form
    /// (`"[Ayrat Ru]"` -> `"Ayrat Ru"`) a real model sometimes echoes verbatim from the
    /// transcript despite the prompt asking for the bare handle.
    let stripUserHandleBrackets (s: string) =
        let t = s.Trim()
        if t.Length >= 2 && t.StartsWith "[" && t.EndsWith "]" then t.Substring(1, t.Length - 2).Trim() else t

    // ── /awards ──────────────────────────────────────────────────────────────

    [<Literal>]
    let AwardsWindowDays = 7.0

    [<Literal>]
    let AwardsTranscriptCap = 800

    let parseAwardsJson (json: string) : AwardEntry list option =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Array then
                None
            else
                let elements = doc.RootElement.EnumerateArray() |> Seq.toList
                if elements.IsEmpty then
                    None
                else
                    let parsed =
                        elements
                        |> List.map (fun el ->
                            if el.ValueKind <> JsonValueKind.Object then
                                None
                            else
                                match el.TryGetProperty "title", el.TryGetProperty "user", el.TryGetProperty "evidence_quote" with
                                | (true, t), (true, u), (true, e) when
                                    t.ValueKind = JsonValueKind.String
                                    && u.ValueKind = JsonValueKind.String
                                    && e.ValueKind = JsonValueKind.String
                                    && not (String.IsNullOrWhiteSpace(t.GetString()))
                                    && not (String.IsNullOrWhiteSpace(u.GetString())) ->
                                    Some
                                        { Title = t.GetString()
                                          User = stripUserHandleBrackets (u.GetString())
                                          EvidenceQuote = e.GetString() }
                                | _ -> None)
                    if parsed |> List.exists Option.isNone then None else Some(parsed |> List.choose id)
        with _ ->
            None

    let awardsRequest (conf: BotConfiguration) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.AwardsPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text transcript ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None
          ReasoningEffort = None }

    let renderAwards (awards: AwardEntry list) : string =
        let lines = awards |> List.map (fun a -> $"🏆 {a.Title} — {a.User}: „{a.EvidenceQuote}\"")
        "Награды недели:\n" + String.concat "\n" lines

    /// `/awards` / `show_awards` tool core: over the last AwardsWindowDays (7) of this
    /// chat's message_log (capped AwardsTranscriptCap, human messages only), the
    /// AWARDS_PROMPT LLM call returns a strict JSON array of 3-5 witty
    /// {title, user, evidence_quote} awards, written to `karma` and rendered.
    let awardsCore
        (db: DbService)
        (chat: IChatCompletion)
        (logger: ILogger)
        (time: TimeProvider)
        (conf: BotConfiguration)
        (chatId: int64)
        (usageCtx: UsageContext)
        : Task<string * string> =
        task {
            let since = time.GetUtcNow().UtcDateTime.AddDays(-AwardsWindowDays)
            let! rows = db.HumanMessagesSince(chatId, since, AwardsTranscriptCap)
            if rows.Length = 0 then
                return "Не за что вручать — эта неделя прошла на редкость тихо.", "awards_empty"
            else
                let transcript = buildHandleTranscript rows
                match! completeJsonWithRetry chat logger usageCtx (awardsRequest conf transcript) parseAwardsJson with
                | None -> return "Не получилось раздать награды 🙁", "awards_failed"
                | Some awards when awards.IsEmpty -> return "Не получилось раздать награды 🙁", "awards_failed"
                | Some awards ->
                    for aw in awards do
                        let! resolvedId =
                            if aw.User.StartsWith "@" then db.ResolveUserIdByUsername(aw.User.TrimStart '@')
                            else Task.FromResult None
                        do! db.InsertKarmaAward(resolvedId, aw.User, aw.Title, aw.EvidenceQuote)
                    return renderAwards awards, "awards_delivered"
        }

    // ── /quote ───────────────────────────────────────────────────────────────

    [<Literal>]
    let QuoteWindowHours = 24.0

    [<Literal>]
    let QuoteTranscriptCap = 500

    let parseQuoteJson (json: string) : QuoteEntry option =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                match
                    doc.RootElement.TryGetProperty "author", doc.RootElement.TryGetProperty "quote", doc.RootElement.TryGetProperty "comment"
                with
                | (true, a), (true, q), (true, c) when
                    a.ValueKind = JsonValueKind.String
                    && q.ValueKind = JsonValueKind.String
                    && c.ValueKind = JsonValueKind.String
                    && not (String.IsNullOrWhiteSpace(q.GetString())) ->
                    Some { Author = a.GetString(); Quote = q.GetString(); Comment = c.GetString() }
                | _ -> None
        with _ ->
            None

    let quoteRequest (conf: BotConfiguration) (transcript: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.QuotePrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text transcript ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None
          ReasoningEffort = None }

    let renderQuote (q: QuoteEntry) : string =
        $"💬 Цитата дня: „{q.Quote}\" — {q.Author}. {q.Comment}"

    /// `/quote` / `show_quote` tool core: the last QuoteWindowHours (24) of this chat's
    /// human, non-command message_log rows (capped QuoteTranscriptCap) feed the
    /// QUOTE_PROMPT LLM call, picking the single most absurd/quotable line.
    let quoteCore
        (db: DbService)
        (chat: IChatCompletion)
        (logger: ILogger)
        (time: TimeProvider)
        (conf: BotConfiguration)
        (chatId: int64)
        (usageCtx: UsageContext)
        : Task<string * string> =
        task {
            let since = time.GetUtcNow().UtcDateTime.AddHours(-QuoteWindowHours)
            let! rows = db.HumanMessagesSince(chatId, since, QuoteTranscriptCap)
            if rows.Length = 0 then
                return "За последние сутки цитировать особо некого.", "quote_empty"
            else
                let transcript = buildHandleTranscript rows
                match! completeJsonWithRetry chat logger usageCtx (quoteRequest conf transcript) parseQuoteJson with
                | Some q -> return renderQuote q, "quote_generated"
                | None -> return "Не получилось выбрать цитату дня 🙁", "quote_failed"
        }

    // ── /karma ───────────────────────────────────────────────────────────────

    [<Literal>]
    let KarmaRecentTitles = 3

    [<Literal>]
    let NoKarmaText = "пока без наград"

    let renderKarma (count: int64) (titles: string[]) : string =
        let titlesText = titles |> Array.map (fun t -> $"- {t}") |> String.concat "\n"
        $"Наград: {count}\nПоследние:\n{titlesText}"

    /// `/karma [@user]` / `show_karma` tool core: self (blank arg) or another chat
    /// member's totals from `karma` — a count plus the newest KarmaRecentTitles (3) titles.
    let karmaCore (db: DbService) (requesterUserId: int64) (args: string) : Task<string * string> =
        task {
            let trimmedArg = args.Trim().TrimStart('@')
            let! targetIdOpt =
                if trimmedArg = "" then Task.FromResult(Some requesterUserId) else db.ResolveUserIdByUsername(trimmedArg)
            match targetIdOpt with
            | None -> return NoKarmaText, "karma_unknown_user"
            | Some targetId ->
                let! count = db.KarmaCount(targetId)
                if count = 0L then
                    return NoKarmaText, "karma_empty"
                else
                    let! titles = db.KarmaNewestTitles(targetId, KarmaRecentTitles)
                    return renderKarma count titles, "karma_shown"
        }

    // ── /model ───────────────────────────────────────────────────────────────

    /// Lenient parse of the LLM_MODELS bot_setting (JSON_BLOB array of
    /// `{"model": "...", "deployment": "..."}`).
    let parseLlmModels (json: string) : LlmModelEntry list =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Array then
                []
            else
                [ for el in doc.RootElement.EnumerateArray() do
                    if el.ValueKind = JsonValueKind.Object then
                        let str (name: string) =
                            match el.TryGetProperty name with
                            | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                            | _ -> None
                        match str "model", str "deployment" with
                        | Some model, Some deployment when model <> "" && deployment <> "" ->
                            { Model = model; Deployment = deployment }
                        | _ -> () ]
        with _ -> []

    /// `/model [name]` / `switch_model` tool core — NOT admin-gated (mirrors the command,
    /// which has never required admin). No arg: shows the current model + catalog. With an
    /// arg that matches an entry's `Model` exactly: upserts LLM_DEPLOYMENT and reloads the
    /// live BotConfiguration in-process (ISettingsReloader) so the switch takes effect on
    /// the very next LLM call.
    let modelCore
        (db: DbService)
        (settingsReloader: ISettingsReloader)
        (conf: BotConfiguration)
        (args: string)
        : Task<string * string> =
        task {
            let models = parseLlmModels conf.LlmModelsJson
            let modelList = if models.IsEmpty then "(пусто)" else models |> List.map (fun m -> m.Model) |> String.concat ", "
            let currentModel =
                models
                |> List.tryFind (fun m -> m.Deployment = conf.LlmDeployment)
                |> Option.map (fun m -> m.Model)
                |> Option.defaultValue conf.LlmDeployment

            if String.IsNullOrWhiteSpace args then
                return $"Текущая модель: {currentModel}\nДоступные модели: {modelList}", "model_shown"
            else
                match models |> List.tryFind (fun m -> m.Model = args) with
                | Some entry ->
                    do! db.UpsertBotSetting("LLM_DEPLOYMENT", entry.Deployment, "FREE_FORM", "llm")
                    do! settingsReloader.Reload()
                    return $"Модель переключена на {entry.Model} ✅", "model_switched"
                | None ->
                    return
                        $"Такую модель не знаю и выдумывать не буду: «{args}». Выбирай из списка: {modelList}",
                        "model_refused"
        }

    // ── /usage ───────────────────────────────────────────────────────────────

    /// $-USD with 4 decimal places, invariant culture (never locale-dependent commas).
    let formatUsd (v: decimal) =
        "$" + v.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)

    /// Compact monospace-ish RU rendering of /usage's totals — no Markdown/parse_mode.
    let renderUsage
        (today: UsageTotalsRow)
        (week: UsageTotalsRow)
        (byModel: UsageByModelRow[])
        (byUser: UsageByUserRow[])
        : string =
        let header =
            [ "📊 Usage"
              ""
              $"Сегодня:  {today.calls,4} выз.  {formatUsd today.cost_usd}"
              $"7 дней:   {week.calls,4} выз.  {formatUsd week.cost_usd}" ]
        let modelLines =
            if byModel.Length = 0 then
                []
            else
                ""
                :: "По моделям (7 дней):"
                :: [ for m in byModel -> $"  {m.model,-24} {m.calls,4} выз.  {formatUsd m.cost_usd}" ]
        let userLines =
            if byUser.Length = 0 then
                []
            else
                ""
                :: "Топ пользователей (7 дней):"
                :: [ for u in byUser ->
                        let name = u.display_name |> Option.ofObj |> Option.defaultValue $"id{u.user_id}"
                        $"  {name,-24} {u.calls,4} выз.  {formatUsd u.cost_usd}" ]
        let footer = [ ""; $"Итого за 7 дней: {formatUsd week.cost_usd}" ]
        header @ modelLines @ userLines @ footer |> String.concat "\n"

    /// `/usage` / `show_usage` tool core — NOT admin-gated (mirrors the command). Today +
    /// last 7 days totals, by-model and top-5-by-user breakdowns, straight from `llm_usage`.
    let usageCore (db: DbService) (time: TimeProvider) (_conf: BotConfiguration) : Task<string * string> =
        task {
            let now = time.GetUtcNow().UtcDateTime
            let todayStart = DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc)
            let weekStart = now.AddDays(-7.0)
            let! today = db.UsageTotals(todayStart)
            let! week = db.UsageTotals(weekStart)
            let! byModel = db.UsageByModel(weekStart)
            let! byUser = db.UsageByUser(weekStart, 5)
            return renderUsage today week byModel byUser, "usage_shown"
        }

    // ── /sql (also sql_query, AdminOnly NL tool) ────────────────────────────

    /// S3-style troll refusal, Алита-styled — a non-admin gets exactly this, no LLM call.
    [<Literal>]
    let SqlNonAdminRefusal = "Куда лезёшь? SQL-консоль не для тебя."

    /// The `/sql` LLM's JSON contract: {"sql": "..."}.
    let parseSqlJson (json: string) : string option =
        try
            use doc = JsonDocument.Parse(json: string)
            if doc.RootElement.ValueKind <> JsonValueKind.Object then
                None
            else
                match doc.RootElement.TryGetProperty "sql" with
                | true, v when v.ValueKind = JsonValueKind.String && not (String.IsNullOrWhiteSpace(v.GetString())) ->
                    Some(v.GetString())
                | _ -> None
        with _ -> None

    let sqlRequest (conf: BotConfiguration) (question: string) : ChatRequest =
        { Deployment = conf.LlmDeployment
          Messages =
            [ { Role = ChatRole.System
                Content = [ ContentPart.Text conf.SqlPrompt ]
                ToolCalls = []
                ToolCallId = None }
              { Role = ChatRole.User
                Content = [ ContentPart.Text question ]
                ToolCalls = []
                ToolCallId = None } ]
          Tools = []
          Temperature = None
          MaxTokens = None
          ReasoningEffort = None }

    [<Literal>]
    let SqlRowLimit = 50

    [<Literal>]
    let SqlCellMaxLen = 30

    /// Code-block rendering of an /sql result set — one row per line, columns joined
    /// by " | ", each cell capped at SqlCellMaxLen.
    let renderSqlTable (columns: string list) (rows: string[] list) : string =
        let capCell (s: string) = if s.Length > SqlCellMaxLen then s.Substring(0, SqlCellMaxLen - 1) + "…" else s
        let header = columns |> List.map capCell |> String.concat " | "
        let sep = columns |> List.map (fun _ -> "---") |> String.concat " | "
        if rows.IsEmpty then
            "```\n" + header + "\n" + sep + "\n(нет строк)\n```"
        else
            let bodyLines = rows |> List.map (fun r -> r |> Array.map capCell |> String.concat " | ")
            "```\n" + header + "\n" + sep + "\n" + String.concat "\n" bodyLines + "\n```"

    let renderSqlRejected (sql: string) (reason: string) = $"```\n{sql}\n```\nОтклонено: {reason}"

    let renderSqlExecError (sql: string) (err: string) =
        let short = if err.Length > 200 then err.Substring(0, 200) + "…" else err
        $"```\n{sql}\n```\nОшибка: {short}"

    /// `/sql <question>` / `sql_query` (AdminOnly) tool core — ADMIN-GATED (`isAdmin`
    /// passed in by the caller: `Admin.isAdmin conf from.Id` for the command,
    /// `ctx.IsAdmin` for the tool — belt-and-braces even though the tool is never OFFERED
    /// to a non-admin caller in the first place, see ToolRegistry.availableToolDefs). For
    /// an admin, SQL_PROMPT asks the model for a single JSON {"sql": "..."} SELECT/WITH
    /// statement (one retry on malformed JSON), validated by SqlGuard.validate and executed
    /// over a fresh read-only connection wrapped in an outer `SELECT ... LIMIT 50`.
    let sqlCore
        (db: DbService)
        (chat: IChatCompletion)
        (logger: ILogger)
        (conf: BotConfiguration)
        (isAdmin: bool)
        (usageCtx: UsageContext)
        (question: string)
        : Task<string * string> =
        task {
            if not isAdmin then
                return SqlNonAdminRefusal, "sql_refused_non_admin"
            elif String.IsNullOrWhiteSpace question then
                return "Спроси что-нибудь про базу: `/sql сколько сообщений за сегодня?`", "sql_empty_question"
            else
                match! completeJsonWithRetry chat logger usageCtx (sqlRequest conf question) parseSqlJson with
                | None -> return "Не получилось составить запрос 🙁", "sql_generation_failed"
                | Some rawSql ->
                    match SqlGuard.validate rawSql with
                    | Error reason -> return renderSqlRejected rawSql reason, "sql_rejected"
                    | Ok validatedSql ->
                        let wrapped = $"SELECT * FROM ({validatedSql}) AS sql_limited LIMIT {SqlRowLimit}"
                        match! db.ExecuteReadOnlySelect(wrapped) with
                        | Error err -> return renderSqlExecError rawSql err, "sql_exec_failed"
                        | Ok(columns, rows) -> return renderSqlTable columns rows, "sql_delivered"
        }
