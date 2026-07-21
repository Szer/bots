module AlitaBot.RealTests.Program

open System
open System.Threading.Tasks
open Xunit

[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
[<assembly: AssemblyFixture(typeof<RealAssemblyFixture>)>]
do ()

/// Console helpers for the one-time MTProto setup (`make tg-login`, `make tg-chats`).
module private TgConsole =

    let private interactivePrompt (key: string) =
        Console.Write $"{key}: "

        match Console.ReadLine() with
        | null -> ""
        | s -> s.Trim()

    let private withClient (action: TgUserClient -> Task<int>) =
        task {
            let env = RealEnv.load ()

            if not env.CanLogin then
                eprintfn "ALITA_TG_API_ID / ALITA_TG_API_HASH / ALITA_TG_API_PHONE missing in %s" RealEnv.envFilePath
                return 1
            else
                use client =
                    new TgUserClient(env.TgApiId, env.TgApiHash, env.TgSessionPath, env.TgPhone, interactivePrompt)

                let! user = client.LoginAsync()
                printfn "Logged in as %s (id %d); session: %s" user.first_name user.id env.TgSessionPath
                return! action client
        }

    /// Interactive first login: prompts for the SMS/app code, saves the session file.
    let login () = withClient (fun _ -> Task.FromResult 0)

    /// Prints every dialog with its Bot API chat id (find the test group id here).
    let listDialogs () =
        withClient (fun client ->
            task {
                let! lines = client.ListDialogsAsync()
                lines |> List.iter (printfn "%s")
                return 0
            })

[<EntryPoint>]
let main argv =
    match argv with
    | [| "login" |] -> TgConsole.login().GetAwaiter().GetResult()
    | [| "list-dialogs" |] -> TgConsole.listDialogs().GetAwaiter().GetResult()
    | [| "selfcheck" |] -> SelfCheck.runAsync().GetAwaiter().GetResult()
    | _ -> Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(argv).GetAwaiter().GetResult()
