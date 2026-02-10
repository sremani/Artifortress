module ObjectStorage

open System
open System.IO
open System.Net
open System.Threading
open System.Threading.Tasks
open Amazon.Runtime
open Amazon.S3
open Amazon.S3.Model
open Microsoft.Extensions.Configuration

type ObjectStorageConfig = {
    Endpoint: string
    AccessKey: string
    SecretKey: string
    Bucket: string
    PresignPartTtlSeconds: int
}

type ObjectStorageError =
    | InvalidRequest of string
    | NotFound of string
    | InvalidRange of string
    | AccessDenied of string
    | TransientFailure of string
    | UnexpectedFailure of string

type MultipartUploadSession = {
    UploadId: string
    ObjectKey: string
    Bucket: string
}

type PresignedPartUpload = {
    Url: Uri
    PartNumber: int
    ExpiresAtUtc: DateTimeOffset
}

type CompletedPart = {
    PartNumber: int
    ETag: string
}

type DownloadedObject = {
    Stream: Stream
    ContentLength: int64
    ContentType: string option
    ETag: string option
    ContentRange: string option
    StatusCode: HttpStatusCode
    Dispose: unit -> unit
}

type IObjectStorageClient =
    abstract member StartMultipartUpload:
        objectKey: string * cancellationToken: CancellationToken -> Task<Result<MultipartUploadSession, ObjectStorageError>>

    abstract member PresignUploadPart:
        objectKey: string *
        uploadId: string *
        partNumber: int *
        expiresAtUtc: DateTimeOffset -> Result<PresignedPartUpload, ObjectStorageError>

    abstract member CompleteMultipartUpload:
        objectKey: string *
        uploadId: string *
        parts: CompletedPart list *
        cancellationToken: CancellationToken -> Task<Result<unit, ObjectStorageError>>

    abstract member AbortMultipartUpload:
        objectKey: string *
        uploadId: string *
        cancellationToken: CancellationToken -> Task<Result<unit, ObjectStorageError>>

    abstract member DownloadObject:
        objectKey: string *
        byteRange: (int64 * int64 option) option *
        cancellationToken: CancellationToken -> Task<Result<DownloadedObject, ObjectStorageError>>

let private normalizeText (value: string) =
    if String.IsNullOrWhiteSpace value then "" else value.Trim()

let private firstNonEmpty (value: string) (fallback: string) =
    if String.IsNullOrWhiteSpace value then fallback else value

let private parsePresignTtlSeconds (raw: string) =
    match Int32.TryParse(raw) with
    | true, parsed when parsed >= 60 && parsed <= 3600 -> parsed
    | _ -> 900

let tryReadConfig (configuration: IConfiguration) =
    let endpoint = configuration.["ObjectStorage:Endpoint"] |> normalizeText |> firstNonEmpty <| "http://localhost:9000"
    let accessKey = configuration.["ObjectStorage:AccessKey"] |> normalizeText |> firstNonEmpty <| "artifortress"
    let secretKey = configuration.["ObjectStorage:SecretKey"] |> normalizeText |> firstNonEmpty <| "artifortress"
    let bucket = configuration.["ObjectStorage:Bucket"] |> normalizeText |> firstNonEmpty <| "artifortress-dev"
    let presignPartTtlSeconds = configuration.["ObjectStorage:PresignPartTtlSeconds"] |> parsePresignTtlSeconds

    Ok
        { Endpoint = endpoint
          AccessKey = accessKey
          SecretKey = secretKey
          Bucket = bucket
          PresignPartTtlSeconds = presignPartTtlSeconds }

type private S3ObjectStorageClient(config: ObjectStorageConfig) =
    let s3Client =
        let s3Config = AmazonS3Config()
        s3Config.ServiceURL <- config.Endpoint
        s3Config.ForcePathStyle <- true
        s3Config.UseHttp <- config.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        s3Config.AuthenticationRegion <- "us-east-1"

        let credentials = BasicAWSCredentials(config.AccessKey, config.SecretKey)
        new AmazonS3Client(credentials, s3Config)

    let mapException (ex: exn) =
        match ex with
        | :? AmazonS3Exception as s3Ex ->
            let message = firstNonEmpty s3Ex.Message "Object storage request failed."

            match s3Ex.StatusCode with
            | HttpStatusCode.NotFound -> NotFound message
            | HttpStatusCode.RequestedRangeNotSatisfiable -> InvalidRange message
            | HttpStatusCode.Forbidden
            | HttpStatusCode.Unauthorized -> AccessDenied message
            | status when int status >= 500 -> TransientFailure message
            | _ ->
                match s3Ex.ErrorCode with
                | "NoSuchUpload"
                | "NoSuchKey"
                | "NotFound" -> NotFound message
                | "InvalidRange" -> InvalidRange message
                | _ -> UnexpectedFailure message
        | :? TaskCanceledException ->
            TransientFailure "Object storage request timed out."
        | _ ->
            UnexpectedFailure(firstNonEmpty ex.Message "Unexpected object storage failure.")

    interface IObjectStorageClient with
        member _.StartMultipartUpload(objectKey: string, cancellationToken: CancellationToken) =
            task {
                let normalizedObjectKey = normalizeText objectKey

                if String.IsNullOrWhiteSpace normalizedObjectKey then
                    return Error(InvalidRequest "Object key is required.")
                else
                    try
                        let request = InitiateMultipartUploadRequest()
                        request.BucketName <- config.Bucket
                        request.Key <- normalizedObjectKey

                        let! response = s3Client.InitiateMultipartUploadAsync(request, cancellationToken)

                        return
                            Ok
                                { UploadId = response.UploadId
                                  ObjectKey = normalizedObjectKey
                                  Bucket = config.Bucket }
                    with ex ->
                        return Error(mapException ex)
            }

        member _.PresignUploadPart(objectKey: string, uploadId: string, partNumber: int, expiresAtUtc: DateTimeOffset) =
            let normalizedObjectKey = normalizeText objectKey
            let normalizedUploadId = normalizeText uploadId

            if String.IsNullOrWhiteSpace normalizedObjectKey then
                Error(InvalidRequest "Object key is required.")
            elif String.IsNullOrWhiteSpace normalizedUploadId then
                Error(InvalidRequest "Upload id is required.")
            elif partNumber < 1 then
                Error(InvalidRequest "Part number must be greater than zero.")
            else
                try
                    let request = GetPreSignedUrlRequest()
                    request.BucketName <- config.Bucket
                    request.Key <- normalizedObjectKey
                    request.UploadId <- normalizedUploadId
                    request.PartNumber <- partNumber
                    request.Verb <- HttpVerb.PUT
                    request.Expires <- expiresAtUtc.UtcDateTime
                    request.Protocol <-
                        if config.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) then
                            Protocol.HTTP
                        else
                            Protocol.HTTPS

                    let signedUrl = s3Client.GetPreSignedURL(request)

                    Ok
                        { Url = Uri(signedUrl)
                          PartNumber = partNumber
                          ExpiresAtUtc = expiresAtUtc }
                with ex ->
                    Error(mapException ex)

        member _.CompleteMultipartUpload
            (objectKey: string, uploadId: string, parts: CompletedPart list, cancellationToken: CancellationToken)
            =
            task {
                let normalizedObjectKey = normalizeText objectKey
                let normalizedUploadId = normalizeText uploadId

                if String.IsNullOrWhiteSpace normalizedObjectKey then
                    return Error(InvalidRequest "Object key is required.")
                elif String.IsNullOrWhiteSpace normalizedUploadId then
                    return Error(InvalidRequest "Upload id is required.")
                elif List.isEmpty parts then
                    return Error(InvalidRequest "At least one completed part is required.")
                else
                    try
                        let request = CompleteMultipartUploadRequest()
                        request.BucketName <- config.Bucket
                        request.Key <- normalizedObjectKey
                        request.UploadId <- normalizedUploadId

                        let partEtags = ResizeArray<PartETag>()

                        parts
                        |> List.sortBy (fun part -> part.PartNumber)
                        |> List.iter (fun part -> partEtags.Add(PartETag(part.PartNumber, part.ETag)))

                        request.PartETags <- partEtags

                        let! _ = s3Client.CompleteMultipartUploadAsync(request, cancellationToken)
                        return Ok()
                    with ex ->
                        return Error(mapException ex)
            }

        member _.AbortMultipartUpload(objectKey: string, uploadId: string, cancellationToken: CancellationToken) =
            task {
                let normalizedObjectKey = normalizeText objectKey
                let normalizedUploadId = normalizeText uploadId

                if String.IsNullOrWhiteSpace normalizedObjectKey then
                    return Error(InvalidRequest "Object key is required.")
                elif String.IsNullOrWhiteSpace normalizedUploadId then
                    return Error(InvalidRequest "Upload id is required.")
                else
                    try
                        let request = AbortMultipartUploadRequest()
                        request.BucketName <- config.Bucket
                        request.Key <- normalizedObjectKey
                        request.UploadId <- normalizedUploadId

                        let! _ = s3Client.AbortMultipartUploadAsync(request, cancellationToken)
                        return Ok()
                    with ex ->
                        return Error(mapException ex)
            }

        member _.DownloadObject(objectKey: string, byteRange: (int64 * int64 option) option, cancellationToken: CancellationToken) =
            task {
                let normalizedObjectKey = normalizeText objectKey

                if String.IsNullOrWhiteSpace normalizedObjectKey then
                    return Error(InvalidRequest "Object key is required.")
                else
                    try
                        let request = GetObjectRequest()
                        request.BucketName <- config.Bucket
                        request.Key <- normalizedObjectKey

                        let parsedRange =
                            match byteRange with
                            | Some(startOffset, Some(endOffset)) when startOffset >= 0L && endOffset >= startOffset ->
                                Ok(Some(ByteRange(startOffset, endOffset)))
                            | Some(startOffset, None) when startOffset >= 0L ->
                                Ok(Some(ByteRange($"bytes={startOffset}-")))
                            | Some _ -> Error "Invalid byte range."
                            | None -> Ok None

                        match parsedRange with
                        | Error err ->
                            return Error(InvalidRequest err)
                        | Ok range ->
                            match range with
                            | Some rangeValue -> request.ByteRange <- rangeValue
                            | None -> ()

                            let! response = s3Client.GetObjectAsync(request, cancellationToken)

                            let contentType =
                                let value = response.Headers.ContentType
                                if String.IsNullOrWhiteSpace value then None else Some value

                            let contentRange =
                                let value = response.Headers["Content-Range"]
                                if String.IsNullOrWhiteSpace value then None else Some value

                            let etag =
                                let value = response.ETag
                                if String.IsNullOrWhiteSpace value then None else Some value

                            return
                                Ok
                                    { Stream = response.ResponseStream
                                      ContentLength = response.Headers.ContentLength
                                      ContentType = contentType
                                      ETag = etag
                                      ContentRange = contentRange
                                      StatusCode = response.HttpStatusCode
                                      Dispose = (fun () -> response.Dispose()) }
                    with ex ->
                        return Error(mapException ex)
            }

let createClient (config: ObjectStorageConfig) : IObjectStorageClient = S3ObjectStorageClient(config) :> IObjectStorageClient
