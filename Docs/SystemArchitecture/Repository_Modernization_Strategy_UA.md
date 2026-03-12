# Стратегія модернізації структури репозиторію (UA)

Дата: `2026-03-01`
Статус: `Recommended`

## Рішення

На цьому етапі **не переносити** поточне рішення з `Tools/` у `OldTools/` і не змішувати нову архітектуру з legacy-кодом в одній теці.

Рекомендована стратегія:

1. `Tools/` лишається як legacy/reference runtime.
2. `Platform/` створюється як нова greenfield-зона для clean modular architecture.
3. `WebRtcTests/exp-022-datachanneldotnet/` лишається лабораторією інтеграційних експериментів.
4. Після досягнення функціонального паритету виконується окремий migration/archival commit:
   - або `Tools/` -> `Tools.Legacy/`
   - або `Legacy/Tools/`

## Чому саме так

1. Мінімізуємо ризик масового ламання шляхів, скриптів і solution references.
2. Не змішуємо greenfield-архітектуру з історичним technical debt.
3. Полегшуємо code review: нова система розвивається окремо.
4. Даємо собі можливість поетапної міграції компонентів без великих rename/move змін.

## Коли можна переносити legacy

Тільки після виконання трьох умов:

1. Новий `Platform/` має мінімальний production-ready control path.
2. Є P0 parity для реєстрації агентів, відкриття сесій і dispatch команд.
3. Всі основні документи, CI і runbooks переведені на нові шляхи.

## Поточний рекомендований layout

```text
Tools/           # legacy/reference
Platform/        # new modular clean architecture
Firmware/        # hardware/firmware foundation
WebRtcTests/     # R&D / integration lab
Docs/            # architecture, SRS, migration docs
```

## 6. Оновлення стану `2026-03-02`

1. `Platform/Clients/` виділяється як окрема зона для thin operator UI та майбутніх web/mobile клієнтів.
2. Перший клієнт:
- `Platform/Clients/HidBridge.ControlPlane.Web`
- `Blazor Web App`
- `Tailwind CSS` browser baseline
3. У `Session Details` вже реалізовані baseline operator actions:
- `request control`
- `release control`
- `force takeover`
- `approve invitation`
- `decline invitation`
4. Web shell синхронізується з `landing/index.html` по visual language і має localization/theme baseline:
- `en` default
- `uk` secondary
- browser locale auto-detection
- manual locale override
- manual theme override залишається окремим відкритим дефектом UI baseline
- baseline identity shell with cookie session, optional OIDC, and tenant/org seams
- authorization-aware UX baseline:
  - current operator / roles / tenant / organization
  - moderation/control allow/deny reasoning
- централізований SSO baseline не будується всередині `HidBridge.ControlPlane.Web`
- dev IdP baseline: `Keycloak` з локальним login/password і federation зовнішніх провайдерів
- ручний перемикач теми в settings (`auto | light | dark`)
- responsive operator layout, синхронізований з `landing/index.html`
- core layout більше не залежить від delayed Tailwind runtime refresh


Оновлення стану `2026-03-03`:
- login/logout між `HidBridge.ControlPlane.Web` і `Keycloak` перевірено локально;
- централізований SSO baseline підтверджено практично;
- наступний practical step: `Google` як перший external IdP у `Keycloak`, далі той самий onboarding pattern для інших провайдерів.
