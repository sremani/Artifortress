# Hosting Decision Lock-in (Companion to Doc 73)

Last updated: 2026-04-28

## Purpose

This is a small companion note to
`docs/73-kublai-com-production-hosting-plan.md`. It collapses several
items in that document's §"Open Decisions" with concrete answers, based on
operator preference and a refined object-storage analysis.

Doc 73 remains the authoritative hosting plan. This note only records the
lock-ins; nothing here replaces or supersedes Doc 73's recommendations.

## Decisions Locked

| Open decision | Locked-in answer | Source in Doc 73 |
| --- | --- | --- |
| Final cloud provider | Akamai/Linode (LKE) | §Open Decisions #1 |
| Production region | Dallas (US-Central) | §Open Decisions #2 |
| PreProd region | Dallas (separate cluster, same DC) | §Open Decisions #3 |
| Cloudflare plan level | Free at launch, Pro when WAF is required | §Open Decisions #5 |
| LKE tier | Standard with HA control plane; Enterprise deferred | §Open Decisions #11 |
| Object storage provider | Cloudflare R2 (Linode Object Storage as fallback) | §Object Storage |

Five of the eleven items in §Open Decisions are now closed. The remaining
six (Dev shared cloud environment, database size and backup retention, log
retention, monitoring provider, public surface at `kublai.com` vs
`api.kublai.com`, web-vs-API split) are tunable post-launch and do not
block initial provisioning.

## Why Cloudflare R2 (Not Linode Object Storage)

Both options pass the Doc 74 "no MinIO in production" rule and both expose
the S3-compatible surface Kublai already targets. R2 wins for an
**artifact repository workload** specifically, because egress dominates the
bill: artifacts are downloaded thousands of times per build but uploaded
once per release.

| Concern | Linode Object Storage (Dallas) | Cloudflare R2 |
| --- | --- | --- |
| Egress | $0.005/GB beyond bundled 1 TB | $0 (zero egress, always) |
| Storage | $0.02/GB-month | $0.015/GB-month |
| Class A operations | included | $4.50 / 1M |
| Class B operations | included | $0.36 / 1M |
| Latency from Dallas LKE | ~1 ms (same DC) | ~5-15 ms (Cloudflare PoP) |
| Vendor surface | extra Linode component | already in Cloudflare for DNS/edge |

Cloudflare's edge cache further softens R2 latency for hot artifacts. The
egress savings outweigh the per-request operations cost at any non-trivial
download volume.

Linode Object Storage remains the declared fallback if R2's S3-compatibility
surface ever gaps a feature Kublai needs (multipart corner cases,
range-read edge behavior). The contract test plan called out in Doc 74
should run against both providers before final commit.

## Cloudflare Tunnel As LKE Ingress

Doc 73 plans for a Linode NodeBalancer in front of LKE. An alternative is to
run `cloudflared` as an in-cluster Deployment that dials out to Cloudflare's
edge, eliminating the public load balancer entirely.

Trade-offs:

- Pro: removes the ~$10/month NodeBalancer line item.
- Pro: no public IP or attack surface on the cluster; only outbound tunnel
  connections exist.
- Pro: Cloudflare WAF, rate limiting, and Access policies apply natively.
- Pro: same ingress pattern is already in use for `chengisci.com`; one
  operational mental model across both projects.
- Con: per-connector throughput ceiling (low Gbps). Run 2-3 `cloudflared`
  replicas at any meaningful scale.
- Con: slightly off the standard `Service: LoadBalancer` pattern teams
  expect on Kubernetes.

Recommendation: launch with Cloudflare Tunnel. If sustained customer
traffic exceeds connector throughput, switch to NodeBalancer plus a
standard ingress controller. That is a single one-way migration, not a
fork in the road.

## Updated Cost Ladder

Locked-in choices reduce the launch baseline slightly versus Doc 73's
budgetary estimate.

| Env | Topology | Monthly |
| --- | --- | --- |
| Dev | Local Docker Compose plus kind, with MinIO retained as a temporary local fixture (per Doc 74) | $0 |
| PreProd | 1× LKE node (8 GB shared), Linode managed PG smallest HA pair, R2 `kublai-preprod-blobs`, Cloudflare Tunnel ingress, `preprod.kublai.com` | ~$130-180 |
| Prod (lean launch) | 3× LKE workers (8 GB dedicated), HA control plane, Linode managed PG HA (4 GB pair), R2 `kublai-prod-blobs`, Cloudflare Tunnel ingress, `kublai.com` + `api.kublai.com` | ~$550-750 |
| Prod (comfortable) | scale PG to 8 GB pair, larger workers, Cloudflare Pro | ~$750-1,100 |

Savings versus Doc 73's baseline come from:

- Cloudflare R2 in place of paid Linode Object Storage at egress-heavy
  load.
- No NodeBalancer line item, with Cloudflare Tunnel as ingress.

Doc 73's broader budget envelope still applies. This note narrows the floor
by roughly $20-60/month at launch and more as egress grows.

## DNS Layout

| Hostname | Routes to | Environment |
| --- | --- | --- |
| `kublai.com` | Prod LKE via Cloudflare Tunnel | Prod |
| `api.kublai.com` | Prod LKE via Cloudflare Tunnel | Prod (recommended split for rate-limit policy clarity) |
| `preprod.kublai.com` | PreProd LKE via Cloudflare Tunnel | PreProd |
| `api-preprod.kublai.com` | PreProd LKE via Cloudflare Tunnel | PreProd |
| `dev.kublai.com` | Reserved; provisioned only if a shared cloud Dev environment becomes warranted | optional |
| `status.kublai.com` | Cloudflare Pages static page (manual incident updates) | meta |

The split between `kublai.com` and `api.kublai.com` is
recommended but not strictly required at launch. It exists primarily so
API rate-limit and WAF policies can be expressed independently of website
policies.

## Items Explicitly Not Touched

Doc 73 and adjacent documents cover the following and remain authoritative.
This note does not modify them.

- Helm chart certification path.
- Production preflight gates and the `make verify-enterprise` flow.
- Backup/restore drill, support-bundle workflow, observability stack.
- LKE node autoscaling rules.
- PostgreSQL HA upgrade triggers.
- The MinIO exit plan (Doc 74).
- The `FortressStore` side-project proposal (Doc 74 §"Side Project Proposal").

## Cutover Implication

Doc 73 §"Cutover Gates" remains binding. The lock-ins above advance items
3 ("production hosting account and region are selected") and 4 ("database
and object storage are provisioned") in posture, but they do not satisfy
those gates. Accounts must still be opened, buckets created, the
production preflight run, and the backup/restore drill executed before any
production traffic is routed.

## References

- `docs/73-kublai-com-production-hosting-plan.md` (authoritative
  hosting plan)
- `docs/74-object-storage-independence-and-minio-exit-plan.md` (MinIO exit
  context and `FortressStore` proposal)

---

*Authored by Claude (Opus 4.7) via Claude Code, in session with Srikanth Remani on 2026-04-28.*
