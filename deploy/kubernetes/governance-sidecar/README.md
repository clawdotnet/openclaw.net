# OpenClaw.NET Governance Sidecar

These manifests show the intended deployment pattern for optional tool governance:

- OpenClaw.NET runs the gateway and native tool runtime.
- A governance sidecar runs in the same Pod.
- OpenClaw.NET calls the sidecar over `http://127.0.0.1:8088`.
- Policies are mounted from a ConfigMap.

Apply the example:

```bash
kubectl apply -f configmap-policy.yaml
kubectl apply -f deployment.yaml
kubectl apply -f service.yaml
```

The example uses `/api/v1/execute` as the decision endpoint because that is OpenClaw.NET's default
adapter setting. Confirm the actual sidecar route and payload before production deployment, then
override `OpenClaw__Governance__DecisionEndpoint` if needed.

Governance is opt-in. If the sidecar is unavailable while governance is enabled, high-risk and
side-effecting tools fail closed by default.
