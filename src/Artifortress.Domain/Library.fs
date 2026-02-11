namespace Artifortress.Domain

open System

type ServiceName = private ServiceName of string

module ServiceName =
    let tryCreate (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error "Service name cannot be empty."
        else
            Ok (ServiceName (value.Trim().ToLowerInvariant()))

    let value (ServiceName name) = name

type BuildMetadata = {
    Service: ServiceName
    Version: string
    BuiltAtUtc: DateTimeOffset
}

type RepoRole =
    | Read
    | Write
    | Admin
    | Promote

module RepoRole =
    let tryParse (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error "Role cannot be empty."
        else
            let normalized = value.Trim().ToLowerInvariant()

            match normalized with
            | "read" -> Ok Read
            | "write" -> Ok Write
            | "admin" -> Ok Admin
            | "promote" -> Ok Promote
            | _ -> Error $"Unsupported role '{value}'."

    let value role =
        match role with
        | Read -> "read"
        | Write -> "write"
        | Admin -> "admin"
        | Promote -> "promote"

    let implies assigned required =
        match assigned, required with
        | Admin, _ -> true
        | Write, Write
        | Write, Read
        | Read, Read
        | Promote, Promote -> true
        | _ -> false

type RepoScope = private { RepoKey: string; Role: RepoRole }

module RepoScope =
    let private validateRepoKey (repoKey: string) =
        if String.IsNullOrWhiteSpace repoKey then
            Error "Repository key cannot be empty."
        elif repoKey.Contains ":" then
            Error "Repository key cannot contain ':'."
        else
            Ok (repoKey.Trim().ToLowerInvariant())

    let tryCreate (repoKey: string) (role: RepoRole) =
        match validateRepoKey repoKey with
        | Error err -> Error err
        | Ok normalizedRepoKey -> Ok { RepoKey = normalizedRepoKey; Role = role }

    let repoKey scope = scope.RepoKey
    let role scope = scope.Role

    let tryParse (value: string) =
        if String.IsNullOrWhiteSpace value then
            Error $"Invalid scope '{value}'. Expected format: repo:<repoKey|*>:<role>."
        else
            let parts = value.Split(':', StringSplitOptions.None)

            if parts.Length <> 3 || parts.[0].Trim().ToLowerInvariant() <> "repo" then
                Error $"Invalid scope '{value}'. Expected format: repo:<repoKey|*>:<role>."
            else
                match RepoRole.tryParse parts.[2] with
                | Error err -> Error err
                | Ok parsedRole -> tryCreate parts.[1] parsedRole

    let value scope = $"repo:{scope.RepoKey}:{RepoRole.value scope.Role}"

    let allows (requiredRepoKey: string) (requiredRole: RepoRole) (scope: RepoScope) =
        if String.IsNullOrWhiteSpace requiredRepoKey then
            false
        else
            let normalizedRequiredRepo = requiredRepoKey.Trim().ToLowerInvariant()
            let repoMatch = scope.RepoKey = "*" || scope.RepoKey = normalizedRequiredRepo
            repoMatch && RepoRole.implies scope.Role requiredRole

module Authorization =
    let hasRole (scopes: seq<RepoScope>) (repoKey: string) (requiredRole: RepoRole) =
        scopes |> Seq.exists (fun scope -> RepoScope.allows repoKey requiredRole scope)
