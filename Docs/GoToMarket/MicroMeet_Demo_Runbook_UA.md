# Micro Meet Demo Runbook

## Мета
Показати короткий сценарій, який виглядає як продукт, а не як інфраструктурний стенд:

1. login
2. Fleet Overview
3. Start session
4. Invite or request second operator
5. Accept / approve invitation
6. Control handoff
7. Timeline update

## Передумови

1. Підняти повний контур:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full
```

2. Перевірити стек:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task doctor -StartApiProbe -RequireApi
```

3. Відкрити `HidBridge.ControlPlane.Web`.

## Happy Path

1. Відкрити `/`.
2. У `Quick Start` натиснути `Launch room` або в рядку endpoint натиснути `Start session`.
3. Переконатися, що браузер переходить на `/sessions/{id}`.
4. У `Session Room` показати:
- `Session Snapshot`
- `Control Operations`
- `Invitation Moderation`
- `Participants`
- `Timeline`

5. Скопіювати `Room link`.
6. Відкрити link у другому профілі браузера.
7. У першому профілі:
- `Grant invite`
   або
- чекати `Request invite` від другого профілю

8. Схвалити або прийняти invite.
9. Показати нового учасника в `Participants`.
10. Виконати:
- `Request control`
- `Release`
- або `Force takeover` для moderator/admin

11. Показати, що `Timeline` оновився.

## Що говорити під час демо

1. Це не просто screen share.
2. Тут є кімната для реального endpoint.
3. Тут є операторські ролі, invite flow і контрольований handoff.
4. Усе, що сталося в кімнаті, лишається в timeline.

## Мінімальний check після демо

```powershell
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task smoke-bearer
```

Очікування:
- `PASS`
