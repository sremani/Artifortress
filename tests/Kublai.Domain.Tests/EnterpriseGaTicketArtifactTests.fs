module EnterpriseGaTicketArtifactTests

open System
open System.IO
open Xunit

let private repoRoot =
    let rec findRoot (dir: DirectoryInfo) =
        let hasReadme = File.Exists(Path.Combine(dir.FullName, "README.md"))
        let hasBoard = File.Exists(Path.Combine(dir.FullName, "docs", "58-enterprise-ga-ticket-board.md"))

        if hasReadme && hasBoard then
            dir.FullName
        elif isNull dir.Parent then
            failwithf "Could not locate repository root from %s" Environment.CurrentDirectory
        else
            findRoot dir.Parent

    findRoot (DirectoryInfo(Environment.CurrentDirectory))

let private readText relativePath =
    File.ReadAllText(Path.Combine(repoRoot, relativePath))

let private assertContains (expected: string) (actual: string) =
    Assert.Contains(expected, actual)

let private legacyBrandTokens =
    [ "Arti" + "fortress"
      "arti" + "fortress"
      "ARTI" + "FORTRESS" ]

let private isSkippedBrandScanPath (relativePath: string) =
    relativePath.StartsWith(".git/", StringComparison.Ordinal)
    || relativePath.StartsWith("artifacts/", StringComparison.Ordinal)
    || relativePath.EndsWith(".nettrace", StringComparison.Ordinal)
    || relativePath.Contains("/bin/", StringComparison.Ordinal)
    || relativePath.Contains("/obj/", StringComparison.Ordinal)

[<Fact>]
let ``enterprise GA board tracks tranche validation tickets`` () =
    let board = readText "docs/58-enterprise-ga-ticket-board.md"

    assertContains "| EGA-21T | Validate cloud-specific production examples | P1 | Validation | done |" board
    assertContains "| EGA-22T | Validate air-gapped/offline install plan | P1 | Validation | done |" board
    assertContains "| EGA-23T | Add release-artifact drill validation evidence | P1 | Validation | in_progress |" board
    assertContains "| EGA-25T | Validate package-format compatibility strategy | P1 | Validation | done |" board
    assertContains "| EGA-34T | Validate Kublai rebrand coverage | P0 | Validation | done |" board

[<Fact>]
let ``source-controlled tree has completed Kublai rebrand`` () =
    let policy = readText "docs/82-kublai-branding-and-rename-policy.md"
    let board = readText "docs/58-enterprise-ga-ticket-board.md"

    assertContains "Kublai is the product, codebase, documentation, deployment, and release brand." policy
    assertContains "docs/82-kublai-branding-and-rename-policy.md" board

    let allFiles =
        Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories)
        |> Seq.choose (fun path ->
            let relativePath = Path.GetRelativePath(repoRoot, path).Replace('\\', '/')

            if isSkippedBrandScanPath relativePath then
                None
            else
                Some(relativePath, path))

    let offenders =
        [ for relativePath, path in allFiles do
              for token in legacyBrandTokens do
                  if relativePath.Contains(token, StringComparison.Ordinal) then
                      yield $"{relativePath} contains legacy brand token in path"

              let text =
                  try
                      Some(File.ReadAllText(path))
                  with _ ->
                      None

              match text with
              | Some content ->
                  for token in legacyBrandTokens do
                      if content.Contains(token, StringComparison.Ordinal) then
                          yield $"{relativePath} contains legacy brand token in content"
              | None -> () ]

    Assert.True(List.isEmpty offenders, String.concat Environment.NewLine offenders)

[<Fact>]
let ``LKE example values use external runtime secrets`` () =
    let preprod = readText "deploy/helm/kublai/values-lke-preprod.example.yaml"
    let prod = readText "deploy/helm/kublai/values-lke-production.example.yaml"
    let chartDefaults = readText "deploy/helm/kublai/values.yaml"
    let secretTemplate = readText "deploy/helm/kublai/templates/secret.yaml"

    assertContains "secrets:\n  create: true\n  existingSecretName: \"\"" chartDefaults
    assertContains "secrets:\n  create: false\n  existingSecretName: kublai-preprod-runtime" preprod
    assertContains "secrets:\n  create: false\n  existingSecretName: kublai-prod-runtime" prod
    assertContains "{{- if .Values.secrets.create }}" secretTemplate
    assertContains "{{ include \"kublai.secretName\" . }}" secretTemplate

[<Fact>]
let ``offline install plan has required bundle manifest and validation hook`` () =
    let plan = readText "docs/81-airgapped-offline-install-plan.md"
    let manifest = readText "deploy/offline/release-manifest.example.env"
    let makefile = readText "Makefile"

    for heading in
        [ "## Offline Bundle Contents"
          "## Mirror Procedure"
          "## Offline Verification"
          "## Offline Install Procedure"
          "## Offline Upgrade Procedure"
          "## Unsupported Assumptions" ] do
        assertContains heading plan

    for key in
        [ "KUBLAI_RELEASE_TAG="
          "KUBLAI_CHART_PACKAGE="
          "KUBLAI_API_IMAGE="
          "KUBLAI_WORKER_IMAGE="
          "KUBLAI_SHA256SUMS="
          "KUBLAI_HELM_SBOM="
          "KUBLAI_OFFLINE_REGISTRY=" ] do
        assertContains key manifest

    assertContains "offline-install-plan-validate:" makefile

[<Fact>]
let ``release artifact drill reports require release metadata`` () =
    let phase6Drill = readText "scripts/phase6-drill.sh"
    let upgradeDrill = readText "scripts/upgrade-compatibility-drill.sh"
    let validator = readText "scripts/release-artifact-drill-validate.sh"

    for script in [ phase6Drill; upgradeDrill ] do
        assertContains "KUBLAI_RELEASE_TAG" script
        assertContains "KUBLAI_API_IMAGE_DIGEST" script
        assertContains "KUBLAI_WORKER_IMAGE_DIGEST" script
        assertContains "KUBLAI_HELM_CHART_DIGEST" script
        assertContains "KUBLAI_RELEASE_SBOM_PATH" script
        assertContains "KUBLAI_RELEASE_PROVENANCE_REPORT" script

    assertContains "release tag" validator
    assertContains "API image digest" validator
    assertContains "worker image digest" validator
    assertContains "Helm chart digest" validator
    assertContains "is unset" validator

[<Fact>]
let ``package format strategy keeps GA claims inside supported API boundary`` () =
    let strategy = readText "docs/80-package-format-compatibility-strategy.md"
    let envelope = readText "docs/59-enterprise-product-envelope.md"

    assertContains "Kublai enterprise GA supports the Kublai HTTP API only." strategy
    assertContains "Day-one support does not include native package-manager protocols" strategy
    assertContains "| Generic blob repository | P1 follow-up |" strategy
    assertContains "| Test Class | Required Coverage |" strategy
    assertContains "No protocol track may enter the enterprise support envelope until its test" strategy
    assertContains "docs/80-package-format-compatibility-strategy.md" envelope
