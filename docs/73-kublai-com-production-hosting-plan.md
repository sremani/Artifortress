# kublai.com Production Hosting Plan

Last updated: 2026-04-28

## Purpose

This document is the initial hosting hardware and cloud-infrastructure plan for
deploying Kublai at `kublai.com`.

It is a launch planning document, not yet deployment evidence. The plan becomes
release evidence only after provider selection, account setup, production
preflight, smoke tests, backup/restore drill, and release sign-off complete.

## Recommendation

Use a managed single-region Kubernetes deployment behind Cloudflare.

Recommended launch provider shape:

- Cloudflare for DNS, proxied HTTPS, TLS, basic DDoS protection, and WAF rules
- Akamai Cloud/Linode LKE for managed Kubernetes API and worker replicas
- managed PostgreSQL with high availability and backups
- S3-compatible object storage for artifact blobs
- managed load balancer in front of the Kubernetes ingress
- separate staging and production namespaces, databases, and buckets
- release artifacts from the signed provenance workflow

This matches the current Kublai support envelope:

- `3+` API replicas
- `2+` worker replicas
- managed HA PostgreSQL
- durable S3-compatible object storage
- readiness-gated Kubernetes/Helm deployment

Current preferred launch candidate: Akamai Cloud/Linode, fronted by
Cloudflare.

Use DigitalOcean as the fallback comparison provider if Akamai account setup,
region availability, or managed database features block launch.

## Baseline Hardware Shape

Start with a conservative production-small cluster sized for a real public
launch, not just a local demo.

### Cloudflare

Use:

- zone: `kublai.com`
- proxied DNS for web/API traffic
- `A` or `CNAME` from `kublai.com` to the cloud load balancer
- `api.kublai.com` if API and website are separated
- Full strict TLS mode after origin certificate wiring
- WAF rules for admin/API rate limiting
- DNS-only records for validation, email, or provider ownership checks

Initial plan:

- Cloudflare Free is enough for DNS, TLS, CDN, and DDoS bootstrap.
- Cloudflare Pro should be budgeted for production WAF controls.
- Cloudflare Load Balancing is optional for the first single-region deployment
  and becomes useful for future multi-region or blue/green cutover.

### Kubernetes Compute

Start with:

- Akamai Cloud/Linode LKE cluster
- high-availability LKE control plane add-on
- `3` worker nodes
- dedicated CPU class preferred over shared CPU
- node size: approximately `2 vCPU / 8 GiB RAM` minimum per node
- autoscaling enabled with a floor of `3` and ceiling of `6`

Initial pod allocation:

- `kublai-api`: `3` replicas
- `kublai-worker`: `2` replicas
- ingress controller: `2` replicas if not provider-managed
- monitoring/logging agents: daemonset or lightweight managed agent

Scaling rule:

- scale workers first when pending search jobs or outbox age rises
- scale API first when request latency rises while backlog is stable
- scale database before app replicas if DB CPU, connections, or write latency
  are the bottleneck

### PostgreSQL

Start with Akamai Managed PostgreSQL:

- high availability enabled
- at least one standby/failover node
- automated daily backups enabled
- point-in-time recovery validated during release certification
- private network access from Kubernetes
- public access disabled unless there is an explicit break-glass process
- initial storage sized with at least `2x` expected metadata growth headroom

Initial sizing:

- launch floor: dedicated `2 vCPU / 4 GiB RAM` HA cluster or nearest managed
  HA equivalent
- raise to dedicated `4 vCPU / 8 GiB RAM` HA cluster before onboarding
  multiple active customer tenants or running regular search rebuilds during
  business hours

### Object Storage

Use managed S3-compatible object storage. Do not self-host MinIO for
`kublai.com` production.

Use one bucket per environment:

- `kublai-prod-blobs`
- `kublai-staging-blobs`
- optional `kublai-prod-backups` if database backups are copied to object
  storage

Required controls:

- versioning or equivalent retention where available
- lifecycle policy for old release/test objects
- server-side encryption where provider supports it
- access keys scoped to Kublai buckets only
- bucket metrics and capacity alerts

Initial capacity:

- start with at least `250 GiB` object storage
- alert at `70%` and `85%` of planned capacity
- review cost and retention policy before storing large customer artifacts

MinIO remains acceptable only as a temporary local/kind validation fixture until
`EGA-33` removes or replaces that dependency.

### Load Balancer And Ingress

Use:

- provider-managed regional load balancer
- HTTPS ingress for `kublai.com`
- health checks against `/health/ready`
- no production traffic routed until production preflight passes

Cloudflare should proxy public HTTP/HTTPS traffic to the load balancer. The
origin should accept only expected Cloudflare/provider traffic where feasible.

### Observability And Operations

Required before public traffic:

- Grafana dashboard from `deploy/grafana-kublai-operations-dashboard.json`
- alert thresholds from `deploy/enterprise-ops-alert-thresholds.yaml`
- Kubernetes event/log access for API and worker pods
- database CPU, memory, storage, connection, and latency alerts
- object-storage capacity and error alerts
- support-bundle workflow tested against production

## Budgetary Estimate

This is a planning estimate only. Confirm exact prices in the provider account
before purchase.

Akamai Cloud/Linode is the preferred launch candidate because it offers LKE,
managed PostgreSQL, S3-compatible Object Storage, NodeBalancers, and predictable
component pricing. As of the current provider pages:

- LKE standard control plane is included with consumed resources.
- LKE high-availability control plane starts at about `$60/month` per cluster.
- LKE Enterprise includes a dedicated HA control plane at about `$300/month`,
  but should be treated as a later upgrade unless compliance or networking
  requirements demand it.
- Three G7 Dedicated 8 GB worker nodes list at about `$258/month`.
- A three-node shared 8 GB worker pool lists at about `$144/month`, but shared
  CPU should remain staging-only unless load testing proves it is acceptable.
- A NodeBalancer is required for ingress and lists at about `$10/month`.
- Managed PostgreSQL HA should start at dedicated 4 GB nodes, about
  `$171.60/month` for two nodes, or dedicated 8 GB nodes, about `$342/month`
  for two nodes, if search rebuilds and tenant activity are expected
  immediately.
- Object Storage is S3-compatible, priced per GB, with a minimum monthly charge
  for small accounts.
- Cloudflare Pro should be budgeted if production WAF controls are required.

Expected Akamai/Linode launch floor before logs/monitoring add-ons and taxes:

- budget production-small: roughly `$500-$700/month`
- comfortable production-small: roughly `$700-$1,100/month`

The lower number assumes three dedicated 8 GB LKE workers, HA control plane,
small dedicated PostgreSQL HA, one load balancer, and initial object storage.
The higher number leaves room for larger database nodes, log retention,
monitoring, backups, and traffic growth.

DigitalOcean remains a pragmatic fallback because it offers managed Kubernetes,
managed PostgreSQL, S3-compatible Spaces, and predictable pricing. Prior
DigitalOcean comparison points:

- DOKS worker nodes are charged as Droplets; the managed control plane is
  included, and HA control plane is a paid option.
- General Purpose Droplets list `2 vCPU / 8 GiB` at about `$63/month` per node.
- A three-node worker pool at that size is about `$189/month`.
- DOKS HA control plane is about `$40/month`.
- Regional load balancer starts around `$12/month`.
- Managed PostgreSQL HA starts with a primary and standby; exact size should be
  selected in the account before launch.
- Spaces starts at about `$5/month` for `250 GiB` storage and `1 TiB` outbound
  transfer.
- Cloudflare Pro should be budgeted if production WAF controls are required.

Expected DigitalOcean comparison floor before logs/monitoring add-ons and
taxes:

- budget cluster: roughly `$300-$500/month`
- more comfortable production-small setup: roughly `$500-$900/month`

DigitalOcean may be cheaper at the floor, but Akamai/Linode is acceptable if we
buy the HA control plane and managed PostgreSQL HA instead of optimizing for the
lowest bill.

### Environment Budget

Expected monthly operating budget for Akamai/Linode plus Cloudflare:

- Dev local-only: `$0` additional cloud spend
- optional shared cloud Dev: roughly `$75-$125/month`
- steady PreProd: roughly `$200-$350/month`
- Prod budget production-small: roughly `$500-$700/month`
- Prod comfortable production-small: roughly `$700-$1,100/month`

Recommended planning envelope:

- lean launch footprint, with local-only Dev: roughly `$750-$1,050/month`
- lean launch footprint, with shared cloud Dev: roughly `$825-$1,175/month`
- comfortable launch footprint: roughly `$1,100-$1,600/month`

The lean launch footprint assumes Prod uses three dedicated LKE workers, the HA
control plane, a small managed PostgreSQL HA cluster, one NodeBalancer, and
initial object storage. PreProd uses smaller always-on compute and managed
database resources, then temporarily scales up for load certification.

Budget exclusions:

- taxes
- domain renewal
- paid external log retention or observability SaaS
- Akamai/Linode managed infrastructure service
- Cloudflare enterprise add-ons
- traffic spikes, object-storage overage, and request pricing
- customer support tooling outside the Kublai stack

## Environment Shape

Use three operating environments: Dev, PreProd, and Prod.

### Dev

Purpose:

- fast product and integration iteration
- disposable data
- developer and CI validation before release-candidate promotion

Recommended shape:

- local Docker Compose and kind remain the default developer path
- optional shared Akamai/Linode LKE dev namespace for team integration
- `1` small Kubernetes node if using cloud dev
- `1` API replica
- `1` worker replica
- non-HA PostgreSQL or ephemeral database fixture
- separate object-storage bucket, never shared with PreProd or Prod
- DNS: `dev.kublai.com` only if a shared cloud dev environment is needed

Rules:

- no customer data
- no production secrets
- can be destroyed and rebuilt without approval
- no release sign-off comes directly from Dev

### PreProd

PreProd is the release rehearsal environment. It should not share production
state, but it should match production interfaces closely enough to catch cloud,
DNS, TLS, object-storage, database, and Helm issues before cutover.

Minimum PreProd:

- `1` or `2` Kubernetes nodes
- `1` API replica
- `1` worker replica
- separate managed PostgreSQL database or cluster
- separate S3-compatible object-storage bucket
- separate Cloudflare DNS records
- DNS: `preprod.kublai.com` and optionally
  `api-preprod.kublai.com`

PreProd can use smaller compute than Prod, but must exercise:

- the same Helm chart and signed release artifacts
- production-style database credentials and connection path
- production-style object-storage provider and bucket policy
- Cloudflare proxy, TLS, and WAF rules
- production preflight
- smoke tests through Cloudflare
- backup/restore drill
- rollback process

Promotion rule:

- release candidates promote Dev -> PreProd -> Prod
- Prod deployment is blocked unless the same artifact version passed PreProd
  preflight and smoke tests
- load certification may run in a temporary larger PreProd profile instead of
  keeping PreProd permanently sized like Prod

### Prod

Prod is the customer-facing `kublai.com` environment.

Prod must use:

- `3+` Kubernetes worker nodes
- `3+` API replicas
- `2+` worker replicas
- HA LKE control plane
- managed PostgreSQL HA
- managed S3-compatible object storage
- Cloudflare proxied DNS and production WAF rules
- backup, restore, observability, and support-bundle evidence

Environment isolation:

- separate Kubernetes namespace or cluster per environment
- separate PostgreSQL cluster or database per environment
- separate object-storage bucket per environment
- separate Cloudflare DNS records per environment
- separate credentials and access keys per environment
- no shared production secrets outside Prod

## Cutover Gates

Do not route `kublai.com` production traffic until:

1. release artifacts verify
2. Helm install/upgrade certification is complete
3. production hosting account and region are selected
4. database and object storage are provisioned
5. DNS, TLS, ingress, and WAF rules are configured
6. `make verify-enterprise` passes for the target release
7. production preflight report is `PASS`
8. smoke tests pass through Cloudflare
9. backup/restore drill is current
10. release owner and rollback owner approve cutover

## Open Decisions

- final cloud provider: preferred candidate is Akamai Cloud/Linode
- production region
- PreProd region
- whether Dev needs a shared cloud environment or stays local/kind only
- Cloudflare plan level
- database size and backup retention
- log retention period
- monitoring provider
- whether `kublai.com` serves product UI, API, or both
- whether API should live at `api.kublai.com`
- whether LKE standard with HA control plane is sufficient or LKE Enterprise is
  required later

## Source References

- Akamai LKE documentation:
  `https://techdocs.akamai.com/cloud-computing/docs/linode-kubernetes-engine`
- Akamai Kubernetes product page:
  `https://www.akamai.com/products/kubernetes`
- Akamai Cloud pricing:
  `https://www.akamai.com/cloud/pricing`
- Akamai managed database documentation:
  `https://techdocs.akamai.com/cloud-computing/docs/managed-databases`
- DigitalOcean Kubernetes pricing:
  `https://docs.digitalocean.com/products/kubernetes/details/pricing/`
- DigitalOcean Droplet pricing:
  `https://www.digitalocean.com/pricing/droplets`
- DigitalOcean PostgreSQL pricing:
  `https://docs.digitalocean.com/products/databases/postgresql/details/pricing/`
- DigitalOcean Spaces pricing:
  `https://docs.digitalocean.com/products/spaces/details/pricing/`
- DigitalOcean Load Balancer pricing:
  `https://docs.digitalocean.com/products/networking/load-balancers/details/pricing/`
- Cloudflare plans:
  `https://www.cloudflare.com/pricing/`
- Cloudflare DNS proxy behavior:
  `https://developers.cloudflare.com/dns/proxy-status/`
