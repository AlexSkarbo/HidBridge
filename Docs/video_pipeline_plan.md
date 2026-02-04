# Video pipeline (draft)

Цей документ описує канал відео окремо від control.

## Джерела

- USB capture card (UVC)
- USB webcam (UVC)
- IP камера (RTSP)
- Wi-Fi камера (RTSP/HTTP)

## Цілі

- мінімальна затримка для live-view
- контроль якості/затримки профілями
- масштабування на багато потоків і клієнтів

## Рекомендовані режими

1) **Low-latency** (live-view)
   - RTSP/SRT passthrough (FFmpeg -> RTSP/SRT listener)
   - мінімальні буфери, короткий GOP

2) **Quality**
   - HLS/LL-HLS для сумісності

## Пропозиція для v1

- FFmpeg публікує RTSP/SRT/RTMP напряму (без медіа-сервера)
- Сервер керування зберігає metadata про джерела
- Клієнти отримують список потоків і URL (REST)

## Майбутнє

- Запис (DVR) та стрім (RTMP)
- Адаптивна якість (ABR)
