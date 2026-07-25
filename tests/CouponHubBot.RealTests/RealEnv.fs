namespace CouponHubBot.RealTests

open System
open System.IO

/// Typed view over ~/.coupon-test/env (KEY=VALUE lines). Process env vars override the
/// file — same convention as AlitaBot.RealTests/RealEnv.fs. Every field name below maps
/// 1:1 to the BINDING "Env contract for the test project" table in the real-test-pipeline
/// contract; do not add/rename vars here without updating that contract first.
///
/// Unlike AlitaBot.RealTests, this harness never spawns the bot itself (no BotProcess, no
/// NgrokTunnel) and never calls Telegram's setWebhook/deleteWebhook (no bot-token env var
/// is in the contract's list for this project — only COUPON_CI_BOT_TOKEN, a GitHub secret
/// consumed directly by the workflow/k8s manifests that own webhook registration). Both
/// `local` and `remote` mode therefore run the exact same fixture path here: seed DB ->
/// wait /healthz -> POST /reload-settings -> log in the MTProto user. `Mode`/`IsRemote` is
/// kept for parity with Alita and for future branching, but nothing below currently reads it.
type RealEnv =
    { /// local (dev loop, e.g. docker-compose smoke profile + ngrok, set up per
      /// src/CouponHubBot/README.dev.md) | remote (CI: bot already deployed to AKS's
      /// coupon-test namespace by the workflow).
      Mode: string
      TestBotUsername: string
      /// COUPON_TEST_CHAT_ID — the separate CI community group (BotConfiguration's
      /// COMMUNITY_CHAT_ID seed value), used ONLY for the membership-gate check
      /// (MembershipService.fs) — commands themselves are all DMs to the bot.
      TestChatId: int64
      TgApiId: string
      TgApiHash: string
      TgPhone: string
      TgSessionPath: string
      /// COUPON_BOT_BASE_URL, e.g. https://coupon-test.szer.dev — base for /healthz,
      /// /reload-settings, /bot and the TEST_MODE-only /test/* hooks.
      BotBaseUrl: string
      /// COUPON_BOT_AUTH_TOKEN — sent as X-Telegram-Bot-Api-Secret-Token on
      /// /reload-settings and (defensively, per the contract) on the /test/* hooks.
      BotAuthToken: string
      /// COUPON_TEST_DB_URL — a ready-to-use Npgsql connection string for the transient
      /// Postgres (runner port-forward :15434 in CI). Passed to NpgsqlConnection as-is;
      /// this project never parses or reconstructs it.
      TestDbUrl: string }

    member this.IsRemote =
        this.Mode.Trim().Equals("remote", StringComparison.OrdinalIgnoreCase)

    member this.HealthUrl = this.BotBaseUrl.TrimEnd('/') + "/healthz"
    member this.ReloadSettingsUrl = this.BotBaseUrl.TrimEnd('/') + "/reload-settings"
    member this.ClockAdvanceUrl = this.BotBaseUrl.TrimEnd('/') + "/test/clock/advance"
    member this.RunReminderUrl = this.BotBaseUrl.TrimEnd('/') + "/test/run-reminder"
    member this.MembershipInvalidateUrl = this.BotBaseUrl.TrimEnd('/') + "/test/membership/invalidate"

    /// Everything needed to reach the bot's HTTP surface and its DB — no MTProto
    /// required. In practice every real test also needs the user client (there is no
    /// other way to drive a DM-only bot), but this mirrors Alita's two-tier skip
    /// structure for a possible future HTTP-only plumbing test.
    member this.HasCore =
        [ this.TestBotUsername; this.BotBaseUrl; this.BotAuthToken; this.TestDbUrl ]
        |> List.forall (String.IsNullOrWhiteSpace >> not)
        && this.TestChatId <> 0L

    /// MTProto test-user credentials AND a previously saved session (`make coupon-tg-login`).
    member this.HasUserClient =
        this.HasCore
        && [ this.TgApiId; this.TgApiHash; this.TgPhone ]
           |> List.forall (String.IsNullOrWhiteSpace >> not)
        && File.Exists this.TgSessionPath

    /// User-client creds present but no session yet — `make coupon-tg-login` can create one.
    member this.CanLogin =
        [ this.TgApiId; this.TgApiHash; this.TgPhone ]
        |> List.forall (String.IsNullOrWhiteSpace >> not)

module RealEnv =

    let couponTestDir =
        Path.Combine(Environment.GetFolderPath Environment.SpecialFolder.UserProfile, ".coupon-test")

    let envFilePath = Path.Combine(couponTestDir, "env")

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
        { Mode = getVarOr "COUPON_REAL_MODE" "local"
          TestBotUsername = getVarOr "COUPON_TEST_BOT_USERNAME" ""
          TestChatId =
            match Int64.TryParse(getVarOr "COUPON_TEST_CHAT_ID" "") with
            | true, v -> v
            | _ -> 0L
          TgApiId = getVarOr "COUPON_TG_API_ID" ""
          TgApiHash = getVarOr "COUPON_TG_API_HASH" ""
          TgPhone = getVarOr "COUPON_TG_API_PHONE" ""
          TgSessionPath = getVarOr "COUPON_TG_SESSION_PATH" (Path.Combine(couponTestDir, "tg.session"))
          BotBaseUrl = getVarOr "COUPON_BOT_BASE_URL" ""
          BotAuthToken = getVarOr "COUPON_BOT_AUTH_TOKEN" ""
          TestDbUrl = getVarOr "COUPON_TEST_DB_URL" "" }

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

    /// tests/CouponHubBot.Ocr.Tests/Images — home of the shared expired-coupon photo
    /// fixture (see AddFlowRealTests.fs's doc comment for why it must stay expired).
    let ocrFixtureImagesDir =
        Path.Combine(repoRoot, "tests", "CouponHubBot.Ocr.Tests", "Images")
