# Micro Meet Publish Checklist

## 1. Repo

- [ ] Кореневий `README.md` містить короткий блок про `Micro Meet`
- [ ] `Platform/README.md` містить локальний quick start
- [ ] `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md` актуальний
- [ ] `Docs/GoToMarket/MicroMeet_GitHub_Package_UA.md` актуальний

## 2. Контур

- [ ] `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task checks`
- [ ] `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task doctor -StartApiProbe -RequireApi`
- [ ] `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full`
- [ ] `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task token-debug`

## 3. Demo UI

- [ ] `Fleet Overview` відкривається
- [ ] `Launch room` працює
- [ ] `Session Room` відкривається
- [ ] `Room link` працює у другому профілі браузера
- [ ] `Grant invite` / `Request invite` працюють
- [ ] `Approve` / `Accept` працюють
- [ ] `Request control` / `Release` / `Force takeover` працюють
- [ ] `Timeline` оновлюється

## 4. Артефакти для GitHub

- [ ] 1 GIF:
  - `Fleet -> Launch room -> Invite -> Accept -> Control handoff`
- [ ] 3-4 скріншоти:
  - `Fleet Overview`
  - `Session Room`
  - `Invitation / control actions`
  - `Timeline`

## 5. Тексти

- [ ] `Docs/GoToMarket/MicroMeet_Reddit_Post_UA.md`
- [ ] `Docs/GoToMarket/MicroMeet_LinkedIn_Post_UA.md`
- [ ] `Docs/GoToMarket/MicroMeet_Release_Notes_v0.1.0_EN.md`
- [ ] `Docs/GoToMarket/MicroMeet_Test_Matrix_UA.md`
- [ ] headline:
  - `Micro Meet: shared control rooms for real endpoints, not just screen sharing`

## 6. Що не забути

- [ ] не вставляти описовий текст у PowerShell, тільки команди
- [ ] перед демо проганяти `full`
- [ ] якщо realm drift-нув:
  - `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task identity-reset`
