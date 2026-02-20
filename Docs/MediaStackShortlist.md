# Media Stack Shortlist / Шортлист медіа-стеків

## Purpose / Мета
EN: Compare replacement media stacks using the same hard acceptance gate.
UA: Порівняти альтернативні медіа-стеки за однаковим жорстким acceptance gate.

## Platform Scope / Платформний scope
EN:
- Host: Windows, Linux, macOS (mandatory).
- Client: Web desktop + Android (mandatory), iOS (target).
UA:
- Хост: Windows, Linux, macOS (обов'язково).
- Клієнт: Web desktop + Android (обов'язково), iOS (ціль).

## Hard Gate (must pass all) / Жорсткий гейт (потрібно пройти все)
1. EN: 1920x1080 stable for 30 minutes.
   UA: Стабільний 1920x1080 протягом 30 хвилин.
2. EN: No freeze > 2 seconds.
   UA: Немає фризів > 2 секунд.
3. EN: Interactive lag within target (p95).
   UA: Інтерактивна затримка в межах цілі (p95).
4. EN: A/V sync stable (no growing drift).
   UA: Стабільна синхронізація A/V (без наростання дрейфу).
5. EN: Mouse/keyboard remain responsive and correct under load.
   UA: Миша/клавіатура залишаються чутливими та коректними під навантаженням.
6. EN: Android client passes; iOS support validated or documented fallback exists.
   UA: Android-клієнт проходить; підтримка iOS підтверджена або є документований fallback.

## Candidates / Кандидати

### A) OBS WebRTC (or OBS + proven WebRTC bridge)
EN:
- Pros: mature capture stack on Windows, practical low-latency profiles, fast setup.
- Cons: integration and control coupling need adapter layer.
- Risk: medium.
UA:
- Плюси: зрілий capture-стек на Windows, практичні low-latency профілі, швидкий старт.
- Мінуси: для інтеграції та керування потрібен адаптер.
- Ризик: середній.

### B) Dedicated media server path (LiveKit/Janus/mediasoup)
EN:
- Pros: robust media handling, better observability, scaling-ready.
- Cons: higher infra and ops complexity.
- Risk: medium-high.
UA:
- Плюси: надійніша обробка медіа, краща спостережуваність, готовність до масштабування.
- Мінуси: вища складність інфраструктури та експлуатації.
- Ризик: середньо-високий.

### C) Native WebRTC stack on Windows (libwebrtc-based component)
EN:
- Pros: strongest long-term quality potential.
- Cons: highest implementation cost/time.
- Risk: high.
UA:
- Плюси: найкращий довгостроковий потенціал якості.
- Мінуси: найвища вартість і тривалість реалізації.
- Ризик: високий.

### D) Keep legacy path (for reference only)
EN:
- Status: rejected as primary direction.
UA:
- Статус: відхилено як основний напрямок.

## Evaluation Matrix Template / Шаблон матриці оцінки
Use one table per candidate.

| Metric | Target | Result | Pass/Fail | Notes |
|---|---:|---:|---|---|
| 1080p 30m stability / Стабільність 1080p 30хв | pass |  |  |  |
| Freeze >2s count / К-сть фризів >2с | 0 |  |  |  |
| Lag p95 (ms) / Затримка p95 (мс) | <= target |  |  |  |
| A/V drift trend / Тренд дрейфу A/V | flat |  |  |  |
| Mouse/keyboard correctness / Коректність миші/клави | pass |  |  |  |
| Android client / Android-клієнт | pass |  |  |  |
| iOS client (or fallback) / iOS-клієнт (або fallback) | pass |  |  |  |

## Go/No-Go Rule / Правило Go/No-Go
EN: Candidate must pass all hard-gate items to proceed.
UA: Кандидат має пройти всі пункти жорсткого гейту для переходу далі.

## Recommended Next Step / Рекомендований наступний крок
EN: Run A and B first (highest practical value), then decide Go/No-Go.
UA: Спочатку протестувати A і B (найбільша практична цінність), потім прийняти Go/No-Go.
