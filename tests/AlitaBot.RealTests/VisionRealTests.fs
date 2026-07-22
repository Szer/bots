namespace AlitaBot.RealTests

open System
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Xunit

/// End-to-end vision test: sends a photo with unambiguous text content ("ALITA 42") as
/// a genuine Telegram photo (not a fake), captioned with a mention + question, and waits
/// for the bot's reply to demonstrate it actually read the image — exercising the full
/// download -> base64 data: URL -> multimodal chat-completion round trip against real
/// Telegram + real Azure (gpt-5-mini, multimodal on this deployment, no re-hosting needed).
module VisionRealTimeouts =
    /// Photo upload + bot download + real vision completion + reply.
    let visionReply = TimeSpan.FromSeconds 120.

module VisionTestImage =
    /// Pre-rendered fallback (committed) for boxes without a working ffmpeg drawtext
    /// (needs libfreetype/libfontconfig) — same text as the live-rendered version below.
    let fixturePath = Path.Combine(RealEnv.repoRoot, "tests", "AlitaBot.RealTests", "fixtures", "alita-42.png")

    let private ffmpegAvailable () =
        try
            let psi =
                ProcessStartInfo(
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true)
            use proc = Process.Start psi
            proc.WaitForExit(5000) |> ignore
            proc.ExitCode = 0
        with _ ->
            false

    /// Renders "ALITA 42" on a white background via ffmpeg's drawtext filter — big,
    /// unambiguous text the LLM should read back verbatim. Falls back to the committed
    /// fixture PNG (same text) when ffmpeg or its drawtext support is unavailable, so
    /// the test still runs on a leaner box.
    let render () : Task<string> =
        task {
            let outPath = Path.Combine(RealEnv.artifactsDir, "vision-test.png")
            if File.Exists outPath then File.Delete outPath

            if not (ffmpegAvailable ()) then
                if not (File.Exists fixturePath) then
                    return failwith $"ffmpeg unavailable and fallback fixture missing: {fixturePath}"
                else
                    return fixturePath
            else
                let psi =
                    ProcessStartInfo(
                        FileName = "ffmpeg",
                        Arguments =
                            "-y -f lavfi -i color=c=white:s=640x360 "
                            + "-vf \"drawtext=text='ALITA 42':fontcolor=black:fontsize=72:font='DejaVu Sans':x=(w-text_w)/2:y=(h-text_h)/2\" "
                            + $"-frames:v 1 -update 1 \"{outPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true)

                use proc = new Process(StartInfo = psi)
                if not (proc.Start()) then
                    return fixturePath
                else
                    let! _stdout = proc.StandardOutput.ReadToEndAsync()
                    let! _stderr = proc.StandardError.ReadToEndAsync()
                    do! proc.WaitForExitAsync()

                    if proc.ExitCode = 0 && File.Exists outPath then
                        return outPath
                    elif File.Exists fixturePath then
                        return fixturePath
                    else
                        return failwith $"ffmpeg drawtext failed (exit {proc.ExitCode}) and fallback fixture missing: {fixturePath}"
        }

type VisionRealTests(fx: RealAssemblyFixture) =
    let env = fx.Env

    [<Fact>]
    member _.``real photo with unambiguous text gets read by the vision-enabled bot``() =
        task {
            fx.SkipUnlessUserClient()

            if env.ResponderMode <> "llm" then
                Assert.Skip "vision test requires RESPONDER_MODE=llm (set RESPONDER_MODE=llm in ~/.alita-test/env or the environment)"

            let! imagePath = VisionTestImage.render ()

            let marker = Guid.NewGuid().ToString "N"
            let caption = $"@{env.BotUsername} что написано на картинке? {marker}"

            let! msgId = fx.UserClient.SendPhoto(env.TestChatId, imagePath, caption)
            let! reply = fx.UserClient.AwaitReplyTo(env.TestChatId, msgId, VisionRealTimeouts.visionReply)

            // STREAM_MODE=edit (the default) sends the first chunk immediately, then keeps
            // editing — `reply.message` here can be an early partial prefix. Wait for edits
            // to go quiet (same idiom as SmokeTests' "streamed reply settles into a final
            // text") before asserting on content, same as the plan's real-Telegram tests do.
            let! finalText = fx.UserClient.AwaitEditsSettled(env.TestChatId, reply.id, Timeouts.editQuiet)

            Assert.False(String.IsNullOrWhiteSpace finalText)
            let lower = finalText.ToLowerInvariant()
            Assert.True(
                lower.Contains "42" || lower.Contains "alita" || lower.Contains "алита",
                $"expected the reply to reference the image's text (\"ALITA 42\"), got: {finalText}")
        }
