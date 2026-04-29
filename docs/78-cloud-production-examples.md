# Cloud Production Examples

Last updated: 2026-04-28

## Purpose

This document makes `EGA-21` concrete for the first managed Kubernetes target:
Akamai/Linode LKE with Cloudflare in front of the public edge.

The examples are deployable starting points, not certification evidence. A
target becomes certified only after release-artifact install, production
preflight, backup/restore drill, upgrade drill, smoke tests, and sign-off are
captured for the release candidate.

## Example Files

| Environment | Values file | Hostname | Runtime Secret |
| --- | --- | --- | --- |
| PreProd | `deploy/helm/kublai/values-lke-preprod.example.yaml` | `api-preprod.kublai.com` | `kublai-preprod-runtime` |
| Prod | `deploy/helm/kublai/values-lke-production.example.yaml` | `api.kublai.com` | `kublai-prod-runtime` |

Validate the examples before editing them:

```bash
make helm-cloud-examples-validate
```

Render a production manifest preview:

```bash
helm template kublai-prod deploy/helm/kublai \
  --namespace kublai-prod \
  --values deploy/helm/kublai/values-lke-production.example.yaml
```

Install or upgrade after substitutions and secret creation:

```bash
helm upgrade --install kublai-prod deploy/helm/kublai \
  --namespace kublai-prod \
  --create-namespace \
  --values deploy/helm/kublai/values-lke-production.example.yaml
```

## Required Substitutions

Replace these placeholders before any real deployment:

| Field | Replace With |
| --- | --- |
| `api.image.tag` and `worker.image.tag` | signed release tag, for example `v0.1.0-rc.1` |
| `ObjectStorage__Endpoint` | Cloudflare R2 S3 endpoint, or Linode Object Storage endpoint if fallback is selected |
| `Auth__Oidc__Issuer` | production or pre-production IdP issuer |
| `Auth__Oidc__Audience` | API audience expected in JWTs |
| `Auth__Oidc__JwksUrl` | IdP JWKS endpoint |
| `Auth__Oidc__RoleMappings` | tenant/repository role mapping accepted by the customer IdP |
| `ingress.host` | hostname routed from Cloudflare to the cluster |
| `ingress.className` | ingress class installed in the target cluster |

If SAML is the customer identity mode, flip `Auth__Saml__Enabled` to `true`,
set the SAML issuer/entity/metadata fields, and keep OIDC disabled unless both
modes are intentionally enabled during migration.

## Runtime Secret

The cloud examples set `secrets.create=false`. The Helm release references a
pre-created Kubernetes Secret instead of storing secret values in the values
file.

Create one Secret per environment with these keys:

```bash
kubectl create secret generic kublai-prod-runtime \
  --namespace kublai-prod \
  --from-literal='ConnectionStrings__Postgres=Host=<private-postgres-host>;Port=5432;Username=<username>;Password=<password>;Database=kublai;SSL Mode=Require;Trust Server Certificate=false' \
  --from-literal='Auth__BootstrapToken=<rotate-after-bootstrap>' \
  --from-literal='Auth__Oidc__Hs256SharedSecret=' \
  --from-literal='Auth__Saml__IdpMetadataXml=' \
  --from-literal='ObjectStorage__AccessKey=<bucket-scoped-access-key>' \
  --from-literal='ObjectStorage__SecretKey=<bucket-scoped-secret-key>'
```

Production operators should create this Secret from their secret-management
system or sealed-secret workflow. The direct `kubectl create secret` command is
only a bootstrap illustration.

## Managed Dependencies

Use separate dependencies per environment:

| Dependency | PreProd | Prod |
| --- | --- | --- |
| LKE cluster | separate cluster in Dallas | separate HA-control-plane cluster in Dallas |
| PostgreSQL | managed Postgres, no shared prod data | managed HA Postgres with backups and PITR validation |
| Object storage | `kublai-preprod-blobs` | `kublai-prod-blobs` |
| Ingress | Cloudflare proxied HTTPS to an LKE ingress controller | Cloudflare proxied HTTPS to an LKE ingress controller, with Cloudflare Tunnel as an operator-selected alternative |
| TLS | Cloudflare proxied HTTPS plus origin TLS | Cloudflare proxied HTTPS plus origin TLS |

MinIO is not part of the production example. It remains a local/kind test
fixture only until the object-storage conformance work replaces that dependency.

## Backup Posture

Before routing production traffic:

1. Confirm managed PostgreSQL automated backups are enabled.
2. Run a restore drill into an isolated database.
3. Capture `docs/reports/phase6-rto-rpo-drill-latest.md`.
4. Confirm object storage has environment-scoped credentials, capacity alerts,
   and lifecycle or retention policy matching customer requirements.
5. Confirm release-artifact upgrade and rollback evidence exists for the same
   tag installed in the cluster.

## Support Boundary

Supported by the current example:

- Helm rendering for LKE-shaped PreProd and Prod values.
- External Kubernetes Secret reference for managed secret injection.
- Managed PostgreSQL and S3-compatible object storage configuration shape.
- Cloudflare-fronted ingress hostnames matching the `kublai.com` plan.

Not certified by this example alone:

- Akamai/Linode account setup, IAM, private networking, or firewall policy.
- Cloudflare Tunnel connector deployment and throughput limits; Tunnel requires
  separate `cloudflared` configuration and may disable chart-owned Ingress.
- R2 versus Linode Object Storage compatibility at edge-case multipart/range
  behavior.
- Production capacity or latency targets on non-local infrastructure.
- Terraform/OpenTofu infrastructure creation.

Certification requires `EGA-23`, `EGA-24`, and the production sign-off evidence
for the exact release tag.
