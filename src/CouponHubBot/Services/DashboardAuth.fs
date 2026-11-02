namespace CouponHubBot.Services

open System
open System.Collections.Generic
open System.Globalization
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open BotInfra

module DashboardAuth =
    let maxInitDataLength = 16384

    let validate (botToken: string) (now: DateTimeOffset) (initData: string) : int64 option =
        try
            if String.IsNullOrEmpty initData || initData.Length > maxInitDataLength then
                None
            else
                let fields = SortedDictionary<string, string>(StringComparer.Ordinal)
                for pair in initData.Split('&') do
                    let parts = pair.Split('=', 2)
                    if parts.Length <> 2 then raise (FormatException())
                    let key = WebUtility.UrlDecode parts[0]
                    let value = WebUtility.UrlDecode parts[1]
                    if String.IsNullOrEmpty key || key.Contains('\n') || key.Contains('\r')
                       || value.Contains('\n') || value.Contains('\r') || not (fields.TryAdd(key, value)) then
                        raise (FormatException())

                let suppliedHash = Convert.FromHexString fields["hash"]
                %fields.Remove("hash")
                let checkString = fields |> Seq.map (fun pair -> pair.Key + "=" + pair.Value) |> String.concat "\n"
                let secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes "WebAppData", Encoding.UTF8.GetBytes botToken)
                let expectedHash = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes checkString)
                let signatureValid = CryptographicOperations.FixedTimeEquals(suppliedHash, expectedHash)
                let authDate = Int64.Parse(fields["auth_date"], NumberStyles.None, CultureInfo.InvariantCulture)
                let ageSeconds = now.ToUnixTimeSeconds() - authDate
                if not signatureValid || ageSeconds < -30L || ageSeconds > 300L then
                    None
                else
                    use user = JsonDocument.Parse(fields["user"])
                    let mutable userId = 0L
                    let ids = user.RootElement.EnumerateObject() |> Seq.filter (fun p -> p.Name = "id") |> Seq.toArray
                    if ids.Length = 1 && ids[0].Value.TryGetInt64(&userId) && userId > 0L then Some userId
                    else None
        with
        | :? FormatException | :? KeyNotFoundException | :? JsonException
        | :? InvalidOperationException | :? OverflowException | :? ArgumentException -> None
