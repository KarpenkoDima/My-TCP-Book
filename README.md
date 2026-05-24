# TCP/IP & Network Engineering — Deep Dive Book

*От физики сетевой карты до QUIC. От ядра Linux до Windows Internals. Для тех, кому мало «просто работает».*

---

## Для кого эта книга

- **Senior System/Network Engineers** — кто хочет понимать, почему пинг растёт при скачивании, где теряются пакеты и как работает BBR
- **High-Load Backend Developers** — кто пишет на .NET/C# и хочет выжать максимум из Kestrel, io_uring и System.IO.Pipelines
- **Windows Enterprise Engineers** — кто устал гадать, почему svchost грузит CPU, почему Windows говорит «Нет интернета» и как EDR-агенты убивают latency
- **DevOps/SRE** — кто строит chaos engineering стенды и тюнит sysctl на production-серверах

## Что вы узнаете

- Почему пинг растёт при скачивании торрента (Bufferbloat, AQM)
- Где именно теряются пакеты в ядре Linux (NAPI budget, sk_buff, ring buffer overflow)
- Как BBR обходит CUBIC на трансатлантических линках и почему ему нужен fq
- Почему `TIME_WAIT` — не баг, а фича, и когда `tw_reuse` безопасен
- Как CrowdStrike Falcon может удвоить p99 latency на SQL Server
- Почему Windows говорит «Нет интернета» при работающем VPN (NCSI + NLA)
- Как написать zero-copy proxy на io_uring и почему sendfile() бесполезен для HTTPS (без kTLS)

---

## Оглавление

### Часть I: Linux Network Stack — От провода до сокета

#### [Модуль 1: Физика и Ядро](modules/Module-01-Physical-Layer-and-Kernel.md)
*«Где реально умирают пакеты»*

- 1.1 Жизнь пакета до Socket API (DMA, Ring Buffer, struct sk_buff)
- 1.2 Hard IRQ и NAPI (прерывания, polling, budget)
- 1.3 Масштабирование (RSS, RPS, RFS, XPS, aRFS)
- 1.4 sk_buff — главная структура ядра (linear data, page frags, cloning)
- 1.5 Offloading (TSO, GRO, GSO, checksum offload)
- 1.6 Kernel Bypass (DPDK, AF_XDP, io_uring zerocopy)

---

#### [Модуль 2: Internet Protocol](modules/Module-02-IP-Protocol.md)
*«Анатомия сетевого уровня»*

- 2.1 IP Header — 20 байт (struct iphdr, ip_rcv() с kernel code)
- 2.2 IP Checksum — one's complement, инкрементальное обновление
- 2.3 Фрагментация (ip_fragment(), PMTUD, DF bit, MTU 1500, Jumbo Frames)
- 2.4 Routing — FIB trie (Patricia trie, longest prefix match, Policy Routing)
- 2.5 IP Forwarding Path (ip_forward(), Netfilter hooks, conntrack overhead)
- 2.6 IP Options — редкие, но опасные (Source Routing, slow path)
- 2.7 Multicast (IGMP, MFC, финансовые фиды, IPTV)
- 2.8 IPsec Integration (XFRM framework, ESP, AES-GCM, hardware offload)
- 2.9 **IPv6** — struct ipv6hdr, Extension Headers, NDP, dual-stack

---

#### [Модуль 3: TCP State Machine](modules/Module-03-TCP-State-Machine.md)
*«Жизненный цикл соединения»*

- 3.1 TCP State Machine — 11 состояний (ASCII-диаграмма полного автомата)
- 3.2 Three-Way Handshake (secure_tcp_seq(), ISN generation)
- 3.3 SYN Flood и защита (SYN/Accept queues, SYN Cookies, TCP Fast Open)
- 3.4 Передача данных — Sliding Window (tcp_sock fields, Window Scaling, Delayed ACK)
- 3.5 Retransmission (RTO Jacobson/Karels, Fast Retransmit, SACK scoreboard, RACK)
- 3.6 Закрытие соединения — Four-Way Handshake (TIME_WAIT, CLOSE_WAIT, RST)
- 3.7 Таймеры TCP (RTO, Persist, Keepalive, TIME_WAIT, FIN_WAIT_2)
- 3.8 Диагностика состояний (ss -ti, nstat, /proc/net/tcp, eBPF)
- 3.9 Типичные production-проблемы (CLOSE_WAIT leak, TIME_WAIT exhaustion)
- **3.10 TLS Handshake** — TLS 1.2 vs 1.3, 0-RTT, TCP Fast Open + TLS, kTLS в ядре

---

#### [Модуль 4: Bufferbloat и Congestion Control](modules/Module-04-Congestion-Control.md)
*«Война за очередь»*

- 4.1 Анатомия Bufferbloat (Little's Law, queueing theory)
- 4.2 CUBIC — рабочая лошадка интернета (cubic_cong_avoid(), Wmax, β=0.7)
- 4.3 BBR — Bottleneck Bandwidth and RTT (фазы: Startup/Drain/ProbeBW/ProbeRTT)
- 4.4 DCTCP и ECN — Congestion Control для дата-центров
- 4.5 AQM — Active Queue Management (CoDel, FQ_CoDel, CAKE с kernel code)
- 4.6 Искусственное ограничение скорости (Shaping)

---

#### [Модуль 5: Traffic Control, Тюнинг и Диагностика](modules/Module-05-Traffic-Control-Tuning-Diagnostics.md)
*«Режим Бога»*

- 5.1 Архитектура Qdisc (struct Qdisc, __dev_xmit_skb(), TX path)
- 5.2 Classless vs Classful Qdisc (pfifo_fast, fq_codel, HTB, HFSC)
- 5.3 TC Filters и Классификация (u32, flower, BPF classifier)
- 5.4 Production Example — полная настройка HTB для веб-сервера
- 5.5 Chaos Engineering с tc netem (delay, loss, corruption, reorder)
- 5.6 Микроберсты — невидимый убийца
- 5.7 eBPF и BCC — рентген ядра (tcpretrans, tcplife, tcpconnlat, bpftrace)
- 5.8 Sysctl Tuning — опасная зона (tcp_rmem/wmem, somaxconn, tw_reuse)
- 5.9 Performance Impact и оптимизация
- 5.10 Продвинутые техники

---

### Часть II: Прикладной уровень и будущее

#### [Модуль 6: Архитектура приложений](modules/Module-06-Application-Architecture.md)
*«Пишем код, который летает»*

- 6.1 Zero-Copy (sendfile, splice, MSG_ZEROCOPY, vmsplice)
- 6.2 IO Models — эволюция (select → poll → epoll → io_uring)
- 6.3 Прикладной уровень .NET/C# (System.IO.Pipelines, SAEA, Span\<T\>, Kestrel internals)
- 6.4 Практические паттерны (TCP_NODELAY, SO_REUSEPORT, connection pooling)

---

#### [Модуль 7: QUIC и HTTP/3](modules/Module-07-QUIC-HTTP3.md)
*«Будущее транспортного уровня»*

- 7.1 Фундаментальные проблемы TCP для Web (HoL blocking, handshake latency)
- 7.2 Архитектура QUIC (UDP + TLS 1.3 + streams)
- 7.3 Handshake — 0-RTT и 1-RTT
- 7.4 Stream Multiplexing без HoL Blocking
- 7.5 Connection Migration (CID, переключение Wi-Fi → LTE)
- 7.6 User-space Congestion Control
- 7.7 Структура QUIC-пакета (Header, Frames)
- 7.8 HTTP/3 (QPACK, server push, priorities)
- 7.9 QUIC в Linux и .NET (System.Net.Quic, msquic)
- 7.10 Проблемы и критика QUIC (middlebox ossification, CPU cost)
- 7.11 Когда QUIC, а когда TCP

---

### Часть III: Windows Network Internals

#### [Модуль 8: Windows Server Network Stack](modules/Module-08-Windows-Network-Stack.md)
*«NDIS, RSS и траблшутинг на уровне ядра»*

- 8.1 Ментальная модель (аэропорт = NDIS pipeline)
- 8.2 Архитектура NDIS 6.x (NET_BUFFER_LIST, filter chain, miniport)
- 8.3 RSS — Receive Side Scaling (Toeplitz hash, NUMA trap, UDP hash)
- 8.4 VMQ и dVMQ — RSS для Hyper-V
- 8.5 RSC — Receive Segment Coalescing (latency trade-off)
- 8.6 SR-IOV — обход гипервизора (VF, Live Migration failover)
- 8.7 WFP — Windows Filtering Platform + **EDR-агенты** (CrowdStrike/Defender callout overhead, диагностика через xperf и netsh wfp show state)
- 8.8 tcpip.sys (CUBIC/DCTCP/LEDBAT, AutoTuning, AFD.sys)
- 8.9 Хирургическая диагностика (ETW, xperf, WPA, DPC/ISR analysis)
- 8.10 Hardcore Lab — Chaos Engineering на Windows
- 8.11 Чеклист для Production (Invoke-NetworkStackAudit)

---

#### [Модуль 9: Windows Client Networking](modules/Module-09-Windows-Client-Networking.md)
*«Wi-Fi, DNS Client, VPN и "Нет интернета"»*

- 9.1 Анатомия клиентского стека (nwifi.sys, WLAN AutoConfig)
- 9.2 Wi-Fi Stack — 802.11 изнутри (roaming, power management, Modern Standby)
- 9.3 DNS Client (NRPT, DoH, SMHNR, negative cache trap)
- 9.4 NCSI — «Нет интернета» (IPv6 NCSI, кастомный endpoint для air-gapped сетей)
- 9.5 NLA — Network Location Awareness (гонка Domain/Public при VPN)
- 9.6 VPN Stack (Always On VPN, IKEv2, split tunneling, MTU black hole)
- 9.7 Сетевые сбросы (winsock reset, ip reset, adapter reset)
- 9.8 ETW-диагностика клиентских проблем
- 9.9 Типичные проблемы — Cookbook
- 9.10 Invoke-ClientNetworkDiag — полная диагностика одной командой

---

### Часть IV: Лаборатории и Chaos Engineering

#### [Модуль 10: EVE-NG Lab](modules/Module-10-EVE-NG-Lab.md)
*«Боевой полигон для .NET и Chaos Engineering»*

- **Развёртывание на Windows** (VMware Workstation, nested virt, отключение Hyper-V)
- **Этап 1 — Инфраструктура:** 7 узлов, 4 зоны, OSPF (VyOS + Cisco IOL)
  - 10.1 Архитектура стенда (топология, адресная схема, ресурсы VM)
  - 10.2 Создание топологии в EVE-NG
  - 10.3 Базовая настройка Linux-узлов (Netplan)
  - 10.4 Конфигурация VyOS/Cisco роутеров (OSPF Area 0)
- **Этап 2 — Ansible:** полная автоматизация
  - 10.5 Inventory (YAML, group_vars, sysctl из Модуля 5)
  - 10.6 Playbooks: base-setup, .NET Runtime, Redis, PostgreSQL
- **Этап 3 — Chaos Engineering:** внедрение сбоев
  - 10.7 Теория (где внедрять: роутер vs endpoint)
  - 10.8 High Latency, Packet Loss, Bandwidth Shaping — детальный разбор
  - 10.9 VyOS-native Traffic Policy
  - 10.10 Мониторинг (Prometheus + Grafana + .NET metrics)
  - 10.11 Скрипт полного развёртывания

---

#### [Лабораторный стенд — Базовый](modules/Lab-Setup.md)
*«Полигон для экспериментов»*

- Уровень 1: Network Namespaces (5 минут, zero overhead)
- Уровень 2: VMware 3-VM топология
- Уровень 3: Физические серверы
- Матрица совместимости задач × уровней

---

## Как читать

**Путь пакета (последовательно):**
`Модуль 1 (NIC) → 2 (IP) → 3 (TCP) → 4 (CC) → 5 (TC) → 6 (App) → 7 (QUIC)`

**Путь .NET-разработчика:**
`Модуль 3 (TCP) → 4 (CC) → 6 (App Architecture) → 7 (QUIC) → 10 (Chaos Lab)`

**Путь Windows-админа:**
`Модуль 8 (Server Stack) → 9 (Client) → 3 (TCP State Machine для понимания netstat)`

**Путь DevOps/SRE:**
`Модуль 10 (EVE-NG Lab) → 5 (TC/Tuning) → 1 (eBPF) → 4 (BBR)`

---

## Требования

- **Linux labs:** Ubuntu 22.04+, root-доступ, ядро 5.15+
- **Windows labs:** Windows Server 2019+ / Windows 10/11, PowerShell 5.1+
- **EVE-NG lab:** Core i9, 32 GB RAM, EVE-NG Community/Professional
- **Знания:** Уверенное владение Linux CLI / PowerShell, понимание TCP/IP на уровне CCNA+
