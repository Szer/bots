namespace Fizruk

open System
open System.Buffers.Binary
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks

/// RakNet unconnected-ping/pong for Minecraft Bedrock's UDP status query. Packet
/// framing and response parsing are pure so they're unit-testable without a socket.
module RakNet =

    [<Literal>]
    let UnconnectedPingId = 0x01uy

    [<Literal>]
    let UnconnectedPongId = 0x1Cuy

    /// The fixed RakNet offline-message magic sequence.
    let magic: byte[] =
        [| 0x00uy; 0xffuy; 0xffuy; 0x00uy; 0xfeuy; 0xfeuy; 0xfeuy; 0xfeuy
           0xfduy; 0xfduy; 0xfduy; 0xfduy; 0x12uy; 0x34uy; 0x56uy; 0x78uy |]

    /// Builds an unconnected ping: id, timestamp, magic, client GUID.
    let encodePing (timestamp: int64) (clientGuid: int64) : byte[] =
        let buf = Array.zeroCreate<byte> (1 + 8 + 16 + 8)
        buf.[0] <- UnconnectedPingId
        BinaryPrimitives.WriteInt64BigEndian(Span(buf, 1, 8), timestamp)
        Array.blit magic 0 buf 9 16
        BinaryPrimitives.WriteInt64BigEndian(Span(buf, 25, 8), clientGuid)
        buf

    /// Parses an unconnected pong into (serverTime, serverGuid, motd). None if the
    /// packet is too short, has the wrong id, or the magic doesn't match.
    let decodePong (data: byte[]) : (int64 * int64 * string) option =
        if data.Length < 1 + 8 + 8 + 16 + 2 then None
        elif data.[0] <> UnconnectedPongId then None
        else
            let magicOk = ReadOnlySpan(data, 17, 16).SequenceEqual(ReadOnlySpan magic)
            if not magicOk then None
            else
                let time = BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan(data, 1, 8))
                let serverGuid = BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan(data, 9, 8))
                let strLen = int (BinaryPrimitives.ReadUInt16BigEndian(ReadOnlySpan(data, 33, 2)))
                if data.Length < 35 + strLen then None
                else Some(time, serverGuid, Encoding.UTF8.GetString(data, 35, strLen))

    /// Splits "MCPE;motd;protocol;version;online;max;..." into (online, max).
    let parseOnlineMax (motd: string) : (int * int) option =
        let parts = motd.Split ';'
        if parts.Length < 6 then None
        else
            match Int32.TryParse parts.[4], Int32.TryParse parts.[5] with
            | (true, online), (true, maxPlayers) -> Some(online, maxPlayers)
            | _ -> None

    /// Sends one unconnected ping and parses the pong's online/max player counts.
    let probeAsync (host: string) (port: int) (timeout: TimeSpan) : Task<Result<int * int, string>> =
        task {
            use cts = new CancellationTokenSource(timeout)
            try
                use udp = new UdpClient()
                let ping = encodePing (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) 0L
                let! _ = udp.SendAsync(ReadOnlyMemory ping, host, port, cts.Token)
                let! result = udp.ReceiveAsync cts.Token
                match decodePong result.Buffer with
                | None -> return Error "invalid RakNet pong"
                | Some(_, _, motd) ->
                    match parseOnlineMax motd with
                    | Some(online, maxPlayers) -> return Ok(online, maxPlayers)
                    | None -> return Error $"unparsable MCPE motd: {motd}"
            with
            | :? OperationCanceledException -> return Error "RakNet probe timed out"
            | ex -> return Error ex.Message
        }
