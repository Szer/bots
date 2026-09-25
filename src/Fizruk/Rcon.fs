namespace Fizruk

open System
open System.IO
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks

/// Source RCON client: authenticates, runs one console command, returns its output.
/// Packet framing and response parsing are pure so they're unit-testable without a socket.
module Rcon =

    [<Literal>]
    let ServerdataAuth = 3

    [<Literal>]
    let ServerdataExecCommand = 2

    [<Literal>]
    let ServerdataAuthResponse = 2

    [<Literal>]
    let ServerdataResponseValue = 0

    /// Packets read hunting for SERVERDATA_AUTH_RESPONSE — Factorio echoes an
    /// empty SERVERDATA_RESPONSE_VALUE (type 0) first, so the real one is 2nd.
    [<Literal>]
    let MaxAuthPackets = 4

    /// Encodes one RCON packet: int32 size, int32 id, int32 type, body, two NUL bytes.
    let encodePacket (id: int32) (packetType: int32) (body: string) : byte[] =
        let bodyBytes = Encoding.UTF8.GetBytes body
        let payload = Array.zeroCreate<byte> (4 + 4 + bodyBytes.Length + 2)
        Buffer.BlockCopy(BitConverter.GetBytes id, 0, payload, 0, 4)
        Buffer.BlockCopy(BitConverter.GetBytes packetType, 0, payload, 4, 4)
        Buffer.BlockCopy(bodyBytes, 0, payload, 8, bodyBytes.Length)
        Array.append (BitConverter.GetBytes payload.Length) payload

    /// Decodes one RCON packet payload (everything after the leading size field)
    /// into (id, type, body).
    let decodePacket (payload: byte[]) : int32 * int32 * string =
        let id = BitConverter.ToInt32(payload, 0)
        let packetType = BitConverter.ToInt32(payload, 4)
        let bodyLen = max 0 (payload.Length - 4 - 4 - 2)
        let body = Encoding.UTF8.GetString(payload, 8, bodyLen)
        id, packetType, body

    /// Parses "Online players (N):\nname1\nname2" into just the player names,
    /// dropping the header line. Empty (header-only) response yields [].
    let parsePlayersResponse (body: string) : string list =
        let lines =
            body.Replace("\r\n", "\n").Split('\n')
            |> Array.map (fun l -> l.Trim())
            |> Array.filter (fun l -> l <> "")
        match List.ofArray lines with
        | _header :: names -> names
        | [] -> []

    /// Reads packets until SERVERDATA_AUTH_RESPONSE (type 2) or MaxAuthPackets is
    /// exhausted, returning that packet's id (the auth verdict, -1 on failure).
    let private readAuthResponseId (readPacket: unit -> Task<int32 * int32 * string>) : Task<int32> =
        task {
            let mutable i = 0
            let mutable found: int32 option = None
            while found.IsNone && i < MaxAuthPackets do
                let! id, packetType, _ = readPacket ()
                if packetType = ServerdataAuthResponse then found <- Some id
                i <- i + 1
            match found with
            | Some id -> return id
            | None -> return failwith "RCON auth response not received within packet budget"
        }

    /// Runs the auth+exec protocol for one command over any Stream — a real
    /// NetworkStream in production, a fake in-memory one in tests.
    let runCommand (stream: Stream) (password: string) (command: string) (ct: CancellationToken) : Task<Result<string, string>> =
        task {
            let readExact (buf: byte[]) =
                task {
                    let mutable offset = 0
                    while offset < buf.Length do
                        let! n = stream.ReadAsync(buf.AsMemory(offset, buf.Length - offset), ct)
                        if n = 0 then failwith "RCON connection closed by server"
                        offset <- offset + n
                }

            let readPacket () =
                task {
                    let sizeBuf = Array.zeroCreate<byte> 4
                    do! readExact sizeBuf
                    let size = BitConverter.ToInt32(sizeBuf, 0)
                    let payload = Array.zeroCreate<byte> size
                    do! readExact payload
                    return decodePacket payload
                }

            let authPacket = encodePacket 1 ServerdataAuth password
            do! stream.WriteAsync(authPacket.AsMemory(), ct)
            let! authId = readAuthResponseId readPacket
            if authId = -1 then
                return Error "RCON authentication failed"
            else
                let execPacket = encodePacket 2 ServerdataExecCommand command
                do! stream.WriteAsync(execPacket.AsMemory(), ct)
                let! _, _, body = readPacket ()
                return Ok body
        }

    /// "/players online" over any Stream, parsed into player names.
    let runProtocol (stream: Stream) (password: string) (ct: CancellationToken) : Task<Result<string list, string>> =
        task {
            let! result = runCommand stream password "/players online" ct
            return result |> Result.map parsePlayersResponse
        }

    /// Connects and runs one command, mapping timeouts and socket errors to Error.
    let execAsync (host: string) (port: int) (password: string) (command: string) (timeout: TimeSpan) : Task<Result<string, string>> =
        task {
            use cts = new CancellationTokenSource(timeout)
            try
                use client = new TcpClient()
                do! client.ConnectAsync(host, port, cts.Token)
                use stream = client.GetStream()
                return! runCommand stream password command cts.Token
            with
            | :? OperationCanceledException -> return Error "RCON request timed out"
            | ex -> return Error ex.Message
        }

    /// Connects and returns the names from "/players online".
    let probeAsync (host: string) (port: int) (password: string) (timeout: TimeSpan) : Task<Result<string list, string>> =
        task {
            let! result = execAsync host port password "/players online" timeout
            return result |> Result.map parsePlayersResponse
        }

    /// Reads the RCON password from the env var a game's players block names.
    let passwordFor (players: PlayersConfig) : Result<string, string> =
        let envName = players.PasswordEnv |> Option.defaultValue ""
        match Environment.GetEnvironmentVariable envName with
        | null -> Error $"env var {envName} not set"
        | password -> Ok password
