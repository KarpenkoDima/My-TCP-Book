# Анализ трёх черновиков (Draft-1, Claude-Draft-2, Claude-Draft-3)

**Дата:** 2026-02-08

---

## Что есть в каждом файле

### Draft-1 (Gemini)
Полный разговор с Gemini:
- Безжалостная оценка оригинального TCP-материала (7/10)
- Предложение нового силлабуса для Senior/Principal уровня (5 модулей)
- Написан **Модуль 1**: Физика и Ядро (DMA, Ring Buffers, NAPI, SoftIRQ, sk_buff, Offloading, Kernel Bypass)
- Написан **Модуль 2**: Bufferbloat & Congestion Control (BBR vs CUBIC, AQM, FQ_CoDel, CAKE) + Case Study (бэкап Амстердам→Нью-Йорк)
- Написан **Модуль 3**: Тюнинг и Диагностика (tc/netem, микроберсты, eBPF/BCC, sysctl tuning)
- Лабораторный стенд (3 уровня сложности + VMware-инструкция)

### Claude-Draft-2 (Claude, ранний разговор)
Оценка + переписка модулей:
- Оценка модулей Gemini (М1: 8/10, М2: 7.5/10)
- Переписанный **Модуль 2** до уровня "10/10" с кодом ядра (BBR internals: `bbr_update_bw()`, `bbr_update_min_rtt()`, CoDel: `codel_dequeue()`, FQ_CoDel: `fq_codel_dequeue()`, CAKE)
- Самооценка + обратная связь
- Новый **Модуль 3**: Traffic Control & Packet Scheduling (`struct Qdisc`, `Qdisc_ops`, TX path `__dev_xmit_skb()` → `__qdisc_run()`, HTB, tc filters, production example, HFSC, IFB)

### Claude-Draft-3 (Claude, поздний разговор)
Короткий:
- Быстрая оценка всего предыдущего
- Начат модуль по **IP** (IP Header `struct iphdr`, `ip_rcv()`, TOS/DSCP/ECN, TTL, Fragmentation `ip_fragment()`, Routing FIB trie) — **не дописан**

---

## Сравнительный анализ: Gemini vs Claude

| Критерий | Draft-1 (Gemini) | Claude-Draft-2 (Claude) |
|---|---|---|
| **Стиль** | Агрессивный, "мотивационный" ("Это ложь", "Hardcore Engineering"). Яркие метафоры (супермаркет, водопровод, парковка) | Академичнее, спокойнее. Объяснения "как учитель" — разбирает по шагам |
| **Глубина кода ядра** | Минимально. Упоминает структуры (`sk_buff`, `NAPI`), но не показывает реальный код | Сильно. Реальные функции ядра с комментариями: `bbr_update_bw()`, `codel_dequeue()`, `__dev_xmit_skb()`, `ip_rcv()`, `struct Qdisc` |
| **Охват тем** | Шире: 5 модулей в плане + лаборатория + архитектура приложений (io_uring, C# Pipelines, QUIC) | Уже, но глубже: 3 модуля + начат IP. Каждая тема разобрана до уровня кода |
| **Практика** | Хороша: `tc netem`, `ss -ti`, BCC tools, конкретный кейс-стади (бэкап Амстердам→Нью-Йорк), лабстенд | Хардкорнее: eBPF tracing BBR state machine, написание своего qdisc модуля, бинарный поиск bandwidth для CAKE |
| **Математика** | Почти нет. Формула CUBIC упомянута, BDP упомянут словами | Есть: Little's Law, BDP = BtlBw × RTprop, pacing_interval, формулы CoDel control_law |
| **Слабые места** | Поверхностность. BBR объяснён как "адаптивный круиз-контроль" без механики. Нет кода ядра. Модули 4-5 не написаны | Нет визуализации/диаграмм. Нет лабстенда. Нет production case studies. IP модуль брошен на полпути |

---

## Ключевые наблюдения

### 1. Gemini сильнее в "верхнеуровневом" планировании
Силлабус из 5 модулей — продуманная дорожная карта. Модули 4 (архитектура приложений: zero-copy, io_uring, Pipelines) и 5 (QUIC/HTTP3) — ценные темы, которых у Claude вообще нет. Лабораторный стенд с VMware — практически готовая инструкция.

### 2. Claude сильнее в "глубине реализации"
Модуль 2 (переписанный) и Модуль 3 у Claude — это другой уровень. Настоящий код ядра с объяснениями, не просто упоминания. Это то, что отличает "статью на Хабре" от "книги для ядерного инженера".

### 3. Оба страдают от отсутствия диаграмм
Оба автора признают это. Для материала такого уровня timing diagrams (BBR state machine, путь пакета через qdisc, работа CoDel) — необходимость, не опция.

### 4. Нет единой структуры
Сейчас есть три разрозненных разговора. Модуль 2 существует в **трёх версиях** (Gemini-оригинал, Gemini-переписка, Claude-переписка). Модуль 3 — в **двух** (у Gemini это tc/netem/eBPF/sysctl, у Claude это Qdisc architecture/HTB/filters). Они покрывают **разные** аспекты и оба ценны.

### 5. Незакрытые темы
- Модуль 4 (io_uring, zero-copy, C# Pipelines) — только план у Gemini
- Модуль 5 (QUIC/HTTP3) — только план у Gemini
- IP модуль — брошен Claude на середине
- Production case studies — ни у кого
- Диаграммы — ни у кого

---

## Рекомендация: как собрать книгу

Оптимальный путь — **взять структуру Gemini + глубину Claude**:

1. **Модуль 1** (Физика и Ядро) — база из Gemini, дополнить кодом ядра в стиле Claude
2. **Модуль 2** (Congestion Control) — Claude-версия как основа (она глубже), добавить кейс-стади из Gemini (Амстердам→Нью-Йорк)
3. **Модуль 3** — **объединить оба**: Qdisc architecture (Claude) + tc netem/chaos engineering (Gemini) + sysctl tuning (Gemini) + eBPF tooling (Gemini) + performance impact (Claude)
4. **Модуль 4** (IP) — дописать начатое Claude (IP header, fragmentation, routing, forwarding path)
5. **Модуль 5** (Архитектура приложений) — по плану Gemini (zero-copy, io_uring, C# Pipelines)
6. **Модуль 6** (QUIC/HTTP3) — по плану Gemini
7. **Лабстенд** — из Gemini (VMware), дополнить заданиями из Claude
