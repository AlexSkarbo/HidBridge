# Micro Meet: GitHub Package

## Що це
`Micro Meet` — це операторський demo-ready шар поверх `HidBridge`, де можна:
- вибрати endpoint
- стартувати кімнату
- запросити другого оператора
- передати control
- показати timeline змін у кімнаті

## Що в цьому реально цікаве
1. Це не звичайний screen-share.
   Тут є керований `control handoff` для реального endpoint.
2. Є модель операторських ролей:
   - viewer
   - moderator
   - admin
3. Є audit/timeline для подій у кімнаті.
4. Є bearer-first auth path з локальним `Keycloak` dev contour.
5. Є готовий локальний operational kit:
   - `doctor`
   - `full`
   - `smoke-bearer`
   - `identity-reset`

## Що саме є продуктом

`Micro Meet` треба показувати не як "ще один модуль у великому репо", а як окремий user-facing вертикальний slice:
- `Fleet Overview`
- `Session Room`
- invite / request flow
- control handoff
- room timeline

Головна теза:
- це кімнати спільного керування реальними endpoint-ами
- а не просто віддалений desktop або звичайний meet clone

## Що показувати в README/GitHub
- `Fleet Overview`
- `Session Room`
- invite/request flow
- control handoff
- timeline
- `doctor/full` як доказ reproducible локального запуску

## Що має бути у першому публічному пакеті

1. Оновлений кореневий `README.md` з коротким позиціонуванням `Micro Meet`.
2. `Platform/README.md` як технічний quick start.
3. `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md` як manual demo path.
4. 3-4 скріншоти:
- Fleet Overview
- Session Room
- Invitation / control handoff
- Timeline
5. Один короткий GIF:
- `Launch room -> invite -> accept -> control handoff`
6. Явний список того, що вже працює локально:
- `doctor`
- `full`
- `smoke-bearer`

## Мінімальний demo script
1. `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full`
2. відкрити `/`
3. натиснути `Launch room`
4. перейти в `/sessions/{id}`
5. відкрити `room link` у другому профілі браузера
6. створити invite або request
7. accept / approve
8. request / release / force takeover
9. показати timeline

## Що викласти на GitHub першою версією
- код
- короткий GIF або 3-4 скріншоти
- `Platform/README.md`
- короткий "why it matters"
- чіткий "how to run locally"

## Suggested README structure

1. `What it is`
2. `Why this is different`
3. `Demo flow`
4. `How to run locally`
5. `Current scope / limitations`
6. `Roadmap`

## Що явно написати як limitation

- це ще `internal baseline`, не production SaaS
- room/collaboration flow already works locally
- media/remote-control story ще може розширюватись далі
- dev identity contour побудований на `Keycloak`

## Який headline я б радив
`Micro Meet: shared control rooms for real endpoints, not just screen sharing`

## Який subtitle я б радив
`Open a room, invite another operator, hand off control, and keep a timeline of what happened.`

## One-line elevator pitch

`Micro Meet` is a small operator-first collaboration layer for real endpoints: start a room from fleet view, invite another operator, transfer control, and keep a timeline of room activity.
