module ObjectStorageTests

open System
open System.IO
open System.Net.Http
open System.Text
open System.Threading
open ObjectStorage
open Xunit
open Xunit.Sdk

let private readEnvOrDefault key defaultValue =
    match Environment.GetEnvironmentVariable(key) with
    | null
    | "" -> defaultValue
    | value -> value

let private buildClient () =
    let config =
        { Endpoint = readEnvOrDefault "ObjectStorage__Endpoint" "http://localhost:9000"
          AccessKey = readEnvOrDefault "ObjectStorage__AccessKey" "artifortress"
          SecretKey = readEnvOrDefault "ObjectStorage__SecretKey" "artifortress"
          Bucket = readEnvOrDefault "ObjectStorage__Bucket" "artifortress-dev"
          PresignPartTtlSeconds = 900 }

    createClient config

let private ensureMinioAvailable (endpoint: string) =
    use client = new HttpClient()
    client.Timeout <- TimeSpan.FromSeconds(2.0)

    try
        // MinIO often returns 403 at root without auth; that still proves reachability.
        use response = client.GetAsync(Uri(endpoint)).Result
        ignore response.StatusCode
    with ex ->
        raise (SkipException.ForSkip($"Skipping object storage test: MinIO unavailable at {endpoint}. Details: {ex.Message}"))

let private requireOk result =
    match result with
    | Ok value -> value
    | Error err -> failwithf "Object storage operation failed: %A" err

let private toCompletedPartEtag (response: HttpResponseMessage) =
    let headerValue =
        if isNull response.Headers.ETag then
            match response.Headers.TryGetValues("ETag") with
            | true, values -> values |> Seq.tryHead |> Option.defaultValue ""
            | _ -> ""
        else
            response.Headers.ETag.Tag

    let normalized = headerValue.Trim().Trim('"')

    if String.IsNullOrWhiteSpace normalized then
        failwith "ETag header was missing from upload part response."
    else
        normalized

[<Fact>]
let ``Object storage client supports multipart upload and ranged download`` () =
    let endpoint = readEnvOrDefault "ObjectStorage__Endpoint" "http://localhost:9000"
    ensureMinioAvailable endpoint

    let client = buildClient ()
    let ct = CancellationToken.None
    let objectKey = $"phase2-object-storage-test-{Guid.NewGuid():N}.bin"
    let payload = Encoding.UTF8.GetBytes("phase2-object-storage-payload")

    let session = client.StartMultipartUpload(objectKey, ct).Result |> requireOk

    let presignedPart =
        client.PresignUploadPart(session.ObjectKey, session.UploadId, 1, DateTimeOffset.UtcNow.AddMinutes(10.0))
        |> requireOk

    use httpClient = new HttpClient()
    use putContent = new ByteArrayContent(payload)
    use putResponse = httpClient.PutAsync(presignedPart.Url, putContent).Result

    Assert.True(
        putResponse.IsSuccessStatusCode,
        $"Expected successful part upload but received {(int putResponse.StatusCode)}."
    )

    let completedPartEtag = toCompletedPartEtag putResponse

    client.CompleteMultipartUpload(
        session.ObjectKey,
        session.UploadId,
        [ { PartNumber = 1; ETag = completedPartEtag } ],
        ct
    )
    |> fun task -> task.Result
    |> requireOk
    |> ignore

    let downloaded = client.DownloadObject(session.ObjectKey, None, ct).Result |> requireOk

    use fullCopy = new MemoryStream()
    downloaded.Stream.CopyTo(fullCopy)
    downloaded.Dispose()
    Assert.Equal<byte>(payload, fullCopy.ToArray())

    let ranged = client.DownloadObject(session.ObjectKey, Some(3L, Some 8L), ct).Result |> requireOk
    use rangedCopy = new MemoryStream()
    ranged.Stream.CopyTo(rangedCopy)
    ranged.Dispose()

    let expectedRange = payload.[3..8]
    Assert.Equal<byte>(expectedRange, rangedCopy.ToArray())
