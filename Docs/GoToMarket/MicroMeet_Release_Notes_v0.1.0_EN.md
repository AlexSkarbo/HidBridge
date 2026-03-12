# Micro Meet v0.1.0 Release Notes

Date: March 12, 2026

## Summary

`Micro Meet` is now in a reproducible demo-ready baseline for local operation:
- fleet-to-room flow
- invitation and control handoff flow
- bearer-first auth rollout across session mutation paths
- policy governance screen and lifecycle management
- operational scripts for doctor/smoke/full verification

## Highlights

1. Session room UX flow
- `Home` supports direct session start from fleet context.
- `Session Details` is structured as a room experience: snapshot, control actions, participants, invitations, timeline.

2. Policy governance
- Dedicated `Policy Governance` screen.
- Assignment lifecycle actions:
  - activate
  - deactivate
  - delete
- Effective role resolution ignores inactive assignments.

3. Auth hardening
- Bearer-first auth path stabilized for real API flows.
- Header fallback rollout profiles added:
  - Phase 1: control mutations
  - Phase 2: invitations
  - Phase 3: participants/shares
  - Phase 4: commands

4. Identity tooling
- Keycloak realm sync script with backup and normalization path.
- Realm reset workflow:
  - backup
  - delete
  - recreate from import
  - verify smoke users/token acquisition

5. Operational automation
- Unified `Platform/run.ps1` task launcher:
  - `doctor`
  - `smoke-bearer`
  - `ci-local`
  - `full`
  - `identity-reset`
  - `token-debug`
- Summary-first output with artifact/log paths.

## Verified Baseline

Expected pass path:

```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task doctor -StartApiProbe -RequireApi
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task ci-local
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full
```

## Known Current Constraints

- This is an internal demo baseline, not production SaaS.
- External IdP setups (for example Google) remain environment-specific and require local provider configuration.
- Media/control expansion can continue in future iterations.

## Next Planned Work

1. `policy scope deactivate semantics`
2. `Ops Status` links polish for logs/artifacts navigation
3. further bearer-only rollout for remaining mutation surfaces
