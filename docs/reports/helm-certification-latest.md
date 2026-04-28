# Helm Certification Report

Generated at: 2026-04-28T19:38:11Z

## Summary

- overall status: PASS
- started at: 2026-04-28T19:37:35Z
- ended at: 2026-04-28T19:38:11Z
- cluster: artifortress-ha
- namespace: artifortress-helm-cert
- release: artifortress
- baseline chart: deploy/helm/artifortress
- baseline chart version: 0.1.0
- target chart: deploy/helm/artifortress
- target chart version: 0.1.0
- API ready replicas after upgrade: 3
- worker ready replicas after upgrade: 2

## Tooling

```text
kind v0.31.0 go1.25.5 linux/amd64
v4.1.4+g05fa379
Client Version: v1.36.0
Kustomize Version: v5.8.1
Server Version: v1.35.0
```

## Validated Scenarios

- Helm lint with kind HA values
- baseline Helm install into kind Kubernetes
- API and worker rollout readiness after install
- API liveness and readiness smoke
- authenticated admin smoke
- repository create/read smoke
- production preflight with Kubernetes and Helm checks after install
- Helm upgrade to target chart values
- API and worker rollout readiness after upgrade
- API liveness and readiness smoke after upgrade
- authenticated admin smoke after upgrade
- repository create/read smoke after upgrade
- production preflight with Kubernetes and Helm checks after upgrade
- Helm uninstall
- uninstall cleanup check for Helm-owned Artifortress resources
- data dependency preservation for Postgres and MinIO resources

## Helm History

```text
REVISION  UPDATED                   STATUS      CHART               APP VERSION  DESCRIPTION
1         Tue Apr 28 14:37:42 2026  superseded  artifortress-0.1.0  latest       Install complete
2         Tue Apr 28 14:37:58 2026  deployed    artifortress-0.1.0  latest       Upgrade complete
```

## Pod Placement Before Uninstall

```text
NAME                                   READY   STATUS      RESTARTS   AGE     IP            NODE                      NOMINATED NODE   READINESS GATES
artifortress-api-6c89d7bccf-fwjss      1/1     Running     0          13s     10.244.2.5    artifortress-ha-worker2   <none>           <none>
artifortress-api-6c89d7bccf-x7xrs      1/1     Running     0          29s     10.244.1.10   artifortress-ha-worker    <none>           <none>
artifortress-api-6c89d7bccf-zm27p      1/1     Running     0          29s     10.244.2.4    artifortress-ha-worker2   <none>           <none>
artifortress-worker-5d69d6c89f-nff24   1/1     Running     0          13s     10.244.2.6    artifortress-ha-worker2   <none>           <none>
artifortress-worker-5d69d6c89f-xmn55   1/1     Running     0          29s     10.244.1.9    artifortress-ha-worker    <none>           <none>
minio-b957f555c-r9wzj                  1/1     Running     0          9m9s    10.244.2.3    artifortress-ha-worker2   <none>           <none>
minio-bootstrap-1777404563-92zdw       0/1     Error       0          8m16s   10.244.1.4    artifortress-ha-worker    <none>           <none>
minio-bootstrap-1777404563-dnbzh       0/1     Error       0          3m36s   10.244.1.7    artifortress-ha-worker    <none>           <none>
minio-bootstrap-1777404563-gjm8m       0/1     Error       0          8m48s   10.244.1.2    artifortress-ha-worker    <none>           <none>
minio-bootstrap-1777404563-l8p6g       0/1     Error       0          6m16s   10.244.1.6    artifortress-ha-worker    <none>           <none>
minio-bootstrap-1777404563-wb7l8       0/1     Error       0          7m36s   10.244.1.5    artifortress-ha-worker    <none>           <none>
minio-bootstrap-1777404563-zhh9p       0/1     Error       0          8m36s   10.244.1.3    artifortress-ha-worker    <none>           <none>
minio-bootstrap-1777405056-w8d4m       0/1     Completed   0          35s     10.244.1.8    artifortress-ha-worker    <none>           <none>
postgres-5b9d58bb58-dqpmd              1/1     Running     0          9m9s    10.244.2.2    artifortress-ha-worker2   <none>           <none>
```

## Preserved Data Dependencies After Uninstall

```text
deployment.apps/minio
deployment.apps/postgres
```

## Preflight Reports

- baseline: `/tmp/artifortress-helm-cert-preflight-baseline.md`
- upgrade: `/tmp/artifortress-helm-cert-preflight-upgrade.md`

## Residual Risks

- default baseline chart path is the current chart unless
  `HELM_CERT_BASE_CHART` points to a previous released chart package
- validation uses local kind infrastructure, not managed cloud Kubernetes
- Postgres and MinIO are single-replica validation dependencies
- ingress/TLS is not validated in the kind certification path
