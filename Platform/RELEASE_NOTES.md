# Platform Release Notes

## 2026-03-12 — `v0.9.0-rc1` candidate

Highlights:
- Bearer rollout hardened through mutation surfaces:
  - Phase 1: control mutations
  - Phase 2: invitation mutations
  - Phase 3: share/participant mutations
  - Phase 4: command mutations
- Keycloak realm tooling stabilized:
  - sync script normalization for smoke/client claims
  - realm reset workflow (`identity-reset`) with backup/delete/recreate/verify
- Policy governance lifecycle extended:
  - assignment activation/deactivation/delete semantics
  - scope activation/deactivation/delete semantics
- Operator shell quality:
  - backend-unavailable handling (no hard crash when API is offline)
  - Ops Status operational artifacts panel
  - Ops Status quick links (Doctor/CI Local/Full/Smoke) and priority ordering

Validation baseline (local):
- `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task doctor -StartApiProbe -RequireApi`
- `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task ci-local`
- `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full`

Demo bootstrap:
- `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-flow`
