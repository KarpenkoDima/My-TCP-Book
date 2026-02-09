# Модуль 6: QUIC и HTTP/3 — Будущее транспортного уровня

*«TCP создавался, когда интернет был сетью университетов. QUIC создан для мира, где вы переключаетесь с Wi-Fi на LTE, не прерывая звонок».*

---

## Часть 6.1: Фундаментальные проблемы TCP для современного Web

### Head-of-Line Blocking (HoL)

TCP гарантирует **упорядоченную** доставку **всех** байтов в потоке. HTTP/2 мультиплексирует несколько логических стримов (запросов) поверх одного TCP-соединения.

Проблема:

```
HTTP/2 стримы поверх TCP:

Stream A: [pkt1] [pkt2] [pkt3]
Stream B: [pkt4] [pkt5] [pkt6]
Stream C: [pkt7] [pkt8] [pkt9]

TCP видит один поток байтов:
[pkt1] [pkt2] [pkt3] [pkt4] [pkt5] [pkt6] [pkt7] [pkt8] [pkt9]

Если pkt2 потерян:
[pkt1] [???] ← TCP ждёт ретрансмита pkt2
              ← pkt3, pkt4, pkt5... ВСЕ ЗАБЛОКИРОВАНЫ
              ← Stream B и C ждут, хотя ИХ пакеты дошли
```

Один потерянный пакет блокирует **все** стримы, даже те, которых потеря не касается. При 1% потерь HTTP/2 поверх TCP может быть **медленнее**, чем HTTP/1.1 с 6 параллельными соединениями.

### Ossification: TCP нельзя менять

Middleboxes (файрволлы, NAT, DPI, load balancers) инспектируют TCP-заголовки. Любое изменение формата — и пакет дропается или модифицируется.

Примеры провалов:
- **TCP Fast Open**: позволяет отправить данные в SYN. Многие файрволлы дропают SYN с payload. Adoption за 10 лет — менее 1%.
- **ECN**: middleboxes обнуляют ECN-биты. Развёртывание заняло два десятилетия.
- **Новые TCP options**: любая новая опция рискует быть отброшенной.

TCP «закостенел» (ossified) — его невозможно эволюционировать, потому что интернет полон устройств, которые делают предположения о формате TCP.

### Handshake Latency

Установка TCP + TLS 1.3:

```
Клиент                              Сервер
   |── SYN ──────────────────────────→|     }
   |←───────────────────── SYN+ACK ──|     } 1 RTT (TCP)
   |── ACK ──────────────────────────→|     }
   |── ClientHello ──────────────────→|     }
   |←──────────── ServerHello+Fin ───|     } 1 RTT (TLS 1.3)
   |── Fin ──────────────────────────→|     }
   |── HTTP Request ─────────────────→|     ← Наконец данные!

Итого: 2 RTT до первого байта данных
```

На мобильной сети с RTT = 100 мс это 200 мс до первого полезного байта.

### Connection Migration

TCP-соединение идентифицируется кортежем `(src_ip, src_port, dst_ip, dst_port)`. Сменили IP (Wi-Fi → LTE)? Соединение мертво. Нужно:

1. Обнаружить разрыв (таймаут, обычно секунды)
2. Новое TCP handshake
3. Новое TLS handshake
4. Восстановить состояние приложения

Мобильные пользователи испытывают это постоянно: зашёл в лифт, вышел из зоны Wi-Fi, переключился на другую базовую станцию.

---

## Часть 6.2: Архитектура QUIC

### Главная идея

QUIC — это транспортный протокол поверх UDP с интегрированным TLS 1.3.

```
Классический стек:           QUIC стек:

┌──────────────┐            ┌──────────────┐
│   HTTP/2     │            │   HTTP/3     │
├──────────────┤            ├──────────────┤
│   TLS 1.3    │            │    QUIC      │
├──────────────┤            │ (transport + │
│    TCP       │            │  TLS 1.3 +   │
├──────────────┤            │  streams)    │
│    IP        │            ├──────────────┤
└──────────────┘            │    UDP       │
                            ├──────────────┤
                            │    IP        │
                            └──────────────┘
```

Ключевые свойства:
- **UDP-based**: проходит через любой middlebox (UDP — «тупой» протокол, никто не инспектирует)
- **Встроенное шифрование**: TLS 1.3 интегрирован в transport layer. Заголовки тоже частично зашифрованы (anti-ossification)
- **Connection ID**: соединение идентифицируется не IP:port, а уникальным ID
- **Множественные стримы**: потеря в одном стриме не блокирует другие

---

## Часть 6.3: Handshake — 0-RTT и 1-RTT

### Первое соединение: 1 RTT

QUIC объединяет transport и crypto handshake в один roundtrip:

```
Клиент                              Сервер
   |── Initial (ClientHello) ────────→|
   |←──── Initial (ServerHello) ─────|  } 1 RTT
   |←──── Handshake (Encrypted) ─────|  }
   |── Handshake (Fin) + DATA ───────→|  ← Данные уже в первом RTT!
```

Сравните с TCP+TLS: 2 RTT. QUIC экономит целый roundtrip.

### Повторное соединение: 0-RTT

Если клиент уже подключался к серверу, он кеширует криптографические параметры:

```
Клиент                              Сервер
   |── Initial + 0-RTT DATA ────────→|  ← Данные В ПЕРВОМ ЖЕ ПАКЕТЕ!
   |←──── Initial + Handshake ───────|
```

Клиент отправляет данные прямо с первым пакетом, не дожидаясь ответа. Для мобильных приложений это революция: переход со страницы на страницу — мгновенный.

**Риск replay-атак:** злоумышленник может перехватить 0-RTT пакет и отправить его заново. Поэтому 0-RTT данные должны быть **идемпотентными** (безопасны при повторной обработке). Серверы должны отслеживать дубликаты.

---

## Часть 6.4: Stream Multiplexing без HoL Blocking

QUIC поддерживает **независимые стримы** внутри одного соединения:

```
QUIC Connection (Connection ID: 0xABCD)
  ├── Stream 0: HTTP Request /index.html
  ├── Stream 4: HTTP Request /style.css
  ├── Stream 8: HTTP Request /app.js
  └── Stream 12: HTTP Request /image.png

Потеря пакета из Stream 4:
  Stream 0: продолжает получать данные ✓
  Stream 4: ждёт ретрансмита ✗
  Stream 8: продолжает получать данные ✓
  Stream 12: продолжает получать данные ✓
```

Каждый стрим имеет свою нумерацию и свой порядок доставки. Потеря в одном не блокирует другие.

### Типы стримов

- **Bidirectional (client-initiated)**: ID 0, 4, 8, ... (чётные, клиент)
- **Bidirectional (server-initiated)**: ID 1, 5, 9, ... (нечётные, сервер)
- **Unidirectional (client-initiated)**: ID 2, 6, 10, ...
- **Unidirectional (server-initiated)**: ID 3, 7, 11, ...

### Flow Control

QUIC реализует flow control на двух уровнях:
- **Per-stream**: максимальный offset данных, которые получатель готов принять в конкретном стриме
- **Per-connection**: суммарный лимит по всем стримам

Это предотвращает ситуацию, когда один жадный стрим забирает все буферы.

---

## Часть 6.5: Connection Migration

### Как это работает

QUIC-соединение привязано к **Connection ID**, а не к IP:port. При смене IP:

```
1. Клиент переключился с Wi-Fi на LTE (новый IP)
2. Клиент отправляет QUIC-пакет с тем же Connection ID, но с нового IP
3. Сервер получает пакет, видит знакомый Connection ID
4. Сервер верифицирует путь (path validation) для защиты от hijacking
5. Соединение продолжает работу без потерь
```

### Path Validation

Для предотвращения атак (отправка пакетов с чужим Connection ID с нового IP):

```
Сервер → Клиент: PATH_CHALLENGE (случайный токен)
Клиент → Сервер: PATH_RESPONSE (тот же токен)
```

Только после успешной валидации сервер принимает новый путь.

### NAT Rebinding

Даже без явной миграции, NAT может изменить внешний порт клиента. TCP-соединение при этом ломается. QUIC — нет, потому что ориентируется на Connection ID.

---

## Часть 6.6: User-space Congestion Control

В TCP congestion control живёт в ядре. Обновление алгоритма = обновление ядра = перезагрузка = downtime.

QUIC работает в user space. Это значит:
- **Обновление CC без перезагрузки**: Google деплоит новые алгоритмы **еженедельно**
- **Per-application CC**: видео-стриминг использует один алгоритм, игры — другой, веб — третий
- **A/B тестирование**: 50% пользователей на BBR v3, 50% на экспериментальном алгоритме

QUIC по умолчанию использует те же алгоритмы (BBR, Cubic), но реализованные в приложении, а не в ядре. Это даёт гибкость, которая невозможна с TCP.

---

## Часть 6.7: Структура QUIC-пакета

### Long Header (Initial, Handshake)

```
+-+-+-+-+-+-+-+-+
|1|1|T T|X X X X|   Header Form (1) + Fixed Bit (1) + Type (2) + Reserved (4)
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                         Version (32)                          |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| DCID Len (8)  |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|               Destination Connection ID (0..160)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| SCID Len (8)  |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                 Source Connection ID (0..160)                  |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### Short Header (после handshake, для данных)

```
+-+-+-+-+-+-+-+-+
|0|1|S|R|R|K|P P|   Header Form (0) + Fixed (1) + Spin (1) + Reserved (2) + Key Phase (1) + PN Len (2)
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|               Destination Connection ID (0..160)              |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                      Packet Number (8..32)                    |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                     Protected Payload (*)                     |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

**Packet Number Encryption:** QUIC шифрует даже номер пакета. Зачем? Чтобы middleboxes не могли анализировать паттерны нумерации и делать предположения о протоколе. Это **anti-ossification** мера: если middlebox не может понять формат, он не будет пытаться его «улучшить».

### Frame Types

Payload QUIC-пакета состоит из фреймов:
- **STREAM**: данные стрима (основная нагрузка)
- **ACK**: подтверждения (с поддержкой ranges, как TCP SACK)
- **CRYPTO**: handshake данные
- **NEW_CONNECTION_ID**: выдача новых Connection ID (для миграции)
- **PATH_CHALLENGE / PATH_RESPONSE**: валидация нового пути
- **PADDING**: заполнение (для обхода MTU ограничений)

---

## Часть 6.8: HTTP/3

HTTP/3 — это HTTP-семантика (GET, POST, headers, body) поверх QUIC.

### Отличия от HTTP/2

| Аспект | HTTP/2 | HTTP/3 |
|---|---|---|
| Transport | TCP | QUIC (UDP) |
| TLS | Отдельный слой | Встроен в QUIC |
| Multiplexing | Стримы внутри TCP (HoL) | Стримы внутри QUIC (без HoL) |
| Header compression | HPACK | QPACK |
| Server Push | Поддержан | Поддержан (но редко используется) |

### QPACK: Сжатие заголовков

HPACK (HTTP/2) полагается на упорядоченную доставку для поддержания синхронизированной таблицы заголовков. В QUIC доставка может быть неупорядоченной.

QPACK решает это через два однонаправленных стрима:
- **Encoder stream**: отправитель сообщает об изменениях таблицы
- **Decoder stream**: получатель подтверждает получение изменений

Это позволяет сжимать заголовки без зависимости от порядка доставки.

---

## Часть 6.9: QUIC в Linux и .NET

### User-space реализации

- **quiche** (Cloudflare, Rust) — зрелая, production-ready. Используется в Cloudflare CDN.
- **msquic** (Microsoft, C) — кросс-платформенная. Основа для .NET QUIC.
- **ngtcp2** (C) — лёгкая, для встраивания. Используется в curl.
- **Quinn** (Rust) — async/await native.

### .NET System.Net.Quic

```csharp
// Сервер (.NET 7+)
using System.Net.Quic;

var listener = await QuicListener.ListenAsync(new QuicListenerOptions
{
    ListenEndPoint = new IPEndPoint(IPAddress.Any, 443),
    ApplicationProtocols = new List<SslApplicationProtocol>
    {
        new SslApplicationProtocol("h3")  // HTTP/3
    },
    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(
        new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate
            }
        })
});

// Принимаем соединение
var connection = await listener.AcceptConnectionAsync();

// Принимаем стрим
var stream = await connection.AcceptInboundStreamAsync();

// Читаем данные
var buffer = new byte[4096];
int bytesRead = await stream.ReadAsync(buffer);
```

```csharp
// Клиент
var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
{
    RemoteEndPoint = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 443),
    DefaultStreamErrorCode = 0,
    DefaultCloseErrorCode = 0,
    ClientAuthenticationOptions = new SslClientAuthenticationOptions
    {
        ApplicationProtocols = new List<SslApplicationProtocol>
        {
            new SslApplicationProtocol("h3")
        },
        TargetHost = "example.com"
    }
});

// Открываем стрим
var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional);
await stream.WriteAsync(Encoding.UTF8.GetBytes("Hello QUIC!"));
```

---

## Часть 6.10: Проблемы и критика QUIC

### Нет hardware offload

TCP имеет десятилетия оптимизаций в NIC: TSO, GRO, checksum offload. QUIC работает поверх UDP — NIC не понимает, что внутри. Каждый пакет обрабатывается CPU.

**Частичное решение:** UDP GSO (Generic Segmentation Offload) — ядро группирует несколько QUIC-пакетов одного размера и отправляет как один большой UDP. NIC разбивает на сегменты.

### Шифрование = overhead

Каждый пакет шифруется/расшифровывается в user space. AES-GCM на современных CPU с AES-NI — ~10 Gbps на одном ядре. Для 100 Gbps нужно 10 ядер только на крипто.

### UDP throttling

Некоторые корпоративные сети и провайдеры ограничивают или блокируют UDP-трафик (особенно на нестандартных портах). QUIC обычно использует порт 443, что помогает, но не гарантирует прохождение.

### Debugging

tcpdump видит только зашифрованные UDP-пакеты. Содержимое недоступно.

**Решения:**
- **SSLKEYLOGFILE**: экспорт ключей для расшифровки в Wireshark
- **qlog**: стандартизированный формат логов QUIC (RFC 9443)
- **qvis**: визуализатор qlog файлов (web-based)

```bash
# Экспорт ключей для Wireshark
export SSLKEYLOGFILE=/tmp/quic-keys.log
# Запускаем приложение с QUIC
# Wireshark: Edit → Preferences → TLS → (Pre)-Master-Secret log filename
```

---

## Часть 6.11: Когда QUIC, а когда TCP

| Сценарий | Рекомендация | Почему |
|---|---|---|
| Высокая латентность (спутник, межконтинентальные) | QUIC | 0-RTT экономит сотни мс |
| Потери > 1% (мобильные, Wi-Fi) | QUIC | Нет HoL blocking между стримами |
| Мобильные клиенты (миграция) | QUIC | Connection Migration |
| Data center (low latency, low loss) | TCP | Hardware offload, низкий overhead |
| 100 Gbps+ throughput | TCP | TSO/GRO offload критичен |
| Legacy-инфраструктура | TCP | Совместимость, tooling |
| Веб-приложения | QUIC | HTTP/3 — будущий стандарт |
| Сеть блокирует UDP | TCP (fallback) | Happy Eyeballs v2 |

**Практический подход:** попробуй QUIC, fallback на TCP. Это называется **Happy Eyeballs v2** — клиент пробует QUIC и TCP параллельно, использует тот, что ответил первым.

---

## Практическое задание

### Задача 1: 0-RTT Handshake

Установите Cloudflare quiche (`cargo build --examples`). Запустите сервер и клиент. Измерьте время первого подключения (1-RTT) и повторного (0-RTT). Сравните с TCP+TLS (`openssl s_time`).

### Задача 2: HTTP/2 vs HTTP/3 с потерями

Используя стенд из модуля 3:

```bash
# На роутере
tc qdisc add dev ens34 root netem loss 1%
```

Скачайте страницу с 20 ресурсами через HTTP/2 (TCP) и HTTP/3 (QUIC). Измерьте Page Load Time. При 1% потерь HTTP/3 должен быть значительно быстрее.

### Задача 3: Connection Migration

Запустите QUIC-загрузку большого файла. Во время передачи смените IP клиента:

```bash
ip addr del 192.168.60.10/24 dev eth0
ip addr add 192.168.60.11/24 dev eth0
```

Проверьте: продолжилась ли загрузка без разрыва?

### Задача 4: C# QUIC Echo Server

Напишите echo-сервер на C# с `System.Net.Quic` (.NET 7+). Клиент отправляет строку, сервер возвращает. Измерьте P99 латентность при 1000 concurrent стримов.

### Задача 5: Расшифровка QUIC в Wireshark

Настройте `SSLKEYLOGFILE`. Захватите QUIC-трафик. Расшифруйте в Wireshark. Найдите: STREAM frames, ACK frames, Connection ID. Сравните с TCP+TLS захватом того же контента.

---

**Это завершающий модуль курса.** Вы прошли путь от электрического сигнала на проводе до QUIC-стримов в user space. Используйте лабораторный стенд для экспериментов и помните: настоящий инженер не верит документации — он проверяет всё на практике.
