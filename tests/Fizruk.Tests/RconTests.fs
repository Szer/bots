module Fizruk.Tests.RconTests

open System
open System.IO
open System.Threading
open Fizruk
open Xunit

/// A Stream with independent read/write ends — reads come from a canned buffer,
/// writes land in a scratch one — so Rcon.runProtocol needs no socket to test.
type private DuplexTestStream(readSource: Stream, writeSink: Stream) =
    inherit Stream()
    override _.CanRead = true
    override _.CanWrite = true
    override _.CanSeek = false
    override _.Length = raise (NotSupportedException())
    override _.Position
        with get () = raise (NotSupportedException())
        and set (_) = raise (NotSupportedException())
    override _.Flush() = writeSink.Flush()
    override _.Read(buffer, offset, count) = readSource.Read(buffer, offset, count)
    override _.Write(buffer, offset, count) = writeSink.Write(buffer, offset, count)
    override _.Seek(_offset, _origin) = raise (NotSupportedException())
    override _.SetLength(_value) = raise (NotSupportedException())
    override _.ReadAsync(buffer: Memory<byte>, cancellationToken) = readSource.ReadAsync(buffer, cancellationToken)
    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken) = writeSink.WriteAsync(buffer, cancellationToken)

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

[<Fact>]
let ``runProtocol skips Factorio's empty type-0 auth echo before the real auth response`` () =
    task {
        let echoPacket = Rcon.encodePacket 1 Rcon.ServerdataResponseValue ""
        let authOkPacket = Rcon.encodePacket 1 Rcon.ServerdataAuthResponse ""
        let playersPacket = Rcon.encodePacket 2 Rcon.ServerdataExecCommand "Online players (2):\nAlice\nBob"
        let serverBytes = Array.concat [ echoPacket; authOkPacket; playersPacket ]
        use readSource = new MemoryStream(serverBytes)
        use writeSink = new MemoryStream()
        use duplex = new DuplexTestStream(readSource, writeSink)

        let! result = Rcon.runProtocol duplex "secret" CancellationToken.None

        match result with
        | Ok names -> Assert.Equal<string list>([ "Alice"; "Bob" ], names)
        | Error e -> Assert.Fail $"expected Ok, got Error \"{e}\""
    }

[<Fact>]
let ``runProtocol reports auth failure only once the real auth response id is -1`` () =
    task {
        let echoPacket = Rcon.encodePacket 1 Rcon.ServerdataResponseValue ""
        let authFailPacket = Rcon.encodePacket -1 Rcon.ServerdataAuthResponse ""
        let serverBytes = Array.append echoPacket authFailPacket
        use readSource = new MemoryStream(serverBytes)
        use writeSink = new MemoryStream()
        use duplex = new DuplexTestStream(readSource, writeSink)

        let! result = Rcon.runProtocol duplex "wrong-password" CancellationToken.None

        Assert.Equal(Error "RCON authentication failed", result)
    }
