# EXP-022 Tuning Matrix (UA + EN)

## UA — Мета матриці

Цей файл потрібен для системного тюнінгу:

- `stability -> latency -> quality`
- щоб кожна зміна параметрів мала вимірюваний результат
- щоб уникати “вражень” без фактів

---

## UA — Фіксований сценарій заміру

Перед серією тестів зафіксувати:

- однакове джерело відео/аудіо
- однаковий target-сценарій (динаміка кадру)
- тривалість тесту: `10-15 хв` на профіль
- однаковий браузер і fullscreen/window режим

Під час тесту:

- 5 замірів latency через clock overlay (рівномірно по часу)
- фіксація фрізів (`count`, `max duration`)
- суб’єктивна якість (блокінг/розмиття/різкість) + короткий коментар

---

## UA — Профілі для старту

### Profile A (Balanced 1080p, baseline)

- Resolution: `1920x1080`
- FPS: `25`
- Video bitrate: `5000k`
- Audio: `Opus 48kHz`, bitrate `64k`
- GOP/keyint: `25` (1 sec)
- Encoder mode: low-latency without aggressive underflow

### Profile B (Lower latency, safer quality floor)

- Resolution: `1920x1080`
- FPS: `24`
- Video bitrate: `4500k`
- Audio: `Opus 48kHz`, bitrate `64k`
- GOP/keyint: `24`
- More strict queue limits on sender side

### Profile C (Stability first)

- Resolution: `1600x900` (або `1280x720`, якщо треба)
- FPS: `25`
- Video bitrate: `3500k` (900p) / `2500k` (720p)
- Audio: `Opus 48kHz`, bitrate `48k-64k`
- GOP/keyint: `25`

---

## UA — Таблиця результатів (заповнювати після кожного прогона)

| Date/Time | Profile | Resolution/FPS | V Bitrate | A Bitrate | Avg Lag (ms) | P95 Lag (ms) | Freeze Count | Max Freeze (ms) | Audio Drops | Quality Score (1-5) | Notes |
|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---|
|  | A | 1080p25 | 5000k | 64k |  |  |  |  |  |  |  |
|  | B | 1080p24 | 4500k | 64k |  |  |  |  |  |  |  |
|  | C | 900p25 | 3500k | 64k |  |  |  |  |  |  |  |

---

## UA — Правила прийняття рішень

- Якщо `Freeze Count > 0` або `Audio Drops > 0`, спочатку виправляємо **стабільність**.
- Якщо стабільність ок, зменшуємо lag:
  - дрібно знижуємо GOP/buffer/queue
  - не змінюємо >2 параметрів за один прогін
- Якщо lag в цілі, піднімаємо quality:
  - крок +500k bitrate
  - або піднімаємо FPS (за наявності запасу)

---

## EN — Matrix Purpose

Use this sheet for controlled tuning:

- `stability -> latency -> quality`
- every parameter change must produce measurable output
- avoid subjective-only conclusions

---

## EN — Fixed Measurement Scenario

Before a test batch, keep constant:

- same video/audio source
- same target scene dynamics
- run length: `10-15 min` per profile
- same browser and display mode

During each run:

- 5 latency samples via clock overlay (uniformly distributed)
- freeze tracking (`count`, `max duration`)
- visual quality notes (blocking/blur/sharpness)

---

## EN — Starter Profiles

### Profile A (Balanced 1080p, baseline)

- Resolution: `1920x1080`
- FPS: `25`
- Video bitrate: `5000k`
- Audio: `Opus 48kHz`, bitrate `64k`
- GOP/keyint: `25` (1 sec)
- Encoder mode: low-latency, non-aggressive

### Profile B (Lower latency, safe quality floor)

- Resolution: `1920x1080`
- FPS: `24`
- Video bitrate: `4500k`
- Audio: `Opus 48kHz`, bitrate `64k`
- GOP/keyint: `24`
- Tighter sender queue policy

### Profile C (Stability-first fallback)

- Resolution: `1600x900` (or `1280x720`)
- FPS: `25`
- Video bitrate: `3500k` (900p) / `2500k` (720p)
- Audio: `Opus 48kHz`, bitrate `48k-64k`
- GOP/keyint: `25`

---

## EN — Result Table (fill after each run)

| Date/Time | Profile | Resolution/FPS | V Bitrate | A Bitrate | Avg Lag (ms) | P95 Lag (ms) | Freeze Count | Max Freeze (ms) | Audio Drops | Quality Score (1-5) | Notes |
|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---|
|  | A | 1080p25 | 5000k | 64k |  |  |  |  |  |  |  |
|  | B | 1080p24 | 4500k | 64k |  |  |  |  |  |  |  |
|  | C | 900p25 | 3500k | 64k |  |  |  |  |  |  |  |

---

## EN — Decision Rules

- If `Freeze Count > 0` or `Audio Drops > 0`, fix **stability first**.
- If stability is good, reduce latency:
  - small GOP/buffer/queue adjustments
  - never change more than 2 parameters per run
- If latency target is met, improve quality:
  - +500k bitrate increments
  - or increase FPS if headroom exists
