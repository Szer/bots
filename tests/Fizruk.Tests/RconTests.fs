module Fizruk.Tests.RconTests

open Fizruk
open Xunit

[<Fact>]
let ``encodePacket then decodePacket round-trips id, type, and body`` () =
    let packet = Rcon.encodePacket 7 Rcon.ServerdataExecCommand "/players online"
    // The leading 4 bytes are the size field; decodePacket takes everything after it.
    let payload = packet.[4..]
    let id, packetType, body = Rcon.decodePacket payload
    Assert.Equal(7, id)
    Assert.Equal(Rcon.ServerdataExecCommand, packetType)
    Assert.Equal("/players online", body)

[<Fact>]
let ``encodePacket size field equals the payload length`` () =
    let packet = Rcon.encodePacket 1 Rcon.ServerdataAuth "secret"
    let size = System.BitConverter.ToInt32(packet, 0)
    Assert.Equal(packet.Length - 4, size)

[<Fact>]
let ``parsePlayersResponse drops the header and returns each name`` () =
    let body = "Online players (2):\nAlice\nBob"
    Assert.Equal<string list>([ "Alice"; "Bob" ], Rcon.parsePlayersResponse body)

[<Fact>]
let ``parsePlayersResponse with zero players returns an empty list`` () =
    let body = "Online players (0):"
    Assert.Equal<string list>([], Rcon.parsePlayersResponse body)

[<Fact>]
let ``parsePlayersResponse tolerates CRLF line endings`` () =
    let body = "Online players (1):\r\nCarol\r\n"
    Assert.Equal<string list>([ "Carol" ], Rcon.parsePlayersResponse body)
