open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Xml.Linq
open Artifortress.Domain
open ObjectStorage
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Npgsql
open NpgsqlTypes

[<CLIMutable>]
type CreatePatRequest = {
    Subject: string
    Scopes: string array
    TtlMinutes: int
}

[<CLIMutable>]
type RevokePatRequest = {
    TokenId: Guid
}

[<CLIMutable>]
type CreateRepoRequest = {
    RepoKey: string
    RepoType: string
    UpstreamUrl: string
    MemberRepos: string array
}

[<CLIMutable>]
type UpsertRoleBindingRequest = {
    Roles: string array
}

[<CLIMutable>]
type CreateDraftVersionRequest = {
    PackageType: string
    PackageNamespace: string
    PackageName: string
    Version: string
}

[<CLIMutable>]
type ArtifactEntryRequest = {
    RelativePath: string
    BlobDigest: string
    ChecksumSha1: string
    ChecksumSha256: string
    SizeBytes: int64
}

[<CLIMutable>]
type UpsertArtifactEntriesRequest = {
    Entries: ArtifactEntryRequest array
}

[<CLIMutable>]
type UpsertManifestRequest = {
    Manifest: JsonElement
    ManifestBlobDigest: string
}

[<CLIMutable>]
type TombstoneVersionRequest = {
    Reason: string
    RetentionDays: int
}

[<CLIMutable>]
type RunGcRequest = {
    DryRun: bool
    RetentionGraceHours: int
    BatchSize: int
}

[<CLIMutable>]
type CreateUploadSessionRequest = {
    ExpectedDigest: string
    ExpectedLength: int64
}

[<CLIMutable>]
type CreateUploadPartRequest = {
    PartNumber: int
}

[<CLIMutable>]
type UploadCompletedPartRequest = {
    PartNumber: int
    ETag: string
}

[<CLIMutable>]
type CompleteUploadPartsRequest = {
    Parts: UploadCompletedPartRequest array
}

[<CLIMutable>]
type AbortUploadRequest = {
    Reason: string
}

[<CLIMutable>]
type CommitUploadRequest = {
    // Reserved for future idempotency/proof fields.
    Noop: string
}

[<CLIMutable>]
type EvaluatePolicyRequest = {
    Action: string
    VersionId: Guid
    DecisionHint: string
    Reason: string
    PolicyEngineVersion: string
}

[<CLIMutable>]
type SamlAcsJsonRequest = {
    SAMLResponse: string
    RelayState: string
}

type PatRecord = {
    TokenId: Guid
    Subject: string
    TokenHash: string
    Scopes: RepoScope list
    ExpiresAtUtc: DateTimeOffset
    RevokedAtUtc: DateTimeOffset option
    CreatedAtUtc: DateTimeOffset
}

type RepoRecord = {
    RepoKey: string
    RepoType: string
    UpstreamUrl: string option
    MemberRepos: string list
    CreatedAtUtc: DateTimeOffset
    UpdatedAtUtc: DateTimeOffset
}

type RoleBindingRecord = {
    RepoKey: string
    Subject: string
    Roles: Set<RepoRole>
    UpdatedAtUtc: DateTimeOffset
}

type PackageVersionRecord = {
    VersionId: Guid
    RepoKey: string
    PackageType: string
    PackageNamespace: string option
    PackageName: string
    Version: string
    State: string
    CreatedAtUtc: DateTimeOffset
    PublishedAtUtc: DateTimeOffset option
}

type PackageVersionTargetRecord = {
    VersionId: Guid
    TenantId: Guid
    RepoId: Guid
    RepoKey: string
    PackageType: string
    PackageNamespace: string option
    PackageName: string
    Version: string
    State: string
    PublishedAtUtc: DateTimeOffset option
}

type NormalizedArtifactEntry = {
    RelativePath: string
    BlobDigest: string
    ChecksumSha1: string option
    ChecksumSha256: string option
    SizeBytes: int64
}

type DraftVersionUpsertOutcome =
    | DraftCreated of PackageVersionRecord
    | DraftExisting of PackageVersionRecord
    | VersionStateConflict of string
    | RepoMissing

type UpsertArtifactEntriesOutcome =
    | UpsertEntriesVersionMissing
    | UpsertEntriesStateConflict of string
    | UpsertEntriesBlobMissing of string
    | UpsertEntriesBlobNotCommitted of string
    | UpsertEntriesSuccess of int

type UpsertManifestOutcome =
    | UpsertManifestVersionMissing
    | UpsertManifestStateConflict of string
    | UpsertManifestBlobMissing of string
    | UpsertManifestBlobNotCommitted of string
    | UpsertManifestSuccess of PackageVersionTargetRecord * string option

type ReadManifestOutcome =
    | ReadManifestVersionMissing
    | ReadManifestMissing
    | ReadManifestSuccess of PackageVersionTargetRecord * string option * string

type PublishVersionOutcome =
    | PublishVersionMissing
    | PublishVersionStateConflict of string
    | PublishVersionMissingEntries
    | PublishVersionMissingManifest
    | PublishVersionBlobNotCommitted of string
    | PublishVersionSuccess of PackageVersionTargetRecord * DateTimeOffset * Guid option
    | PublishVersionAlreadyPublished of PackageVersionTargetRecord

type TombstoneVersionOutcome =
    | TombstoneVersionMissing
    | TombstoneVersionStateConflict of string
    | TombstoneVersionSuccess of PackageVersionTargetRecord * DateTimeOffset * DateTimeOffset
    | TombstoneVersionAlreadyTombstoned of PackageVersionTargetRecord * DateTimeOffset option

type GcCandidateBlob = {
    Digest: string
    StorageKey: string
}

type GcRunResult = {
    RunId: Guid
    Mode: string
    MarkedCount: int
    CandidateBlobCount: int
    DeletedBlobCount: int
    DeletedVersionCount: int
    DeleteErrorCount: int
    CandidateDigests: string list
}

type UploadSessionRecord = {
    UploadId: Guid
    RepoKey: string
    ExpectedDigest: string
    ExpectedLength: int64
    State: string
    ObjectStagingKey: string option
    StorageUploadId: string option
    CommittedBlobDigest: string option
    ExpiresAtUtc: DateTimeOffset
    CreatedAtUtc: DateTimeOffset
}

type AuditRecord = {
    AuditId: int64
    TenantId: Guid
    Actor: string
    Action: string
    ResourceType: string
    ResourceId: string
    OccurredAtUtc: DateTimeOffset
    Details: Map<string, string>
}

type PolicyEvaluationResult = {
    EvaluationId: int64
    VersionId: Guid
    Action: string
    Decision: string
    DecisionSource: string
    Reason: string
    PolicyEngineVersion: string option
    EvaluatedAtUtc: DateTimeOffset
    QuarantineId: Guid option
}

type QuarantineItemRecord = {
    QuarantineId: Guid
    RepoKey: string
    VersionId: Guid
    Status: string
    Reason: string
    CreatedAtUtc: DateTimeOffset
    ResolvedAtUtc: DateTimeOffset option
    ResolvedBySubject: string option
}

type QuarantineResolveOutcome =
    | QuarantineResolved of QuarantineItemRecord
    | QuarantineAlreadyResolved of string
    | QuarantineMissing

type Principal = {
    TenantId: Guid
    TokenId: Guid
    Subject: string
    Scopes: RepoScope list
    AuthSource: string
}

type OidcRs256SigningKey = {
    Kid: string option
    Parameters: RSAParameters
}

type OidcClaimRoleMapping = {
    ClaimName: string
    ClaimValue: string
    RepoKey: string
    Role: RepoRole
}

type OidcTokenValidationConfig = {
    Issuer: string
    Audience: string
    Hs256SharedSecret: string option
    Rs256SigningKeys: OidcRs256SigningKey list
    ClaimRoleMappings: OidcClaimRoleMapping list
}

type SamlIntegrationConfig = {
    Enabled: bool
    IdpMetadataUrl: string option
    ServiceProviderEntityId: string option
    ExpectedIssuer: string option
    RoleMappings: OidcClaimRoleMapping list
    IssuedPatTtlMinutes: int
}

type AppState = {
    ConnectionString: string
    TenantSlug: string
    TenantName: string
    ObjectStorageClient: IObjectStorageClient
    PresignPartTtlSeconds: int
    PolicyEvaluationTimeoutMs: int
    DefaultTombstoneRetentionDays: int
    DefaultGcRetentionGraceHours: int
    DefaultGcBatchSize: int
    OidcTokenValidation: OidcTokenValidationConfig option
    SamlIntegration: SamlIntegrationConfig
}

let nowUtc () = DateTimeOffset.UtcNow

let toUtcDateTimeOffset (value: DateTime) =
    if value.Kind = DateTimeKind.Utc then
        DateTimeOffset(value)
    elif value.Kind = DateTimeKind.Unspecified then
        DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc))
    else
        DateTimeOffset(value.ToUniversalTime())

let normalizeText (value: string) =
    if String.IsNullOrWhiteSpace value then "" else value.Trim()

let normalizeRepoKey (repoKey: string) = normalizeText repoKey |> fun value -> value.ToLowerInvariant()
let normalizeSubject (subject: string) = normalizeText subject |> fun value -> value.ToLowerInvariant()
let normalizePackageType (packageType: string) = normalizeText packageType |> fun value -> value.ToLowerInvariant()
let normalizePackageNamespace (packageNamespace: string) = normalizeText packageNamespace |> fun value -> value.ToLowerInvariant()
let normalizePackageName (packageName: string) = normalizeText packageName |> fun value -> value.ToLowerInvariant()

let private isHexChar (ch: char) =
    (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')

let normalizeDigest (digest: string) = normalizeText digest |> fun value -> value.ToLowerInvariant()

let validateUploadSessionRequest (request: CreateUploadSessionRequest) =
    let digest = normalizeDigest request.ExpectedDigest

    if digest.Length <> 64 || not (digest |> Seq.forall isHexChar) then
        Error "expectedDigest must be a 64-character lowercase hex SHA-256 digest."
    elif request.ExpectedLength <= 0L then
        Error "expectedLength must be greater than zero."
    else
        Ok(digest, request.ExpectedLength)

let validateUploadPartRequest (request: CreateUploadPartRequest) =
    if request.PartNumber < 1 then
        Error "partNumber must be greater than zero."
    else
        Ok request.PartNumber

let validateDraftVersionRequest (request: CreateDraftVersionRequest) =
    let packageType = normalizePackageType request.PackageType
    let packageNamespace =
        let value = normalizePackageNamespace request.PackageNamespace
        if String.IsNullOrWhiteSpace value then None else Some value

    let packageName = normalizePackageName request.PackageName
    let version = normalizeText request.Version

    if String.IsNullOrWhiteSpace packageType then
        Error "packageType is required."
    elif String.IsNullOrWhiteSpace packageName then
        Error "packageName is required."
    elif String.IsNullOrWhiteSpace version then
        Error "version is required."
    else
        Ok(packageType, packageNamespace, packageName, version)

let private validateHexValue (fieldName: string) (expectedLength: int) (rawValue: string) =
    let normalized = normalizeDigest rawValue

    if normalized.Length <> expectedLength || not (normalized |> Seq.forall isHexChar) then
        Error $"{fieldName} must be a {expectedLength}-character lowercase hex value."
    else
        Ok normalized

let private validateOptionalHexValue (fieldName: string) (expectedLength: int) (rawValue: string) =
    let normalized = normalizeDigest rawValue

    if String.IsNullOrWhiteSpace normalized then
        Ok None
    else
        validateHexValue fieldName expectedLength normalized |> Result.map Some

let validateArtifactEntriesRequest (request: UpsertArtifactEntriesRequest) =
    let values = if isNull request.Entries then [||] else request.Entries

    if values.Length = 0 then
        Error "entries must contain at least one item."
    else
        let parseResult =
            values
            |> Array.toList
            |> List.fold
                (fun acc value ->
                    match acc with
                    | Error err -> Error err
                    | Ok (entries: NormalizedArtifactEntry list, seenPaths: Set<string>) ->
                        let relativePath = normalizeText value.RelativePath
                        let normalizedPath = relativePath.ToLowerInvariant()

                        if String.IsNullOrWhiteSpace relativePath then
                            Error "relativePath is required for each artifact entry."
                        elif Set.contains normalizedPath seenPaths then
                            Error $"Duplicate relativePath '{relativePath}' is not allowed in a single request."
                        elif value.SizeBytes <= 0L then
                            Error $"sizeBytes for '{relativePath}' must be greater than zero."
                        else
                            match
                                validateHexValue "blobDigest" 64 value.BlobDigest
                                |> Result.bind (fun blobDigest ->
                                    validateOptionalHexValue "checksumSha1" 40 value.ChecksumSha1
                                    |> Result.bind (fun checksumSha1 ->
                                        validateOptionalHexValue "checksumSha256" 64 value.ChecksumSha256
                                        |> Result.map (fun checksumSha256 -> blobDigest, checksumSha1, checksumSha256)))
                            with
                            | Error err -> Error err
                            | Ok(blobDigest, checksumSha1, checksumSha256) ->
                                Ok(
                                    ({ RelativePath = relativePath
                                       BlobDigest = blobDigest
                                       ChecksumSha1 = checksumSha1
                                       ChecksumSha256 = checksumSha256
                                       SizeBytes = value.SizeBytes }
                                     :: entries),
                                    Set.add normalizedPath seenPaths
                                ))
                (Ok(([]: NormalizedArtifactEntry list), Set.empty))

        parseResult |> Result.map (fun (entries, _) -> entries |> List.rev)

let private tryReadRequiredManifestProperty (manifest: JsonElement) (propertyName: string) =
    let mutable prop = Unchecked.defaultof<JsonElement>

    if not (manifest.TryGetProperty(propertyName, &prop)) then
        Error $"manifest.{propertyName} is required for this package type."
    elif prop.ValueKind <> JsonValueKind.String then
        Error $"manifest.{propertyName} must be a string."
    else
        let value = normalizeText (prop.GetString())
        if String.IsNullOrWhiteSpace value then Error $"manifest.{propertyName} cannot be empty." else Ok value

let private validateManifestForPackageType (packageType: string) (manifest: JsonElement) =
    if manifest.ValueKind <> JsonValueKind.Object then
        Error "manifest must be a JSON object."
    else
        match packageType with
        | "nuget" ->
            tryReadRequiredManifestProperty manifest "id"
            |> Result.bind (fun _ -> tryReadRequiredManifestProperty manifest "version")
            |> Result.map (fun _ -> ())
        | "npm" ->
            tryReadRequiredManifestProperty manifest "name"
            |> Result.bind (fun _ -> tryReadRequiredManifestProperty manifest "version")
            |> Result.map (fun _ -> ())
        | "maven" ->
            tryReadRequiredManifestProperty manifest "groupId"
            |> Result.bind (fun _ -> tryReadRequiredManifestProperty manifest "artifactId")
            |> Result.bind (fun _ -> tryReadRequiredManifestProperty manifest "version")
            |> Result.map (fun _ -> ())
        | _ -> Ok ()

let validateManifestRequest (packageType: string) (request: UpsertManifestRequest) =
    let manifestBlobDigestRaw = normalizeText request.ManifestBlobDigest

    let manifestBlobDigestResult =
        if String.IsNullOrWhiteSpace manifestBlobDigestRaw then
            Ok None
        else
            validateHexValue "manifestBlobDigest" 64 manifestBlobDigestRaw |> Result.map Some

    validateManifestForPackageType packageType request.Manifest
    |> Result.bind (fun _ -> manifestBlobDigestResult)
    |> Result.map (fun manifestBlobDigest ->
        let manifestJson = request.Manifest.GetRawText()
        manifestJson, manifestBlobDigest)

let validateTombstoneRequest (defaultRetentionDays: int) (request: TombstoneVersionRequest) =
    let reason = normalizeText request.Reason
    let retentionDays = if request.RetentionDays > 0 then request.RetentionDays else defaultRetentionDays

    if String.IsNullOrWhiteSpace reason then
        Error "reason is required."
    elif retentionDays < 1 || retentionDays > 3650 then
        Error "retentionDays must be between 1 and 3650."
    else
        Ok(reason, retentionDays)

let validateGcRequest
    (defaultRetentionGraceHours: int)
    (defaultBatchSize: int)
    (requestOption: RunGcRequest option)
    =
    match requestOption with
    | None -> Ok(true, defaultRetentionGraceHours, defaultBatchSize)
    | Some request ->
        let retentionGraceHours = request.RetentionGraceHours

        let batchSize =
            if request.BatchSize = 0 then
                defaultBatchSize
            else
                request.BatchSize

        if retentionGraceHours < 0 || retentionGraceHours > 24 * 365 then
            Error "retentionGraceHours must be between 0 and 8760."
        elif batchSize < 1 || batchSize > 5000 then
            Error "batchSize must be between 1 and 5000."
        else
            Ok(request.DryRun, retentionGraceHours, batchSize)

let validateEvaluatePolicyRequest (request: EvaluatePolicyRequest) =
    let action = normalizeText request.Action |> fun value -> value.ToLowerInvariant()
    let versionId = request.VersionId
    let decisionHint = normalizeText request.DecisionHint |> fun value -> value.ToLowerInvariant()
    let reason = normalizeText request.Reason
    let policyEngineVersion =
        let value = normalizeText request.PolicyEngineVersion
        if String.IsNullOrWhiteSpace value then None else Some value

    let resolvedDecisionResult =
        if String.IsNullOrWhiteSpace decisionHint then
            Ok("allow", "default_allow")
        else
            match decisionHint with
            | "allow" -> Ok("allow", "hint_allow")
            | "deny" -> Ok("deny", "hint_deny")
            | "quarantine" -> Ok("quarantine", "hint_quarantine")
            | _ -> Error "decisionHint must be one of: allow, deny, quarantine."

    if action <> "publish" && action <> "promote" then
        Error "action must be one of: publish, promote."
    elif versionId = Guid.Empty then
        Error "versionId is required and must be a non-empty GUID."
    elif String.IsNullOrWhiteSpace reason then
        Error "reason is required."
    else
        resolvedDecisionResult
        |> Result.map (fun (decision, decisionSource) ->
            action, versionId, decision, decisionSource, reason, policyEngineVersion)

let validateQuarantineStatusFilter (statusValue: string) =
    let normalized = normalizeText statusValue |> fun value -> value.ToLowerInvariant()

    if String.IsNullOrWhiteSpace normalized then
        Ok None
    else
        match normalized with
        | "quarantined"
        | "released"
        | "rejected" -> Ok(Some normalized)
        | _ -> Error "status must be one of: quarantined, released, rejected."

let private normalizePartEtag (etag: string) =
    normalizeText etag |> fun value -> value.Trim('"')

let validateCompleteUploadRequest (request: CompleteUploadPartsRequest) =
    let values = if isNull request.Parts then [||] else request.Parts

    if values.Length = 0 then
        Error "At least one completed part is required."
    else
        let parsedResult =
            values
            |> Array.toList
            |> List.fold
                (fun acc value ->
                    match acc with
                    | Error err -> Error err
                    | Ok (parts: CompletedPart list) ->
                        let partNumber = value.PartNumber
                        let etag = normalizePartEtag value.ETag

                        if partNumber < 1 then
                            Error "partNumber must be greater than zero."
                        elif String.IsNullOrWhiteSpace etag then
                            Error "etag is required for each completed part."
                        elif parts |> List.exists (fun part -> part.PartNumber = partNumber) then
                            Error $"Duplicate partNumber '{partNumber}' is not allowed."
                        else
                            Ok(({ PartNumber = partNumber; ETag = etag }: CompletedPart) :: parts))
                (Ok ([]: CompletedPart list))

        parsedResult |> Result.map List.rev

let normalizeAbortReason (reason: string) =
    let value = normalizeText reason
    if String.IsNullOrWhiteSpace value then "client_abort" else value

let parseSingleRangeHeader (rawRangeHeader: string) =
    let headerValue = normalizeText rawRangeHeader

    if String.IsNullOrWhiteSpace headerValue then
        Ok None
    elif not (headerValue.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) then
        Error "Range header must use bytes=<start>-<end> format."
    else
        let spec = headerValue.Substring("bytes=".Length).Trim()

        if spec.Contains(",") then
            Error "Multiple byte ranges are not supported."
        else
            let parts = spec.Split('-', StringSplitOptions.None)

            if parts.Length <> 2 then
                Error "Range header must include a single '-' separator."
            else
                let startText = parts.[0].Trim()
                let endText = parts.[1].Trim()

                if String.IsNullOrWhiteSpace startText then
                    Error "Suffix byte ranges are not supported."
                else
                    match Int64.TryParse(startText) with
                    | false, _ -> Error "Range start must be a non-negative integer."
                    | true, startOffset when startOffset < 0L -> Error "Range start must be non-negative."
                    | true, startOffset ->
                        if String.IsNullOrWhiteSpace endText then
                            Ok(Some(startOffset, None))
                        else
                            match Int64.TryParse(endText) with
                            | false, _ -> Error "Range end must be a non-negative integer."
                            | true, endOffset when endOffset < startOffset ->
                                Error "Range end must be greater than or equal to range start."
                            | true, endOffset -> Ok(Some(startOffset, Some endOffset))

let tryResolvePolicyDecisionWithTimeout
    (timeoutMs: int)
    (decision: string)
    (decisionSource: string)
    (policyEngineVersion: string option)
    =
    let shouldSimulateTimeout =
        match policyEngineVersion with
        | Some value when String.Equals(normalizeText value, "simulate_timeout", StringComparison.OrdinalIgnoreCase) -> true
        | _ -> false

    let operation () =
        if shouldSimulateTimeout then
            Threading.Thread.Sleep(timeoutMs + 50)

        decision, decisionSource

    let task = Threading.Tasks.Task.Run(fun () -> operation ())

    if task.Wait(timeoutMs) then
        Ok task.Result
    else
        Error "Policy evaluation timed out."

let buildStagingObjectKey (tenantId: Guid) (repoKey: string) (uploadId: Guid) =
    $"staging/{tenantId:N}/{repoKey}/{uploadId:N}"

let computeSha256AndLength (stream: IO.Stream) =
    use hasher = SHA256.Create()
    let buffer = Array.zeroCreate<byte> 65536
    let mutable totalBytes = 0L
    let mutable bytesRead = stream.Read(buffer, 0, buffer.Length)

    while bytesRead > 0 do
        hasher.TransformBlock(buffer, 0, bytesRead, null, 0) |> ignore
        totalBytes <- totalBytes + int64 bytesRead
        bytesRead <- stream.Read(buffer, 0, buffer.Length)

    hasher.TransformFinalBlock(Array.empty<byte>, 0, 0) |> ignore

    let digest =
        if isNull hasher.Hash then
            ""
        else
            hasher.Hash |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    digest, totalBytes

let toTokenHash (rawToken: string) =
    use hasher = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(rawToken)
    hasher.ComputeHash(bytes) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

let secureEquals (left: string) (right: string) =
    if String.IsNullOrEmpty left || String.IsNullOrEmpty right then
        false
    else
        let leftBytes = Encoding.UTF8.GetBytes(left)
        let rightBytes = Encoding.UTF8.GetBytes(right)

        if leftBytes.Length <> rightBytes.Length then
            false
        else
            CryptographicOperations.FixedTimeEquals(ReadOnlySpan(leftBytes), ReadOnlySpan(rightBytes))

let private parseBoolean (rawValue: string) =
    match Boolean.TryParse(rawValue) with
    | true, value -> value
    | _ -> false

let private parseBase64UrlBytes (value: string) =
    if String.IsNullOrWhiteSpace value then
        Error "Base64url value was empty."
    else
        try
            let normalized = value.Replace('-', '+').Replace('_', '/')
            let remainder = normalized.Length % 4

            let padded =
                if remainder = 0 then
                    normalized
                elif remainder = 2 then
                    normalized + "=="
                elif remainder = 3 then
                    normalized + "="
                else
                    ""

            if String.IsNullOrWhiteSpace padded then
                Error "Base64url value had invalid padding."
            else
                Ok(Convert.FromBase64String(padded))
        with ex ->
            Error $"Invalid base64url segment: {ex.Message}"

let private tryReadStringClaim (payload: JsonElement) (claimName: string) =
    let mutable claim = Unchecked.defaultof<JsonElement>

    if payload.TryGetProperty(claimName, &claim) && claim.ValueKind = JsonValueKind.String then
        let value = normalizeText (claim.GetString())
        if String.IsNullOrWhiteSpace value then None else Some value
    else
        None

let private tryReadNumericDateClaim (payload: JsonElement) (claimName: string) =
    let mutable claim = Unchecked.defaultof<JsonElement>

    if payload.TryGetProperty(claimName, &claim) then
        match claim.ValueKind with
        | JsonValueKind.Number ->
            match claim.TryGetInt64() with
            | true, value -> Some value
            | _ -> None
        | JsonValueKind.String ->
            match Int64.TryParse(normalizeText (claim.GetString())) with
            | true, value -> Some value
            | _ -> None
        | _ -> None
    else
        None

let private tokenAudienceMatches (payload: JsonElement) (requiredAudience: string) =
    let mutable audienceClaim = Unchecked.defaultof<JsonElement>

    if not (payload.TryGetProperty("aud", &audienceClaim)) then
        false
    else
        match audienceClaim.ValueKind with
        | JsonValueKind.String ->
            String.Equals(audienceClaim.GetString(), requiredAudience, StringComparison.Ordinal)
        | JsonValueKind.Array ->
            audienceClaim.EnumerateArray()
            |> Seq.exists (fun element ->
                element.ValueKind = JsonValueKind.String
                && String.Equals(element.GetString(), requiredAudience, StringComparison.Ordinal))
        | _ -> false

let private extractScopeValuesFromPayload (payload: JsonElement) =
    let splitScopes (rawValue: string) =
        rawValue.Split(' ', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

    let readStringOrArrayClaim (claimName: string) =
        let mutable claim = Unchecked.defaultof<JsonElement>

        if payload.TryGetProperty(claimName, &claim) then
            match claim.ValueKind with
            | JsonValueKind.String -> splitScopes (claim.GetString())
            | JsonValueKind.Array ->
                claim.EnumerateArray()
                |> Seq.choose (fun element ->
                    if element.ValueKind = JsonValueKind.String then
                        Some(normalizeText (element.GetString()))
                    else
                        None)
                |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                |> Seq.toList
            | _ -> []
        else
            []

    [ readStringOrArrayClaim "scope"
      readStringOrArrayClaim "scp"
      readStringOrArrayClaim "artifortress_scopes" ]
    |> List.collect id
    |> List.map normalizeText
    |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
    |> List.distinct

let private parseRepoScopesFromClaimValues (scopeValues: string array) =
    let parsed =
        scopeValues
        |> Array.toList
        |> List.map (fun scopeText -> RepoScope.tryParse scopeText)

    let errors =
        parsed
        |> List.choose (function
            | Error err -> Some err
            | Ok _ -> None)

    if errors.IsEmpty then
        Ok(
            parsed
            |> List.choose (function
                | Ok scope -> Some scope
                | Error _ -> None)
        )
    else
        Error(String.concat "; " errors)

let private extractStringValuesForClaim (payload: JsonElement) (claimName: string) =
    let tryGetClaimElement () =
        let mutable direct = Unchecked.defaultof<JsonElement>

        if payload.TryGetProperty(claimName, &direct) then
            Some direct
        else
            payload.EnumerateObject()
            |> Seq.tryFind (fun prop -> String.Equals(prop.Name, claimName, StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun prop -> prop.Value)

    match tryGetClaimElement () with
    | Some claim ->
        match claim.ValueKind with
        | JsonValueKind.String ->
            let value = normalizeText (claim.GetString())
            if String.IsNullOrWhiteSpace value then [] else [ value ]
        | JsonValueKind.Array ->
            claim.EnumerateArray()
            |> Seq.choose (fun element ->
                if element.ValueKind = JsonValueKind.String then
                    Some(normalizeText (element.GetString()))
                else
                    None)
            |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
            |> Seq.toList
        | _ -> []
    | None -> []

let parseOidcClaimRoleMappings (rawValue: string) =
    let normalized = normalizeText rawValue

    if String.IsNullOrWhiteSpace normalized then
        Ok []
    else
        let entries =
            normalized.Split(';', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        entries
        |> List.fold
            (fun acc entry ->
                match acc with
                | Error err -> Error err
                | Ok mappings ->
                    let parts = entry.Split('|', StringSplitOptions.None)

                    if parts.Length <> 4 then
                        Error
                            $"Invalid OIDC role mapping entry '{entry}'. Expected format: claimName|claimValue|repoKey|role."
                    else
                        let claimName = normalizeText parts.[0] |> fun value -> value.ToLowerInvariant()
                        let claimValue = normalizeText parts.[1]
                        let repoKey = normalizeRepoKey parts.[2]
                        let roleValue = normalizeText parts.[3]

                        if String.IsNullOrWhiteSpace claimName then
                            Error $"Invalid OIDC role mapping entry '{entry}': claimName is required."
                        elif String.IsNullOrWhiteSpace claimValue then
                            Error $"Invalid OIDC role mapping entry '{entry}': claimValue is required."
                        elif String.IsNullOrWhiteSpace repoKey then
                            Error $"Invalid OIDC role mapping entry '{entry}': repoKey is required."
                        else
                            match RepoRole.tryParse roleValue with
                            | Error err -> Error $"Invalid OIDC role mapping entry '{entry}': {err}"
                            | Ok parsedRole ->
                                match RepoScope.tryCreate repoKey parsedRole with
                                | Error err -> Error $"Invalid OIDC role mapping entry '{entry}': {err}"
                                | Ok _ ->
                                    Ok(
                                        { ClaimName = claimName
                                          ClaimValue = claimValue
                                          RepoKey = repoKey
                                          Role = parsedRole }
                                        :: mappings
                                    ))
            (Ok [])
        |> Result.map List.rev

let private extractRepoScopesFromClaimRoleMappings
    (claimRoleMappings: OidcClaimRoleMapping list)
    (payload: JsonElement)
    =
    claimRoleMappings
    |> List.fold
        (fun acc mapping ->
            match acc with
            | Error err -> Error err
            | Ok scopes ->
                let claimValues = extractStringValuesForClaim payload mapping.ClaimName

                let mappingMatched =
                    if String.Equals(mapping.ClaimValue, "*", StringComparison.Ordinal) then
                        not claimValues.IsEmpty
                    else
                        claimValues
                        |> List.exists (fun claimValue ->
                            String.Equals(claimValue, mapping.ClaimValue, StringComparison.OrdinalIgnoreCase))

                if not mappingMatched then
                    Ok scopes
                else
                    match RepoScope.tryCreate mapping.RepoKey mapping.Role with
                    | Ok scope -> Ok(scope :: scopes)
                    | Error err -> Error $"OIDC role mapping is invalid at runtime: {err}")
        (Ok [])
    |> Result.map List.rev

let private deriveDeterministicGuid (value: string) =
    use hasher = SHA256.Create()
    let hash = hasher.ComputeHash(Encoding.UTF8.GetBytes(value))
    hash |> Array.take 16 |> Guid

let private tryReadJwkStringProperty (element: JsonElement) (propertyName: string) =
    let mutable prop = Unchecked.defaultof<JsonElement>

    if element.TryGetProperty(propertyName, &prop) && prop.ValueKind = JsonValueKind.String then
        let value = normalizeText (prop.GetString())
        if String.IsNullOrWhiteSpace value then None else Some value
    else
        None

let parseOidcJwksJson (jwksJson: string) =
    let normalized = normalizeText jwksJson

    if String.IsNullOrWhiteSpace normalized then
        Ok []
    else
        try
            use doc = JsonDocument.Parse(normalized)
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error "OIDC JWKS must be a JSON object."
            else
                let mutable keysElement = Unchecked.defaultof<JsonElement>

                if not (root.TryGetProperty("keys", &keysElement)) || keysElement.ValueKind <> JsonValueKind.Array then
                    Error "OIDC JWKS must include a 'keys' array."
                else
                    let parseResult =
                        keysElement.EnumerateArray()
                        |> Seq.fold
                            (fun acc keyElement ->
                                match acc with
                                | Error err -> Error err
                                | Ok keys ->
                                    if keyElement.ValueKind <> JsonValueKind.Object then
                                        Error "OIDC JWKS key entries must be objects."
                                    else
                                        let kty = tryReadJwkStringProperty keyElement "kty" |> Option.defaultValue ""
                                        let useClaim = tryReadJwkStringProperty keyElement "use"
                                        let nValue = tryReadJwkStringProperty keyElement "n"
                                        let eValue = tryReadJwkStringProperty keyElement "e"
                                        let kid = tryReadJwkStringProperty keyElement "kid"

                                        if not (String.Equals(kty, "RSA", StringComparison.OrdinalIgnoreCase)) then
                                            Ok keys
                                        elif useClaim.IsSome
                                             && not (String.Equals(useClaim.Value, "sig", StringComparison.OrdinalIgnoreCase))
                                        then
                                            Ok keys
                                        else
                                            match nValue, eValue with
                                            | Some modulus, Some exponent ->
                                                parseBase64UrlBytes modulus
                                                |> Result.bind (fun modulusBytes ->
                                                    parseBase64UrlBytes exponent
                                                    |> Result.map (fun exponentBytes -> modulusBytes, exponentBytes))
                                                |> Result.bind (fun (modulusBytes, exponentBytes) ->
                                                    if modulusBytes.Length = 0 || exponentBytes.Length = 0 then
                                                        Error "OIDC JWKS RSA key modulus/exponent cannot be empty."
                                                    else
                                                        let mutable rsaParameters = RSAParameters()
                                                        rsaParameters.Modulus <- modulusBytes
                                                        rsaParameters.Exponent <- exponentBytes

                                                        Ok
                                                            ({ Kid = kid
                                                               Parameters = rsaParameters }
                                                             :: keys))
                                            | _ -> Error "OIDC JWKS RSA keys require both 'n' and 'e' properties.")
                            (Ok [])

                    match parseResult with
                    | Error err -> Error err
                    | Ok keys ->
                        let parsedKeys = keys |> List.rev
                        if parsedKeys.IsEmpty then Error "OIDC JWKS did not contain any usable RSA signing keys." else Ok parsedKeys
        with ex ->
            Error $"OIDC JWKS could not be parsed: {ex.Message}"

let private validateOidcClaims (config: OidcTokenValidationConfig) (payload: JsonElement) =
    let issuer = tryReadStringClaim payload "iss" |> Option.defaultValue ""
    let subject = tryReadStringClaim payload "sub" |> Option.defaultValue ""
    let expirationUnix = tryReadNumericDateClaim payload "exp"
    let notBeforeUnix = tryReadNumericDateClaim payload "nbf"
    let nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()

    if not (String.Equals(issuer, config.Issuer, StringComparison.Ordinal)) then
        Error "OIDC token issuer did not match configured issuer."
    elif not (tokenAudienceMatches payload config.Audience) then
        Error "OIDC token audience did not match configured audience."
    elif String.IsNullOrWhiteSpace subject then
        Error "OIDC token is missing subject claim."
    elif expirationUnix.IsNone || expirationUnix.Value <= nowUnix then
        Error "OIDC token is expired or missing exp claim."
    elif notBeforeUnix.IsSome && notBeforeUnix.Value > nowUnix then
        Error "OIDC token nbf claim is in the future."
    else
        let directScopesResult =
            let scopeValues = extractScopeValuesFromPayload payload |> List.toArray
            if scopeValues.Length = 0 then Ok [] else parseRepoScopesFromClaimValues scopeValues

        let mappedScopesResult = extractRepoScopesFromClaimRoleMappings config.ClaimRoleMappings payload

        match directScopesResult, mappedScopesResult with
        | Error err, _
        | _, Error err -> Error err
        | Ok directScopes, Ok mappedScopes ->
            let resolvedScopes =
                List.append directScopes mappedScopes
                |> List.distinctBy RepoScope.value

            if resolvedScopes.IsEmpty then
                Error "OIDC token does not include any repository scopes and no claim-role mappings matched."
            else
                let tokenId =
                    match tryReadStringClaim payload "jti" with
                    | Some value ->
                        match Guid.TryParse(value) with
                        | true, parsed -> parsed
                        | _ -> deriveDeterministicGuid $"{issuer}:{subject}:{value}"
                    | None -> deriveDeterministicGuid $"{issuer}:{subject}"

                Ok
                    {| Subject = subject
                       TokenId = tokenId
                       Scopes = resolvedScopes |}

let private selectOidcRs256Key (signingKeys: OidcRs256SigningKey list) (kid: string option) =
    if signingKeys.IsEmpty then
        Error "OIDC token uses RS256 but no JWKS signing keys are configured."
    else
        match kid with
        | Some keyId ->
            signingKeys
            |> List.tryFind (fun key -> key.Kid.IsSome && String.Equals(key.Kid.Value, keyId, StringComparison.Ordinal))
            |> function
                | Some key -> Ok key
                | None -> Error "OIDC token kid did not match any configured JWKS key."
        | None ->
            match signingKeys with
            | [ single ] -> Ok single
            | _ -> Error "OIDC token is missing kid and multiple JWKS keys are configured."

let validateOidcBearerToken (config: OidcTokenValidationConfig) (token: string) =
    let segments = token.Split('.', StringSplitOptions.None)

    if segments.Length <> 3 then
        Error "OIDC token must be a compact JWT with three segments."
    else
        let headerSegment = segments.[0]
        let payloadSegment = segments.[1]
        let signatureSegment = segments.[2]
        let signedPayload = $"{headerSegment}.{payloadSegment}"

        parseBase64UrlBytes headerSegment
        |> Result.bind (fun headerBytes ->
            parseBase64UrlBytes payloadSegment
            |> Result.bind (fun payloadBytes ->
                parseBase64UrlBytes signatureSegment
                |> Result.bind (fun signatureBytes ->
                    try
                        use headerDoc = JsonDocument.Parse(headerBytes)
                        use payloadDoc = JsonDocument.Parse(payloadBytes)
                        let header = headerDoc.RootElement
                        let payload = payloadDoc.RootElement
                        let alg = tryReadStringClaim header "alg" |> Option.defaultValue ""
                        let kid = tryReadStringClaim header "kid"

                        let signatureValidationResult =
                            if String.Equals(alg, "HS256", StringComparison.Ordinal) then
                                match config.Hs256SharedSecret with
                                | Some sharedSecret when not (String.IsNullOrWhiteSpace sharedSecret) ->
                                    let expectedSignatureBytes =
                                        use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret))
                                        hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload))

                                    if signatureBytes.Length <> expectedSignatureBytes.Length
                                       || not
                                           (CryptographicOperations.FixedTimeEquals(
                                               ReadOnlySpan(signatureBytes),
                                               ReadOnlySpan(expectedSignatureBytes)
                                           ))
                                    then
                                        Error "OIDC token signature validation failed."
                                    else
                                        Ok()
                                | _ -> Error "OIDC token uses HS256 but no shared secret is configured."
                            elif String.Equals(alg, "RS256", StringComparison.Ordinal) then
                                selectOidcRs256Key config.Rs256SigningKeys kid
                                |> Result.bind (fun signingKey ->
                                    use rsa = RSA.Create()
                                    rsa.ImportParameters(signingKey.Parameters)

                                    let isValid =
                                        rsa.VerifyData(
                                            Encoding.UTF8.GetBytes(signedPayload),
                                            signatureBytes,
                                            HashAlgorithmName.SHA256,
                                            RSASignaturePadding.Pkcs1
                                        )

                                    if isValid then
                                        Ok()
                                    else
                                        Error "OIDC token signature validation failed.")
                            else
                                Error "OIDC token signing algorithm is not supported."

                        signatureValidationResult |> Result.bind (fun _ -> validateOidcClaims config payload)
                    with ex ->
                        Error $"OIDC token payload could not be parsed: {ex.Message}")))

let private xmlEscape (value: string) =
    let escaped = System.Security.SecurityElement.Escape(value)
    if isNull escaped then "" else escaped

let private buildAbsoluteUrl (ctx: HttpContext) (path: string) =
    let host = ctx.Request.Host.Value
    let pathBase = ctx.Request.PathBase.ToString().TrimEnd('/')
    $"{ctx.Request.Scheme}://{host}{pathBase}{path}"

let private buildSamlServiceProviderMetadataXml
    (serviceProviderEntityId: string)
    (assertionConsumerServiceUrl: string)
    (idpMetadataUrl: string option)
    =
    let idpMetadataExtension =
        match idpMetadataUrl with
        | Some url ->
            $"<md:Extensions><af:IdpMetadataUrl xmlns:af=\"urn:artifortress:saml:1.0\">{xmlEscape url}</af:IdpMetadataUrl></md:Extensions>"
        | None -> ""

    $"""<?xml version="1.0" encoding="utf-8"?>
<md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{xmlEscape serviceProviderEntityId}">
  <md:SPSSODescriptor AuthnRequestsSigned="false" WantAssertionsSigned="false" protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
    {idpMetadataExtension}
    <md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:unspecified</md:NameIDFormat>
    <md:AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{xmlEscape assertionConsumerServiceUrl}" index="0" isDefault="true" />
  </md:SPSSODescriptor>
</md:EntityDescriptor>"""

let private readSamlAcsRequest (ctx: HttpContext) =
    if ctx.Request.HasFormContentType then
        try
            let form = ctx.Request.ReadFormAsync().GetAwaiter().GetResult()
            let samlResponse = normalizeText (form.["SAMLResponse"].ToString())
            let relayState = normalizeText (form.["RelayState"].ToString())
            Ok(samlResponse, relayState)
        with ex ->
            Error $"Invalid SAML form body: {ex.Message}"
    else
        try
            let payload = ctx.Request.ReadFromJsonAsync<SamlAcsJsonRequest>().GetAwaiter().GetResult()

            if obj.ReferenceEquals(payload, null) then
                Error "Request body is required."
            else
                Ok(normalizeText payload.SAMLResponse, normalizeText payload.RelayState)
        with ex ->
            Error $"Invalid JSON body: {ex.Message}"

let private validateSamlAcsPayload (rawSamlResponse: string) =
    if String.IsNullOrWhiteSpace rawSamlResponse then
        Error "SAMLResponse is required."
    else
        let decodedBytesResult =
            match parseBase64UrlBytes rawSamlResponse with
            | Ok payloadBytes -> Ok payloadBytes
            | Error _ ->
                try
                    Ok(Convert.FromBase64String(rawSamlResponse))
                with _ ->
                    Error "SAMLResponse must be valid base64 or base64url content."

        decodedBytesResult
        |> Result.bind (fun payloadBytes ->
            if payloadBytes.Length = 0 then
                Error "SAMLResponse cannot be empty after decoding."
            else
                Ok payloadBytes)

type SamlAssertionEnvelope = {
    Issuer: string option
    Subject: string option
    Audience: string option
    Attributes: Map<string, string list>
}

let private tryReadFirstXmlValueByLocalName (doc: XDocument) (localName: string) =
    doc.Descendants()
    |> Seq.tryFind (fun element -> String.Equals(element.Name.LocalName, localName, StringComparison.Ordinal))
    |> Option.map (fun element -> normalizeText element.Value)
    |> Option.filter (fun value -> not (String.IsNullOrWhiteSpace value))

let private readSamlAssertionEnvelope (payloadBytes: byte array) =
    try
        let xmlText = Encoding.UTF8.GetString(payloadBytes)
        let doc = XDocument.Parse(xmlText, LoadOptions.PreserveWhitespace)

        let attributes =
            doc.Descendants()
            |> Seq.filter (fun element -> String.Equals(element.Name.LocalName, "Attribute", StringComparison.Ordinal))
            |> Seq.choose (fun attributeElement ->
                let nameAttribute = attributeElement.Attribute(XName.Get("Name"))

                if isNull nameAttribute then
                    None
                else
                    let name = normalizeText nameAttribute.Value |> fun value -> value.ToLowerInvariant()

                    if String.IsNullOrWhiteSpace name then
                        None
                    else
                        let values =
                            attributeElement.Descendants()
                            |> Seq.filter (fun valueElement ->
                                String.Equals(valueElement.Name.LocalName, "AttributeValue", StringComparison.Ordinal))
                            |> Seq.map (fun valueElement -> normalizeText valueElement.Value)
                            |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                            |> Seq.toList

                        if values.IsEmpty then None else Some(name, values))
            |> Seq.groupBy fst
            |> Seq.map (fun (name, values) ->
                let flattenedValues =
                    values
                    |> Seq.collect snd
                    |> Seq.map normalizeText
                    |> Seq.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                    |> Seq.distinct
                    |> Seq.toList

                name, flattenedValues)
            |> Map.ofSeq

        Ok
            { Issuer = tryReadFirstXmlValueByLocalName doc "Issuer"
              Subject = tryReadFirstXmlValueByLocalName doc "NameID"
              Audience = tryReadFirstXmlValueByLocalName doc "Audience"
              Attributes = attributes }
    with ex ->
        Error $"SAMLResponse could not be parsed as XML: {ex.Message}"

let private splitScopeValuesFromAttribute (rawValue: string) =
    rawValue.Split(' ', StringSplitOptions.TrimEntries ||| StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList

let private extractSamlDirectScopeValues (attributes: Map<string, string list>) =
    [ "scope"; "scp"; "artifortress_scopes" ]
    |> List.collect (fun key ->
        match attributes |> Map.tryFind key with
        | Some values -> values
        | None -> [])
    |> List.collect splitScopeValuesFromAttribute
    |> List.map normalizeText
    |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
    |> List.distinct

let private extractRepoScopesFromSamlRoleMappings
    (roleMappings: OidcClaimRoleMapping list)
    (attributes: Map<string, string list>)
    =
    roleMappings
    |> List.fold
        (fun acc mapping ->
            match acc with
            | Error err -> Error err
            | Ok scopes ->
                let claimValues =
                    attributes
                    |> Map.tryFind mapping.ClaimName
                    |> Option.defaultValue []

                let mappingMatched =
                    if String.Equals(mapping.ClaimValue, "*", StringComparison.Ordinal) then
                        not claimValues.IsEmpty
                    else
                        claimValues
                        |> List.exists (fun claimValue ->
                            String.Equals(claimValue, mapping.ClaimValue, StringComparison.OrdinalIgnoreCase))

                if not mappingMatched then
                    Ok scopes
                else
                    match RepoScope.tryCreate mapping.RepoKey mapping.Role with
                    | Ok scope -> Ok(scope :: scopes)
                    | Error err -> Error $"SAML role mapping is invalid at runtime: {err}")
        (Ok [])
    |> Result.map List.rev

let private resolveSamlScopes (config: SamlIntegrationConfig) (envelope: SamlAssertionEnvelope) =
    let directScopesResult =
        let directScopeValues = extractSamlDirectScopeValues envelope.Attributes
        if directScopeValues.IsEmpty then Ok [] else parseRepoScopesFromClaimValues (directScopeValues |> List.toArray)

    let mappedScopesResult = extractRepoScopesFromSamlRoleMappings config.RoleMappings envelope.Attributes

    match directScopesResult, mappedScopesResult with
    | Error err, _
    | _, Error err -> Error err
    | Ok directScopes, Ok mappedScopes ->
        let resolvedScopes =
            List.append directScopes mappedScopes
            |> List.distinctBy RepoScope.value

        if resolvedScopes.IsEmpty then
            Error "SAML assertion did not contain repository scopes and no role mappings matched."
        else
            Ok resolvedScopes

let private validateSamlAssertion (config: SamlIntegrationConfig) (envelope: SamlAssertionEnvelope) =
    let issuerResult =
        match config.ExpectedIssuer with
        | None -> Ok()
        | Some expectedIssuer ->
            match envelope.Issuer with
            | Some issuer when String.Equals(issuer, expectedIssuer, StringComparison.Ordinal) -> Ok()
            | Some _ -> Error "SAML assertion issuer did not match configured expected issuer."
            | None -> Error "SAML assertion is missing issuer."

    let audienceResult =
        match config.ServiceProviderEntityId with
        | None -> Ok()
        | Some expectedAudience ->
            match envelope.Audience with
            | Some audience when String.Equals(audience, expectedAudience, StringComparison.Ordinal) -> Ok()
            | Some _ -> Error "SAML assertion audience did not match service provider entity id."
            | None -> Error "SAML assertion is missing audience."

    let subjectResult =
        match envelope.Subject with
        | Some subject when not (String.IsNullOrWhiteSpace subject) -> Ok subject
        | _ -> Error "SAML assertion subject (NameID) is missing."

    match issuerResult, audienceResult, subjectResult with
    | Error err, _, _ -> Error err
    | _, Error err, _ -> Error err
    | _, _, Error err -> Error err
    | Ok(), Ok(), Ok subject ->
        resolveSamlScopes config envelope
        |> Result.map (fun scopes -> subject, scopes)

let createPlainToken () =
    let bytes = RandomNumberGenerator.GetBytes(32)
    Convert.ToHexString(bytes).ToLowerInvariant()

let badRequest message =
    Results.Json({| error = "bad_request"; message = message |}, statusCode = StatusCodes.Status400BadRequest)

let unauthorized message =
    Results.Json({| error = "unauthorized"; message = message |}, statusCode = StatusCodes.Status401Unauthorized)

let forbidden message =
    Results.Json({| error = "forbidden"; message = message |}, statusCode = StatusCodes.Status403Forbidden)

let conflict message =
    Results.Json({| error = "conflict"; message = message |}, statusCode = StatusCodes.Status409Conflict)

let serviceUnavailable message =
    Results.Json(
        {| error = "service_unavailable"
           message = message |},
        statusCode = StatusCodes.Status503ServiceUnavailable
    )

let mapObjectStorageErrorToResult err =
    match err with
    | InvalidRequest message -> badRequest message
    | NotFound message ->
        Results.Json({| error = "object_not_found"; message = message |}, statusCode = StatusCodes.Status404NotFound)
    | InvalidRange message ->
        Results.Json({| error = "invalid_range"; message = message |}, statusCode = StatusCodes.Status416RequestedRangeNotSatisfiable)
    | AccessDenied message ->
        Results.Json({| error = "storage_access_denied"; message = message |}, statusCode = StatusCodes.Status503ServiceUnavailable)
    | TransientFailure message -> serviceUnavailable message
    | UnexpectedFailure message -> serviceUnavailable message

let tryReadBearerToken (ctx: HttpContext) =
    let header = ctx.Request.Headers.Authorization.ToString()

    if header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) then
        let token = header.Substring("Bearer ".Length).Trim()
        if String.IsNullOrWhiteSpace token then None else Some token
    else
        None

let parseScopes (rawScopes: string array) =
    let values = if isNull rawScopes then [||] else rawScopes

    let parsed =
        values
        |> Array.toList
        |> List.map (fun scopeText -> RepoScope.tryParse scopeText)

    let errors =
        parsed
        |> List.choose (function
            | Error err -> Some err
            | Ok _ -> None)

    if errors.IsEmpty then
        Ok(
            parsed
            |> List.choose (function
                | Ok scope -> Some scope
                | Error _ -> None)
        )
    else
        Error(String.concat "; " errors)

let parseRoles (rawRoles: string array) =
    let values = if isNull rawRoles then [||] else rawRoles

    if values.Length = 0 then
        Error "At least one role is required."
    else
        let parsed =
            values
            |> Array.toList
            |> List.map (fun roleText -> RepoRole.tryParse roleText)

        let errors =
            parsed
            |> List.choose (function
                | Error err -> Some err
                | Ok _ -> None)

        if errors.IsEmpty then
            Ok(
                parsed
                |> List.choose (function
                    | Ok role -> Some role
                    | Error _ -> None)
                |> Set.ofList
            )
        else
            Error(String.concat "; " errors)

let readJsonBody<'T> (ctx: HttpContext) =
    try
        let payload = ctx.Request.ReadFromJsonAsync<'T>().GetAwaiter().GetResult()

        if obj.ReferenceEquals(payload, null) then
            Error "Request body is required."
        else
            Ok payload
    with ex ->
        Error $"Invalid JSON body: {ex.Message}"

let readOptionalJsonBody<'T> (ctx: HttpContext) =
    let hasBody =
        (ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > 0L)
        || ctx.Request.Headers.ContainsKey("Transfer-Encoding")
        || (ctx.Request.Body.CanSeek && ctx.Request.Body.Length > 0L)

    if hasBody then
        readJsonBody<'T> ctx |> Result.map Some
    else
        Ok None

let withConnection (state: AppState) (handler: NpgsqlConnection -> Result<'T, string>) : Result<'T, string> =
    try
        use conn = new NpgsqlConnection(state.ConnectionString)
        conn.Open()
        handler conn
    with ex ->
        Error $"Database operation failed: {ex.Message}"

let private describeObjectStorageError (err: ObjectStorageError) =
    match err with
    | InvalidRequest message
    | NotFound message
    | InvalidRange message
    | AccessDenied message
    | TransientFailure message
    | UnexpectedFailure message -> message

let private checkPostgresReadiness (state: AppState) =
    match
        withConnection state (fun conn ->
            use cmd = new NpgsqlCommand("select 1;", conn)
            cmd.ExecuteScalar() |> ignore
            Ok())
    with
    | Ok () -> true, None
    | Error err -> false, Some err

let private checkObjectStorageReadiness (state: AppState) (cancellationToken: Threading.CancellationToken) =
    task {
        use timeoutCts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds(3.0))
        use linkedCts = Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)

        try
            let! checkResult = state.ObjectStorageClient.CheckAvailability(linkedCts.Token)

            match checkResult with
            | Ok () -> return true, None
            | Error err -> return false, Some(describeObjectStorageError err)
        with
        | :? OperationCanceledException when timeoutCts.IsCancellationRequested && not cancellationToken.IsCancellationRequested ->
            return false, Some "Object storage readiness check timed out."
        | ex ->
            let message =
                if String.IsNullOrWhiteSpace ex.Message then
                    "Object storage readiness check failed."
                else
                    ex.Message

            return false, Some message
    }

let ensureTenantId (state: AppState) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
insert into tenants (slug, name)
values (@slug, @name)
on conflict (slug) do update set name = excluded.name
returning tenant_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("slug", state.TenantSlug) |> ignore
    cmd.Parameters.AddWithValue("name", state.TenantName) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Error "Failed to resolve tenant id."
    else
        Ok(scalar :?> Guid)

let hasAnyTokensForTenant (tenantId: Guid) (conn: NpgsqlConnection) =
    use cmd = new NpgsqlCommand("select exists(select 1 from personal_access_tokens where tenant_id = @tenant_id);", conn)
    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as existsValue -> Ok existsValue
    | _ -> Error "Could not determine token existence."

let tryReadPrincipalByTokenHash (tokenHash: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select token_id, tenant_id, subject, scopes
from personal_access_tokens
where token_hash = @token_hash
  and revoked_at is null
  and expires_at > now()
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("token_hash", tokenHash) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let tokenId = reader.GetGuid(0)
        let tenantId = reader.GetGuid(1)
        let subject = reader.GetString(2)
        let rawScopes = reader.GetFieldValue<string[]>(3)

        match parseScopes rawScopes with
        | Ok scopes ->
            Ok(
                Some
                    { TenantId = tenantId
                      TokenId = tokenId
                      Subject = subject
                      Scopes = scopes
                      AuthSource = "pat" }
            )
        | Error err -> Error $"Persisted scopes are invalid: {err}"
    else
        Ok None

let insertPat
    (tenantId: Guid)
    (subject: string)
    (tokenId: Guid)
    (tokenHash: string)
    (scopes: RepoScope list)
    (expiresAtUtc: DateTimeOffset)
    (createdBy: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into personal_access_tokens
  (token_id, tenant_id, subject, token_hash, scopes, expires_at, created_by_subject)
values
  (@token_id, @tenant_id, @subject, @token_hash, @scopes, @expires_at, @created_by_subject);
""",
            conn
        )

    let scopeValues = scopes |> List.map RepoScope.value |> List.toArray
    cmd.Parameters.AddWithValue("token_id", tokenId) |> ignore
    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("subject", subject) |> ignore
    cmd.Parameters.AddWithValue("token_hash", tokenHash) |> ignore

    let scopesParam = cmd.Parameters.Add("scopes", NpgsqlDbType.Array ||| NpgsqlDbType.Text)
    scopesParam.Value <- scopeValues

    cmd.Parameters.AddWithValue("expires_at", expiresAtUtc) |> ignore
    cmd.Parameters.AddWithValue("created_by_subject", createdBy) |> ignore

    let rows = cmd.ExecuteNonQuery()
    if rows = 1 then Ok() else Error "Token insert did not affect expected rows."

let revokePat (tenantId: Guid) (tokenId: Guid) (revokedBy: string) (conn: NpgsqlConnection) =
    use tx = conn.BeginTransaction()

    use updateCmd =
        new NpgsqlCommand(
            """
update personal_access_tokens
set revoked_at = now()
where tenant_id = @tenant_id
  and token_id = @token_id
  and revoked_at is null
returning token_id;
""",
            conn,
            tx
        )

    updateCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    updateCmd.Parameters.AddWithValue("token_id", tokenId) |> ignore

    let updatedToken = updateCmd.ExecuteScalar()

    if isNull updatedToken || updatedToken = box DBNull.Value then
        tx.Rollback()
        Ok false
    else
        use revocationCmd =
            new NpgsqlCommand(
                """
insert into token_revocations (tenant_id, token_id, revoked_by_subject)
values (@tenant_id, @token_id, @revoked_by_subject);
""",
                conn,
                tx
            )

        revocationCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        revocationCmd.Parameters.AddWithValue("token_id", tokenId) |> ignore
        revocationCmd.Parameters.AddWithValue("revoked_by_subject", revokedBy) |> ignore
        revocationCmd.ExecuteNonQuery() |> ignore

        tx.Commit()
        Ok true

let serializeRepoConfig (repo: RepoRecord) =
    match repo.RepoType with
    | "remote" ->
        JsonSerializer.Serialize(
            {| upstreamUrl =
                match repo.UpstreamUrl with
                | Some value -> value
                | None -> "" |}
        )
    | "virtual" -> JsonSerializer.Serialize({| memberRepos = repo.MemberRepos |> List.toArray |})
    | _ -> "{}"

let tryReadRepoConfig (repoType: string) (configJson: string) =
    try
        let jsonText = if String.IsNullOrWhiteSpace configJson then "{}" else configJson
        use doc = JsonDocument.Parse(jsonText)
        let root = doc.RootElement

        match repoType with
        | "remote" ->
            let mutable upstreamProp = Unchecked.defaultof<JsonElement>

            if root.TryGetProperty("upstreamUrl", &upstreamProp) then
                let upstream = upstreamProp.GetString()
                if String.IsNullOrWhiteSpace upstream then Ok(None, [])
                else Ok(Some upstream, [])
            else
                Ok(None, [])
        | "virtual" ->
            let mutable membersProp = Unchecked.defaultof<JsonElement>

            if root.TryGetProperty("memberRepos", &membersProp) && membersProp.ValueKind = JsonValueKind.Array then
                let members =
                    membersProp.EnumerateArray()
                    |> Seq.choose (fun item ->
                        let value = item.GetString()
                        if String.IsNullOrWhiteSpace value then None else Some(normalizeRepoKey value))
                    |> Seq.distinct
                    |> Seq.toList

                Ok(None, members)
            else
                Ok(None, [])
        | _ -> Ok(None, [])
    with ex ->
        Error $"Invalid repository config json: {ex.Message}"

let readReposForTenant (tenantId: Guid) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select repo_key, repo_type, config::text, created_at
from repos
where tenant_id = @tenant_id
order by repo_key;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore

    use reader = cmd.ExecuteReader()

    let rec loop acc =
        if reader.Read() then
            let repoKey = reader.GetString(0)
            let repoType = reader.GetString(1)
            let configJson = if reader.IsDBNull(2) then "{}" else reader.GetString(2)
            let createdAtUtc = reader.GetFieldValue<DateTimeOffset>(3)

            match tryReadRepoConfig repoType configJson with
            | Error err -> Error err
            | Ok(upstreamUrl, memberRepos) ->
                let repo =
                    { RepoKey = repoKey
                      RepoType = repoType
                      UpstreamUrl = upstreamUrl
                      MemberRepos = memberRepos
                      CreatedAtUtc = createdAtUtc
                      UpdatedAtUtc = createdAtUtc }

                loop (repo :: acc)
        else
            Ok(List.rev acc)

    loop []

let tryReadRepoByKey (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select repo_key, repo_type, config::text, created_at
from repos
where tenant_id = @tenant_id
  and repo_key = @repo_key
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let persistedRepoKey = reader.GetString(0)
        let repoType = reader.GetString(1)
        let configJson = if reader.IsDBNull(2) then "{}" else reader.GetString(2)
        let createdAtUtc = reader.GetFieldValue<DateTimeOffset>(3)

        match tryReadRepoConfig repoType configJson with
        | Error err -> Error err
        | Ok(upstreamUrl, memberRepos) ->
            Ok(
                Some
                    { RepoKey = persistedRepoKey
                      RepoType = repoType
                      UpstreamUrl = upstreamUrl
                      MemberRepos = memberRepos
                      CreatedAtUtc = createdAtUtc
                      UpdatedAtUtc = createdAtUtc }
            )
    else
        Ok None

let insertRepoForTenant (tenantId: Guid) (repo: RepoRecord) (conn: NpgsqlConnection) =
    try
        use cmd =
            new NpgsqlCommand(
                """
insert into repos (tenant_id, repo_key, repo_type, config)
values (@tenant_id, @repo_key, @repo_type, @config);
""",
                conn
            )

        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        cmd.Parameters.AddWithValue("repo_key", repo.RepoKey) |> ignore
        cmd.Parameters.AddWithValue("repo_type", repo.RepoType) |> ignore

        let configParam = cmd.Parameters.Add("config", NpgsqlDbType.Jsonb)
        configParam.Value <- serializeRepoConfig repo

        let rows = cmd.ExecuteNonQuery()
        Ok(rows = 1)
    with :? PostgresException as ex when ex.SqlState = "23505" ->
        Ok false

let repoExistsForTenant (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select exists(
  select 1
  from repos
  where tenant_id = @tenant_id
    and repo_key = @repo_key
);
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as existsValue -> Ok existsValue
    | _ -> Error "Could not determine repository existence."

let tryReadRepoIdForTenant (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select repo_id
from repos
where tenant_id = @tenant_id
  and repo_key = @repo_key
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Ok None
    else
        match scalar with
        | :? Guid as repoId -> Ok(Some repoId)
        | _ -> Error "Unexpected repository id type returned from database."

let upsertPackageForRepo
    (tenantId: Guid)
    (repoId: Guid)
    (packageType: string)
    (packageNamespace: string option)
    (packageName: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into packages (tenant_id, repo_id, package_type, namespace, name)
values (@tenant_id, @repo_id, @package_type, @namespace, @name)
on conflict (repo_id, package_type, coalesce(namespace, ''), name)
do update set name = excluded.name
returning package_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
    cmd.Parameters.AddWithValue("package_type", packageType) |> ignore

    let namespaceParam = cmd.Parameters.Add("namespace", NpgsqlDbType.Text)
    namespaceParam.Value <- (match packageNamespace with | Some value -> box value | None -> box DBNull.Value)

    cmd.Parameters.AddWithValue("name", packageName) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Error "Package upsert did not return package id."
    else
        match scalar with
        | :? Guid as packageId -> Ok packageId
        | _ -> Error "Unexpected package id type returned from database."

let tryInsertDraftPackageVersion
    (tenantId: Guid)
    (repoId: Guid)
    (packageId: Guid)
    (version: string)
    (createdBy: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into package_versions (tenant_id, repo_id, package_id, version, state, created_by_subject)
values (@tenant_id, @repo_id, @package_id, @version, 'draft', @created_by_subject)
on conflict (repo_id, package_id, version) do nothing
returning version_id, state, created_at, published_at;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
    cmd.Parameters.AddWithValue("package_id", packageId) |> ignore
    cmd.Parameters.AddWithValue("version", version) |> ignore
    cmd.Parameters.AddWithValue("created_by_subject", createdBy) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let versionId = reader.GetGuid(0)
        let stateValue = reader.GetString(1)
        let createdAt = reader.GetFieldValue<DateTime>(2) |> toUtcDateTimeOffset
        let publishedAt =
            if reader.IsDBNull(3) then None else Some(reader.GetFieldValue<DateTime>(3) |> toUtcDateTimeOffset)

        Ok(Some(versionId, stateValue, createdAt, publishedAt))
    else
        Ok None

let tryReadPackageVersionForRepo
    (repoId: Guid)
    (packageId: Guid)
    (version: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
select version_id, state, created_at, published_at
from package_versions
where repo_id = @repo_id
  and package_id = @package_id
  and version = @version
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
    cmd.Parameters.AddWithValue("package_id", packageId) |> ignore
    cmd.Parameters.AddWithValue("version", version) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let versionId = reader.GetGuid(0)
        let stateValue = reader.GetString(1)
        let createdAt = reader.GetFieldValue<DateTime>(2) |> toUtcDateTimeOffset
        let publishedAt =
            if reader.IsDBNull(3) then None else Some(reader.GetFieldValue<DateTime>(3) |> toUtcDateTimeOffset)

        Ok(Some(versionId, stateValue, createdAt, publishedAt))
    else
        Ok None

let createOrGetDraftVersionForRepo
    (tenantId: Guid)
    (repoKey: string)
    (packageType: string)
    (packageNamespace: string option)
    (packageName: string)
    (version: string)
    (createdBy: string)
    (conn: NpgsqlConnection)
    =
    let toRecord (versionId, stateValue, createdAtUtc, publishedAtUtc) =
        { VersionId = versionId
          RepoKey = repoKey
          PackageType = packageType
          PackageNamespace = packageNamespace
          PackageName = packageName
          Version = version
          State = stateValue
          CreatedAtUtc = createdAtUtc
          PublishedAtUtc = publishedAtUtc }

    tryReadRepoIdForTenant tenantId repoKey conn
    |> Result.bind (fun repoIdOption ->
        match repoIdOption with
        | None -> Ok RepoMissing
        | Some repoId ->
            upsertPackageForRepo tenantId repoId packageType packageNamespace packageName conn
            |> Result.bind (fun packageId ->
                tryInsertDraftPackageVersion tenantId repoId packageId version createdBy conn
                |> Result.bind (fun insertResult ->
                    match insertResult with
                    | Some insertedVersion ->
                        insertedVersion |> toRecord |> DraftCreated |> Ok
                    | None ->
                        tryReadPackageVersionForRepo repoId packageId version conn
                        |> Result.bind (fun existing ->
                            match existing with
                            | None -> Error "Could not resolve package version after upsert."
                            | Some existingVersion ->
                                let record = toRecord existingVersion
                                if String.Equals(record.State, "draft", StringComparison.Ordinal) then
                                    Ok(DraftExisting record)
                                else
                                    Ok(VersionStateConflict record.State)))))

let private tryReadPackageVersionTargetForRepoInternal
    (tenantId: Guid)
    (repoKey: string)
    (versionId: Guid)
    (forUpdate: bool)
    (conn: NpgsqlConnection)
    (tx: NpgsqlTransaction option)
    =
    let baseSql =
        """
select pv.version_id,
       pv.tenant_id,
       pv.repo_id,
       r.repo_key,
       p.package_type,
       p.namespace,
       p.name,
       pv.version,
       pv.state,
       pv.published_at
from package_versions pv
join repos r on r.repo_id = pv.repo_id and r.tenant_id = pv.tenant_id
join packages p on p.package_id = pv.package_id
where pv.tenant_id = @tenant_id
  and r.repo_key = @repo_key
  and pv.version_id = @version_id
"""

    let sql = if forUpdate then $"{baseSql}for update;" else $"{baseSql}limit 1;"
    use cmd = new NpgsqlCommand(sql, conn)

    match tx with
    | Some transaction -> cmd.Transaction <- transaction
    | None -> ()

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("version_id", versionId) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let target =
            { VersionId = reader.GetGuid(0)
              TenantId = reader.GetGuid(1)
              RepoId = reader.GetGuid(2)
              RepoKey = reader.GetString(3)
              PackageType = reader.GetString(4)
              PackageNamespace = if reader.IsDBNull(5) then None else Some(reader.GetString(5))
              PackageName = reader.GetString(6)
              Version = reader.GetString(7)
              State = reader.GetString(8)
              PublishedAtUtc = if reader.IsDBNull(9) then None else Some(reader.GetFieldValue<DateTime>(9) |> toUtcDateTimeOffset) }

        Ok(Some target)
    else
        Ok None

let tryReadPackageVersionTargetForRepo (tenantId: Guid) (repoKey: string) (versionId: Guid) (conn: NpgsqlConnection) =
    tryReadPackageVersionTargetForRepoInternal tenantId repoKey versionId false conn None

let private tryReadLockedPackageVersionTargetForRepo
    (tenantId: Guid)
    (repoKey: string)
    (versionId: Guid)
    (conn: NpgsqlConnection)
    (tx: NpgsqlTransaction)
    =
    tryReadPackageVersionTargetForRepoInternal tenantId repoKey versionId true conn (Some tx)

let private blobExistsByDigestInTransaction (digest: string) (conn: NpgsqlConnection) (tx: NpgsqlTransaction) =
    use cmd = new NpgsqlCommand("select exists(select 1 from blobs where digest = @digest);", conn, tx)
    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as existsValue -> Ok existsValue
    | _ -> Error "Could not determine blob existence."

let private repoHasCommittedDigestInTransaction (tenantId: Guid) (repoId: Guid) (digest: string) (conn: NpgsqlConnection) (tx: NpgsqlTransaction) =
    use cmd =
        new NpgsqlCommand(
            """
select exists(
  select 1
  from upload_sessions
  where tenant_id = @tenant_id
    and repo_id = @repo_id
    and state = 'committed'
    and committed_blob_digest = @digest
);
""",
            conn,
            tx
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as existsValue -> Ok existsValue
    | _ -> Error "Could not determine committed blob visibility for repository."

let private upsertArtifactEntryForVersionInTransaction
    (versionId: Guid)
    (entry: NormalizedArtifactEntry)
    (conn: NpgsqlConnection)
    (tx: NpgsqlTransaction)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into artifact_entries
  (version_id, relative_path, blob_digest, checksum_sha1, checksum_sha256, size_bytes)
values
  (@version_id, @relative_path, @blob_digest, @checksum_sha1, @checksum_sha256, @size_bytes)
on conflict (version_id, relative_path)
do update set
  blob_digest = excluded.blob_digest,
  checksum_sha1 = excluded.checksum_sha1,
  checksum_sha256 = excluded.checksum_sha256,
  size_bytes = excluded.size_bytes;
""",
            conn,
            tx
        )

    cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
    cmd.Parameters.AddWithValue("relative_path", entry.RelativePath) |> ignore
    cmd.Parameters.AddWithValue("blob_digest", entry.BlobDigest) |> ignore

    let checksumSha1Param = cmd.Parameters.Add("checksum_sha1", NpgsqlDbType.Char)
    checksumSha1Param.Value <- (match entry.ChecksumSha1 with | Some value -> box value | None -> box DBNull.Value)

    let checksumSha256Param = cmd.Parameters.Add("checksum_sha256", NpgsqlDbType.Char)
    checksumSha256Param.Value <- (match entry.ChecksumSha256 with | Some value -> box value | None -> box DBNull.Value)

    cmd.Parameters.AddWithValue("size_bytes", entry.SizeBytes) |> ignore
    cmd.ExecuteNonQuery() |> ignore
    Ok()

let upsertArtifactEntriesForDraftVersion
    (tenantId: Guid)
    (repoKey: string)
    (versionId: Guid)
    (entries: NormalizedArtifactEntry list)
    (conn: NpgsqlConnection)
    =
    use tx = conn.BeginTransaction()

    let outcomeResult =
        tryReadLockedPackageVersionTargetForRepo tenantId repoKey versionId conn tx
        |> Result.bind (fun targetOption ->
            match targetOption with
            | None -> Ok UpsertEntriesVersionMissing
            | Some target when target.State <> "draft" -> Ok(UpsertEntriesStateConflict target.State)
            | Some target ->
                let rec persist remaining count =
                    match remaining with
                    | [] -> Ok(UpsertEntriesSuccess count)
                    | entry :: tail ->
                        blobExistsByDigestInTransaction entry.BlobDigest conn tx
                        |> Result.bind (fun blobExists ->
                            if not blobExists then
                                Ok(UpsertEntriesBlobMissing entry.BlobDigest)
                            else
                                repoHasCommittedDigestInTransaction tenantId target.RepoId entry.BlobDigest conn tx
                                |> Result.bind (fun committedInRepo ->
                                    if not committedInRepo then
                                        Ok(UpsertEntriesBlobNotCommitted entry.BlobDigest)
                                    else
                                        upsertArtifactEntryForVersionInTransaction versionId entry conn tx
                                        |> Result.bind (fun () -> persist tail (count + 1))))

                persist entries 0)

    match outcomeResult with
    | Error err ->
        tx.Rollback()
        Error err
    | Ok outcome ->
        match outcome with
        | UpsertEntriesSuccess _ ->
            tx.Commit()
            Ok outcome
        | _ ->
            tx.Rollback()
            Ok outcome

let upsertManifestForDraftVersion
    (tenantId: Guid)
    (repoKey: string)
    (versionId: Guid)
    (manifestJson: string)
    (manifestBlobDigest: string option)
    (updatedBySubject: string)
    (conn: NpgsqlConnection)
    =
    use tx = conn.BeginTransaction()

    let outcomeResult =
        tryReadLockedPackageVersionTargetForRepo tenantId repoKey versionId conn tx
        |> Result.bind (fun targetOption ->
            match targetOption with
            | None -> Ok UpsertManifestVersionMissing
            | Some target when target.State <> "draft" -> Ok(UpsertManifestStateConflict target.State)
            | Some target ->
                let digestValidationResult =
                    match manifestBlobDigest with
                    | None -> Ok true
                    | Some digest ->
                        blobExistsByDigestInTransaction digest conn tx
                        |> Result.bind (fun blobExists ->
                            if not blobExists then
                                Ok false
                            else
                                repoHasCommittedDigestInTransaction tenantId target.RepoId digest conn tx)

                digestValidationResult
                |> Result.bind (fun digestIsValid ->
                    match manifestBlobDigest, digestIsValid with
                    | Some digest, false ->
                        blobExistsByDigestInTransaction digest conn tx
                        |> Result.map (fun exists ->
                            if exists then
                                UpsertManifestBlobNotCommitted digest
                            else
                                UpsertManifestBlobMissing digest)
                    | _ ->
                        use cmd =
                            new NpgsqlCommand(
                                """
insert into manifests
  (version_id, tenant_id, repo_id, package_type, manifest_blob_digest, manifest_json, created_by_subject, updated_by_subject)
values
  (@version_id, @tenant_id, @repo_id, @package_type, @manifest_blob_digest, @manifest_json, @updated_by_subject, @updated_by_subject)
on conflict (version_id)
do update set
  package_type = excluded.package_type,
  manifest_blob_digest = excluded.manifest_blob_digest,
  manifest_json = excluded.manifest_json,
  updated_by_subject = excluded.updated_by_subject,
  updated_at = now();
""",
                                conn,
                                tx
                            )

                        cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
                        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                        cmd.Parameters.AddWithValue("repo_id", target.RepoId) |> ignore
                        cmd.Parameters.AddWithValue("package_type", target.PackageType) |> ignore
                        cmd.Parameters.AddWithValue("updated_by_subject", updatedBySubject) |> ignore

                        let manifestBlobDigestParam = cmd.Parameters.Add("manifest_blob_digest", NpgsqlDbType.Char)
                        manifestBlobDigestParam.Value <- (match manifestBlobDigest with | Some value -> box value | None -> box DBNull.Value)

                        let manifestJsonParam = cmd.Parameters.Add("manifest_json", NpgsqlDbType.Jsonb)
                        manifestJsonParam.Value <- manifestJson

                        cmd.ExecuteNonQuery() |> ignore
                        Ok(UpsertManifestSuccess(target, manifestBlobDigest))))

    match outcomeResult with
    | Error err ->
        tx.Rollback()
        Error err
    | Ok outcome ->
        match outcome with
        | UpsertManifestSuccess _ ->
            tx.Commit()
            Ok outcome
        | _ ->
            tx.Rollback()
            Ok outcome

let readManifestForVersion (tenantId: Guid) (repoKey: string) (versionId: Guid) (conn: NpgsqlConnection) =
    tryReadPackageVersionTargetForRepo tenantId repoKey versionId conn
    |> Result.bind (fun versionTargetOption ->
        match versionTargetOption with
        | None -> Ok ReadManifestVersionMissing
        | Some versionTarget ->
            use cmd =
                new NpgsqlCommand(
                    """
select manifest_blob_digest, manifest_json::text
from manifests
where version_id = @version_id
limit 1;
""",
                    conn
                )

            cmd.Parameters.AddWithValue("version_id", versionId) |> ignore

            use reader = cmd.ExecuteReader()

            if reader.Read() then
                let manifestBlobDigest =
                    if reader.IsDBNull(0) then None else Some(reader.GetString(0))

                let manifestJson = reader.GetString(1)
                Ok(ReadManifestSuccess(versionTarget, manifestBlobDigest, manifestJson))
            else
                Ok ReadManifestMissing)

let private insertAuditRecordInTransaction
    (tenantId: Guid)
    (actor: string)
    (action: string)
    (resourceType: string)
    (resourceId: string)
    (details: Map<string, string>)
    (conn: NpgsqlConnection)
    (tx: NpgsqlTransaction)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into audit_log (tenant_id, actor_subject, action, resource_type, resource_id, details)
values (@tenant_id, @actor_subject, @action, @resource_type, @resource_id, @details);
""",
            conn,
            tx
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("actor_subject", actor) |> ignore
    cmd.Parameters.AddWithValue("action", action) |> ignore
    cmd.Parameters.AddWithValue("resource_type", resourceType) |> ignore
    cmd.Parameters.AddWithValue("resource_id", resourceId) |> ignore

    let detailsParam = cmd.Parameters.Add("details", NpgsqlDbType.Jsonb)
    detailsParam.Value <- (details |> Map.toSeq |> dict |> JsonSerializer.Serialize)

    let rows = cmd.ExecuteNonQuery()
    if rows = 1 then Ok() else Error "Audit insert did not affect expected rows."

let publishDraftVersionForRepo
    (tenantId: Guid)
    (repoKey: string)
    (versionId: Guid)
    (publishedBySubject: string)
    (conn: NpgsqlConnection)
    =
    use tx = conn.BeginTransaction()

    let outcomeResult =
        tryReadLockedPackageVersionTargetForRepo tenantId repoKey versionId conn tx
        |> Result.bind (fun targetOption ->
            match targetOption with
            | None -> Ok PublishVersionMissing
            | Some target when target.State = "published" ->
                Ok(PublishVersionAlreadyPublished target)
            | Some target when target.State <> "draft" ->
                Ok(PublishVersionStateConflict target.State)
            | Some target ->
                use entryCountCmd =
                    new NpgsqlCommand(
                        "select count(*) from artifact_entries where version_id = @version_id;",
                        conn,
                        tx
                    )

                entryCountCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
                let entryCountScalar = entryCountCmd.ExecuteScalar()

                let entryCountResult =
                    match entryCountScalar with
                    | :? int64 as value -> Ok value
                    | :? int32 as value -> Ok(int64 value)
                    | _ -> Error "Could not determine artifact entry count for publish."

                match entryCountResult with
                | Error err -> Error err
                | Ok entryCount when entryCount = 0L -> Ok PublishVersionMissingEntries
                | Ok _ ->
                    use missingDigestCmd =
                        new NpgsqlCommand(
                            """
select ae.blob_digest
from artifact_entries ae
where ae.version_id = @version_id
  and not exists (
    select 1
    from upload_sessions us
    where us.tenant_id = @tenant_id
      and us.repo_id = @repo_id
      and us.state = 'committed'
      and us.committed_blob_digest = ae.blob_digest
  )
limit 1;
""",
                            conn,
                            tx
                        )

                    missingDigestCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
                    missingDigestCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                    missingDigestCmd.Parameters.AddWithValue("repo_id", target.RepoId) |> ignore

                    let missingDigestScalar = missingDigestCmd.ExecuteScalar()

                    if not (isNull missingDigestScalar || missingDigestScalar = box DBNull.Value) then
                        match missingDigestScalar with
                        | :? string as missingDigest -> Ok(PublishVersionBlobNotCommitted missingDigest)
                        | _ -> Error "Unexpected missing blob digest type returned for publish."
                    else
                        use manifestCountCmd =
                            new NpgsqlCommand(
                                "select count(*) from manifests where version_id = @version_id;",
                                conn,
                                tx
                            )

                        manifestCountCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
                        let manifestCountScalar = manifestCountCmd.ExecuteScalar()

                        let manifestCountResult =
                            match manifestCountScalar with
                            | :? int64 as value -> Ok value
                            | :? int32 as value -> Ok(int64 value)
                            | _ -> Error "Could not determine manifest count for publish."

                        match manifestCountResult with
                        | Error err -> Error err
                        | Ok manifestCount when manifestCount = 0L -> Ok PublishVersionMissingManifest
                        | Ok _ ->
                            use updateCmd =
                                new NpgsqlCommand(
                                    """
update package_versions
set state = 'published',
    published_at = coalesce(published_at, now())
where tenant_id = @tenant_id
  and version_id = @version_id
  and state = 'draft'
returning published_at;
""",
                                    conn,
                                    tx
                                )

                            updateCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                            updateCmd.Parameters.AddWithValue("version_id", versionId) |> ignore

                            let publishedAtScalar = updateCmd.ExecuteScalar()

                            if isNull publishedAtScalar || publishedAtScalar = box DBNull.Value then
                                Ok(PublishVersionStateConflict "draft_transition_failed")
                            else
                                let publishedAtResult =
                                    match publishedAtScalar with
                                    | :? DateTimeOffset as dto -> Ok dto
                                    | :? DateTime as dt -> Ok(toUtcDateTimeOffset dt)
                                    | _ -> Error "Unexpected published_at type returned from publish update."

                                match publishedAtResult with
                                | Error err -> Error err
                                | Ok publishedAtUtc ->
                                    let payloadJson =
                                        JsonSerializer.Serialize(
                                            {| versionId = versionId
                                               repoKey = target.RepoKey
                                               packageType = target.PackageType
                                               packageNamespace = target.PackageNamespace
                                               packageName = target.PackageName
                                               version = target.Version
                                               publishedAtUtc = publishedAtUtc |}
                                        )

                                    use outboxCmd =
                                        new NpgsqlCommand(
                                            """
insert into outbox_events
  (tenant_id, aggregate_type, aggregate_id, event_type, payload, occurred_at, available_at)
select
  @tenant_id,
  'package_version',
  @aggregate_id,
  'version.published',
  @payload,
  now(),
  now()
where not exists (
  select 1
  from outbox_events
  where tenant_id = @tenant_id
    and aggregate_type = 'package_version'
    and aggregate_id = @aggregate_id
    and event_type = 'version.published'
)
returning event_id;
""",
                                            conn,
                                            tx
                                        )

                                    outboxCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                                    outboxCmd.Parameters.AddWithValue("aggregate_id", versionId.ToString()) |> ignore

                                    let payloadParam = outboxCmd.Parameters.Add("payload", NpgsqlDbType.Jsonb)
                                    payloadParam.Value <- payloadJson

                                    let outboxEventScalar = outboxCmd.ExecuteScalar()

                                    let outboxEventId =
                                        if isNull outboxEventScalar || outboxEventScalar = box DBNull.Value then
                                            None
                                        else
                                            match outboxEventScalar with
                                            | :? Guid as eventId -> Some eventId
                                            | _ -> None

                                    match
                                        insertAuditRecordInTransaction
                                            tenantId
                                            publishedBySubject
                                            "package.version.published"
                                            "package_version"
                                            (versionId.ToString())
                                            (Map.ofList
                                                [ "repoKey", target.RepoKey
                                                  "packageType", target.PackageType
                                                  "packageName", target.PackageName
                                                  "version", target.Version
                                                  "state", "published" ])
                                            conn
                                            tx
                                    with
                                    | Error err -> Error err
                                    | Ok () ->
                                        let publishedTarget =
                                            { target with
                                                State = "published"
                                                PublishedAtUtc = Some publishedAtUtc }

                                        Ok(PublishVersionSuccess(publishedTarget, publishedAtUtc, outboxEventId))
            )

    match outcomeResult with
    | Error err ->
        tx.Rollback()
        Error err
    | Ok outcome ->
        match outcome with
        | PublishVersionSuccess _ ->
            tx.Commit()
            Ok outcome
        | _ ->
            tx.Rollback()
            Ok outcome

let private tryReadTombstoneRetentionForVersionInTransaction
    (versionId: Guid)
    (conn: NpgsqlConnection)
    (tx: NpgsqlTransaction)
    =
    use cmd =
        new NpgsqlCommand(
            """
select retention_until
from tombstones
where version_id = @version_id
limit 1;
""",
            conn,
            tx
        )

    cmd.Parameters.AddWithValue("version_id", versionId) |> ignore
    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Ok None
    else
        match scalar with
        | :? DateTimeOffset as dto -> Ok(Some dto)
        | :? DateTime as dt -> Ok(Some(toUtcDateTimeOffset dt))
        | _ -> Error "Unexpected retention_until type returned from tombstones."

let tombstoneVersionForRepo
    (tenantId: Guid)
    (repoKey: string)
    (versionId: Guid)
    (reason: string)
    (retentionDays: int)
    (deletedBySubject: string)
    (conn: NpgsqlConnection)
    =
    use tx = conn.BeginTransaction()

    let outcomeResult =
        tryReadLockedPackageVersionTargetForRepo tenantId repoKey versionId conn tx
        |> Result.bind (fun targetOption ->
            match targetOption with
            | None -> Ok TombstoneVersionMissing
            | Some target when target.State = "tombstoned" ->
                tryReadTombstoneRetentionForVersionInTransaction versionId conn tx
                |> Result.map (fun retentionUntil -> TombstoneVersionAlreadyTombstoned(target, retentionUntil))
            | Some target when target.State <> "draft" && target.State <> "published" ->
                Ok(TombstoneVersionStateConflict target.State)
            | Some target ->
                let retentionUntilUtc = (nowUtc ()).AddDays(float retentionDays)

                use updateCmd =
                    new NpgsqlCommand(
                        """
update package_versions
set state = 'tombstoned',
    tombstoned_at = now(),
    tombstone_reason = @reason
where tenant_id = @tenant_id
  and version_id = @version_id
  and state in ('draft', 'published')
returning tombstoned_at;
""",
                        conn,
                        tx
                    )

                updateCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                updateCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
                updateCmd.Parameters.AddWithValue("reason", reason) |> ignore

                let tombstonedAtScalar = updateCmd.ExecuteScalar()

                if isNull tombstonedAtScalar || tombstonedAtScalar = box DBNull.Value then
                    Ok(TombstoneVersionStateConflict "tombstone_transition_failed")
                else
                    let tombstonedAtResult =
                        match tombstonedAtScalar with
                        | :? DateTimeOffset as dto -> Ok dto
                        | :? DateTime as dt -> Ok(toUtcDateTimeOffset dt)
                        | _ -> Error "Unexpected tombstoned_at type returned from tombstone update."

                    tombstonedAtResult
                    |> Result.bind (fun tombstonedAtUtc ->
                        use tombstoneCmd =
                            new NpgsqlCommand(
                                """
insert into tombstones
  (tenant_id, repo_id, version_id, deleted_by_subject, deleted_at, retention_until, reason)
values
  (@tenant_id, @repo_id, @version_id, @deleted_by_subject, now(), @retention_until, @reason)
on conflict (version_id)
do update set
  deleted_by_subject = excluded.deleted_by_subject,
  deleted_at = now(),
  retention_until = excluded.retention_until,
  reason = excluded.reason;
""",
                                conn,
                                tx
                            )

                        tombstoneCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                        tombstoneCmd.Parameters.AddWithValue("repo_id", target.RepoId) |> ignore
                        tombstoneCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
                        tombstoneCmd.Parameters.AddWithValue("deleted_by_subject", deletedBySubject) |> ignore
                        tombstoneCmd.Parameters.AddWithValue("retention_until", retentionUntilUtc) |> ignore
                        tombstoneCmd.Parameters.AddWithValue("reason", reason) |> ignore
                        tombstoneCmd.ExecuteNonQuery() |> ignore

                        insertAuditRecordInTransaction
                            tenantId
                            deletedBySubject
                            "package.version.tombstoned"
                            "package_version"
                            (versionId.ToString())
                            (Map.ofList
                                [ "repoKey", target.RepoKey
                                  "packageType", target.PackageType
                                  "packageName", target.PackageName
                                  "version", target.Version
                                  "retentionDays", retentionDays.ToString()
                                  "reason", reason ])
                            conn
                            tx
                        |> Result.map (fun () ->
                            let tombstonedTarget =
                                { target with
                                    State = "tombstoned"
                                    PublishedAtUtc = target.PublishedAtUtc }

                            TombstoneVersionSuccess(tombstonedTarget, tombstonedAtUtc, retentionUntilUtc))))

    match outcomeResult with
    | Error err ->
        tx.Rollback()
        Error err
    | Ok outcome ->
        match outcome with
        | TombstoneVersionSuccess _ ->
            tx.Commit()
            Ok outcome
        | _ ->
            tx.Rollback()
            Ok outcome

let private insertGcRun
    (tenantId: Guid)
    (initiatedBySubject: string)
    (mode: string)
    (retentionGraceHours: int)
    (batchSize: int)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into gc_runs
  (tenant_id, initiated_by_subject, mode, retention_grace_hours, batch_size)
values
  (@tenant_id, @initiated_by_subject, @mode, @retention_grace_hours, @batch_size)
returning run_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("initiated_by_subject", initiatedBySubject) |> ignore
    cmd.Parameters.AddWithValue("mode", mode) |> ignore
    cmd.Parameters.AddWithValue("retention_grace_hours", retentionGraceHours) |> ignore
    cmd.Parameters.AddWithValue("batch_size", batchSize) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Error "GC run insert did not return run id."
    else
        match scalar with
        | :? Guid as runId -> Ok runId
        | _ -> Error "Unexpected GC run id type returned from database."

let private insertGcMarksForRun (tenantId: Guid) (runId: Guid) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
insert into gc_marks (run_id, digest)
select @run_id, roots.digest
from (
  select distinct ae.blob_digest as digest
  from artifact_entries ae
  join package_versions pv on pv.version_id = ae.version_id
  left join tombstones ts on ts.version_id = pv.version_id
  where pv.tenant_id = @tenant_id
    and (
      pv.state <> 'tombstoned'
      or ts.retention_until > now()
      or ts.version_id is null
    )

  union

  select distinct m.manifest_blob_digest as digest
  from manifests m
  join package_versions pv on pv.version_id = m.version_id
  left join tombstones ts on ts.version_id = pv.version_id
  where pv.tenant_id = @tenant_id
    and m.manifest_blob_digest is not null
    and (
      pv.state <> 'tombstoned'
      or ts.retention_until > now()
      or ts.version_id is null
    )
) roots;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("run_id", runId) |> ignore
    let rows = cmd.ExecuteNonQuery()
    Ok rows

let private deleteExpiredTombstonedVersions (tenantId: Guid) (batchSize: int) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
with doomed as (
  select pv.version_id
  from package_versions pv
  join tombstones ts on ts.version_id = pv.version_id
  where pv.tenant_id = @tenant_id
    and pv.state = 'tombstoned'
    and ts.retention_until <= now()
  order by ts.retention_until, pv.created_at
  limit @batch_size
)
delete from package_versions pv
using doomed d
where pv.version_id = d.version_id
returning pv.version_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("batch_size", batchSize) |> ignore

    use reader = cmd.ExecuteReader()
    let deleted = ResizeArray<Guid>()

    let rec loop () =
        if reader.Read() then
            deleted.Add(reader.GetGuid(0))
            loop ()

    loop ()
    Ok(deleted |> Seq.toList)

let private readExpiredTombstonedVersionIds (tenantId: Guid) (batchSize: int) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select pv.version_id
from package_versions pv
join tombstones ts on ts.version_id = pv.version_id
where pv.tenant_id = @tenant_id
  and pv.state = 'tombstoned'
  and ts.retention_until <= now()
order by ts.retention_until, pv.created_at
limit @batch_size;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("batch_size", batchSize) |> ignore

    use reader = cmd.ExecuteReader()
    let versionIds = ResizeArray<Guid>()

    let rec loop () =
        if reader.Read() then
            versionIds.Add(reader.GetGuid(0))
            loop ()

    loop ()
    Ok(versionIds |> Seq.toList)

let private readGcCandidateBlobsForRun
    (runId: Guid)
    (retentionGraceHours: int)
    (batchSize: int)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
select b.digest, b.storage_key
from blobs b
where b.created_at <= now() - make_interval(hours => @retention_grace_hours)
  and not exists (
    select 1 from gc_marks gm
    where gm.run_id = @run_id
      and gm.digest = b.digest
  )
  and not exists (
    select 1 from artifact_entries ae
    where ae.blob_digest = b.digest
  )
  and not exists (
    select 1 from manifests m
    where m.manifest_blob_digest = b.digest
  )
order by b.created_at
limit @batch_size;
""",
            conn
        )

    cmd.Parameters.AddWithValue("run_id", runId) |> ignore
    cmd.Parameters.AddWithValue("retention_grace_hours", retentionGraceHours) |> ignore
    cmd.Parameters.AddWithValue("batch_size", batchSize) |> ignore

    use reader = cmd.ExecuteReader()
    let candidates = ResizeArray<GcCandidateBlob>()

    let rec loop () =
        if reader.Read() then
            candidates.Add(
                { Digest = reader.GetString(0)
                  StorageKey = reader.GetString(1) }
            )

            loop ()

    loop ()
    Ok(candidates |> Seq.toList)

let private deleteBlobsByDigests (digests: string list) (conn: NpgsqlConnection) =
    if digests.IsEmpty then
        Ok 0
    else
        use cmd =
            new NpgsqlCommand(
                """
delete from blobs
where digest = any(@digests);
""",
                conn
            )

        let digestsParam = cmd.Parameters.Add("digests", NpgsqlDbType.Array ||| NpgsqlDbType.Char)
        digestsParam.Value <- (digests |> List.toArray)

        let rows = cmd.ExecuteNonQuery()
        Ok rows

let private clearUploadSessionCommittedBlobReferences (digests: string list) (conn: NpgsqlConnection) =
    if digests.IsEmpty then
        Ok 0
    else
        use cmd =
            new NpgsqlCommand(
                """
update upload_sessions
set committed_blob_digest = null
where committed_blob_digest = any(@digests);
""",
                conn
            )

        let digestsParam = cmd.Parameters.Add("digests", NpgsqlDbType.Array ||| NpgsqlDbType.Char)
        digestsParam.Value <- (digests |> List.toArray)

        let rows = cmd.ExecuteNonQuery()
        Ok rows

let private finalizeGcRun
    (runId: Guid)
    (markedCount: int)
    (candidateBlobCount: int)
    (deletedBlobCount: int)
    (deletedVersionCount: int)
    (deleteErrorCount: int)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
update gc_runs
set completed_at = now(),
    marked_count = @marked_count,
    candidate_blob_count = @candidate_blob_count,
    deleted_blob_count = @deleted_blob_count,
    deleted_version_count = @deleted_version_count,
    delete_error_count = @delete_error_count
where run_id = @run_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("run_id", runId) |> ignore
    cmd.Parameters.AddWithValue("marked_count", markedCount) |> ignore
    cmd.Parameters.AddWithValue("candidate_blob_count", candidateBlobCount) |> ignore
    cmd.Parameters.AddWithValue("deleted_blob_count", deletedBlobCount) |> ignore
    cmd.Parameters.AddWithValue("deleted_version_count", deletedVersionCount) |> ignore
    cmd.Parameters.AddWithValue("delete_error_count", deleteErrorCount) |> ignore
    cmd.ExecuteNonQuery() |> ignore
    Ok()

let private finalizeGcRunAsFailed (runId: Guid) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
update gc_runs
set completed_at = coalesce(completed_at, now()),
    delete_error_count = case when delete_error_count < 1 then 1 else delete_error_count end
where run_id = @run_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("run_id", runId) |> ignore
    cmd.ExecuteNonQuery() |> ignore
    Ok()

let runGcSweep
    (objectStorageClient: IObjectStorageClient)
    (tenantId: Guid)
    (initiatedBySubject: string)
    (dryRun: bool)
    (retentionGraceHours: int)
    (batchSize: int)
    (cancellationToken: Threading.CancellationToken)
    (conn: NpgsqlConnection)
    =
    let mode = if dryRun then "dry_run" else "execute"

    insertGcRun tenantId initiatedBySubject mode retentionGraceHours batchSize conn
    |> Result.bind (fun runId ->
        let runResult =
            insertGcMarksForRun tenantId runId conn
            |> Result.bind (fun markedCount ->
                let versionResult =
                    if dryRun then
                        readExpiredTombstonedVersionIds tenantId batchSize conn
                        |> Result.map (fun versionIds -> versionIds, 0)
                    else
                        deleteExpiredTombstonedVersions tenantId batchSize conn
                        |> Result.map (fun versionIds -> versionIds, versionIds.Length)

                versionResult
                |> Result.bind (fun (_versionIds, deletedVersionCount) ->
                    readGcCandidateBlobsForRun runId retentionGraceHours batchSize conn
                    |> Result.bind (fun candidates ->
                        let candidateBlobCount = candidates.Length
                        let candidateDigests = candidates |> List.map (fun candidate -> candidate.Digest)

                        let deleteResult =
                            if dryRun then
                                Ok(0, 0)
                            else
                                let mutable deletableDigests = []
                                let mutable deleteErrorCount = 0

                                for candidate in candidates do
                                    match objectStorageClient.DeleteObject(candidate.StorageKey, cancellationToken).Result with
                                    | Ok () ->
                                        deletableDigests <- candidate.Digest :: deletableDigests
                                    | Error(NotFound _) ->
                                        deletableDigests <- candidate.Digest :: deletableDigests
                                    | Error _ ->
                                        deleteErrorCount <- deleteErrorCount + 1

                                let digestsToDelete = deletableDigests |> List.rev

                                clearUploadSessionCommittedBlobReferences digestsToDelete conn
                                |> Result.bind (fun _ -> deleteBlobsByDigests digestsToDelete conn)
                                |> Result.map (fun deletedBlobCount -> deletedBlobCount, deleteErrorCount)

                        deleteResult
                        |> Result.bind (fun (deletedBlobCount, deleteErrorCount) ->
                            finalizeGcRun
                                runId
                                markedCount
                                candidateBlobCount
                                deletedBlobCount
                                deletedVersionCount
                                deleteErrorCount
                                conn
                            |> Result.map (fun () ->
                                { RunId = runId
                                  Mode = mode
                                  MarkedCount = markedCount
                                  CandidateBlobCount = candidateBlobCount
                                  DeletedBlobCount = deletedBlobCount
                                  DeletedVersionCount = deletedVersionCount
                                  DeleteErrorCount = deleteErrorCount
                                  CandidateDigests = candidateDigests |> List.truncate 20 })))))

        match runResult with
        | Ok result -> Ok result
        | Error err ->
            match finalizeGcRunAsFailed runId conn with
            | Ok () -> Error err
            | Error finalizeErr -> Error $"{err}; additionally failed to finalize GC run: {finalizeErr}")

let readBlobReconcileSummary (tenantId: Guid) (sampleLimit: int) (conn: NpgsqlConnection) =
    let scalarCount (sql: string) =
        use cmd = new NpgsqlCommand(sql, conn)
        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? int64 as count -> Ok count
        | :? int32 as count -> Ok(int64 count)
        | _ -> Error "Unexpected count type returned during reconcile query."

    let missingArtifactCountSql =
        """
select count(*)
from artifact_entries ae
join package_versions pv on pv.version_id = ae.version_id
left join blobs b on b.digest = ae.blob_digest
where pv.tenant_id = @tenant_id
  and b.digest is null;
"""

    let missingManifestCountSql =
        """
select count(*)
from manifests m
left join blobs b on b.digest = m.manifest_blob_digest
where m.tenant_id = @tenant_id
  and m.manifest_blob_digest is not null
  and b.digest is null;
"""

    let orphanBlobCountSql =
        """
select count(*)
from blobs b
where not exists (
  select 1 from artifact_entries ae where ae.blob_digest = b.digest
)
and not exists (
  select 1 from manifests m where m.manifest_blob_digest = b.digest
);
"""

    scalarCount missingArtifactCountSql
    |> Result.bind (fun missingArtifactBlobRefs ->
        scalarCount missingManifestCountSql
        |> Result.bind (fun missingManifestBlobRefs ->
            scalarCount orphanBlobCountSql
            |> Result.bind (fun orphanBlobCount ->
                use missingArtifactSampleCmd =
                    new NpgsqlCommand(
                        """
select ae.version_id::text || ':' || ae.relative_path || ':' || ae.blob_digest
from artifact_entries ae
join package_versions pv on pv.version_id = ae.version_id
left join blobs b on b.digest = ae.blob_digest
where pv.tenant_id = @tenant_id
  and b.digest is null
order by ae.entry_id
limit @sample_limit;
""",
                        conn
                    )

                missingArtifactSampleCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                missingArtifactSampleCmd.Parameters.AddWithValue("sample_limit", sampleLimit) |> ignore

                use missingArtifactReader = missingArtifactSampleCmd.ExecuteReader()
                let missingArtifactSamples = ResizeArray<string>()

                let rec readMissingArtifact () =
                    if missingArtifactReader.Read() then
                        missingArtifactSamples.Add(missingArtifactReader.GetString(0))
                        readMissingArtifact ()

                readMissingArtifact ()
                missingArtifactReader.Close()

                use missingManifestSampleCmd =
                    new NpgsqlCommand(
                        """
select m.version_id::text || ':' || m.manifest_blob_digest
from manifests m
left join blobs b on b.digest = m.manifest_blob_digest
where m.tenant_id = @tenant_id
  and m.manifest_blob_digest is not null
  and b.digest is null
order by m.version_id
limit @sample_limit;
""",
                        conn
                    )

                missingManifestSampleCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                missingManifestSampleCmd.Parameters.AddWithValue("sample_limit", sampleLimit) |> ignore

                use missingManifestReader = missingManifestSampleCmd.ExecuteReader()
                let missingManifestSamples = ResizeArray<string>()

                let rec readMissingManifest () =
                    if missingManifestReader.Read() then
                        missingManifestSamples.Add(missingManifestReader.GetString(0))
                        readMissingManifest ()

                readMissingManifest ()
                missingManifestReader.Close()

                use orphanSampleCmd =
                    new NpgsqlCommand(
                        """
select b.digest
from blobs b
where not exists (
  select 1 from artifact_entries ae where ae.blob_digest = b.digest
)
and not exists (
  select 1 from manifests m where m.manifest_blob_digest = b.digest
)
order by b.created_at desc
limit @sample_limit;
""",
                        conn
                    )

                orphanSampleCmd.Parameters.AddWithValue("sample_limit", sampleLimit) |> ignore

                use orphanReader = orphanSampleCmd.ExecuteReader()
                let orphanSamples = ResizeArray<string>()

                let rec readOrphans () =
                    if orphanReader.Read() then
                        orphanSamples.Add(orphanReader.GetString(0))
                        readOrphans ()

                readOrphans ()

                Ok(
                    {| checkedAtUtc = nowUtc ()
                       missingArtifactBlobRefs = missingArtifactBlobRefs
                       missingManifestBlobRefs = missingManifestBlobRefs
                       orphanBlobCount = orphanBlobCount
                       sampleLimit = sampleLimit
                       missingArtifactSamples = missingArtifactSamples |> Seq.toArray
                       missingManifestSamples = missingManifestSamples |> Seq.toArray
                       orphanBlobSamples = orphanSamples |> Seq.toArray |}))))

let readOpsSummary (tenantId: Guid) (conn: NpgsqlConnection) =
    let scalarCount (sql: string) =
        use cmd = new NpgsqlCommand(sql, conn)
        cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        let scalar = cmd.ExecuteScalar()

        match scalar with
        | :? int64 as count -> Ok count
        | :? int32 as count -> Ok(int64 count)
        | _ -> Error "Unexpected count type returned during operations summary query."

    let pendingOutboxSql =
        """
select count(*)
from outbox_events
where tenant_id = @tenant_id
  and delivered_at is null;
"""

    let availableOutboxSql =
        """
select count(*)
from outbox_events
where tenant_id = @tenant_id
  and delivered_at is null
  and available_at <= now();
"""

    let oldestPendingOutboxAgeSecondsSql =
        """
select coalesce(max(extract(epoch from (now() - occurred_at))), 0)::bigint
from outbox_events
where tenant_id = @tenant_id
  and delivered_at is null;
"""

    let failedSearchJobsSql =
        """
select count(*)
from search_index_jobs
where tenant_id = @tenant_id
  and status = 'failed';
"""

    let pendingSearchJobsSql =
        """
select count(*)
from search_index_jobs
where tenant_id = @tenant_id
  and status in ('pending', 'processing');
"""

    let incompleteGcRunsSql =
        """
select count(*)
from gc_runs
where tenant_id = @tenant_id
  and completed_at is null;
"""

    let recentPolicyTimeoutsSql =
        """
select count(*)
from audit_log
where tenant_id = @tenant_id
  and action = 'policy.timeout'
  and occurred_at >= now() - interval '24 hours';
"""

    scalarCount pendingOutboxSql
    |> Result.bind (fun pendingOutboxEvents ->
        scalarCount availableOutboxSql
        |> Result.bind (fun availableOutboxEvents ->
            scalarCount oldestPendingOutboxAgeSecondsSql
            |> Result.bind (fun oldestPendingOutboxAgeSeconds ->
                scalarCount failedSearchJobsSql
                |> Result.bind (fun failedSearchJobs ->
                    scalarCount pendingSearchJobsSql
                    |> Result.bind (fun pendingSearchJobs ->
                        scalarCount incompleteGcRunsSql
                        |> Result.bind (fun incompleteGcRuns ->
                            scalarCount recentPolicyTimeoutsSql
                            |> Result.map (fun recentPolicyTimeouts24h ->
                                {| checkedAtUtc = nowUtc ()
                                   pendingOutboxEvents = pendingOutboxEvents
                                   availableOutboxEvents = availableOutboxEvents
                                   oldestPendingOutboxAgeSeconds = oldestPendingOutboxAgeSeconds
                                   failedSearchJobs = failedSearchJobs
                                   pendingSearchJobs = pendingSearchJobs
                                   incompleteGcRuns = incompleteGcRuns
                                   recentPolicyTimeouts24h = recentPolicyTimeouts24h |})))))))

let evaluatePolicyForVersion
    (tenantId: Guid)
    (repoKey: string)
    (versionId: Guid)
    (action: string)
    (decision: string)
    (decisionSource: string)
    (reason: string)
    (policyEngineVersion: string option)
    (evaluatedBySubject: string)
    (conn: NpgsqlConnection)
    =
    use tx = conn.BeginTransaction()

    use targetCmd =
        new NpgsqlCommand(
            """
select r.repo_id, pv.state
from repos r
join package_versions pv on pv.repo_id = r.repo_id and pv.tenant_id = r.tenant_id
where r.tenant_id = @tenant_id
  and r.repo_key = @repo_key
  and pv.version_id = @version_id
limit 1;
""",
            conn,
            tx
        )

    targetCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    targetCmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    targetCmd.Parameters.AddWithValue("version_id", versionId) |> ignore

    use targetReader = targetCmd.ExecuteReader()

    let target =
        if targetReader.Read() then
            let repoId = targetReader.GetGuid(0)
            let versionState = targetReader.GetString(1)
            Some(repoId, versionState)
        else
            None

    targetReader.Close()

    match target with
    | None ->
        tx.Rollback()
        Ok None
    | Some(repoId, versionState) ->
        let details =
            [ "decisionSource", decisionSource; "versionState", versionState ]
            |> Map.ofList
            |> Map.toSeq
            |> dict
            |> JsonSerializer.Serialize

        use insertEvalCmd =
            new NpgsqlCommand(
                """
insert into policy_evaluations
  (tenant_id, repo_id, version_id, action, decision, policy_engine_version, reason, details, evaluated_by_subject)
values
  (@tenant_id, @repo_id, @version_id, @action, @decision, @policy_engine_version, @reason, @details, @evaluated_by_subject)
returning evaluation_id, evaluated_at;
""",
                conn,
                tx
            )

        insertEvalCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        insertEvalCmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
        insertEvalCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
        insertEvalCmd.Parameters.AddWithValue("action", action) |> ignore
        insertEvalCmd.Parameters.AddWithValue("decision", decision) |> ignore
        insertEvalCmd.Parameters.AddWithValue("reason", reason) |> ignore
        insertEvalCmd.Parameters.AddWithValue("evaluated_by_subject", evaluatedBySubject) |> ignore

        let policyEngineParam = insertEvalCmd.Parameters.Add("policy_engine_version", NpgsqlDbType.Text)
        policyEngineParam.Value <- (match policyEngineVersion with | Some value -> box value | None -> box DBNull.Value)

        let detailsParam = insertEvalCmd.Parameters.Add("details", NpgsqlDbType.Jsonb)
        detailsParam.Value <- details

        use evalReader = insertEvalCmd.ExecuteReader()

        let evaluation =
            if evalReader.Read() then
                let evaluationId = evalReader.GetInt64(0)
                let evaluatedAtUtc = evalReader.GetFieldValue<DateTime>(1) |> toUtcDateTimeOffset
                Some(evaluationId, evaluatedAtUtc)
            else
                None

        evalReader.Close()

        match evaluation with
        | None ->
            tx.Rollback()
            Error "Policy evaluation insert did not return evaluation metadata."
        | Some(evaluationId, evaluatedAtUtc) ->
            let quarantineIdResult =
                if decision <> "quarantine" then
                    Ok None
                else
                    use quarantineCmd =
                        new NpgsqlCommand(
                            """
insert into quarantine_items
  (tenant_id, repo_id, version_id, status, reason)
values
  (@tenant_id, @repo_id, @version_id, 'quarantined', @reason)
on conflict (tenant_id, repo_id, version_id)
do update set
  status = 'quarantined',
  reason = excluded.reason,
  resolved_at = null,
  resolved_by_subject = null
returning quarantine_id;
""",
                            conn,
                            tx
                        )

                    quarantineCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
                    quarantineCmd.Parameters.AddWithValue("repo_id", repoId) |> ignore
                    quarantineCmd.Parameters.AddWithValue("version_id", versionId) |> ignore
                    quarantineCmd.Parameters.AddWithValue("reason", reason) |> ignore

                    let scalar = quarantineCmd.ExecuteScalar()

                    if isNull scalar || scalar = box DBNull.Value then
                        Error "Quarantine upsert did not return quarantine id."
                    else
                        match scalar with
                        | :? Guid as quarantineId -> Ok(Some quarantineId)
                        | _ -> Error "Unexpected quarantine id type returned from database."

            match quarantineIdResult with
            | Error err ->
                tx.Rollback()
                Error err
            | Ok quarantineId ->
                tx.Commit()

                Ok(
                    Some
                        { EvaluationId = evaluationId
                          VersionId = versionId
                          Action = action
                          Decision = decision
                          DecisionSource = decisionSource
                          Reason = reason
                          PolicyEngineVersion = policyEngineVersion
                          EvaluatedAtUtc = evaluatedAtUtc
                          QuarantineId = quarantineId }
                )

let readQuarantineItemsForRepo
    (tenantId: Guid)
    (repoKey: string)
    (statusFilter: string option)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
select qi.quarantine_id, qi.version_id, qi.status, qi.reason, qi.created_at, qi.resolved_at, qi.resolved_by_subject
from quarantine_items qi
join repos r on r.repo_id = qi.repo_id
where qi.tenant_id = @tenant_id
  and r.repo_key = @repo_key
  and (@status_filter is null or qi.status = @status_filter)
order by qi.created_at desc;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    let statusParam = cmd.Parameters.Add("status_filter", NpgsqlDbType.Text)
    statusParam.Value <- (match statusFilter with | Some value -> box value | None -> box DBNull.Value)

    use reader = cmd.ExecuteReader()
    let entries = ResizeArray<QuarantineItemRecord>()

    let rec loop () =
        if reader.Read() then
            let quarantineId = reader.GetGuid(0)
            let versionId = reader.GetGuid(1)
            let status = reader.GetString(2)
            let reason = reader.GetString(3)
            let createdAtUtc = reader.GetFieldValue<DateTime>(4) |> toUtcDateTimeOffset
            let resolvedAtUtc =
                if reader.IsDBNull(5) then None else Some(reader.GetFieldValue<DateTime>(5) |> toUtcDateTimeOffset)

            let resolvedBySubject =
                if reader.IsDBNull(6) then None else Some(reader.GetString(6))

            entries.Add(
                { QuarantineId = quarantineId
                  RepoKey = repoKey
                  VersionId = versionId
                  Status = status
                  Reason = reason
                  CreatedAtUtc = createdAtUtc
                  ResolvedAtUtc = resolvedAtUtc
                  ResolvedBySubject = resolvedBySubject }
            )

            loop ()
        else
            Ok(entries |> Seq.toList)

    loop ()

let tryReadQuarantineItemForRepo (tenantId: Guid) (repoKey: string) (quarantineId: Guid) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select qi.quarantine_id, qi.version_id, qi.status, qi.reason, qi.created_at, qi.resolved_at, qi.resolved_by_subject
from quarantine_items qi
join repos r on r.repo_id = qi.repo_id
where qi.tenant_id = @tenant_id
  and r.repo_key = @repo_key
  and qi.quarantine_id = @quarantine_id
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("quarantine_id", quarantineId) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let versionId = reader.GetGuid(1)
        let status = reader.GetString(2)
        let reason = reader.GetString(3)
        let createdAtUtc = reader.GetFieldValue<DateTime>(4) |> toUtcDateTimeOffset
        let resolvedAtUtc =
            if reader.IsDBNull(5) then None else Some(reader.GetFieldValue<DateTime>(5) |> toUtcDateTimeOffset)

        let resolvedBySubject =
            if reader.IsDBNull(6) then None else Some(reader.GetString(6))

        Ok(
            Some
                { QuarantineId = quarantineId
                  RepoKey = repoKey
                  VersionId = versionId
                  Status = status
                  Reason = reason
                  CreatedAtUtc = createdAtUtc
                  ResolvedAtUtc = resolvedAtUtc
                  ResolvedBySubject = resolvedBySubject }
        )
    else
        Ok None

let resolveQuarantineItemForRepo
    (tenantId: Guid)
    (repoKey: string)
    (quarantineId: Guid)
    (targetStatus: string)
    (resolvedBySubject: string)
    (conn: NpgsqlConnection)
    =
    use updateCmd =
        new NpgsqlCommand(
            """
update quarantine_items qi
set status = @target_status,
    resolved_at = now(),
    resolved_by_subject = @resolved_by_subject
from repos r
where qi.tenant_id = @tenant_id
  and r.tenant_id = qi.tenant_id
  and r.repo_id = qi.repo_id
  and r.repo_key = @repo_key
  and qi.quarantine_id = @quarantine_id
  and qi.status = 'quarantined'
returning qi.version_id, qi.status, qi.reason, qi.created_at, qi.resolved_at, qi.resolved_by_subject;
""",
            conn
        )

    updateCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    updateCmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    updateCmd.Parameters.AddWithValue("quarantine_id", quarantineId) |> ignore
    updateCmd.Parameters.AddWithValue("target_status", targetStatus) |> ignore
    updateCmd.Parameters.AddWithValue("resolved_by_subject", resolvedBySubject) |> ignore

    use updateReader = updateCmd.ExecuteReader()

    if updateReader.Read() then
        let versionId = updateReader.GetGuid(0)
        let status = updateReader.GetString(1)
        let reason = updateReader.GetString(2)
        let createdAtUtc = updateReader.GetFieldValue<DateTime>(3) |> toUtcDateTimeOffset
        let resolvedAtUtc =
            if updateReader.IsDBNull(4) then None else Some(updateReader.GetFieldValue<DateTime>(4) |> toUtcDateTimeOffset)

        let resolvedByValue =
            if updateReader.IsDBNull(5) then None else Some(updateReader.GetString(5))

        Ok(
            QuarantineResolved
                { QuarantineId = quarantineId
                  RepoKey = repoKey
                  VersionId = versionId
                  Status = status
                  Reason = reason
                  CreatedAtUtc = createdAtUtc
                  ResolvedAtUtc = resolvedAtUtc
                  ResolvedBySubject = resolvedByValue }
        )
    else
        updateReader.Close()

        tryReadQuarantineItemForRepo tenantId repoKey quarantineId conn
        |> Result.map (function
            | Some item -> QuarantineAlreadyResolved item.Status
            | None -> QuarantineMissing)

let deleteRepoForTenant (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
delete from repos
where tenant_id = @tenant_id
  and repo_key = @repo_key;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    let rows = cmd.ExecuteNonQuery()
    Ok(rows > 0)

let tryReadBlobLengthByDigest (digest: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select length_bytes
from blobs
where digest = @digest
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Ok None
    else
        match scalar with
        | :? int64 as lengthValue -> Ok(Some lengthValue)
        | :? int32 as lengthValue -> Ok(Some(int64 lengthValue))
        | _ -> Error "Unexpected blob length type returned from database."

let tryReadBlobStorageKeyByDigest (digest: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select storage_key
from blobs
where digest = @digest
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Ok None
    else
        match scalar with
        | :? string as storageKey -> Ok(Some storageKey)
        | _ -> Error "Unexpected blob storage key type returned from database."

let repoHasCommittedBlobDigest (tenantId: Guid) (repoKey: string) (digest: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select exists(
  select 1
  from upload_sessions us
  join repos r on r.repo_id = us.repo_id
  where us.tenant_id = @tenant_id
    and r.repo_key = @repo_key
    and us.state = 'committed'
    and us.committed_blob_digest = @digest
);
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as existsValue -> Ok existsValue
    | _ -> Error "Could not determine committed blob visibility for repository."

let repoBlobDigestBlockedByQuarantine (tenantId: Guid) (repoKey: string) (digest: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select exists(
  select 1
  from artifact_entries ae
  join package_versions pv on pv.version_id = ae.version_id
  join repos r on r.repo_id = pv.repo_id and r.tenant_id = pv.tenant_id
  join quarantine_items qi
    on qi.tenant_id = pv.tenant_id
   and qi.repo_id = pv.repo_id
   and qi.version_id = pv.version_id
  where pv.tenant_id = @tenant_id
    and r.repo_key = @repo_key
    and ae.blob_digest = @digest
    and qi.status in ('quarantined', 'rejected')
);
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("digest", digest) |> ignore

    let scalar = cmd.ExecuteScalar()

    match scalar with
    | :? bool as blocked -> Ok blocked
    | _ -> Error "Could not determine quarantine visibility for repository blob."

let insertUploadSessionForRepo
    (tenantId: Guid)
    (repoKey: string)
    (expectedDigest: string)
    (expectedLength: int64)
    (stateValue: string)
    (objectStagingKey: string option)
    (storageUploadId: string option)
    (committedBlobDigest: string option)
    (createdBySubject: string)
    (expiresAtUtc: DateTimeOffset)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
with target_repo as (
  select repo_id
  from repos
  where tenant_id = @tenant_id
    and repo_key = @repo_key
)
insert into upload_sessions
  (tenant_id, repo_id, expected_digest, expected_length, state, object_staging_key, storage_upload_id, committed_blob_digest, created_by_subject, expires_at, committed_at)
select
  @tenant_id,
  target_repo.repo_id,
  @expected_digest,
  @expected_length,
  @state,
  @object_staging_key,
  @storage_upload_id,
  @committed_blob_digest,
  @created_by_subject,
  @expires_at,
  case when @state = 'committed' then now() else null end
from target_repo
returning upload_id, expected_digest, expected_length, state, object_staging_key, storage_upload_id, committed_blob_digest, expires_at, created_at;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("expected_digest", expectedDigest) |> ignore
    cmd.Parameters.AddWithValue("expected_length", expectedLength) |> ignore
    cmd.Parameters.AddWithValue("state", stateValue) |> ignore

    let objectStagingParam = cmd.Parameters.Add("object_staging_key", NpgsqlDbType.Text)
    objectStagingParam.Value <- (match objectStagingKey with | Some value -> box value | None -> box DBNull.Value)

    let storageUploadParam = cmd.Parameters.Add("storage_upload_id", NpgsqlDbType.Text)
    storageUploadParam.Value <- (match storageUploadId with | Some value -> box value | None -> box DBNull.Value)

    let committedBlobParam = cmd.Parameters.Add("committed_blob_digest", NpgsqlDbType.Char)
    committedBlobParam.Value <- (match committedBlobDigest with | Some value -> box value | None -> box DBNull.Value)

    cmd.Parameters.AddWithValue("created_by_subject", createdBySubject) |> ignore
    cmd.Parameters.AddWithValue("expires_at", expiresAtUtc) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let uploadId = reader.GetGuid(0)
        let returnedDigest = reader.GetString(1)
        let returnedLength = reader.GetInt64(2)
        let returnedState = reader.GetString(3)
        let returnedObjectStagingKey = if reader.IsDBNull(4) then None else Some(reader.GetString(4))
        let returnedStorageUploadId = if reader.IsDBNull(5) then None else Some(reader.GetString(5))
        let returnedCommittedBlobDigest = if reader.IsDBNull(6) then None else Some(reader.GetString(6))
        let expiresAt = reader.GetFieldValue<DateTimeOffset>(7)
        let createdAt = reader.GetFieldValue<DateTimeOffset>(8)

        Ok(
            Some
                { UploadId = uploadId
                  RepoKey = repoKey
                  ExpectedDigest = returnedDigest
                  ExpectedLength = returnedLength
                  State = returnedState
                  ObjectStagingKey = returnedObjectStagingKey
                  StorageUploadId = returnedStorageUploadId
                  CommittedBlobDigest = returnedCommittedBlobDigest
                  ExpiresAtUtc = expiresAt
                  CreatedAtUtc = createdAt }
        )
    else
        Ok None

let tryReadUploadSessionForRepo (tenantId: Guid) (repoKey: string) (uploadId: Guid) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select us.upload_id,
       us.expected_digest,
       us.expected_length,
       us.state,
       us.object_staging_key,
       us.storage_upload_id,
       us.committed_blob_digest,
       us.expires_at,
       us.created_at
from upload_sessions us
join repos r on r.repo_id = us.repo_id
where us.tenant_id = @tenant_id
  and r.repo_key = @repo_key
  and us.upload_id = @upload_id
limit 1;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("upload_id", uploadId) |> ignore

    use reader = cmd.ExecuteReader()

    if reader.Read() then
        let expectedDigest = reader.GetString(1)
        let expectedLength = reader.GetInt64(2)
        let stateValue = reader.GetString(3)
        let objectStagingKey = if reader.IsDBNull(4) then None else Some(reader.GetString(4))
        let storageUploadId = if reader.IsDBNull(5) then None else Some(reader.GetString(5))
        let committedBlobDigest = if reader.IsDBNull(6) then None else Some(reader.GetString(6))
        let expiresAtUtc = reader.GetFieldValue<DateTimeOffset>(7)
        let createdAtUtc = reader.GetFieldValue<DateTimeOffset>(8)

        Ok(
            Some
                { UploadId = uploadId
                  RepoKey = repoKey
                  ExpectedDigest = expectedDigest
                  ExpectedLength = expectedLength
                  State = stateValue
                  ObjectStagingKey = objectStagingKey
                  StorageUploadId = storageUploadId
                  CommittedBlobDigest = committedBlobDigest
                  ExpiresAtUtc = expiresAtUtc
                  CreatedAtUtc = createdAtUtc }
        )
    else
        Ok None

let updateUploadSessionState (tenantId: Guid) (uploadId: Guid) (expectedCurrentState: string) (nextState: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
update upload_sessions
set state = @next_state,
    updated_at = now()
where tenant_id = @tenant_id
  and upload_id = @upload_id
  and state = @expected_current_state
returning upload_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("upload_id", uploadId) |> ignore
    cmd.Parameters.AddWithValue("expected_current_state", expectedCurrentState) |> ignore
    cmd.Parameters.AddWithValue("next_state", nextState) |> ignore

    let scalar = cmd.ExecuteScalar()
    Ok(not (isNull scalar || scalar = box DBNull.Value))

let markUploadSessionAborted (tenantId: Guid) (uploadId: Guid) (reason: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
update upload_sessions
set state = 'aborted',
    aborted_at = now(),
    aborted_reason = @reason,
    updated_at = now()
where tenant_id = @tenant_id
  and upload_id = @upload_id
  and state in ('initiated', 'parts_uploading', 'pending_commit')
returning upload_id;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("upload_id", uploadId) |> ignore
    cmd.Parameters.AddWithValue("reason", reason) |> ignore

    let scalar = cmd.ExecuteScalar()
    Ok(not (isNull scalar || scalar = box DBNull.Value))

let commitUploadSessionWithBlob
    (tenantId: Guid)
    (uploadId: Guid)
    (expectedDigest: string)
    (expectedLength: int64)
    (storageKey: string)
    (objectEtag: string option)
    (conn: NpgsqlConnection)
    =
    use tx = conn.BeginTransaction()

    use upsertBlobCmd =
        new NpgsqlCommand(
            """
insert into blobs (digest, length_bytes, storage_key, object_etag)
values (@digest, @length_bytes, @storage_key, @object_etag)
on conflict (digest)
do update set
  object_etag = coalesce(blobs.object_etag, excluded.object_etag)
returning length_bytes;
""",
            conn,
            tx
        )

    upsertBlobCmd.Parameters.AddWithValue("digest", expectedDigest) |> ignore
    upsertBlobCmd.Parameters.AddWithValue("length_bytes", expectedLength) |> ignore
    upsertBlobCmd.Parameters.AddWithValue("storage_key", storageKey) |> ignore

    let objectEtagParam = upsertBlobCmd.Parameters.Add("object_etag", NpgsqlDbType.Text)
    objectEtagParam.Value <- (match objectEtag with | Some value -> box value | None -> box DBNull.Value)

    let lengthScalar = upsertBlobCmd.ExecuteScalar()

    let persistedLengthResult =
        if isNull lengthScalar || lengthScalar = box DBNull.Value then
            Error "Blob upsert did not return persisted length."
        else
            match lengthScalar with
            | :? int64 as value -> Ok value
            | :? int32 as value -> Ok(int64 value)
            | _ -> Error "Unexpected persisted blob length type."

    match persistedLengthResult with
    | Error err ->
        tx.Rollback()
        Error err
    | Ok persistedLength when persistedLength <> expectedLength ->
        tx.Rollback()
        Error "Digest already exists with a different length."
    | Ok _ ->
        use updateSessionCmd =
            new NpgsqlCommand(
                """
update upload_sessions
set state = 'committed',
    committed_blob_digest = @digest,
    committed_at = now(),
    updated_at = now()
where tenant_id = @tenant_id
  and upload_id = @upload_id
  and state = 'pending_commit'
returning upload_id;
""",
                conn,
                tx
            )

        updateSessionCmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
        updateSessionCmd.Parameters.AddWithValue("upload_id", uploadId) |> ignore
        updateSessionCmd.Parameters.AddWithValue("digest", expectedDigest) |> ignore

        let updateScalar = updateSessionCmd.ExecuteScalar()
        let updated = not (isNull updateScalar || updateScalar = box DBNull.Value)

        if updated then
            tx.Commit()
            Ok true
        else
            tx.Rollback()
            Ok false

let private tryAuthenticateWithOidcToken (state: AppState) (rawToken: string) =
    match state.OidcTokenValidation with
    | None -> Ok None
    | Some oidcConfig ->
        match validateOidcBearerToken oidcConfig rawToken with
        | Error _ -> Ok None
        | Ok oidcPrincipal ->
            withConnection state (fun conn ->
                ensureTenantId state conn
                |> Result.map (fun tenantId ->
                    Some
                        { TenantId = tenantId
                          TokenId = oidcPrincipal.TokenId
                          Subject = oidcPrincipal.Subject
                          Scopes = oidcPrincipal.Scopes
                          AuthSource = "oidc" }))

let tryAuthenticate (state: AppState) (ctx: HttpContext) =
    match tryReadBearerToken ctx with
    | None -> Ok None
    | Some rawToken ->
        let tokenHash = toTokenHash rawToken
        match withConnection state (tryReadPrincipalByTokenHash tokenHash) with
        | Error err -> Error err
        | Ok(Some principal) -> Ok(Some principal)
        | Ok None -> tryAuthenticateWithOidcToken state rawToken

let requireRole (state: AppState) (ctx: HttpContext) (repoKey: string) (requiredRole: RepoRole) =
    match tryAuthenticate state ctx with
    | Error err -> Error(serviceUnavailable err)
    | Ok None -> Error(unauthorized "Missing or invalid bearer token.")
    | Ok(Some principal) when Authorization.hasRole principal.Scopes repoKey requiredRole -> Ok principal
    | Ok(Some _) -> Error(forbidden "Caller does not have the required repository role.")

let serializeAuditDetails (details: Map<string, string>) =
    details |> Map.toSeq |> dict |> JsonSerializer.Serialize

let tryParseAuditDetails (jsonText: string) =
    try
        let normalized = if String.IsNullOrWhiteSpace jsonText then "{}" else jsonText
        use doc = JsonDocument.Parse(normalized)
        let root = doc.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Error "Audit details json must be an object."
        else
            root.EnumerateObject()
            |> Seq.map (fun prop ->
                let value =
                    match prop.Value.ValueKind with
                    | JsonValueKind.String -> prop.Value.GetString()
                    | JsonValueKind.True
                    | JsonValueKind.False
                    | JsonValueKind.Number
                    | JsonValueKind.Array
                    | JsonValueKind.Object
                    | JsonValueKind.Null -> prop.Value.GetRawText()
                    | _ -> prop.Value.ToString()

                prop.Name, value)
            |> Map.ofSeq
            |> Ok
    with ex ->
        Error $"Invalid audit details json: {ex.Message}"

let insertAuditRecord
    (tenantId: Guid)
    (actor: string)
    (action: string)
    (resourceType: string)
    (resourceId: string)
    (details: Map<string, string>)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
insert into audit_log (tenant_id, actor_subject, action, resource_type, resource_id, details)
values (@tenant_id, @actor_subject, @action, @resource_type, @resource_id, @details);
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("actor_subject", actor) |> ignore
    cmd.Parameters.AddWithValue("action", action) |> ignore
    cmd.Parameters.AddWithValue("resource_type", resourceType) |> ignore
    cmd.Parameters.AddWithValue("resource_id", resourceId) |> ignore

    let detailsParam = cmd.Parameters.Add("details", NpgsqlDbType.Jsonb)
    detailsParam.Value <- serializeAuditDetails details

    let rows = cmd.ExecuteNonQuery()
    if rows = 1 then Ok() else Error "Audit insert did not affect expected rows."

let writeAudit
    (state: AppState)
    (tenantId: Guid)
    (actor: string)
    (action: string)
    (resourceType: string)
    (resourceId: string)
    (details: Map<string, string>)
    =
    withConnection state (insertAuditRecord tenantId actor action resourceType resourceId details)

let writeUploadAudit (state: AppState) (principal: Principal) (action: string) (uploadId: Guid) (details: Map<string, string>) =
    writeAudit
        state
        principal.TenantId
        principal.Subject
        action
        "upload_session"
        (uploadId.ToString())
        details

let readAuditRecords (tenantId: Guid) (limit: int) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select audit_id, tenant_id, actor_subject, action, resource_type, resource_id, details::text, occurred_at
from audit_log
where tenant_id = @tenant_id
order by occurred_at desc
limit @limit;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("limit", limit) |> ignore

    use reader = cmd.ExecuteReader()
    let entries = ResizeArray<AuditRecord>()

    let rec loop () =
        if reader.Read() then
            let auditId = reader.GetInt64(0)
            let tenantIdValue = reader.GetGuid(1)
            let actor = reader.GetString(2)
            let action = reader.GetString(3)
            let resourceType = reader.GetString(4)
            let resourceId = reader.GetString(5)
            let detailsJson = if reader.IsDBNull(6) then "{}" else reader.GetString(6)
            let occurredAtUtc = reader.GetFieldValue<DateTime>(7) |> toUtcDateTimeOffset

            match tryParseAuditDetails detailsJson with
            | Error err -> Error err
            | Ok details ->
                entries.Add(
                    { AuditId = auditId
                      TenantId = tenantIdValue
                      Actor = actor
                      Action = action
                      ResourceType = resourceType
                      ResourceId = resourceId
                      OccurredAtUtc = occurredAtUtc
                      Details = details }
                )
                loop ()
        else
            Ok(entries |> Seq.toList)

    loop ()

let readScopesFromBindings (tenantId: Guid) (subject: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select r.repo_key, rb.roles
from role_bindings rb
join repos r on r.repo_id = rb.repo_id
where rb.tenant_id = @tenant_id
  and rb.subject = @subject;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("subject", subject) |> ignore

    use reader = cmd.ExecuteReader()
    let scopes = ResizeArray<RepoScope>()

    let rec loop () =
        if reader.Read() then
            let repoKey = reader.GetString(0)
            let roleValues = reader.GetFieldValue<string[]>(1)

            let parseResult =
                roleValues
                |> Array.toList
                |> List.fold
                    (fun acc roleText ->
                        match acc with
                        | Error err -> Error err
                        | Ok () ->
                            match RepoRole.tryParse roleText with
                            | Error err -> Error err
                            | Ok parsedRole ->
                                match RepoScope.tryCreate repoKey parsedRole with
                                | Ok scope ->
                                    scopes.Add(scope)
                                    Ok()
                                | Error err -> Error err)
                    (Ok())

            match parseResult with
            | Error err -> Error $"Invalid persisted role binding value: {err}"
            | Ok () -> loop ()
        else
            Ok(scopes |> Seq.distinctBy RepoScope.value |> Seq.toList)

    loop ()

let upsertRoleBindingForRepo
    (tenantId: Guid)
    (repoKey: string)
    (subject: string)
    (roles: Set<RepoRole>)
    (updatedBySubject: string)
    (conn: NpgsqlConnection)
    =
    use cmd =
        new NpgsqlCommand(
            """
with target_repo as (
  select repo_id
  from repos
  where tenant_id = @tenant_id
    and repo_key = @repo_key
)
insert into role_bindings (tenant_id, repo_id, subject, roles, updated_by_subject, updated_at)
select @tenant_id, target_repo.repo_id, @subject, @roles, @updated_by_subject, now()
from target_repo
on conflict (tenant_id, repo_id, subject)
do update set
  roles = excluded.roles,
  updated_by_subject = excluded.updated_by_subject,
  updated_at = now()
returning updated_at;
""",
            conn
        )

    let roleValues = roles |> Seq.map RepoRole.value |> Seq.toArray
    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore
    cmd.Parameters.AddWithValue("subject", subject) |> ignore
    cmd.Parameters.AddWithValue("updated_by_subject", updatedBySubject) |> ignore

    let rolesParam = cmd.Parameters.Add("roles", NpgsqlDbType.Array ||| NpgsqlDbType.Text)
    rolesParam.Value <- roleValues

    let scalar = cmd.ExecuteScalar()

    if isNull scalar || scalar = box DBNull.Value then
        Ok None
    else
        let updatedAtResult =
            match scalar with
            | :? DateTimeOffset as dto -> Ok dto
            | :? DateTime as dt -> Ok(toUtcDateTimeOffset dt)
            | _ -> Error "Unexpected timestamp type returned for role binding upsert."

        match updatedAtResult with
        | Error err -> Error err
        | Ok updatedAt ->
            Ok(
                Some
                    { RepoKey = repoKey
                      Subject = subject
                      Roles = roles
                      UpdatedAtUtc = updatedAt }
            )

let readRoleBindingsForRepo (tenantId: Guid) (repoKey: string) (conn: NpgsqlConnection) =
    use cmd =
        new NpgsqlCommand(
            """
select rb.subject, rb.roles, rb.updated_at
from role_bindings rb
join repos r on r.repo_id = rb.repo_id
where rb.tenant_id = @tenant_id
  and r.repo_key = @repo_key
order by rb.subject;
""",
            conn
        )

    cmd.Parameters.AddWithValue("tenant_id", tenantId) |> ignore
    cmd.Parameters.AddWithValue("repo_key", repoKey) |> ignore

    use reader = cmd.ExecuteReader()
    let bindings = ResizeArray<RoleBindingRecord>()

    let rec loop () =
        if reader.Read() then
            let subject = reader.GetString(0)
            let roleValues = reader.GetFieldValue<string[]>(1)
            let updatedAt = reader.GetFieldValue<DateTime>(2) |> toUtcDateTimeOffset

            let parseResult =
                roleValues
                |> Array.toList
                |> List.fold
                    (fun acc roleText ->
                        match acc with
                        | Error err -> Error err
                        | Ok roles ->
                            match RepoRole.tryParse roleText with
                            | Error err -> Error err
                            | Ok role -> Ok(Set.add role roles))
                    (Ok Set.empty)

            match parseResult with
            | Error err -> Error $"Invalid persisted role binding value: {err}"
            | Ok roles ->
                bindings.Add(
                    { RepoKey = repoKey
                      Subject = subject
                      Roles = roles
                      UpdatedAtUtc = updatedAt }
                )
                loop ()
        else
            Ok(bindings |> Seq.toList)

    loop ()

let validateRepoRequest (request: CreateRepoRequest) =
    let repoKey = normalizeRepoKey request.RepoKey
    let repoType = normalizeText request.RepoType |> fun value -> value.ToLowerInvariant()
    let upstreamUrl = normalizeText request.UpstreamUrl
    let members = if isNull request.MemberRepos then [||] else request.MemberRepos

    if String.IsNullOrWhiteSpace repoKey then
        Error "repoKey is required."
    elif repoKey.Contains ":" then
        Error "repoKey cannot contain ':'."
    else
        match repoType with
        | "local" ->
            Ok
                { RepoKey = repoKey
                  RepoType = repoType
                  UpstreamUrl = None
                  MemberRepos = []
                  CreatedAtUtc = nowUtc ()
                  UpdatedAtUtc = nowUtc () }
        | "remote" ->
            if String.IsNullOrWhiteSpace upstreamUrl then
                Error "Remote repositories require upstreamUrl."
            else
                let isUri, parsedUri = Uri.TryCreate(upstreamUrl, UriKind.Absolute)
                if not isUri then
                    Error "upstreamUrl must be a valid absolute URI."
                else
                    Ok
                        { RepoKey = repoKey
                          RepoType = repoType
                          UpstreamUrl = Some(parsedUri.ToString())
                          MemberRepos = []
                          CreatedAtUtc = nowUtc ()
                          UpdatedAtUtc = nowUtc () }
        | "virtual" ->
            let normalizedMembers =
                members
                |> Array.map normalizeRepoKey
                |> Array.filter (fun memberRepo -> not (String.IsNullOrWhiteSpace memberRepo))
                |> Array.distinct
                |> Array.toList

            if normalizedMembers.IsEmpty then
                Error "Virtual repositories require at least one member repo key."
            else
                Ok
                    { RepoKey = repoKey
                      RepoType = repoType
                      UpstreamUrl = None
                      MemberRepos = normalizedMembers
                      CreatedAtUtc = nowUtc ()
                      UpdatedAtUtc = nowUtc () }
        | _ -> Error "repoType must be one of: local, remote, virtual."

let abortMultipartUploadBestEffort
    (state: AppState)
    (objectStagingKey: string)
    (storageUploadId: string)
    (cancellationToken: Threading.CancellationToken)
    =
    if not (String.IsNullOrWhiteSpace objectStagingKey || String.IsNullOrWhiteSpace storageUploadId) then
        match
            state.ObjectStorageClient.AbortMultipartUpload(objectStagingKey, storageUploadId, cancellationToken).Result
        with
        | _ -> ()

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    let connectionString =
        match builder.Configuration.GetConnectionString("Postgres") with
        | null
        | "" -> "Host=localhost;Port=5432;Username=artifortress;Password=artifortress;Database=artifortress"
        | value -> value

    let state =
        let objectStorageConfig =
            match ObjectStorage.tryReadConfig builder.Configuration with
            | Ok config -> config
            | Error err -> failwith err

        let objectStorageClient = ObjectStorage.createClient objectStorageConfig

        let policyEvaluationTimeoutMs =
            let raw = builder.Configuration.["Policy:EvaluationTimeoutMs"]

            match Int32.TryParse raw with
            | true, value when value > 0 -> value
            | _ -> 250

        let defaultTombstoneRetentionDays =
            let raw = builder.Configuration.["Lifecycle:DefaultTombstoneRetentionDays"]

            match Int32.TryParse raw with
            | true, value when value >= 1 && value <= 3650 -> value
            | _ -> 30

        let defaultGcRetentionGraceHours =
            let raw = builder.Configuration.["Lifecycle:DefaultGcRetentionGraceHours"]

            match Int32.TryParse raw with
            | true, value when value >= 0 && value <= 24 * 365 -> value
            | _ -> 24

        let defaultGcBatchSize =
            let raw = builder.Configuration.["Lifecycle:DefaultGcBatchSize"]

            match Int32.TryParse raw with
            | true, value when value >= 1 && value <= 5000 -> value
            | _ -> 200

        let oidcTokenValidation =
            let enabled = builder.Configuration.["Auth:Oidc:Enabled"] |> parseBoolean

            if not enabled then
                None
            else
                let issuer = normalizeText builder.Configuration.["Auth:Oidc:Issuer"]
                let audience = normalizeText builder.Configuration.["Auth:Oidc:Audience"]
                let hs256SharedSecret = normalizeText builder.Configuration.["Auth:Oidc:Hs256SharedSecret"]
                let jwksJson = normalizeText builder.Configuration.["Auth:Oidc:JwksJson"]
                let roleMappingsRaw = normalizeText builder.Configuration.["Auth:Oidc:RoleMappings"]

                if String.IsNullOrWhiteSpace issuer then
                    failwith "Auth:Oidc:Issuer is required when Auth:Oidc:Enabled=true."
                elif String.IsNullOrWhiteSpace audience then
                    failwith "Auth:Oidc:Audience is required when Auth:Oidc:Enabled=true."
                else
                    let hs256SecretOption =
                        if String.IsNullOrWhiteSpace hs256SharedSecret then None else Some hs256SharedSecret

                    let rs256SigningKeys =
                        if String.IsNullOrWhiteSpace jwksJson then
                            []
                        else
                            match parseOidcJwksJson jwksJson with
                            | Ok keys -> keys
                            | Error err -> failwith $"Auth:Oidc:JwksJson is invalid: {err}"

                    if hs256SecretOption.IsNone && rs256SigningKeys.IsEmpty then
                        failwith
                            "Auth:Oidc:Enabled=true requires at least one signing mode: Auth:Oidc:Hs256SharedSecret and/or Auth:Oidc:JwksJson."

                    let claimRoleMappings =
                        match parseOidcClaimRoleMappings roleMappingsRaw with
                        | Ok mappings -> mappings
                        | Error err -> failwith $"Auth:Oidc:RoleMappings is invalid: {err}"

                    Some
                        { Issuer = issuer
                          Audience = audience
                          Hs256SharedSecret = hs256SecretOption
                          Rs256SigningKeys = rs256SigningKeys
                          ClaimRoleMappings = claimRoleMappings }

        let samlIntegration =
            let enabled = builder.Configuration.["Auth:Saml:Enabled"] |> parseBoolean
            let metadataUrl = normalizeText builder.Configuration.["Auth:Saml:IdpMetadataUrl"]
            let serviceProviderEntityId = normalizeText builder.Configuration.["Auth:Saml:ServiceProviderEntityId"]
            let expectedIssuer = normalizeText builder.Configuration.["Auth:Saml:ExpectedIssuer"]
            let roleMappingsRaw = normalizeText builder.Configuration.["Auth:Saml:RoleMappings"]

            let issuedPatTtlMinutes =
                let raw = builder.Configuration.["Auth:Saml:IssuedPatTtlMinutes"]

                match Int32.TryParse raw with
                | true, value when value >= 5 && value <= 1440 -> value
                | _ -> 60

            let optionalValue value =
                if String.IsNullOrWhiteSpace value then None else Some value

            let parsedRoleMappings =
                match parseOidcClaimRoleMappings roleMappingsRaw with
                | Ok mappings -> mappings
                | Error err -> failwith $"Auth:Saml:RoleMappings is invalid: {err}"

            { Enabled = enabled
              IdpMetadataUrl = optionalValue metadataUrl
              ServiceProviderEntityId = optionalValue serviceProviderEntityId
              ExpectedIssuer = optionalValue expectedIssuer
              RoleMappings = parsedRoleMappings
              IssuedPatTtlMinutes = issuedPatTtlMinutes }

        if samlIntegration.Enabled then
            let metadataUrlResult =
                match samlIntegration.IdpMetadataUrl with
                | None -> Error "Auth:Saml:IdpMetadataUrl is required when Auth:Saml:Enabled=true."
                | Some url ->
                    match Uri.TryCreate(url, UriKind.Absolute) with
                    | true, _ -> Ok()
                    | _ -> Error "Auth:Saml:IdpMetadataUrl must be a valid absolute URI."

            let serviceProviderEntityIdResult =
                match samlIntegration.ServiceProviderEntityId with
                | None -> Error "Auth:Saml:ServiceProviderEntityId is required when Auth:Saml:Enabled=true."
                | Some _ -> Ok()

            let expectedIssuerResult =
                match samlIntegration.ExpectedIssuer with
                | None -> Error "Auth:Saml:ExpectedIssuer is required when Auth:Saml:Enabled=true."
                | Some _ -> Ok()

            match metadataUrlResult, serviceProviderEntityIdResult, expectedIssuerResult with
            | Error err, _, _ -> failwith err
            | _, Error err, _ -> failwith err
            | _, _, Error err -> failwith err
            | Ok(), Ok(), Ok() -> ()

        { ConnectionString = connectionString
          TenantSlug = "default"
          TenantName = "Default Tenant"
          ObjectStorageClient = objectStorageClient
          PresignPartTtlSeconds = objectStorageConfig.PresignPartTtlSeconds
          PolicyEvaluationTimeoutMs = policyEvaluationTimeoutMs
          DefaultTombstoneRetentionDays = defaultTombstoneRetentionDays
          DefaultGcRetentionGraceHours = defaultGcRetentionGraceHours
          DefaultGcBatchSize = defaultGcBatchSize
          OidcTokenValidation = oidcTokenValidation
          SamlIntegration = samlIntegration }

    let app = builder.Build()

    app.MapGet(
        "/",
        Func<IResult>(fun () ->
            Results.Ok(
                {| service = "artifortress-api"
                   environment = builder.Environment.EnvironmentName
                   timestampUtc = nowUtc () |}
            ))
    )
    |> ignore

    app.MapGet("/health/live", Func<IResult>(fun () -> Results.Ok({| status = "ok" |}))) |> ignore

    app.MapGet(
        "/health/ready",
        Func<HttpContext, Threading.Tasks.Task<IResult>>(fun ctx ->
            task {
                let postgresHealthy, postgresDetail = checkPostgresReadiness state
                let! objectStorageHealthy, objectStorageDetail = checkObjectStorageReadiness state ctx.RequestAborted

                let dependencies =
                    [| {| name = "postgres"
                          healthy = postgresHealthy
                          detail = postgresDetail |}
                       {| name = "object_storage"
                          healthy = objectStorageHealthy
                          detail = objectStorageDetail |} |]

                if postgresHealthy && objectStorageHealthy then
                    return
                        Results.Ok(
                            {| status = "ready"
                               checkedAtUtc = nowUtc ()
                               dependencies = dependencies |}
                        )
                else
                    return
                        Results.Json(
                            {| status = "not_ready"
                               checkedAtUtc = nowUtc ()
                               dependencies = dependencies |},
                            statusCode = StatusCodes.Status503ServiceUnavailable
                        )
            })
    )
    |> ignore

    app.MapGet(
        "/v1/auth/whoami",
        Func<HttpContext, IResult>(fun ctx ->
            match tryAuthenticate state ctx with
            | Error err -> serviceUnavailable err
            | Ok None -> unauthorized "Missing or invalid bearer token."
            | Ok(Some principal) ->
                let scopeValues = principal.Scopes |> List.map RepoScope.value
                Results.Ok({| subject = principal.Subject; scopes = scopeValues; authSource = principal.AuthSource |}))
    )
    |> ignore

    app.MapGet(
        "/v1/auth/saml/metadata",
        Func<HttpContext, IResult>(fun ctx ->
            if not state.SamlIntegration.Enabled then
                Results.Json(
                    {| error = "not_found"
                       message = "SAML metadata endpoint is disabled." |},
                    statusCode = StatusCodes.Status404NotFound
                )
            else
                match state.SamlIntegration.ServiceProviderEntityId with
                | None -> serviceUnavailable "SAML service provider entity id is not configured."
                | Some entityId ->
                    let acsUrl = buildAbsoluteUrl ctx "/v1/auth/saml/acs"

                    let metadataXml =
                        buildSamlServiceProviderMetadataXml entityId acsUrl state.SamlIntegration.IdpMetadataUrl

                    Results.Text(metadataXml, "application/samlmetadata+xml; charset=utf-8"))
    )
    |> ignore

    app.MapPost(
        "/v1/auth/saml/acs",
        Func<HttpContext, IResult>(fun ctx ->
            if not state.SamlIntegration.Enabled then
                Results.Json(
                    {| error = "not_found"
                       message = "SAML ACS endpoint is disabled." |},
                    statusCode = StatusCodes.Status404NotFound
                )
            else
                match readSamlAcsRequest ctx with
                | Error err -> badRequest err
                | Ok(samlResponse, relayState) ->
                    match validateSamlAcsPayload samlResponse with
                    | Error err -> badRequest err
                    | Ok payloadBytes ->
                        match readSamlAssertionEnvelope payloadBytes with
                        | Error err -> badRequest err
                        | Ok envelope ->
                            match validateSamlAssertion state.SamlIntegration envelope with
                            | Error err -> unauthorized err
                            | Ok(subject, scopes) ->
                                let normalizedSubject = normalizeSubject subject

                                if String.IsNullOrWhiteSpace normalizedSubject then
                                    badRequest "SAML assertion subject is invalid after normalization."
                                else
                                    let rawToken = createPlainToken ()
                                    let tokenHash = toTokenHash rawToken
                                    let tokenId = Guid.NewGuid()
                                    let expiry = (nowUtc ()).AddMinutes(float state.SamlIntegration.IssuedPatTtlMinutes)
                                    let actor = $"saml:{normalizedSubject}"

                                    match
                                        withConnection state (fun conn ->
                                            ensureTenantId state conn
                                            |> Result.bind (fun tenantId ->
                                                insertPat tenantId normalizedSubject tokenId tokenHash scopes expiry actor conn
                                                |> Result.map (fun () -> tenantId)))
                                    with
                                    | Error err -> serviceUnavailable err
                                    | Ok tenantId ->
                                        match
                                            writeAudit
                                                state
                                                tenantId
                                                actor
                                                "auth.saml.exchange"
                                                "token"
                                                (tokenId.ToString())
                                                (Map.ofList [ "subject", normalizedSubject; "relayState", relayState ])
                                        with
                                        | Error err -> serviceUnavailable err
                                        | Ok() ->
                                            Results.Ok(
                                                {| tokenId = tokenId
                                                   token = rawToken
                                                   subject = normalizedSubject
                                                   scopes = scopes |> List.map RepoScope.value
                                                   expiresAtUtc = expiry
                                                   authSource = "saml"
                                                   relayState = relayState |}
                                            ))
    )
    |> ignore

    app.MapPost(
        "/v1/auth/pats",
        Func<HttpContext, IResult>(fun ctx ->
            let bootstrapHeader = ctx.Request.Headers.["X-Bootstrap-Token"].ToString()
            let bootstrapSecret = builder.Configuration.["Auth:BootstrapToken"]
            let isBootstrapAuthorized =
                not (String.IsNullOrWhiteSpace bootstrapSecret)
                && secureEquals bootstrapHeader bootstrapSecret

            match tryAuthenticate state ctx with
            | Error err -> serviceUnavailable err
            | Ok caller ->
                let callerIsAdmin =
                    match caller with
                    | Some principal -> Authorization.hasRole principal.Scopes "*" RepoRole.Admin
                    | None -> false

                match withConnection state (fun conn ->
                    ensureTenantId state conn
                    |> Result.bind (fun tenantId ->
                        hasAnyTokensForTenant tenantId conn |> Result.map (fun hasTokens -> tenantId, hasTokens))) with
                | Error err -> serviceUnavailable err
                | Ok(tenantId, hasTokens) ->
                    let allowIssuance = not hasTokens || isBootstrapAuthorized || callerIsAdmin

                    if not allowIssuance then
                        forbidden "PAT issuance requires bootstrap token or admin scope."
                    else
                        match readJsonBody<CreatePatRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            let subject = normalizeSubject request.Subject

                            if String.IsNullOrWhiteSpace subject then
                                badRequest "Subject is required."
                            else
                                let ttlMinutes = request.TtlMinutes

                                if ttlMinutes < 5 || ttlMinutes > 1440 then
                                    badRequest "ttlMinutes must be between 5 and 1440."
                                else
                                    let requestedScopes = if isNull request.Scopes then [||] else request.Scopes
                                    let effectiveScopeInputResult =
                                        if requestedScopes.Length = 0 then
                                            withConnection state (readScopesFromBindings tenantId subject)
                                            |> Result.map (fun derivedScopes -> derivedScopes |> List.map RepoScope.value |> List.toArray)
                                        else
                                            Ok requestedScopes

                                    match effectiveScopeInputResult with
                                    | Error err -> serviceUnavailable err
                                    | Ok effectiveScopeInput ->
                                        if effectiveScopeInput.Length = 0 then
                                            badRequest "No scopes were provided and no role bindings were found for the subject."
                                        else
                                            match parseScopes effectiveScopeInput with
                                            | Error err -> badRequest err
                                            | Ok scopes ->
                                                let rawToken = createPlainToken ()
                                                let tokenHash = toTokenHash rawToken
                                                let tokenId = Guid.NewGuid()
                                                let expiry = (nowUtc ()).AddMinutes(float ttlMinutes)

                                                let actor =
                                                    match caller with
                                                    | Some principal -> principal.Subject
                                                    | None when isBootstrapAuthorized -> "bootstrap"
                                                    | None -> "anonymous"

                                                match withConnection state (insertPat tenantId subject tokenId tokenHash scopes expiry actor) with
                                                | Error err -> serviceUnavailable err
                                                | Ok() ->
                                                    match
                                                        writeAudit
                                                            state
                                                            tenantId
                                                            actor
                                                            "auth.pat.issued"
                                                            "token"
                                                            (tokenId.ToString())
                                                            (Map.ofList [ "subject", subject; "ttlMinutes", ttlMinutes.ToString() ])
                                                    with
                                                    | Error err -> serviceUnavailable err
                                                    | Ok() ->
                                                        Results.Ok(
                                                            {| tokenId = tokenId
                                                               token = rawToken
                                                               subject = subject
                                                               scopes = scopes |> List.map RepoScope.value
                                                               expiresAtUtc = expiry |}
                                                        ))
    )
    |> ignore

    app.MapPost(
        "/v1/auth/pats/revoke",
        Func<HttpContext, IResult>(fun ctx ->
            match requireRole state ctx "*" RepoRole.Admin with
            | Error result -> result
            | Ok principal ->
                match readJsonBody<RevokePatRequest> ctx with
                | Error err -> badRequest err
                | Ok request ->
                    match withConnection state (revokePat principal.TenantId request.TokenId principal.Subject) with
                    | Error err -> serviceUnavailable err
                    | Ok false -> Results.NotFound({| error = "not_found"; message = "Token id was not found." |})
                    | Ok true ->
                        match
                            writeAudit
                                state
                                principal.TenantId
                                principal.Subject
                                "auth.pat.revoked"
                                "token"
                                (request.TokenId.ToString())
                                Map.empty
                        with
                        | Error err -> serviceUnavailable err
                        | Ok() -> Results.Ok({| tokenId = request.TokenId; status = "revoked" |}))
    )
    |> ignore

    app.MapGet(
        "/v1/repos",
        Func<HttpContext, IResult>(fun ctx ->
            match tryAuthenticate state ctx with
            | Error err -> serviceUnavailable err
            | Ok None -> unauthorized "Missing or invalid bearer token."
            | Ok(Some principal) ->
                match withConnection state (readReposForTenant principal.TenantId) with
                | Error err -> serviceUnavailable err
                | Ok repos ->
                    let visibleRepos =
                        repos
                        |> List.filter (fun repo -> Authorization.hasRole principal.Scopes repo.RepoKey RepoRole.Read)
                        |> List.sortBy (fun repo -> repo.RepoKey)
                        |> List.toArray

                    Results.Ok(visibleRepos))
    )
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Read with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (tryReadRepoByKey principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok(Some repo) -> Results.Ok(repo)
                    | Ok None -> Results.NotFound({| error = "not_found"; message = "Repository was not found." |}))
    )
    |> ignore

    app.MapPost(
        "/v1/repos",
        Func<HttpContext, IResult>(fun ctx ->
            match requireRole state ctx "*" RepoRole.Admin with
            | Error result -> result
            | Ok principal ->
                match readJsonBody<CreateRepoRequest> ctx with
                | Error err -> badRequest err
                | Ok request ->
                    match validateRepoRequest request with
                    | Error err -> badRequest err
                    | Ok repo ->
                        match withConnection state (insertRepoForTenant principal.TenantId repo) with
                        | Error err -> serviceUnavailable err
                        | Ok false -> conflict "Repository key already exists."
                        | Ok true ->
                            match
                                writeAudit
                                    state
                                    principal.TenantId
                                    principal.Subject
                                    "repo.created"
                                    "repo"
                                    repo.RepoKey
                                    (Map.ofList [ "repoType", repo.RepoType ])
                            with
                            | Error err -> serviceUnavailable err
                            | Ok() -> Results.Created($"/v1/repos/{repo.RepoKey}", repo))
    )
    |> ignore

    app.MapDelete(
        "/v1/repos/{repoKey}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Admin with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (deleteRepoForTenant principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok true ->
                        match writeAudit state principal.TenantId principal.Subject "repo.deleted" "repo" repoKey Map.empty with
                        | Error err -> serviceUnavailable err
                        | Ok() -> Results.Ok({| repoKey = repoKey; status = "deleted" |})
                    | Ok false ->
                        Results.NotFound({| error = "not_found"; message = "Repository was not found." |}))
    )
    |> ignore

    app.MapPut(
        "/v1/repos/{repoKey}/bindings/{subject}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let subject = normalizeSubject (ctx.Request.RouteValues["subject"].ToString())

            if String.IsNullOrWhiteSpace repoKey || String.IsNullOrWhiteSpace subject then
                badRequest "repoKey and subject route parameters are required."
            else
                match requireRole state ctx repoKey RepoRole.Admin with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok false -> Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                    | Ok true ->
                        match readJsonBody<UpsertRoleBindingRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match parseRoles request.Roles with
                            | Error err -> badRequest err
                            | Ok roles ->
                                match
                                    withConnection state (upsertRoleBindingForRepo principal.TenantId repoKey subject roles principal.Subject)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok None ->
                                    Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                                | Ok(Some binding) ->
                                    let key = $"{repoKey}:{subject}"

                                    match
                                        writeAudit
                                            state
                                            principal.TenantId
                                            principal.Subject
                                            "repo.binding.upserted"
                                            "repo_binding"
                                            key
                                            (Map.ofList [ "roles", roles |> Seq.map RepoRole.value |> String.concat "," ])
                                    with
                                    | Error err -> serviceUnavailable err
                                    | Ok() ->
                                        Results.Ok(
                                            {| repoKey = binding.RepoKey
                                               subject = binding.Subject
                                               roles = binding.Roles |> Seq.map RepoRole.value |> Seq.toArray
                                               updatedAtUtc = binding.UpdatedAtUtc |}
                                        ))
    )
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}/bindings",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Admin with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok false ->
                        Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                    | Ok true ->
                        match withConnection state (readRoleBindingsForRepo principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok bindings ->
                            let response =
                                bindings
                                |> List.sortBy (fun binding -> binding.Subject)
                                |> List.map (fun binding ->
                                    {| repoKey = binding.RepoKey
                                       subject = binding.Subject
                                       roles = binding.Roles |> Seq.map RepoRole.value |> Seq.toArray
                                       updatedAtUtc = binding.UpdatedAtUtc |})
                                |> List.toArray

                            Results.Ok(response))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/packages/versions/drafts",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Write with
                | Error result -> result
                | Ok principal ->
                    match readJsonBody<CreateDraftVersionRequest> ctx with
                    | Error err -> badRequest err
                    | Ok request ->
                        match validateDraftVersionRequest request with
                        | Error err -> badRequest err
                        | Ok(packageType, packageNamespace, packageName, version) ->
                            match
                                withConnection
                                    state
                                    (createOrGetDraftVersionForRepo
                                        principal.TenantId
                                        repoKey
                                        packageType
                                        packageNamespace
                                        packageName
                                        version
                                        principal.Subject)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok RepoMissing ->
                                Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                            | Ok(VersionStateConflict stateValue) ->
                                conflict $"Version already exists in state '{stateValue}' and cannot be reused as a draft."
                            | Ok(DraftCreated draftVersion) ->
                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "package.version.draft.created"
                                        "package_version"
                                        (draftVersion.VersionId.ToString())
                                        (Map.ofList
                                            [ "repoKey", repoKey
                                              "packageType", draftVersion.PackageType
                                              "packageName", draftVersion.PackageName
                                              "version", draftVersion.Version ])
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Created(
                                        $"/v1/repos/{repoKey}/packages/versions/{draftVersion.Version}",
                                        {| versionId = draftVersion.VersionId
                                           repoKey = draftVersion.RepoKey
                                           packageType = draftVersion.PackageType
                                           packageNamespace = draftVersion.PackageNamespace
                                           packageName = draftVersion.PackageName
                                           version = draftVersion.Version
                                           state = draftVersion.State
                                           createdAtUtc = draftVersion.CreatedAtUtc
                                           reusedDraft = false |}
                                    )
                            | Ok(DraftExisting draftVersion) ->
                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "package.version.draft.reused"
                                        "package_version"
                                        (draftVersion.VersionId.ToString())
                                        (Map.ofList
                                            [ "repoKey", repoKey
                                              "packageType", draftVersion.PackageType
                                              "packageName", draftVersion.PackageName
                                              "version", draftVersion.Version ])
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Ok(
                                        {| versionId = draftVersion.VersionId
                                           repoKey = draftVersion.RepoKey
                                           packageType = draftVersion.PackageType
                                           packageNamespace = draftVersion.PackageNamespace
                                           packageName = draftVersion.PackageName
                                           version = draftVersion.Version
                                           state = draftVersion.State
                                           createdAtUtc = draftVersion.CreatedAtUtc
                                           reusedDraft = true |}
                                    )
    ))
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/packages/versions/{versionId}/entries",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let versionIdRaw = normalizeText (ctx.Request.RouteValues["versionId"].ToString())

            let parsedVersionId =
                match Guid.TryParse(versionIdRaw) with
                | true, parsed -> Ok parsed
                | _ -> Error "versionId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedVersionId with
                | Error err -> badRequest err
                | Ok versionId ->
                    match requireRole state ctx repoKey RepoRole.Write with
                    | Error result -> result
                    | Ok principal ->
                        match readJsonBody<UpsertArtifactEntriesRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match validateArtifactEntriesRequest request with
                            | Error err -> badRequest err
                            | Ok entries ->
                                match
                                    withConnection
                                        state
                                        (upsertArtifactEntriesForDraftVersion
                                            principal.TenantId
                                            repoKey
                                            versionId
                                            entries)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok UpsertEntriesVersionMissing ->
                                    Results.NotFound({| error = "not_found"; message = "Package version was not found." |})
                                | Ok(UpsertEntriesStateConflict stateValue) ->
                                    conflict $"Package version is in state '{stateValue}' and cannot be modified."
                                | Ok(UpsertEntriesBlobMissing digest) ->
                                    conflict $"Blob digest '{digest}' does not exist."
                                | Ok(UpsertEntriesBlobNotCommitted digest) ->
                                    conflict $"Blob digest '{digest}' is not committed in this repository."
                                | Ok(UpsertEntriesSuccess entryCount) ->
                                    match
                                        writeAudit
                                            state
                                            principal.TenantId
                                            principal.Subject
                                            "package.version.entries.upserted"
                                            "package_version"
                                            (versionId.ToString())
                                            (Map.ofList
                                                [ "repoKey", repoKey
                                                  "entryCount", entryCount.ToString()
                                                  "state", "draft" ])
                                    with
                                    | Error err -> serviceUnavailable err
                                    | Ok() ->
                                        Results.Ok(
                                            {| versionId = versionId
                                               repoKey = repoKey
                                               state = "draft"
                                               entryCount = entryCount |}
                                        ))
    )
    |> ignore

    app.MapPut(
        "/v1/repos/{repoKey}/packages/versions/{versionId}/manifest",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let versionIdRaw = normalizeText (ctx.Request.RouteValues["versionId"].ToString())

            let parsedVersionId =
                match Guid.TryParse(versionIdRaw) with
                | true, parsed -> Ok parsed
                | _ -> Error "versionId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedVersionId with
                | Error err -> badRequest err
                | Ok versionId ->
                    match requireRole state ctx repoKey RepoRole.Write with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (tryReadPackageVersionTargetForRepo principal.TenantId repoKey versionId) with
                        | Error err -> serviceUnavailable err
                        | Ok None ->
                            Results.NotFound({| error = "not_found"; message = "Package version was not found." |})
                        | Ok(Some versionTarget) ->
                            match readJsonBody<UpsertManifestRequest> ctx with
                            | Error err -> badRequest err
                            | Ok request ->
                                match validateManifestRequest versionTarget.PackageType request with
                                | Error err -> badRequest err
                                | Ok(manifestJson, manifestBlobDigest) ->
                                    match
                                        withConnection
                                            state
                                            (upsertManifestForDraftVersion
                                                principal.TenantId
                                                repoKey
                                                versionId
                                                manifestJson
                                                manifestBlobDigest
                                                principal.Subject)
                                    with
                                    | Error err -> serviceUnavailable err
                                    | Ok UpsertManifestVersionMissing ->
                                        Results.NotFound({| error = "not_found"; message = "Package version was not found." |})
                                    | Ok(UpsertManifestStateConflict stateValue) ->
                                        conflict $"Package version is in state '{stateValue}' and cannot be modified."
                                    | Ok(UpsertManifestBlobMissing digest) ->
                                        conflict $"Manifest blob digest '{digest}' does not exist."
                                    | Ok(UpsertManifestBlobNotCommitted digest) ->
                                        conflict $"Manifest blob digest '{digest}' is not committed in this repository."
                                    | Ok(UpsertManifestSuccess(updatedTarget, storedManifestBlobDigest)) ->
                                        match
                                            writeAudit
                                                state
                                                principal.TenantId
                                                principal.Subject
                                                "package.version.manifest.upserted"
                                                "package_version"
                                                (versionId.ToString())
                                                (Map.ofList
                                                    [ "repoKey", repoKey
                                                      "packageType", updatedTarget.PackageType
                                                      "hasManifestBlobDigest", (storedManifestBlobDigest.IsSome).ToString().ToLowerInvariant()
                                                      "state", "draft" ])
                                        with
                                        | Error err -> serviceUnavailable err
                                        | Ok() ->
                                            Results.Ok(
                                                {| versionId = versionId
                                                   repoKey = repoKey
                                                   packageType = updatedTarget.PackageType
                                                   state = "draft"
                                                   manifestBlobDigest = storedManifestBlobDigest |}
                                            ))
    )
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}/packages/versions/{versionId}/manifest",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let versionIdRaw = normalizeText (ctx.Request.RouteValues["versionId"].ToString())

            let parsedVersionId =
                match Guid.TryParse(versionIdRaw) with
                | true, parsed -> Ok parsed
                | _ -> Error "versionId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedVersionId with
                | Error err -> badRequest err
                | Ok versionId ->
                    match requireRole state ctx repoKey RepoRole.Read with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (readManifestForVersion principal.TenantId repoKey versionId) with
                        | Error err -> serviceUnavailable err
                        | Ok ReadManifestVersionMissing ->
                            Results.NotFound({| error = "not_found"; message = "Package version was not found." |})
                        | Ok ReadManifestMissing ->
                            Results.NotFound({| error = "not_found"; message = "Manifest was not found for package version." |})
                        | Ok(ReadManifestSuccess(versionTarget, manifestBlobDigest, manifestJson)) ->
                            try
                                use manifestDoc = JsonDocument.Parse(manifestJson)
                                let manifestValue = manifestDoc.RootElement.Clone()

                                Results.Ok(
                                    {| versionId = versionId
                                       repoKey = repoKey
                                       packageType = versionTarget.PackageType
                                       state = versionTarget.State
                                       manifestBlobDigest = manifestBlobDigest
                                       manifest = manifestValue |}
                                )
                            with ex ->
                                serviceUnavailable $"Stored manifest could not be parsed: {ex.Message}"
    ))
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/packages/versions/{versionId}/publish",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let versionIdRaw = normalizeText (ctx.Request.RouteValues["versionId"].ToString())

            let parsedVersionId =
                match Guid.TryParse(versionIdRaw) with
                | true, parsed -> Ok parsed
                | _ -> Error "versionId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedVersionId with
                | Error err -> badRequest err
                | Ok versionId ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match
                            withConnection
                                state
                                (publishDraftVersionForRepo
                                    principal.TenantId
                                    repoKey
                                    versionId
                                    principal.Subject)
                        with
                        | Error err -> serviceUnavailable err
                        | Ok PublishVersionMissing ->
                            Results.NotFound({| error = "not_found"; message = "Package version was not found." |})
                        | Ok(PublishVersionStateConflict stateValue) ->
                            conflict $"Package version is in state '{stateValue}' and cannot be published."
                        | Ok PublishVersionMissingEntries ->
                            conflict "Package version cannot be published without artifact entries."
                        | Ok PublishVersionMissingManifest ->
                            conflict "Package version cannot be published without a manifest."
                        | Ok(PublishVersionBlobNotCommitted digest) ->
                            conflict $"Package version references blob digest '{digest}' that is not committed in this repository."
                        | Ok(PublishVersionAlreadyPublished target) ->
                            Results.Ok(
                                {| versionId = target.VersionId
                                   repoKey = target.RepoKey
                                   packageType = target.PackageType
                                   packageNamespace = target.PackageNamespace
                                   packageName = target.PackageName
                                   version = target.Version
                                   state = target.State
                                   publishedAtUtc = target.PublishedAtUtc
                                   eventEmitted = false
                                   idempotent = true |}
                            )
                        | Ok(PublishVersionSuccess(target, publishedAtUtc, outboxEventId)) ->
                            Results.Ok(
                                {| versionId = target.VersionId
                                   repoKey = target.RepoKey
                                   packageType = target.PackageType
                                   packageNamespace = target.PackageNamespace
                                   packageName = target.PackageName
                                   version = target.Version
                                   state = "published"
                                   publishedAtUtc = publishedAtUtc
                                   outboxEventId = outboxEventId
                                   eventEmitted = outboxEventId.IsSome
                                   idempotent = false |}
                            ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/packages/versions/{versionId}/tombstone",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let versionIdRaw = normalizeText (ctx.Request.RouteValues["versionId"].ToString())

            let parsedVersionId =
                match Guid.TryParse(versionIdRaw) with
                | true, parsed -> Ok parsed
                | _ -> Error "versionId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedVersionId with
                | Error err -> badRequest err
                | Ok versionId ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match readJsonBody<TombstoneVersionRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match validateTombstoneRequest state.DefaultTombstoneRetentionDays request with
                            | Error err -> badRequest err
                            | Ok(reason, retentionDays) ->
                                match
                                    withConnection
                                        state
                                        (tombstoneVersionForRepo
                                            principal.TenantId
                                            repoKey
                                            versionId
                                            reason
                                            retentionDays
                                            principal.Subject)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok TombstoneVersionMissing ->
                                    Results.NotFound({| error = "not_found"; message = "Package version was not found." |})
                                | Ok(TombstoneVersionStateConflict stateValue) ->
                                    conflict $"Package version is in state '{stateValue}' and cannot be tombstoned."
                                | Ok(TombstoneVersionAlreadyTombstoned(target, retentionUntilUtc)) ->
                                    Results.Ok(
                                        {| versionId = target.VersionId
                                           repoKey = target.RepoKey
                                           packageType = target.PackageType
                                           packageNamespace = target.PackageNamespace
                                           packageName = target.PackageName
                                           version = target.Version
                                           state = target.State
                                           retentionUntilUtc = retentionUntilUtc
                                           idempotent = true |}
                                    )
                                | Ok(TombstoneVersionSuccess(target, tombstonedAtUtc, retentionUntilUtc)) ->
                                    Results.Ok(
                                        {| versionId = target.VersionId
                                           repoKey = target.RepoKey
                                           packageType = target.PackageType
                                           packageNamespace = target.PackageNamespace
                                           packageName = target.PackageName
                                           version = target.Version
                                           state = target.State
                                           tombstonedAtUtc = tombstonedAtUtc
                                           retentionUntilUtc = retentionUntilUtc
                                           idempotent = false |}
                                    ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/policy/evaluations",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Promote with
                | Error result -> result
                | Ok principal ->
                    match readJsonBody<EvaluatePolicyRequest> ctx with
                    | Error err -> badRequest err
                    | Ok request ->
                        match validateEvaluatePolicyRequest request with
                        | Error err -> badRequest err
                        | Ok(action, versionId, validatedDecision, validatedDecisionSource, reason, policyEngineVersion) ->
                            match
                                tryResolvePolicyDecisionWithTimeout
                                    state.PolicyEvaluationTimeoutMs
                                    validatedDecision
                                    validatedDecisionSource
                                    policyEngineVersion
                            with
                            | Error _ ->
                                let timeoutAuditDetails =
                                    Map.ofList
                                        [ "repoKey", repoKey
                                          "versionId", versionId.ToString()
                                          "action", action
                                          "timeoutMs", state.PolicyEvaluationTimeoutMs.ToString()
                                          "failClosed", "true" ]

                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "policy.timeout"
                                        "policy_evaluation"
                                        (versionId.ToString())
                                        timeoutAuditDetails
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Json(
                                        {| error = "policy_timeout"
                                           message = "Policy evaluation timed out; operation failed closed."
                                           action = action
                                           failClosed = true
                                           timeoutMs = state.PolicyEvaluationTimeoutMs |},
                                        statusCode = StatusCodes.Status503ServiceUnavailable
                                    )
                            | Ok(decision, decisionSource) ->
                                match
                                    withConnection
                                        state
                                        (evaluatePolicyForVersion
                                            principal.TenantId
                                            repoKey
                                            versionId
                                            action
                                            decision
                                            decisionSource
                                            reason
                                            policyEngineVersion
                                            principal.Subject)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok None ->
                                    Results.NotFound(
                                        {| error = "not_found"
                                           message = "Package version was not found in repository." |}
                                    )
                                | Ok(Some evaluation) ->
                                    let auditDetails =
                                        [ "repoKey", repoKey
                                          "versionId", evaluation.VersionId.ToString()
                                          "action", evaluation.Action
                                          "decision", evaluation.Decision
                                          "decisionSource", evaluation.DecisionSource ]
                                        |> (fun values ->
                                            match evaluation.PolicyEngineVersion with
                                            | Some value -> ("policyEngineVersion", value) :: values
                                            | None -> values)
                                        |> (fun values ->
                                            match evaluation.QuarantineId with
                                            | Some value -> ("quarantineId", value.ToString()) :: values
                                            | None -> values)
                                        |> Map.ofList

                                    match
                                        writeAudit
                                            state
                                            principal.TenantId
                                            principal.Subject
                                            "policy.evaluated"
                                            "policy_evaluation"
                                            (evaluation.EvaluationId.ToString())
                                            auditDetails
                                    with
                                    | Error err -> serviceUnavailable err
                                    | Ok() ->
                                        Results.Created(
                                            $"/v1/repos/{repoKey}/policy/evaluations/{evaluation.EvaluationId}",
                                            {| evaluationId = evaluation.EvaluationId
                                               repoKey = repoKey
                                               versionId = evaluation.VersionId
                                               action = evaluation.Action
                                               decision = evaluation.Decision
                                               decisionSource = evaluation.DecisionSource
                                               reason = evaluation.Reason
                                               policyEngineVersion = evaluation.PolicyEngineVersion
                                               evaluatedAtUtc = evaluation.EvaluatedAtUtc
                                               quarantineId = evaluation.QuarantineId
                                               quarantined = evaluation.QuarantineId.IsSome |}
                                        )
    ))
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}/quarantine",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let statusFilterRaw = ctx.Request.Query["status"].ToString()

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match validateQuarantineStatusFilter statusFilterRaw with
                | Error err -> badRequest err
                | Ok statusFilter ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match withConnection state (readQuarantineItemsForRepo principal.TenantId repoKey statusFilter) with
                            | Error err -> serviceUnavailable err
                            | Ok items ->
                                let response =
                                    items
                                    |> List.map (fun item ->
                                        {| quarantineId = item.QuarantineId
                                           repoKey = item.RepoKey
                                           versionId = item.VersionId
                                           status = item.Status
                                           reason = item.Reason
                                           createdAtUtc = item.CreatedAtUtc
                                           resolvedAtUtc = item.ResolvedAtUtc
                                           resolvedBySubject = item.ResolvedBySubject |})
                                    |> List.toArray

                                Results.Ok(response))
    )
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}/quarantine/{quarantineId}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let quarantineIdRaw = normalizeText (ctx.Request.RouteValues["quarantineId"].ToString())

            let parsedQuarantineId =
                match Guid.TryParse(quarantineIdRaw) with
                | true, quarantineId -> Ok quarantineId
                | _ -> Error "quarantineId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedQuarantineId with
                | Error err -> badRequest err
                | Ok quarantineId ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match
                                withConnection
                                    state
                                    (tryReadQuarantineItemForRepo principal.TenantId repoKey quarantineId)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok None ->
                                Results.NotFound({| error = "not_found"; message = "Quarantine item was not found." |})
                            | Ok(Some item) ->
                                Results.Ok(
                                    {| quarantineId = item.QuarantineId
                                       repoKey = item.RepoKey
                                       versionId = item.VersionId
                                       status = item.Status
                                       reason = item.Reason
                                       createdAtUtc = item.CreatedAtUtc
                                       resolvedAtUtc = item.ResolvedAtUtc
                                       resolvedBySubject = item.ResolvedBySubject |}
                                ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/quarantine/{quarantineId}/release",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let quarantineIdRaw = normalizeText (ctx.Request.RouteValues["quarantineId"].ToString())

            let parsedQuarantineId =
                match Guid.TryParse(quarantineIdRaw) with
                | true, quarantineId -> Ok quarantineId
                | _ -> Error "quarantineId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedQuarantineId with
                | Error err -> badRequest err
                | Ok quarantineId ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match
                                withConnection
                                    state
                                    (resolveQuarantineItemForRepo
                                        principal.TenantId
                                        repoKey
                                        quarantineId
                                        "released"
                                        principal.Subject)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok QuarantineMissing ->
                                Results.NotFound({| error = "not_found"; message = "Quarantine item was not found." |})
                            | Ok(QuarantineAlreadyResolved statusValue) ->
                                conflict $"Quarantine item is already in status '{statusValue}'."
                            | Ok(QuarantineResolved item) ->
                                let auditDetails =
                                    Map.ofList
                                        [ "repoKey", repoKey
                                          "versionId", item.VersionId.ToString()
                                          "status", item.Status ]

                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "quarantine.released"
                                        "quarantine_item"
                                        (item.QuarantineId.ToString())
                                        auditDetails
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Ok(
                                        {| quarantineId = item.QuarantineId
                                           repoKey = item.RepoKey
                                           versionId = item.VersionId
                                           status = item.Status
                                           reason = item.Reason
                                           createdAtUtc = item.CreatedAtUtc
                                           resolvedAtUtc = item.ResolvedAtUtc
                                           resolvedBySubject = item.ResolvedBySubject |}
                                    ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/quarantine/{quarantineId}/reject",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let quarantineIdRaw = normalizeText (ctx.Request.RouteValues["quarantineId"].ToString())

            let parsedQuarantineId =
                match Guid.TryParse(quarantineIdRaw) with
                | true, quarantineId -> Ok quarantineId
                | _ -> Error "quarantineId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedQuarantineId with
                | Error err -> badRequest err
                | Ok quarantineId ->
                    match requireRole state ctx repoKey RepoRole.Promote with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match
                                withConnection
                                    state
                                    (resolveQuarantineItemForRepo
                                        principal.TenantId
                                        repoKey
                                        quarantineId
                                        "rejected"
                                        principal.Subject)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok QuarantineMissing ->
                                Results.NotFound({| error = "not_found"; message = "Quarantine item was not found." |})
                            | Ok(QuarantineAlreadyResolved statusValue) ->
                                conflict $"Quarantine item is already in status '{statusValue}'."
                            | Ok(QuarantineResolved item) ->
                                let auditDetails =
                                    Map.ofList
                                        [ "repoKey", repoKey
                                          "versionId", item.VersionId.ToString()
                                          "status", item.Status ]

                                match
                                    writeAudit
                                        state
                                        principal.TenantId
                                        principal.Subject
                                        "quarantine.rejected"
                                        "quarantine_item"
                                        (item.QuarantineId.ToString())
                                        auditDetails
                                with
                                | Error err -> serviceUnavailable err
                                | Ok() ->
                                    Results.Ok(
                                        {| quarantineId = item.QuarantineId
                                           repoKey = item.RepoKey
                                           versionId = item.VersionId
                                           status = item.Status
                                           reason = item.Reason
                                           createdAtUtc = item.CreatedAtUtc
                                           resolvedAtUtc = item.ResolvedAtUtc
                                           resolvedBySubject = item.ResolvedBySubject |}
                                    ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match requireRole state ctx repoKey RepoRole.Write with
                | Error result -> result
                | Ok principal ->
                    match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                    | Error err -> serviceUnavailable err
                    | Ok false ->
                        Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                    | Ok true ->
                        match readJsonBody<CreateUploadSessionRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match validateUploadSessionRequest request with
                            | Error err -> badRequest err
                            | Ok(digest, expectedLength) ->
                                let expiresAtUtc = (nowUtc ()).AddMinutes(60.0)

                                match withConnection state (tryReadBlobLengthByDigest digest) with
                                | Error err -> serviceUnavailable err
                                | Ok(Some existingLength) when existingLength <> expectedLength ->
                                    conflict "Digest already exists with a different length."
                                | Ok(Some _) ->
                                    match
                                        withConnection
                                            state
                                            (insertUploadSessionForRepo
                                                principal.TenantId
                                                repoKey
                                                digest
                                                expectedLength
                                                "committed"
                                                None
                                                None
                                                (Some digest)
                                                principal.Subject
                                                expiresAtUtc)
                                    with
                                    | Error err -> serviceUnavailable err
                                    | Ok None ->
                                        Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                                    | Ok(Some session) ->
                                        match
                                            writeUploadAudit
                                                state
                                                principal
                                                "upload.session.created"
                                                session.UploadId
                                                (Map.ofList [ "repoKey", repoKey; "state", session.State; "deduped", "true" ])
                                        with
                                        | Error err -> serviceUnavailable err
                                        | Ok() ->
                                            Results.Ok(
                                                {| uploadId = session.UploadId
                                                   repoKey = repoKey
                                                   expectedDigest = session.ExpectedDigest
                                                   expectedLength = session.ExpectedLength
                                                   state = session.State
                                                   deduped = true
                                                   expiresAtUtc = session.ExpiresAtUtc |}
                                            )
                                | Ok None ->
                                    let uploadId = Guid.NewGuid()
                                    let objectStagingKey = buildStagingObjectKey principal.TenantId repoKey uploadId

                                    match
                                        state.ObjectStorageClient.StartMultipartUpload(objectStagingKey, ctx.RequestAborted).Result
                                    with
                                    | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                    | Ok storageSession ->
                                        match
                                            withConnection
                                                state
                                                (insertUploadSessionForRepo
                                                    principal.TenantId
                                                    repoKey
                                                    digest
                                                    expectedLength
                                                    "initiated"
                                                    (Some objectStagingKey)
                                                    (Some storageSession.UploadId)
                                                    None
                                                    principal.Subject
                                                    expiresAtUtc)
                                        with
                                        | Error err ->
                                            abortMultipartUploadBestEffort
                                                state
                                                objectStagingKey
                                                storageSession.UploadId
                                                ctx.RequestAborted

                                            serviceUnavailable err
                                        | Ok None ->
                                            abortMultipartUploadBestEffort
                                                state
                                                objectStagingKey
                                                storageSession.UploadId
                                                ctx.RequestAborted

                                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                                        | Ok(Some session) ->
                                            match
                                                writeUploadAudit
                                                    state
                                                    principal
                                                    "upload.session.created"
                                                    session.UploadId
                                                    (Map.ofList [ "repoKey", repoKey; "state", session.State; "deduped", "false" ])
                                            with
                                            | Error err -> serviceUnavailable err
                                            | Ok() ->
                                                Results.Created(
                                                    $"/v1/repos/{repoKey}/uploads/{session.UploadId}",
                                                    {| uploadId = session.UploadId
                                                       repoKey = repoKey
                                                       expectedDigest = session.ExpectedDigest
                                                       expectedLength = session.ExpectedLength
                                                       state = session.State
                                                       deduped = false
                                                       expiresAtUtc = session.ExpiresAtUtc
                                                       objectStagingKey = session.ObjectStagingKey
                                                       storageUploadId = session.StorageUploadId |}
                                                ))
    )
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads/{uploadId}/parts",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let uploadIdRaw = normalizeText (ctx.Request.RouteValues["uploadId"].ToString())

            let parsedUploadId =
                match Guid.TryParse(uploadIdRaw) with
                | true, uploadId -> Ok uploadId
                | _ -> Error "uploadId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedUploadId with
                | Error err -> badRequest err
                | Ok uploadId ->
                    match requireRole state ctx repoKey RepoRole.Write with
                    | Error result -> result
                    | Ok principal ->
                        match readJsonBody<CreateUploadPartRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match validateUploadPartRequest request with
                            | Error err -> badRequest err
                            | Ok partNumber ->
                                match
                                    withConnection state (tryReadUploadSessionForRepo principal.TenantId repoKey uploadId)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok None ->
                                    Results.NotFound({| error = "not_found"; message = "Upload session was not found." |})
                                | Ok(Some session) ->
                                    if session.ExpiresAtUtc <= nowUtc () then
                                        conflict "Upload session has expired."
                                    else
                                        match session.State with
                                        | "committed"
                                        | "aborted" -> conflict "Upload session is terminal and cannot accept more parts."
                                        | "pending_commit" -> conflict "Upload session is pending commit and cannot accept more parts."
                                        | "initiated"
                                        | "parts_uploading" ->
                                            let objectStagingKey = session.ObjectStagingKey |> Option.defaultValue ""
                                            let storageUploadId = session.StorageUploadId |> Option.defaultValue ""

                                            if String.IsNullOrWhiteSpace objectStagingKey || String.IsNullOrWhiteSpace storageUploadId then
                                                conflict "Upload session storage metadata is incomplete."
                                            else
                                                let urlExpiryUtc =
                                                    (nowUtc ()).AddSeconds(float state.PresignPartTtlSeconds)

                                                match
                                                    state.ObjectStorageClient.PresignUploadPart(
                                                        objectStagingKey,
                                                        storageUploadId,
                                                        partNumber,
                                                        urlExpiryUtc
                                                    )
                                                with
                                                | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                                | Ok presignedPart ->
                                                    let updatedStateResult =
                                                        if session.State = "initiated" then
                                                            withConnection
                                                                state
                                                                (updateUploadSessionState
                                                                    principal.TenantId
                                                                    session.UploadId
                                                                    "initiated"
                                                                    "parts_uploading")
                                                        else
                                                            Ok true

                                                    match updatedStateResult with
                                                    | Error err -> serviceUnavailable err
                                                    | Ok false ->
                                                        conflict "Upload session state changed; retry request."
                                                    | Ok true ->
                                                        match
                                                            writeUploadAudit
                                                                state
                                                                principal
                                                                "upload.part.presigned"
                                                                session.UploadId
                                                                (Map.ofList
                                                                    [ "repoKey", repoKey
                                                                      "partNumber", presignedPart.PartNumber.ToString()
                                                                      "state", "parts_uploading" ])
                                                        with
                                                        | Error err -> serviceUnavailable err
                                                        | Ok() ->
                                                            Results.Ok(
                                                                {| uploadId = session.UploadId
                                                                   repoKey = repoKey
                                                                   state = "parts_uploading"
                                                                   partNumber = presignedPart.PartNumber
                                                                   uploadUrl = presignedPart.Url.ToString()
                                                                   expiresAtUtc = presignedPart.ExpiresAtUtc |}
                                                            )
                                        | _ -> conflict "Upload session state is invalid."
    ))
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads/{uploadId}/complete",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let uploadIdRaw = normalizeText (ctx.Request.RouteValues["uploadId"].ToString())

            let parsedUploadId =
                match Guid.TryParse(uploadIdRaw) with
                | true, uploadId -> Ok uploadId
                | _ -> Error "uploadId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedUploadId with
                | Error err -> badRequest err
                | Ok uploadId ->
                    match requireRole state ctx repoKey RepoRole.Write with
                    | Error result -> result
                    | Ok principal ->
                        match readJsonBody<CompleteUploadPartsRequest> ctx with
                        | Error err -> badRequest err
                        | Ok request ->
                            match validateCompleteUploadRequest request with
                            | Error err -> badRequest err
                            | Ok completedParts ->
                                match
                                    withConnection state (tryReadUploadSessionForRepo principal.TenantId repoKey uploadId)
                                with
                                | Error err -> serviceUnavailable err
                                | Ok None ->
                                    Results.NotFound({| error = "not_found"; message = "Upload session was not found." |})
                                | Ok(Some session) ->
                                    if session.ExpiresAtUtc <= nowUtc () then
                                        conflict "Upload session has expired."
                                    else
                                        match session.State with
                                        | "initiated" ->
                                            conflict "Upload session has no uploaded parts to complete."
                                        | "pending_commit" ->
                                            Results.Ok(
                                                {| uploadId = session.UploadId
                                                   repoKey = repoKey
                                                   state = session.State
                                                   expectedDigest = session.ExpectedDigest
                                                   expectedLength = session.ExpectedLength |}
                                            )
                                        | "committed"
                                        | "aborted" -> conflict "Upload session is terminal and cannot be completed."
                                        | "parts_uploading" ->
                                            let objectStagingKey = session.ObjectStagingKey |> Option.defaultValue ""
                                            let storageUploadId = session.StorageUploadId |> Option.defaultValue ""

                                            if String.IsNullOrWhiteSpace objectStagingKey || String.IsNullOrWhiteSpace storageUploadId then
                                                conflict "Upload session storage metadata is incomplete."
                                            else
                                                match
                                                    state.ObjectStorageClient.CompleteMultipartUpload(
                                                        objectStagingKey,
                                                        storageUploadId,
                                                        completedParts,
                                                        ctx.RequestAborted
                                                    ).Result
                                                with
                                                | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                                | Ok() ->
                                                    match
                                                        withConnection
                                                            state
                                                            (updateUploadSessionState
                                                                principal.TenantId
                                                                session.UploadId
                                                                "parts_uploading"
                                                                "pending_commit")
                                                    with
                                                    | Error err -> serviceUnavailable err
                                                    | Ok false ->
                                                        conflict "Upload session state changed; retry request."
                                                    | Ok true ->
                                                        match
                                                            writeUploadAudit
                                                                state
                                                                principal
                                                                "upload.completed"
                                                                session.UploadId
                                                                (Map.ofList [ "repoKey", repoKey; "state", "pending_commit" ])
                                                        with
                                                        | Error err -> serviceUnavailable err
                                                        | Ok() ->
                                                            Results.Ok(
                                                                {| uploadId = session.UploadId
                                                                   repoKey = repoKey
                                                                   state = "pending_commit"
                                                                   expectedDigest = session.ExpectedDigest
                                                                   expectedLength = session.ExpectedLength |}
                                                            )
                                        | _ -> conflict "Upload session state is invalid."
    ))
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads/{uploadId}/abort",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let uploadIdRaw = normalizeText (ctx.Request.RouteValues["uploadId"].ToString())

            let parsedUploadId =
                match Guid.TryParse(uploadIdRaw) with
                | true, uploadId -> Ok uploadId
                | _ -> Error "uploadId route parameter must be a valid GUID."

            let abortReasonResult =
                if ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > 0L then
                    match readJsonBody<AbortUploadRequest> ctx with
                    | Error err -> Error err
                    | Ok request -> Ok(normalizeAbortReason request.Reason)
                else
                    Ok "client_abort"

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedUploadId with
                | Error err -> badRequest err
                | Ok uploadId ->
                    match abortReasonResult with
                    | Error err -> badRequest err
                    | Ok abortReason ->
                        match requireRole state ctx repoKey RepoRole.Write with
                        | Error result -> result
                        | Ok principal ->
                            match
                                withConnection state (tryReadUploadSessionForRepo principal.TenantId repoKey uploadId)
                            with
                            | Error err -> serviceUnavailable err
                            | Ok None ->
                                Results.NotFound({| error = "not_found"; message = "Upload session was not found." |})
                            | Ok(Some session) ->
                                match session.State with
                                | "committed" -> conflict "Upload session is already committed and cannot be aborted."
                                | "aborted" ->
                                    Results.Ok(
                                        {| uploadId = session.UploadId
                                           repoKey = repoKey
                                           state = "aborted"
                                           reason = abortReason |}
                                    )
                                | "initiated"
                                | "parts_uploading"
                                | "pending_commit" ->
                                    let objectStagingKey = session.ObjectStagingKey |> Option.defaultValue ""
                                    let storageUploadId = session.StorageUploadId |> Option.defaultValue ""

                                    let abortStorageResult =
                                        if String.IsNullOrWhiteSpace objectStagingKey || String.IsNullOrWhiteSpace storageUploadId then
                                            Ok()
                                        else
                                            match
                                                state.ObjectStorageClient.AbortMultipartUpload(
                                                    objectStagingKey,
                                                    storageUploadId,
                                                    ctx.RequestAborted
                                                ).Result
                                            with
                                            | Ok() -> Ok()
                                            | Error(NotFound _) -> Ok()
                                            | Error storageErr -> Error storageErr

                                    match abortStorageResult with
                                    | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                    | Ok() ->
                                        match withConnection state (markUploadSessionAborted principal.TenantId session.UploadId abortReason) with
                                        | Error err -> serviceUnavailable err
                                        | Ok false ->
                                            conflict "Upload session state changed; retry request."
                                        | Ok true ->
                                            match
                                                writeUploadAudit
                                                    state
                                                    principal
                                                    "upload.aborted"
                                                    session.UploadId
                                                    (Map.ofList [ "repoKey", repoKey; "reason", abortReason; "state", "aborted" ])
                                            with
                                            | Error err -> serviceUnavailable err
                                            | Ok() ->
                                                Results.Ok(
                                                    {| uploadId = session.UploadId
                                                       repoKey = repoKey
                                                       state = "aborted"
                                                       reason = abortReason |}
                                                )
                                | _ -> conflict "Upload session state is invalid."
    ))
    |> ignore

    app.MapPost(
        "/v1/repos/{repoKey}/uploads/{uploadId}/commit",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let uploadIdRaw = normalizeText (ctx.Request.RouteValues["uploadId"].ToString())

            let parsedUploadId =
                match Guid.TryParse(uploadIdRaw) with
                | true, uploadId -> Ok uploadId
                | _ -> Error "uploadId route parameter must be a valid GUID."

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            else
                match parsedUploadId with
                | Error err -> badRequest err
                | Ok uploadId ->
                    match requireRole state ctx repoKey RepoRole.Write with
                    | Error result -> result
                    | Ok principal ->
                        match
                            withConnection state (tryReadUploadSessionForRepo principal.TenantId repoKey uploadId)
                        with
                        | Error err -> serviceUnavailable err
                        | Ok None ->
                            Results.NotFound({| error = "not_found"; message = "Upload session was not found." |})
                        | Ok(Some session) ->
                            if session.ExpiresAtUtc <= nowUtc () then
                                conflict "Upload session has expired."
                            else
                                match session.State with
                                | "initiated"
                                | "parts_uploading" ->
                                    conflict "Upload session must be completed before commit."
                                | "aborted" -> conflict "Upload session is aborted and cannot be committed."
                                | "committed" ->
                                    Results.Ok(
                                        {| uploadId = session.UploadId
                                           repoKey = repoKey
                                           state = session.State
                                           digest = session.CommittedBlobDigest
                                           length = session.ExpectedLength |}
                                    )
                                | "pending_commit" ->
                                    let objectStagingKey = session.ObjectStagingKey |> Option.defaultValue ""

                                    if String.IsNullOrWhiteSpace objectStagingKey then
                                        conflict "Upload session storage metadata is incomplete."
                                    else
                                        match
                                            state.ObjectStorageClient.DownloadObject(
                                                objectStagingKey,
                                                None,
                                                ctx.RequestAborted
                                            ).Result
                                        with
                                        | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                        | Ok downloaded ->
                                            try
                                                let actualDigest, actualLength = computeSha256AndLength downloaded.Stream
                                                downloaded.Dispose()

                                                if actualDigest <> session.ExpectedDigest || actualLength <> session.ExpectedLength then
                                                    let reason =
                                                        if actualDigest <> session.ExpectedDigest then
                                                            "digest_mismatch"
                                                        else
                                                            "length_mismatch"

                                                    match
                                                        withConnection
                                                            state
                                                            (markUploadSessionAborted principal.TenantId session.UploadId reason)
                                                    with
                                                    | Error err -> serviceUnavailable err
                                                    | Ok _ ->
                                                        match
                                                            writeUploadAudit
                                                                state
                                                                principal
                                                                "upload.commit.verification_failed"
                                                                session.UploadId
                                                                (Map.ofList
                                                                    [ "repoKey", repoKey
                                                                      "reason", reason
                                                                      "expectedDigest", session.ExpectedDigest
                                                                      "actualDigest", actualDigest
                                                                      "expectedLength", session.ExpectedLength.ToString()
                                                                      "actualLength", actualLength.ToString() ])
                                                        with
                                                        | Error err -> serviceUnavailable err
                                                        | Ok() ->
                                                            Results.Json(
                                                                {| error = "upload_verification_failed"
                                                                   message = "Uploaded content failed digest/length verification."
                                                                   expectedDigest = session.ExpectedDigest
                                                                   actualDigest = actualDigest
                                                                   expectedLength = session.ExpectedLength
                                                                   actualLength = actualLength |},
                                                                statusCode = StatusCodes.Status409Conflict
                                                            )
                                                else
                                                    match
                                                        withConnection
                                                            state
                                                            (commitUploadSessionWithBlob
                                                                principal.TenantId
                                                                session.UploadId
                                                                session.ExpectedDigest
                                                                session.ExpectedLength
                                                                objectStagingKey
                                                                downloaded.ETag)
                                                    with
                                                    | Error err ->
                                                        if err = "Digest already exists with a different length." then
                                                            conflict err
                                                        else
                                                            serviceUnavailable err
                                                    | Ok false ->
                                                        conflict "Upload session state changed; retry request."
                                                    | Ok true ->
                                                        match
                                                            writeUploadAudit
                                                                state
                                                                principal
                                                                "upload.committed"
                                                                session.UploadId
                                                                (Map.ofList
                                                                    [ "repoKey", repoKey
                                                                      "digest", session.ExpectedDigest
                                                                      "length", session.ExpectedLength.ToString()
                                                                      "state", "committed" ])
                                                        with
                                                        | Error err -> serviceUnavailable err
                                                        | Ok() ->
                                                            Results.Ok(
                                                                {| uploadId = session.UploadId
                                                                   repoKey = repoKey
                                                                   state = "committed"
                                                                   digest = session.ExpectedDigest
                                                                   length = session.ExpectedLength |}
                                                            )
                                            with ex ->
                                                downloaded.Dispose()
                                                serviceUnavailable $"Upload verification failed: {ex.Message}"
                                | _ -> conflict "Upload session state is invalid."
    ))
    |> ignore

    app.MapGet(
        "/v1/repos/{repoKey}/blobs/{digest}",
        Func<HttpContext, IResult>(fun ctx ->
            let repoKey = normalizeRepoKey (ctx.Request.RouteValues["repoKey"].ToString())
            let digest = normalizeDigest (ctx.Request.RouteValues["digest"].ToString())
            let rangeHeader = ctx.Request.Headers.Range.ToString()

            if String.IsNullOrWhiteSpace repoKey then
                badRequest "repoKey route parameter is required."
            elif digest.Length <> 64 || not (digest |> Seq.forall isHexChar) then
                badRequest "digest route parameter must be a 64-character lowercase hex SHA-256 digest."
            else
                match parseSingleRangeHeader rangeHeader with
                | Error err -> badRequest err
                | Ok parsedRange ->
                    match requireRole state ctx repoKey RepoRole.Read with
                    | Error result -> result
                    | Ok principal ->
                        match withConnection state (repoExistsForTenant principal.TenantId repoKey) with
                        | Error err -> serviceUnavailable err
                        | Ok false ->
                            Results.NotFound({| error = "not_found"; message = "Repository was not found." |})
                        | Ok true ->
                            match withConnection state (repoHasCommittedBlobDigest principal.TenantId repoKey digest) with
                            | Error err -> serviceUnavailable err
                            | Ok false ->
                                Results.NotFound({| error = "not_found"; message = "Blob was not found." |})
                            | Ok true ->
                                match withConnection state (repoBlobDigestBlockedByQuarantine principal.TenantId repoKey digest) with
                                | Error err -> serviceUnavailable err
                                | Ok true ->
                                    Results.Json(
                                        {| error = "quarantined_blob"
                                           message = "Blob is unavailable because a linked package version is quarantined." |},
                                        statusCode = StatusCodes.Status423Locked
                                    )
                                | Ok false ->
                                    match withConnection state (tryReadBlobStorageKeyByDigest digest) with
                                    | Error err -> serviceUnavailable err
                                    | Ok None ->
                                        Results.NotFound({| error = "not_found"; message = "Blob was not found." |})
                                    | Ok(Some storageKey) ->
                                        match
                                            state.ObjectStorageClient.DownloadObject(storageKey, parsedRange, ctx.RequestAborted).Result
                                        with
                                        | Error storageErr -> mapObjectStorageErrorToResult storageErr
                                        | Ok downloaded ->
                                            ctx.Response.Headers.["Accept-Ranges"] <- "bytes"

                                            match downloaded.ETag with
                                            | Some etagValue -> ctx.Response.Headers.["ETag"] <- etagValue
                                            | None -> ()

                                            match downloaded.ContentRange with
                                            | Some contentRangeValue -> ctx.Response.Headers.["Content-Range"] <- contentRangeValue
                                            | None -> ()

                                            if downloaded.ContentLength >= 0L then
                                                ctx.Response.ContentLength <- downloaded.ContentLength

                                            ctx.Response.RegisterForDispose(
                                                { new IDisposable with
                                                    member _.Dispose() = downloaded.Dispose() }
                                            )

                                            ctx.Response.StatusCode <- int downloaded.StatusCode

                                            Results.Stream(
                                                downloaded.Stream,
                                                contentType = (downloaded.ContentType |> Option.defaultValue "application/octet-stream")
                                            )
    ))
    |> ignore

    app.MapGet(
        "/v1/admin/ops/summary",
        Func<HttpContext, IResult>(fun ctx ->
            match requireRole state ctx "*" RepoRole.Admin with
            | Error result -> result
            | Ok principal ->
                match withConnection state (readOpsSummary principal.TenantId) with
                | Error err -> serviceUnavailable err
                | Ok summary ->
                    match
                        writeAudit
                            state
                            principal.TenantId
                            principal.Subject
                            "ops.summary.read"
                            "operations"
                            (principal.TenantId.ToString())
                            (Map.ofList
                                [ "pendingOutboxEvents", summary.pendingOutboxEvents.ToString()
                                  "availableOutboxEvents", summary.availableOutboxEvents.ToString()
                                  "oldestPendingOutboxAgeSeconds", summary.oldestPendingOutboxAgeSeconds.ToString()
                                  "failedSearchJobs", summary.failedSearchJobs.ToString()
                                  "pendingSearchJobs", summary.pendingSearchJobs.ToString()
                                  "incompleteGcRuns", summary.incompleteGcRuns.ToString()
                                  "recentPolicyTimeouts24h", summary.recentPolicyTimeouts24h.ToString() ])
                    with
                    | Error err -> serviceUnavailable err
                    | Ok () -> Results.Ok(summary))
    )
    |> ignore

    app.MapGet(
        "/v1/admin/reconcile/blobs",
        Func<HttpContext, IResult>(fun ctx ->
            match requireRole state ctx "*" RepoRole.Admin with
            | Error result -> result
            | Ok principal ->
                let sampleLimitRaw = ctx.Request.Query["limit"].ToString()

                let sampleLimit =
                    match Int32.TryParse(sampleLimitRaw) with
                    | true, parsed when parsed > 0 -> min parsed 200
                    | _ -> 20

                match withConnection state (readBlobReconcileSummary principal.TenantId sampleLimit) with
                | Error err -> serviceUnavailable err
                | Ok summary ->
                    match
                        writeAudit
                            state
                            principal.TenantId
                            principal.Subject
                            "reconcile.blobs.checked"
                            "reconcile"
                            (principal.TenantId.ToString())
                            (Map.ofList
                                [ "sampleLimit", sampleLimit.ToString()
                                  "missingArtifactBlobRefs", summary.missingArtifactBlobRefs.ToString()
                                  "missingManifestBlobRefs", summary.missingManifestBlobRefs.ToString()
                                  "orphanBlobCount", summary.orphanBlobCount.ToString() ])
                    with
                    | Error err -> serviceUnavailable err
                    | Ok () -> Results.Ok(summary))
    )
    |> ignore

    app.MapPost(
        "/v1/admin/gc/runs",
        Func<HttpContext, IResult>(fun ctx ->
            match requireRole state ctx "*" RepoRole.Admin with
            | Error result -> result
            | Ok principal ->
                match readOptionalJsonBody<RunGcRequest> ctx with
                | Error err -> badRequest err
                | Ok requestOption ->
                    match
                        validateGcRequest
                            state.DefaultGcRetentionGraceHours
                            state.DefaultGcBatchSize
                            requestOption
                    with
                    | Error err -> badRequest err
                    | Ok(dryRun, retentionGraceHours, batchSize) ->
                        match
                            withConnection
                                state
                                (runGcSweep
                                    state.ObjectStorageClient
                                    principal.TenantId
                                    principal.Subject
                                    dryRun
                                    retentionGraceHours
                                    batchSize
                                    ctx.RequestAborted)
                        with
                        | Error err -> serviceUnavailable err
                        | Ok gcRun ->
                            let auditAction =
                                if dryRun then
                                    "gc.run.dry_run"
                                else
                                    "gc.run.execute"

                            match
                                writeAudit
                                    state
                                    principal.TenantId
                                    principal.Subject
                                    auditAction
                                    "gc_run"
                                    (gcRun.RunId.ToString())
                                    (Map.ofList
                                        [ "mode", gcRun.Mode
                                          "retentionGraceHours", retentionGraceHours.ToString()
                                          "batchSize", batchSize.ToString()
                                          "markedCount", gcRun.MarkedCount.ToString()
                                          "candidateBlobCount", gcRun.CandidateBlobCount.ToString()
                                          "deletedBlobCount", gcRun.DeletedBlobCount.ToString()
                                          "deletedVersionCount", gcRun.DeletedVersionCount.ToString()
                                          "deleteErrorCount", gcRun.DeleteErrorCount.ToString() ])
                            with
                            | Error err -> serviceUnavailable err
                            | Ok () ->
                                Results.Ok(
                                    {| runId = gcRun.RunId
                                       mode = gcRun.Mode
                                       dryRun = dryRun
                                       retentionGraceHours = retentionGraceHours
                                       batchSize = batchSize
                                       markedCount = gcRun.MarkedCount
                                       candidateBlobCount = gcRun.CandidateBlobCount
                                       deletedBlobCount = gcRun.DeletedBlobCount
                                       deletedVersionCount = gcRun.DeletedVersionCount
                                       deleteErrorCount = gcRun.DeleteErrorCount
                                       candidateDigests = gcRun.CandidateDigests |> List.toArray |}
                                ))
    )
    |> ignore

    app.MapGet(
        "/v1/audit",
        Func<HttpContext, IResult>(fun ctx ->
            match requireRole state ctx "*" RepoRole.Admin with
            | Error result -> result
            | Ok principal ->
                let rawLimit = ctx.Request.Query["limit"].ToString()

                let limit =
                    match Int32.TryParse rawLimit with
                    | true, parsed when parsed > 0 -> min parsed 500
                    | _ -> 100

                match withConnection state (readAuditRecords principal.TenantId limit) with
                | Error err -> serviceUnavailable err
                | Ok entries -> Results.Ok(entries |> List.toArray))
    )
    |> ignore

    app.Run()
    0
