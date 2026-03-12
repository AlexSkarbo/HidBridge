# HidBridge ControlPlane Web — Відкриті Питання Дизайну

Дата: `2026-03-02`
Статус: `draft`
Мова документа: `Українська`

## 1. Призначення

Цей документ фіксує відкриті питання по дизайну та UX для `Platform/Clients/HidBridge.ControlPlane.Web`, які не треба втрачати під час поетапної реалізації:

1. visual language;
2. responsive behavior;
3. information density;
4. operator workflows;
5. localization behavior;
6. identity-aware UX;
7. tenant/org-aware UX.

Документ не блокує поточну розробку. Він потрібен як backlog рішень, до яких треба повернутися після стабілізації baseline.

## 2. Поточний baseline

Вже реалізовано:

1. `Blazor Web App`
2. `Tailwind CSS` browser baseline
3. thin operator shell
4. screens:
   - `Fleet Overview`
   - `Session Details`
   - `Audit Dashboard`
   - `Telemetry Dashboard`
   - `Settings`
5. operator actions:
   - `request control`
   - `release control`
   - `force takeover`
   - `approve invitation`
   - `decline invitation`
6. localization baseline:
   - `en` default
   - `uk` secondary
   - browser locale auto-detection
   - manual locale override
7. theme baseline:
   - auto mode
   - manual `light`
   - manual `dark`
8. known implementation gap:
   - ручний theme switch у `Settings` працює нестабільно і потребує окремого завершального виправлення
   - browser/OS theme detection працює стабільніше за manual toggle

## 3. Відкриті питання дизайну

### 3.1 Information Architecture

1. Чи потрібен окремий `Operations` home dashboard, який агрегує:
   - fleet health
   - active sessions
   - pending invitations
   - active control leases
   - recent incidents
2. Чи залишати `Fleet Overview` home page головною точкою входу, чи винести її в окремий розділ?
3. Чи потрібен окремий розділ `Diagnostics`, який об’єднає:
   - audit
   - telemetry
   - replay/archive

### 3.2 Session UX

1. Чи розбивати `Session Details` на вкладки:
   - overview
   - control
   - invitations
   - audit
   - telemetry
2. Чи потрібно відокремити `Control Operations` від `Invitation Moderation` на різні панелі або сторінки?
3. Як показувати escalation стан:
   - хто owner
   - хто active controller
   - хто moderator
   - хто observer

### 3.3 Dashboard Density

1. Яка цільова щільність даних допустима на laptop / desktop / ultrawide?
2. Чи потрібен compact mode?
3. Чи потрібна user-configurable card density / table density?

### 3.4 Visual Language

1. Наскільки строго thin operator shell має наслідувати `landing/index.html`?
2. Чи потрібні:
   - motion accents
   - micro-transitions
   - skeleton loaders
3. Чи потрібен окремий operator icon set?

### 3.4.1 Поточний відкладений дефект

1. Manual theme toggle у `Settings` ще не можна вважати production-ready.
2. Потрібно окремо перевірити:
   - cookie-based theme persistence
   - SSR/interactive handoff
   - перший рендер без повторного refresh
3. До окремого фіксу стабільною поведінкою вважається:
   - `prefers-color-scheme`
   - browser-level light/dark switching

### 3.5 Localization

1. Яка стратегія fallback для нових рядків:
   - strict English fallback
   - build-time fail on missing translation
2. Чи треба локалізувати:
   - timestamps
   - numbers
   - status labels
   - policy errors
3. Чи потрібна локалізація audit/timeline message templates на backend?

### 3.6 Identity-aware UX

1. Як показувати поточний principal у shell header?
2. Чи потрібен явний блок:
   - current org
   - current tenant
   - current role set
3. Як показувати відмову policy:
   - inline reason
   - toast
   - audit-linked denial details

### 3.7 Tenant / Organization UX

1. Чи потрібен tenant switcher?
2. Чи потрібен org switcher?
3. Як показувати cross-org restricted resources?
4. Чи потрібні tenant-aware кольори/лейбли для уникнення operator mistakes?

### 3.8 Accessibility

1. Чи потрібен keyboard-first navigation mode?
2. Чи потрібен high-contrast mode понад light/dark?
3. Чи потрібна reduced-motion adaptation?

## 4. Наступні рішення, які треба буде прийняти

Після стабілізації `Identity & Access baseline`:

1. визначити header identity model;
2. визначити org/tenant context switch UX;
3. визначити policy denial UX;
4. визначити production Tailwind pipeline замість browser baseline;
5. визначити остаточну design token system.

## 5. Правило для поточної реалізації

До повернення до цього документа діє такий принцип:

1. не ускладнювати UI декоративними патернами;
2. зберігати операторську щільність і читабельність;
3. не ламати сумісність з `landing/index.html` visual language;
4. тримати всі нові UI-рішення responsive;
5. уникати глибокої прив’язки UI до raw domain data — тільки через read models / projections.
