# HA Kubernetes Validation Report

Generated at: 2026-04-28T04:03:25Z

## Summary

- overall status: PASS
- started at: 2026-04-28T03:59:20Z
- ended at: 2026-04-28T04:03:25Z
- cluster: kublai-ha
- namespace: kublai-ha-validation
- release: kublai
- API ready replicas: 3
- worker ready replicas: 2

## Tooling

```text
kind v0.31.0 go1.25.5 linux/amd64
v4.1.4+g05fa379
Client Version: v1.36.0
Kustomize Version: v5.8.1
Server Version: v1.35.0
```

## Validated Scenarios

- kind cluster creation or reuse
- local API and worker image builds
- image load into kind nodes
- in-cluster Postgres and MinIO dependency startup
- MinIO bucket bootstrap
- SQL migrations applied through current head
- Helm install/upgrade
- API and worker rollout readiness
- API liveness/readiness over port-forward
- production preflight with Kubernetes and Helm checks
- API and worker rolling restart
- worker scale-down and restore
- API scale-down and restore

## Pod Placement

```text
NAME                                  READY   STATUS      RESTARTS   AGE     IP            NODE                      NOMINATED NODE   READINESS GATES
kublai-api-58f4cc9b7d-5vz6z     1/1     Running     0          12s     10.244.2.13   kublai-ha-worker    <none>           <none>
kublai-api-58f4cc9b7d-mc6fr     1/1     Running     0          2m37s   10.244.1.6    kublai-ha-worker2   <none>           <none>
kublai-api-58f4cc9b7d-qhqfr     1/1     Running     0          12s     10.244.2.12   kublai-ha-worker    <none>           <none>
kublai-worker-9b9fb4d94-f7kh9   1/1     Running     0          13s     10.244.2.11   kublai-ha-worker    <none>           <none>
kublai-worker-9b9fb4d94-fhgc6   1/1     Running     0          13s     10.244.1.7    kublai-ha-worker2   <none>           <none>
minio-b957f555c-pb8l9                 1/1     Running     0          9m20s   10.244.1.2    kublai-ha-worker2   <none>           <none>
minio-bootstrap-1777348452-sld4z      0/1     Completed   0          9m13s   10.244.2.3    kublai-ha-worker    <none>           <none>
minio-bootstrap-1777348760-842nn      0/1     Completed   0          4m5s    10.244.2.7    kublai-ha-worker    <none>           <none>
postgres-5b9d58bb58-62jdg             1/1     Running     0          9m20s   10.244.2.2    kublai-ha-worker    <none>           <none>
```

## Production Preflight

- report: `/tmp/kublai-kind-production-preflight.md`

## Residual Risks

- validation uses local kind infrastructure, not managed cloud Kubernetes
- Postgres and MinIO are single-replica validation dependencies
- ingress/TLS is not validated in the kind path
- production capacity is not certified by this validation
