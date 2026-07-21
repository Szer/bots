namespace AlitaBot.RealTests

open System
open System.IO

/// Typed view over ~/.alita-test/env (KEY=VALUE lines). Process env vars override the file.
type RealEnv =
    { NgrokDomain: string
      NgrokApiKey: string
      NgrokAuthtoken: string
      BotToken: string
      BotUsername: string
      WebhookSecret: string
      TestChatId: int64
      TgApiId: string
      TgApiHash: string
      TgPhone: string
      TgSessionPath: string
      AzureFoundryEndpoint: string
      AzureFoundryKey: string
      LlmDeployment: string
      /// echo | llm — forwarded to the spawned bot (default echo).
      ResponderMode: string
      /// draft | edit | plain — forwarded to the spawned bot (default edit, matches prod).
      StreamMode: string }

    /// Bot user id is the numeric prefix of the bot token ("123456:ABC-..." -> 123456).
    member this.BotUserId =
        match this.BotToken.Split(':') with
        | [||] -> 0L
        | parts ->
            match Int64.TryParse parts[0] with
            | true, v -> v
            | _ -> 0L

    /// Everything the webhook plumbing needs (ngrok + bot token + chat id + secret).
    member this.HasCore =
        [ this.NgrokDomain; this.BotToken; this.BotUsername; this.WebhookSecret ]
        |> List.forall (String.IsNullOrWhiteSpace >> not)
        && this.TestChatId <> 0L

    /// MTProto test-user credentials AND a previously saved session (`make tg-login`).
    member this.HasUserClient =
        [ this.TgApiId; this.TgApiHash; this.TgPhone ]
        |> List.forall (String.IsNullOrWhiteSpace >> not)
        && File.Exists this.TgSessionPath

    /// User-client creds present but no session yet — `make tg-login` can create one.
    member this.CanLogin =
        [ this.TgApiId; this.TgApiHash; this.TgPhone ]
        |> List.forall (String.IsNullOrWhiteSpace >> not)

module RealEnv =

    let alitaTestDir =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".alita-test")

    let envFilePath = Path.Combine(alitaTestDir, "env")

    let private fileVars =
        lazy
            (if File.Exists envFilePath then
                 File.ReadAllLines envFilePath
                 |> Array.choose (fun rawLine ->
                     let line = rawLine.Trim()
                     let idx = line.IndexOf '='
                     if line.StartsWith "#" || idx <= 0 then
                         None
                     else
                         Some(line.Substring(0, idx).Trim(), line.Substring(idx + 1).Trim()))
                 |> dict
             else
                 dict [])

    /// Process env wins over the file; empty values count as missing.
    let getVar (key: string) =
        match Environment.GetEnvironmentVariable key with
        | null
        | "" ->
            match fileVars.Value.TryGetValue key with
            | true, v when v <> "" -> Some v
            | _ -> None
        | v -> Some v

    let getVarOr (key: string) (defaultValue: string) =
        getVar key |> Option.defaultValue defaultValue

    let load () =
        { NgrokDomain = getVarOr "ALITA_NGROK_DOMAIN" ""
          NgrokApiKey = getVarOr "ALITA_NGROK_API_KEY" ""
          NgrokAuthtoken = getVarOr "ALITA_NGROK_AUTHTOKEN" ""
          BotToken = getVarOr "ALITA_TEST_BOT_TOKEN" ""
          BotUsername = getVarOr "ALITA_TEST_BOT_USERNAME" ""
          WebhookSecret = getVarOr "ALITA_WEBHOOK_SECRET" ""
          TestChatId =
            match Int64.TryParse(getVarOr "ALITA_TEST_CHAT_ID" "") with
            | true, v -> v
            | _ -> 0L
          TgApiId = getVarOr "ALITA_TG_API_ID" ""
          TgApiHash = getVarOr "ALITA_TG_API_HASH" ""
          TgPhone = getVarOr "ALITA_TG_API_PHONE" ""
          TgSessionPath = getVarOr "ALITA_TG_API_SESSION" (Path.Combine(alitaTestDir, "tg.session"))
          AzureFoundryEndpoint = getVarOr "AZURE_FOUNDRY_ENDPOINT" ""
          AzureFoundryKey = getVarOr "AZURE_FOUNDRY_KEY" ""
          LlmDeployment = getVarOr "ALITA_LLM_DEPLOYMENT" ""
          ResponderMode = getVarOr "RESPONDER_MODE" "echo"
          StreamMode = getVarOr "STREAM_MODE" "edit" }

    /// Repo root = nearest ancestor of the test binary containing bots.slnx.
    let repoRoot =
        let rec findUp (dir: DirectoryInfo) =
            if File.Exists(Path.Combine(dir.FullName, "bots.slnx")) then
                dir.FullName
            else
                match dir.Parent with
                | null -> failwith "Could not locate repo root (bots.slnx) above the test binary"
                | parent -> findUp parent

        findUp (DirectoryInfo AppContext.BaseDirectory)

    /// test-artifacts/AlitaBot.RealTests — bot.log, ngrok.log, etc. Always written.
    let artifactsDir =
        let dir = Path.Combine(repoRoot, "test-artifacts", "AlitaBot.RealTests")
        Directory.CreateDirectory dir |> ignore
        dir
