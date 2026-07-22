namespace AlitaBot.RealTests

open System
open System.IO

/// Typed view over ~/.alita-test/env (KEY=VALUE lines). Process env vars override the file.
type RealEnv =
    { /// local (default, zero behavior change) | remote (CI: bot deployed to AKS,
      /// no ngrok/BotProcess/DevDb-compose — see RealAssemblyFixture).
      Mode: string
      NgrokDomain: string
      NgrokApiKey: string
      NgrokAuthtoken: string
      /// remote mode only — full webhook URL (e.g. https://alita-test.szer.dev/bot),
      /// used verbatim instead of deriving one from NgrokDomain.
      WebhookPublicUrl: string
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
      SttDeployment: string
      TtsDeployment: string
      /// Empty when unconfigured (quota denied at S3 deploy time, see AlitaBot/docs/TECH-DEBT.md)
      /// — ImageGenRealTests self-skips rather than failing.
      ImageDeployment: string
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

    /// CI mode: bot already deployed to AKS (alita-test namespace) by the workflow —
    /// fixture skips NgrokTunnel/BotProcess/DevDb-compose and talks to the public URL.
    member this.IsRemote =
        this.Mode.Trim().Equals("remote", StringComparison.OrdinalIgnoreCase)

    /// The webhook URL Telegram should POST to: ALITA_WEBHOOK_PUBLIC_URL verbatim in
    /// remote mode, or derived from the ngrok domain locally.
    member this.WebhookUrl =
        if this.IsRemote then
            this.WebhookPublicUrl
        else
            $"https://{this.NgrokDomain}/bot"

    /// The public base URL (WebhookUrl with the trailing "/bot" stripped) —
    /// used to derive HealthUrl/ReloadSettingsUrl.
    member private this.PublicBase =
        if this.IsRemote then
            let u = this.WebhookPublicUrl.TrimEnd '/'
            if u.EndsWith "/bot" then u.Substring(0, u.Length - "/bot".Length) else u
        else
            $"https://{this.NgrokDomain}"

    /// The bot's public /healthz — same host as WebhookUrl, "/bot" swapped for "/healthz".
    member this.HealthUrl = this.PublicBase + "/healthz"

    /// Remote mode only: the already-running pod's bot_setting cache is stale
    /// until this is POSTed (X-Telegram-Bot-Api-Secret-Token: WebhookSecret) —
    /// unlike local mode, where DevDb.applyRealSettingsAsync runs *before*
    /// BotProcess ever starts, so a fresh process reads correct settings at
    /// boot and no reload is needed.
    member this.ReloadSettingsUrl = this.PublicBase + "/reload-settings"

    /// Everything the webhook plumbing needs (bot token + chat id + secret, plus
    /// either an ngrok domain (local) or ALITA_WEBHOOK_PUBLIC_URL (remote)).
    member this.HasCore =
        let transport = if this.IsRemote then this.WebhookPublicUrl else this.NgrokDomain

        [ transport; this.BotToken; this.BotUsername; this.WebhookSecret ]
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
        { Mode = getVarOr "ALITA_REAL_MODE" "local"
          NgrokDomain = getVarOr "ALITA_NGROK_DOMAIN" ""
          NgrokApiKey = getVarOr "ALITA_NGROK_API_KEY" ""
          NgrokAuthtoken = getVarOr "ALITA_NGROK_AUTHTOKEN" ""
          WebhookPublicUrl = getVarOr "ALITA_WEBHOOK_PUBLIC_URL" ""
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
          SttDeployment = getVarOr "ALITA_STT_DEPLOYMENT" ""
          TtsDeployment = getVarOr "ALITA_TTS_DEPLOYMENT" ""
          ImageDeployment = getVarOr "ALITA_IMAGE_DEPLOYMENT" ""
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
