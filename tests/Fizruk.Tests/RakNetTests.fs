module Fizruk.Tests.RakNetTests

open System
open System.Buffers.Binary
open System.Text
open Fizruk
open Xunit

/// Hand-builds a realistic unconnected-pong packet, independently of RakNet's own
/// encoder, so decodePong is verified against the wire format.
let private buildPong (serverTime: int64) (serverGuid: int64) (motd: string) : byte[] =
    let motdBytes = Encoding.UTF8.GetBytes motd
    let buf = Array.zeroCreate<byte> (1 + 8 + 8 + 16 + 2 + motdBytes.Length)
    buf.[0] <- RakNet.UnconnectedPongId
    BinaryPrimitives.WriteInt64BigEndian(Span(buf, 1, 8), serverTime)
    BinaryPrimitives.WriteInt64BigEndian(Span(buf, 9, 8), serverGuid)
    Array.blit RakNet.magic 0 buf 17 16
    BinaryPrimitives.WriteUInt16BigEndian(Span(buf, 33, 2), uint16 motdBytes.Length)
    Array.blit motdBytes 0 buf 35 motdBytes.Length
    buf

let private sampleMotd = "MCPE;Fizruk Creative;686;1.21.0;3;10;1234567890;Bedrock level;Creative;1;19132;19133;"

[<Fact>]
let ``encodePing lays out id, timestamp, magic, and client guid`` () =
    let packet = RakNet.encodePing 42L 99L
    Assert.Equal(RakNet.UnconnectedPingId, packet.[0])
    Assert.Equal(42L, BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan(packet, 1, 8)))
    Assert.Equal<byte[]>(RakNet.magic, packet.[9..24])
    Assert.Equal(99L, BinaryPrimitives.ReadInt64BigEndian(ReadOnlySpan(packet, 25, 8)))

[<Fact>]
let ``decodePong parses time, server guid, and motd from a well-formed pong`` () =
    let pong = buildPong 123456789L 987654321L sampleMotd
    match RakNet.decodePong pong with
    | None -> Assert.Fail "expected Some"
    | Some(time, serverGuid, motd) ->
        Assert.Equal(123456789L, time)
        Assert.Equal(987654321L, serverGuid)
        Assert.Equal(sampleMotd, motd)

[<Fact>]
let ``decodePong rejects a packet with the wrong id`` () =
    let pong = buildPong 1L 2L sampleMotd
    pong.[0] <- 0x00uy
    Assert.Equal(None, RakNet.decodePong pong)

[<Fact>]
let ``decodePong rejects a packet with corrupted magic`` () =
    let pong = buildPong 1L 2L sampleMotd
    pong.[20] <- pong.[20] ^^^ 0xFFuy
    Assert.Equal(None, RakNet.decodePong pong)

[<Fact>]
let ``decodePong rejects a packet shorter than the fixed header`` () =
    Assert.Equal(None, RakNet.decodePong (Array.zeroCreate<byte> 10))

[<Fact>]
let ``parseOnlineMax reads fields 5 and 6 of the MCPE motd`` () =
    Assert.Equal(Some(3, 10), RakNet.parseOnlineMax sampleMotd)

[<Fact>]
let ``parseOnlineMax rejects a motd with too few fields`` () =
    Assert.Equal(None, RakNet.parseOnlineMax "MCPE;only;three")

[<Fact>]
let ``end-to-end decode of a built pong feeds parseOnlineMax`` () =
    let pong = buildPong 1L 2L sampleMotd
    let onlineMax = RakNet.decodePong pong |> Option.bind (fun (_, _, motd) -> RakNet.parseOnlineMax motd)
    Assert.Equal(Some(3, 10), onlineMax)
