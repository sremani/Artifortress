# Artifortress Domain Model (F#)

This model focuses on compile-time safety for identity, immutability, and legal state transitions.

## 1. Primitive and Value Types

```fsharp
module Artifortress.Domain

open System

type TenantId = private TenantId of Guid
type RepoId = private RepoId of Guid
type PackageId = private PackageId of Guid
type VersionId = private VersionId of Guid
type UploadId = private UploadId of Guid
type EventId = private EventId of Guid

type Sha256Digest = private Sha256Digest of string
type ContentLength = private ContentLength of int64
type ObjectKey = private ObjectKey of string
type UtcTimestamp = private UtcTimestamp of DateTimeOffset

type RepoType =
    | Local
    | Remote of upstreamUrl: Uri
    | Virtual of memberRepos: RepoId list

type PackageType =
    | Maven
    | Npm
    | Pypi
    | Nuget
    | Docker
    | Generic
```

## 2. Identity and Security

```fsharp
type Role =
    | RepoRead
    | RepoWrite
    | RepoAdmin
    | RepoPromote

type Subject =
    | User of userId: Guid
    | ServiceAccount of accountId: Guid

type Scope = {
    Tenant: TenantId
    Repo: RepoId option
    Roles: Set<Role>
}
```

## 3. Blob and Upload Model

```fsharp
type BlobRef = {
    Digest: Sha256Digest
    Length: ContentLength
}

type BlobRecord = {
    Ref: BlobRef
    StorageKey: ObjectKey
    CreatedAt: UtcTimestamp
}

type UploadState =
    | Initiated
    | PartsUploading
    | PendingCommit
    | Committed of BlobRef
    | Aborted of reason: string

type UploadSession = {
    UploadId: UploadId
    TenantId: TenantId
    RepoId: RepoId
    ExpectedDigest: Sha256Digest
    ExpectedLength: ContentLength
    State: UploadState
    CreatedAt: UtcTimestamp
    ExpiresAt: UtcTimestamp
}
```

## 4. Package Model

```fsharp
type PackageCoordinate = {
    PackageType: PackageType
    Namespace: string option
    Name: string
}

type VersionLabel = private VersionLabel of string

type VersionState =
    | Draft
    | Published
    | Tombstoned of tombstonedAt: UtcTimestamp * reason: string

type ArtifactEntry = {
    RelativePath: string
    Blob: BlobRef
    Checksums: Map<string, string>
}

type PackageVersion = {
    VersionId: VersionId
    PackageId: PackageId
    Version: VersionLabel
    ManifestBlob: BlobRef option
    Entries: ArtifactEntry list
    State: VersionState
    CreatedBy: Subject
    CreatedAt: UtcTimestamp
}
```

## 5. Policy and Provenance

```fsharp
type PolicyDecision =
    | Allow
    | Deny of reason: string
    | Quarantine of reason: string

type Attestation = {
    PredicateType: string
    StatementBlob: BlobRef
    SignedBy: string option
    CreatedAt: UtcTimestamp
}
```

## 6. Events (Outbox Boundary)

```fsharp
type DomainEvent =
    | UploadCommitted of repoId: RepoId * blobRef: BlobRef
    | VersionPublished of repoId: RepoId * packageId: PackageId * versionId: VersionId
    | VersionTombstoned of repoId: RepoId * versionId: VersionId
    | PolicyEvaluated of versionId: VersionId * decision: PolicyDecision
```

## 7. Smart Constructors and Invariants

```fsharp
module Validators =
    let trySha256 (input: string) =
        let is64Hex =
            input.Length = 64 &&
            input |> Seq.forall (fun c -> Uri.IsHexDigit c)
        if is64Hex then Ok (Sha256Digest(input.ToLowerInvariant()))
        else Error "Invalid SHA256 digest format"

    let tryContentLength (value: int64) =
        if value > 0L then Ok (ContentLength value)
        else Error "Content length must be positive"

    let tryVersionLabel (value: string) =
        if String.IsNullOrWhiteSpace value then Error "Version cannot be empty"
        else Ok (VersionLabel value)
```

Invariants enforced in model and transitions:

- Digest must be valid sha256 format and normalized lowercase.
- Content length must be positive.
- `Published` versions are immutable and cannot return to `Draft`.
- `ArtifactEntry` paths are unique inside a single version.
- Every published entry references an existing `BlobRecord`.
- Tombstoned versions remain addressable for audit but hidden from normal resolve APIs.

## 8. State Transition Functions (Sketch)

```fsharp
type PublishError =
    | MissingBlob of Sha256Digest
    | DuplicatePath of string
    | PolicyRejected of string
    | InvalidState of string

let publishVersion
    (validatePolicy: PackageVersion -> PolicyDecision)
    (blobExists: BlobRef -> bool)
    (version: PackageVersion) : Result<PackageVersion * DomainEvent list, PublishError> =

    match version.State with
    | Published -> Error (InvalidState "Version already published")
    | Tombstoned _ -> Error (InvalidState "Tombstoned version cannot be published")
    | Draft ->
        let duplicatePath =
            version.Entries
            |> List.groupBy (fun e -> e.RelativePath)
            |> List.tryFind (fun (_, g) -> List.length g > 1)
            |> Option.map fst

        match duplicatePath with
        | Some path -> Error (DuplicatePath path)
        | None ->
            let missing = version.Entries |> List.tryFind (fun e -> blobExists e.Blob |> not)
            match missing with
            | Some m -> Error (MissingBlob m.Blob.Digest)
            | None ->
                match validatePolicy version with
                | Deny reason -> Error (PolicyRejected reason)
                | Quarantine reason -> Error (PolicyRejected $"Quarantined: {reason}")
                | Allow ->
                    let published = { version with State = Published }
                    let events = [ VersionPublished (Unchecked.defaultof<RepoId>, version.PackageId, version.VersionId) ]
                    Ok (published, events)
```

Note:
- Repository id and actor context should be injected from command context.
- Persistence and outbox write occur atomically in transaction boundary outside this pure transition.
