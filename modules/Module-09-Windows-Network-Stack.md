# Модуль 9: Windows Network Stack Internals — NDIS, RSS и траблшутинг на уровне ядра

*«Когда стандартные логи молчат, а Performance Monitor показывает "всё нормально" — вы ещё даже не начали диагностику».*

---

## Введение и Суть (The "Why")

Каждый Senior Windows-инженер знает, как настроить NIC Teaming, включить RSS через GUI и посмотреть счётчики в PerfMon. Но когда на Hyper-V хосте с 40GbE адаптером сетевая производительность виртуальных машин падает до 5 Gbit/s, а `Get-NetAdapterStatistics` показывает отсутствие ошибок — стандартные инструменты бессильны.

Проблема в том, что **Windows Network Stack — это не монолит**. Это конвейер из десятков драйверов, фильтров и offload-механизмов, где каждый компонент может стать bottleneck. И чтобы найти его, нужно понимать архитектуру от NIC до сокета.

В этом модуле мы спустимся от `socket()` вызова в user space до `tcpip.sys` → `ndis.sys` → miniport driver, разберём RSS/VMQ/dVMQ/RSC/SR-IOV на уровне битов, и научимся диагностировать проблемы инструментами, которые Microsoft использует внутри.

---

## Часть 9.1: Ментальная модель — Конвейер обработки пакетов

### Аналогия: Аэропорт

Представьте Windows Network Stack как аэропорт:

```
Пассажир (пакет) прибывает на посадочную полосу (NIC)
    ↓
Паспортный контроль (NDIS miniport driver) — декодирует, проверяет
    ↓
Таможня (NDIS filter drivers: WFP, антивирус, NIC teaming)
    ↓
Зал прилёта (NDIS protocol driver = tcpip.sys) — маршрутизация
    ↓
Выход к такси (AFD.sys) — доставка до приложения (socket)
    ↓
Пассажир садится в такси (Winsock → Application)
```

Каждый «контроль» — это отдельный драйвер. И каждый может создать очередь.

**Критический момент:** В Linux пакет проходит через единый `sk_buff` pipeline. В Windows — через **NDIS filter stack**, где каждый вендор (антивирус, файрвол, VPN, мониторинг) вставляет свой filter driver. Три filter driver — и ваш latency утроился.

---

## Часть 9.2: Архитектура NDIS 6.x — Deep Dive

### NDIS: Network Driver Interface Specification

NDIS — это **фреймворк**, через который все сетевые компоненты Windows общаются друг с другом. Версия 6.80+ (Windows Server 2019+) поддерживает все современные offload-механизмы.

```
┌─────────────────────────────────────────────────────────────┐
│                    User Mode                                │
│   ┌──────────┐  ┌──────────┐  ┌──────────┐                │
│   │ App.exe  │  │ IIS/HTTP │  │ SQL Svr  │                │
│   └────┬─────┘  └────┬─────┘  └────┬─────┘                │
│        │              │              │                      │
│   ┌────┴──────────────┴──────────────┴─────┐               │
│   │         Winsock (ws2_32.dll)           │               │
│   └────────────────────┬───────────────────┘               │
├────────────────────────┼───────────────────────────────────┤
│                  Kernel Mode                                │
│   ┌────────────────────┴───────────────────┐               │
│   │          AFD.sys                        │               │
│   │   (Ancillary Function Driver)          │               │
│   │   Управляет socket buffers,            │               │
│   │   send/receive completion              │               │
│   └────────────────────┬───────────────────┘               │
│   ┌────────────────────┴───────────────────┐               │
│   │         tcpip.sys                       │               │
│   │   TCP/IP Protocol Driver               │               │
│   │   TCP state machine, IP routing,       │               │
│   │   congestion control (CUBIC/LEDBAT)    │               │
│   └────────────────────┬───────────────────┘               │
│   ┌────────────────────┴───────────────────┐               │
│   │         NDIS.sys                        │               │
│   │   NDIS Framework (6.80+)               │               │
│   │   NBL management, OID dispatch,        │               │
│   │   filter chain orchestration           │               │
│   └────┬───────────────┬───────────────────┘               │
│   ┌────┴────┐     ┌────┴──────────┐                        │
│   │ Filter  │     │   Filter      │                        │
│   │ Driver  │     │   Driver      │                        │
│   │ (WFP)   │     │  (AV/EDR)     │                        │
│   └────┬────┘     └────┬──────────┘                        │
│   ┌────┴───────────────┴───────────────────┐               │
│   │     NDIS Miniport Driver               │               │
│   │     (e.g., mlx5.sys, ixn65x64.sys)    │               │
│   │     Прямое взаимодействие с NIC        │               │
│   └────────────────────┬───────────────────┘               │
│                        │ DMA                                │
├────────────────────────┼───────────────────────────────────┤
│                   ┌────┴────┐                               │
│                   │   NIC   │     Hardware                  │
│                   └─────────┘                               │
└─────────────────────────────────────────────────────────────┘
```

### NET_BUFFER_LIST (NBL) — Windows-аналог sk_buff

В Linux пакет описывается `sk_buff`. В Windows — `NET_BUFFER_LIST` (NBL):

```
NET_BUFFER_LIST (NBL)
├── NET_BUFFER (NB) → описывает один пакет
│   ├── MDL (Memory Descriptor List) → описывает физ. страницы данных
│   │   └── Mapped virtual address → собственно байты пакета
│   ├── DataOffset → смещение до начала данных
│   └── DataLength → длина данных
├── NET_BUFFER (NB) → следующий пакет (chained)
├── Context area → метаданные для filter drivers
└── Next NBL → следующий NBL в цепочке
```

!!! warning "Критическая разница с Linux"
    В Linux `sk_buff` — один пакет. В Windows `NET_BUFFER_LIST` может содержать
    **цепочку** `NET_BUFFER`, каждый из которых — отдельный пакет. Это позволяет
    batch-processing: miniport driver отдаёт 64 пакета за один вызов `NdisMIndicateReceiveNetBufferLists()`.
    Это фундамент высокой производительности NDIS 6.x по сравнению с NDIS 5.x.

### Просмотр NDIS-стека

```powershell
# Показать все NDIS miniport drivers
Get-NetAdapter | Select-Object Name, InterfaceDescription, DriverFileName, DriverVersion

# Показать NDIS filter drivers (ВСЕ фильтры в стеке)
# Каждый фильтр — это задержка. Антивирус = +50-200μs на пакет.
Get-NetAdapterBinding | Where-Object { $_.Enabled -eq $true } |
    Select-Object Name, DisplayName, ComponentID

# Детальная информация о miniport driver
Get-NetAdapterAdvancedProperty -Name "Ethernet0" |
    Format-Table DisplayName, DisplayValue, ValidDisplayValues -AutoSize

# Через WMI/CIM — глубже
Get-CimInstance -ClassName MSFT_NetAdapter -Namespace root/StandardCimv2 |
    Select-Object Name, DriverDescription, NdisMedium, Speed
```

!!! tip "Диагностический приём: Отключение фильтров"
    Если подозреваете, что filter driver тормозит сеть — отключайте по одному:
    ```powershell
    # Список всех привязок
    Get-NetAdapterBinding -Name "Ethernet0"

    # Отключаем WFP Lightweight Filter (Windows Firewall)
    Disable-NetAdapterBinding -Name "Ethernet0" -ComponentID "ms_wfplwf_lower"

    # Отключаем антивирус (название зависит от вендора)
    Disable-NetAdapterBinding -Name "Ethernet0" -ComponentID "AvFilter"

    # Проверяем: выросла ли производительность?
    # Если да — нашли виновника.
    ```

---

## Часть 9.3: Receive Side Scaling (RSS) — Многоядерная обработка пакетов

### Проблема: Один процессор на весь трафик

Без RSS **весь** сетевой трафик обрабатывается одним ядром CPU. На 10GbE это ~14.8 Mpps (пакетов в секунду) при минимальном размере пакета. Одно ядро не может обработать столько — bottleneck.

### Как работает RSS

RSS распределяет входящие пакеты по нескольким очередям (hardware queues) NIC, каждая привязана к своему ядру CPU.

```
                        NIC Hardware
┌──────────────────────────────────────────────┐
│                                              │
│  Входящий пакет → Hash Function              │
│                   (Toeplitz)                 │
│                      │                       │
│    ┌─────────┬───────┼───────┬─────────┐     │
│    ↓         ↓       ↓       ↓         ↓     │
│  Queue 0  Queue 1  Queue 2  Queue 3  Queue N │
│    │         │       │       │         │     │
└────┼─────────┼───────┼───────┼─────────┼─────┘
     │         │       │       │         │
     ↓         ↓       ↓       ↓         ↓
   CPU 0     CPU 1   CPU 2   CPU 3    CPU N
   (DPC)     (DPC)   (DPC)   (DPC)    (DPC)
```

### Toeplitz Hash: Детерминированное распределение

RSS использует **Toeplitz hash** — криптографически слабый, но быстрый хеш. На вход: `{SrcIP, DstIP, SrcPort, DstPort}`. На выходе: номер очереди.

**Критическое свойство:** все пакеты одного TCP-соединения **всегда** попадают в одну очередь → обрабатываются одним ядром → гарантируется порядок.

```powershell
# === Проверяем RSS ===

# Включён ли RSS?
Get-NetAdapterRss -Name "Ethernet0"
# Enabled                 : True
# NumberOfReceiveQueues   : 8
# Profile                 : NUMAStatic
# BaseProcessorGroup      : 0
# BaseProcessorNumber     : 0
# MaxProcessorGroup       : 0
# MaxProcessorNumber      : 7
# MaxProcessors           : 8

# Какие процессоры используются?
Get-NetAdapterRss -Name "Ethernet0" | Select-Object -ExpandProperty IndirectionTable
# Индексы CPU для каждого bucket в indirection table
```

### Настройка RSS

```powershell
# Включить RSS (если был отключён)
Enable-NetAdapterRss -Name "Ethernet0"

# Задать количество очередей
# ВАЖНО: не ставьте больше, чем физических ядер (не HT!)
# HyperThreading-ядра делят execution units — два DPC на одном
# физическом ядре будут конкурировать за кэш.
Set-NetAdapterRss -Name "Ethernet0" -NumberOfReceiveQueues 8

# Привязать RSS к конкретным ядрам
# Пример: ядра 0-7 для NIC, ядра 8-15 для VM workload
Set-NetAdapterRss -Name "Ethernet0" `
    -BaseProcessorNumber 0 `
    -MaxProcessorNumber 7 `
    -Profile "NUMAStatic"

# Профили RSS:
# NUMAStatic    — привязка к ядрам одного NUMA-узла (рекомендуется!)
# NUMAStatic​Rss — то же, но с динамическим ребалансом
# Conservative  — минимум ядер
# ClosestProcessor — ближайшие к NIC ядра
```

!!! danger "NUMA — главная ловушка RSS"
    На двухсокетных серверах NIC физически подключена к одному NUMA-узлу.
    Если RSS раскидает пакеты на ядра **другого** NUMA-узла — каждый пакет
    будет проходить через QPI/UPI interconnect. Это +100-200ns на пакет.
    При 1 Mpps = 100-200ms дополнительной latency PER SECOND.

    Проверить NUMA-привязку NIC:
    ```powershell
    Get-NetAdapterHardwareInfo -Name "Ethernet0" | Select-Object NumaNode
    # NumaNode: 0  ← NIC на NUMA node 0

    # RSS должен использовать ядра ТОГО ЖЕ NUMA node
    Set-NetAdapterRss -Name "Ethernet0" -Profile "NUMAStatic"
    ```

### RSS Hash: Что хешируется

```powershell
# Посмотреть текущий тип хеша
Get-NetAdapterRss -Name "Ethernet0" | Select-Object -ExpandProperty HashType
# IPv4, TCP/IPv4, IPv6, TCP/IPv6

# По умолчанию хешируются: SrcIP + DstIP + SrcPort + DstPort (для TCP)
# Для UDP: только SrcIP + DstIP (нет портов — все UDP с одного IP → одна очередь!)

# Включить хеширование UDP-портов (важно для DNS, QUIC!)
Set-NetAdapterRss -Name "Ethernet0" -HashType "IPv4", "TCP/IPv4", "UDP/IPv4", "IPv6", "TCP/IPv6", "UDP/IPv6"
```

!!! warning "UDP и RSS: Скрытая ловушка"
    По умолчанию Windows **не хеширует** порты для UDP. Это значит, что
    весь UDP-трафик с одного IP (например, тысячи DNS-ответов от 8.8.8.8)
    попадает на **одно** ядро. Для DNS-серверов и QUIC — это катастрофа.
    Включайте UDP hash всегда.

### Диагностика RSS через ETW

```powershell
# Запускаем трассировку сетевого стека
netsh trace start capture=yes tracefile=C:\traces\rss_diag.etl `
    provider=Microsoft-Windows-NDIS-PacketCapture `
    provider=Microsoft-Windows-TCPIP `
    maxSize=512

# Генерируем нагрузку...

netsh trace stop

# Анализ: распределение пакетов по процессорам
# В Message Analyzer (или WPA): фильтруем по ProcessorNumber
# Если все пакеты на CPU 0 — RSS не работает
```

```powershell
# Быстрая проверка: счётчики прерываний по процессорам
# Если DPC концентрируются на одном ядре — RSS сломан

# Через Performance Counter
$counters = Get-Counter -Counter "\Processor(*)\% DPC Time" -SampleInterval 1 -MaxSamples 5
$counters.CounterSamples | Where-Object { $_.CookedValue -gt 5 } |
    Sort-Object CookedValue -Descending |
    Format-Table InstanceName, @{N="DPC%";E={[math]::Round($_.CookedValue,1)}}

# Ожидание: DPC нагрузка распределена по ядрам 0-7 (RSS working)
# Проблема: вся DPC нагрузка на CPU 0 (RSS broken/disabled)
```

---

## Часть 9.4: VMQ и Dynamic VMQ (dVMQ) — RSS для виртуализации

### Проблема: Hyper-V убивает RSS

Когда NIC отдаётся виртуальной машине через vSwitch, **RSS перестаёт работать**. vSwitch — это software switch, он перехватывает все пакеты и раскидывает их по VM. Пакеты проходят через один CPU.

### VMQ: Virtual Machine Queues

VMQ решает проблему, выделяя **отдельную hardware queue** для каждой VM:

```
                        NIC Hardware
┌──────────────────────────────────────────────┐
│                                              │
│  MAC filter → Пакет для VM-1? → Queue 1     │
│  MAC filter → Пакет для VM-2? → Queue 2     │
│  MAC filter → Default?       → Queue 0      │
│                                              │
│  Queue 0 ──→ CPU 0 (Host/vSwitch)           │
│  Queue 1 ──→ CPU 2 (VM-1 vCPU)             │
│  Queue 2 ──→ CPU 4 (VM-2 vCPU)             │
│                                              │
└──────────────────────────────────────────────┘
```

```powershell
# Включить VMQ
Set-NetAdapterVmq -Name "Ethernet0" -Enabled $true

# Посмотреть VMQ allocation
Get-NetAdapterVmqQueue -Name "Ethernet0" |
    Format-Table QueueID, MacAddress, VlanID, ProcessorAffinityMask -AutoSize

# Сколько VMQ очередей доступно?
Get-NetAdapterVmq -Name "Ethernet0" | Select-Object NumberOfReceiveQueues
# Типично: 16-128 для enterprise NIC (Intel X710, Mellanox ConnectX-5)
```

### dVMQ: Dynamic VMQ (Windows Server 2019+)

VMQ выделяет одну очередь на VM. Если VM генерирует 10 Gbit/s — одно ядро снова bottleneck. dVMQ решает это: **несколько очередей на одну VM**, фактически RSS внутри VMQ.

```powershell
# dVMQ включается автоматически при:
# 1. Windows Server 2019+
# 2. NIC поддерживает dVMQ
# 3. VMQ включён

# Проверить поддержку dVMQ
Get-NetAdapterAdvancedProperty -Name "Ethernet0" -DisplayName "*VMQ*"

# Настроить количество очередей для VM (в Hyper-V)
Set-VMNetworkAdapter -VMName "app-01" -VmqWeight 100
Set-VMNetworkAdapter -VMName "app-01" -VrssEnabled $true
# VrssEnabled = Virtual RSS = RSS внутри VM через dVMQ
```

!!! tip "dVMQ vs VMQ: Когда что использовать"
    | Сценарий | VMQ | dVMQ |
    |---|---|---|
    | Много VM с низким трафиком (< 1 Gbit/s каждая) | Достаточно | Overkill |
    | Мало VM с высоким трафиком (> 5 Gbit/s) | Bottleneck | Необходим |
    | SQL Server в VM | VMQ хватает | dVMQ для OLTP |
    | .NET API с 10K+ RPS | Не хватает | Обязательно |

---

## Часть 9.5: Receive Segment Coalescing (RSC) — Пакетный GRO

### Проблема: Миллионы мелких пакетов

Каждый TCP ACK — ~60 байт. При 10 Gbit/s bulk transfer генерируется ~1 Mpps только ACK-ов. Каждый пакет = прерывание + DPC + NDIS traversal.

### RSC: Склеиваем пакеты

RSC (Windows-аналог Linux GRO) объединяет несколько TCP-сегментов в один большой перед передачей в `tcpip.sys`:

```
Без RSC:                        С RSC:
Пакет 1 (1460 байт) → DPC      ┐
Пакет 2 (1460 байт) → DPC      ├→ Один "пакет" (43800 байт) → один DPC
Пакет 3 (1460 байт) → DPC      │
...                              │
Пакет 30 (1460 байт) → DPC     ┘

30 DPC вызовов → 1 DPC вызов = 30x меньше overhead
```

```powershell
# Проверить RSC
Get-NetAdapterRsc -Name "Ethernet0"
# IPv4Enabled : True
# IPv6Enabled : True

# Включить RSC
Enable-NetAdapterRsc -Name "Ethernet0" -IPv4 -IPv6

# Статистика RSC (сколько пакетов склеено)
Get-NetAdapterStatistics -Name "Ethernet0" |
    Select-Object ReceivedUnicastPackets,
                  @{N="CoalescedPackets";E={$_.RscStatistics.CoalescedPackets}},
                  @{N="CoalescedBytes";E={$_.RscStatistics.CoalescedBytes}},
                  @{N="CoalesceEvents";E={$_.RscStatistics.CoalesceEvents}}
```

!!! danger "RSC убивает latency-sensitive приложения"
    RSC накапливает пакеты перед передачей (coalescing window ~1ms).
    Для bulk transfer — отлично. Для low-latency (финансы, игры, RDP) —
    добавляет до 1ms задержки. **Отключайте RSC на NIC для latency-sensitive VM.**

    ```powershell
    # Отключить RSC для конкретного адаптера
    Disable-NetAdapterRsc -Name "RDP-NIC" -IPv4 -IPv6
    ```

---

## Часть 9.6: SR-IOV — Обход гипервизора (Nuclear Option)

### Проблема: vSwitch = bottleneck

Даже с VMQ/dVMQ пакеты проходят через Hyper-V vSwitch (software). Это добавляет ~10-50μs на пакет. При 1 Mpps = 10-50 секунд CPU time в секунду.

### SR-IOV: Прямой доступ VM к NIC

**Single Root I/O Virtualization** позволяет NIC создать «виртуальные копии» самой себя (Virtual Functions — VF). Каждая VM получает **прямой DMA-доступ** к VF, минуя vSwitch полностью:

```
Без SR-IOV:                           С SR-IOV:

VM ←→ vSwitch ←→ NIC                  VM ←─── DMA ───→ NIC VF
      ↑ CPU!                                 ↑ Zero CPU!

Пакет проходит:                       Пакет проходит:
NIC → DMA → Host RAM →               NIC VF → DMA → VM RAM
  vSwitch (CPU) → VM RAM              (всё в hardware)
= ~30μs                               = ~3μs
```

```powershell
# === Проверка поддержки SR-IOV ===

# 1. NIC поддерживает?
Get-NetAdapterSriov -Name "Ethernet0"
# Enabled            : True
# NumVFs             : 64   ← максимум Virtual Functions
# SriovSupport       : Supported

# 2. BIOS/UEFI: VT-d (Intel) или AMD-Vi включён?
# Проверяем через systeminfo или:
Get-CimInstance -ClassName Win32_Processor |
    Select-Object Name, VirtualizationFirmwareEnabled, SecondLevelAddressTranslationExtensions
# VirtualizationFirmwareEnabled: True
# SecondLevelAddressTranslationExtensions: True (= IOMMU/VT-d)

# 3. Hyper-V vSwitch создан с поддержкой SR-IOV?
# ВАЖНО: SR-IOV нужно включить ПРИ СОЗДАНИИ vSwitch!
# Нельзя добавить потом.
Get-VMSwitch | Select-Object Name, IovEnabled, IovSupport, IovSupportReasons
```

### Включение SR-IOV

```powershell
# Шаг 1: Создаём vSwitch с SR-IOV (если ещё не создан)
# ВНИМАНИЕ: пересоздание vSwitch = downtime для всех VM на этом свитче!
New-VMSwitch -Name "SRIOVSwitch" `
    -NetAdapterName "Ethernet0" `
    -EnableIov $true `
    -AllowManagementOS $true

# Шаг 2: Включаем SR-IOV для VM
Set-VMNetworkAdapter -VMName "app-01" `
    -IovWeight 100 `
    -IovQueuePairsRequested 4  # 4 queue pairs = 4 RSS queues в VM

# Шаг 3: Проверяем, что VF назначена VM
Get-VMNetworkAdapter -VMName "app-01" |
    Select-Object VMName, IovWeight, VirtualFunction, SwitchName

# Внутри VM: проверяем, что VF-драйвер загружен
# (выполнять внутри VM)
Get-NetAdapter | Where-Object { $_.InterfaceDescription -match "Virtual Function" }
# Name        InterfaceDescription
# ----        --------------------
# Ethernet 2  Mellanox ConnectX-5 Virtual Function
```

!!! warning "SR-IOV Failover: Что происходит при Live Migration"
    При Live Migration VM переносится на другой хост. VF **не мигрирует** —
    это hardware ресурс. Windows автоматически переключает VM на synthetic
    (software) datapath через vSwitch. Это вызывает:

    1. **Кратковременный разрыв** (~1-3 секунды) — TCP переживёт (retransmit)
    2. **Деградацию производительности** до переназначения VF на новом хосте
    3. **Возможное изменение MAC** — если NIC на новом хосте другого вендора

    Проверить текущий datapath:
    ```powershell
    # Внутри VM:
    Get-NetAdapter | Select-Object Name, InterfaceDescription, Status
    # Если "Virtual Function" в описании — SR-IOV active
    # Если "Microsoft Hyper-V Network Adapter" — synthetic (fallback)
    ```

---

## Часть 9.7: Windows Filtering Platform (WFP) — Файрвол изнутри

### Архитектура WFP

WFP — это не просто Windows Firewall. Это **фреймворк**, в который встраиваются все:
- Windows Defender Firewall
- IPsec
- Антивирусы (CrowdStrike Falcon, Carbon Black)
- VPN-клиенты (GlobalProtect, Cisco AnyConnect)
- Network monitoring (Wireshark, NetMon)

```
                    WFP Architecture
┌─────────────────────────────────────────────┐
│               User Mode                      │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  │
│  │ Firewall │  │   VPN    │  │   EDR    │  │
│  │  Service │  │ Client   │  │  Agent   │  │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘  │
│       │              │              │        │
│  ┌────┴──────────────┴──────────────┴────┐  │
│  │     Base Filtering Engine (BFE)       │  │
│  │     (svchost.exe → bfe.dll)           │  │
│  └─────────────────┬────────────────────┘  │
├────────────────────┼────────────────────────┤
│               Kernel Mode                    │
│  ┌─────────────────┴────────────────────┐  │
│  │    WFP Callout Drivers               │  │
│  │    (fwpkclnt.sys, AV filter, etc.)   │  │
│  │                                       │  │
│  │    Layers (точки перехвата):          │  │
│  │    ├─ INBOUND_TRANSPORT (v4/v6)      │  │
│  │    ├─ OUTBOUND_TRANSPORT             │  │
│  │    ├─ INBOUND_NETWORK                │  │
│  │    ├─ OUTBOUND_NETWORK               │  │
│  │    ├─ FORWARD                        │  │
│  │    ├─ ALE_AUTH_CONNECT               │  │
│  │    ├─ ALE_AUTH_RECV_ACCEPT           │  │
│  │    └─ STREAM (L7 inspection!)        │  │
│  └──────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
```

### Диагностика WFP: Кто блокирует?

Самая частая проблема: «порт открыт, приложение слушает, но клиент не может подключиться». Стандартный ответ — «проверьте файрвол». Но **какой** файрвол? WFP может иметь десятки callout drivers.

```powershell
# === Шаг 1: Показать ВСЕ WFP filters ===
# Это золотой инструмент. Показывает ВСЁ, что WFP проверяет.
netsh wfp show filters

# Выход: XML файл с КАЖДЫМ правилом. Может быть 5000+ строк.
# Ищем блокирующие правила:
Select-String -Path .\filters.xml -Pattern "action.*BLOCK" -Context 5,0

# === Шаг 2: WFP state dump ===
netsh wfp show state

# === Шаг 3: Аудит WFP в реальном времени ===
# Включаем WFP audit log
auditpol /set /subcategory:"Filtering Platform Packet Drop" /success:enable /failure:enable
auditpol /set /subcategory:"Filtering Platform Connection" /success:enable /failure:enable

# Смотрим в Security Event Log:
Get-WinEvent -LogName Security -FilterXPath "*[System[EventID=5157]]" -MaxEvents 10 |
    Format-List TimeCreated, Message
# Event 5157 = WFP blocked a connection
# Event 5156 = WFP allowed a connection
```

!!! tip "Найти виновника: Filter ID → Driver"
    Когда WFP аудит показывает Block с Filter ID, найти, кто создал фильтр:
    ```powershell
    # Дамп всех фильтров в файл
    netsh wfp show filters file=wfp_filters.xml

    # Ищем Filter ID из Event Log (например, 12345)
    Select-String -Path wfp_filters.xml -Pattern "filterId.*12345" -Context 0,20
    # providerKey покажет GUID вендора
    # Погуглите GUID → найдёте: CrowdStrike / Palo Alto GP / etc.
    ```

### pktmon: Встроенный packet sniffer (Windows Server 2019+)

`pktmon` — встроенный аналог `tcpdump`, работающий на **всех уровнях** стека. Показывает, где именно пакет дропается.

```powershell
# Показать все компоненты NDIS-стека
pktmon list
# ID   Name                          Type
# --   ----                          ----
# 1    Ethernet0                     Network adapter
# 2    ms_wfplwf_lower               NDIS LWF
# 3    ms_ndiswan                    NDIS LWF
# 4    vmswitch                      NDIS LWF
# ...

# Мониторим ДРОПЫ по всему стеку
pktmon start --capture --pkt-size 0 --comp all --type drop

# Генерируем трафик... ждём...

pktmon stop

# Анализируем: ГДЕ дропнулся пакет
pktmon format PktMon.etl -o drops.txt
# Каждый дроп покажет: компонент, причину, timestamp

# Конвертируем в pcapng для Wireshark
pktmon pcapng PktMon.etl -o capture.pcapng
```

!!! danger "pktmon vs Wireshark: Критическая разница"
    Wireshark (через npcap) перехватывает пакеты **после** NDIS miniport —
    он видит только то, что прошло драйвер. Если пакет дропается в
    hardware (RSS mismatch, VMQ error) или в filter driver ДО точки перехвата
    Wireshark — вы его **не увидите**.

    `pktmon` работает на **каждом уровне** NDIS стека. Он покажет:
    - Пакет пришёл в miniport → ✓
    - Передан в WFP filter → ✓
    - **Дропнут WFP** → ✗ (с причиной!)

    Для серьёзной диагностики — всегда `pktmon`, не Wireshark.

---

## Часть 9.8: tcpip.sys — TCP/IP стек Windows изнутри

### Congestion Control в Windows

Windows поддерживает несколько алгоритмов:

```powershell
# Текущий алгоритм
Get-NetTCPSetting | Select-Object SettingName, CongestionProvider
# SettingName  CongestionProvider
# Internet     CUBIC          ← для интернет-трафика
# Datacenter   CUBIC          ← для дата-центра
# Compat       NewReno        ← совместимость
# Custom       DCTCP          ← если настроено

# Переключить на CUBIC (по умолчанию с Windows Server 2019)
Set-NetTCPSetting -SettingName "Internet" -CongestionProvider CUBIC

# DCTCP для дата-центров (требует ECN на всём пути)
Set-NetTCPSetting -SettingName "Datacenter" -CongestionProvider DCTCP

# Включить ECN (необходимо для DCTCP)
Set-NetTCPSetting -SettingName "Datacenter" -EcnCapability Enabled
```

!!! warning "Windows НЕ поддерживает BBR"
    В отличие от Linux, Windows не имеет BBR. Для Windows-серверов в
    интернете это означает ~10-15% меньшую пропускную способность на
    каналах с потерями по сравнению с Linux + BBR.
    Альтернатива: LEDBAT (Low Extra Delay Background Transport) для
    фоновых задач (WSUS, SCCM).
    ```powershell
    Set-NetTCPSetting -SettingName "Internet" -CongestionProvider LEDBAT
    ```

### TCP-параметры через PowerShell

```powershell
# Все TCP settings
Get-NetTCPSetting | Format-List *

# Ключевые параметры:
# AutoTuningLevelLocal : Normal (автоматическая настройка window size)
# ScalingHeuristics    : Disabled (не уменьшать window после проблем)
# InitialCongestionWindow : 10 (начальное окно в MSS — RFC 6928)
# InitialRto           : 1000ms (начальный RTO)
# MinRto               : 300ms  (минимальный RTO)
# MaxSynRetransmissions : 2 (SYN retry)
# DelayedAckTimeout    : 40ms

# Увеличить Initial Congestion Window (для быстрого старта)
Set-NetTCPSetting -SettingName "Internet" -InitialCongestionWindow 20

# Отключить Nagle (для low-latency)
# На уровне стека нельзя, только в приложении:
# socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);

# Autotuning — автоматическая настройка receive window
# Normal = масштабируется до 16MB
# Disabled = фиксированный 64KB (НИКОГДА не делайте на серверах!)
Set-NetTCPSetting -SettingName "Internet" -AutoTuningLevelLocal Normal
```

### AFD.sys — Ancillary Function Driver

AFD.sys — мост между Winsock (user mode) и tcpip.sys (kernel). Управляет:
- Socket buffers (send/receive)
- Completion ports (IOCP)
- Poll/select operations
- Buffer management

```powershell
# Посмотреть AFD-параметры
Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\AFD\Parameters"
# DefaultReceiveWindow : 65536 (по умолчанию — маловато для 10GbE)
# DefaultSendWindow    : 65536

# Увеличить буферы AFD (для высоконагруженных .NET-сервисов)
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\AFD\Parameters" `
    -Name "DefaultReceiveWindow" -Value 1048576 -Type DWord
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\AFD\Parameters" `
    -Name "DefaultSendWindow" -Value 1048576 -Type DWord
# Требует перезагрузки!
```

!!! danger "AFD connection limit: Скрытый лимит"
    AFD имеет лимит на количество одновременных non-paged pool allocations.
    При 10K+ одновременных TCP-соединений AFD может исчерпать non-paged pool.

    Диагностика:
    ```powershell
    # Проверить non-paged pool usage
    Get-Counter '\Memory\Pool Nonpaged Bytes' -SampleInterval 1 -MaxSamples 3

    # Кто потребляет pool? (Sysinternals poolmon или:)
    # В WinDbg (на memory dump):
    # !poolused 2
    # Tag   Allocs  Frees   Diff    Used
    # AFD   450321  449800  521     5242880
    #                       ^^^ 521 outstanding AFD allocations
    ```

---

## Часть 9.9: Хирургическая диагностика — ETW, xperf, WPA

### ETW: Event Tracing for Windows

ETW — это **the** инструмент диагностики в Windows. Не Event Log (это журнал для админов), а **kernel-level трассировка** с наносекундной точностью.

```powershell
# === Сетевая трассировка с ETW ===

# Полная сетевая трассировка (все провайдеры)
netsh trace start capture=yes `
    scenario=NetConnection `
    tracefile=C:\traces\net_diag.etl `
    maxSize=1024 `
    fileMode=circular `
    overwrite=yes

# Подождать, пока проблема воспроизведётся...

netsh trace stop
# Анализ в Windows Performance Analyzer (WPA) или Network Monitor

# === Целевая трассировка: только TCP retransmits ===
netsh trace start `
    provider=Microsoft-Windows-TCPIP level=5 keywords=0xFFFFFFFF `
    tracefile=C:\traces\tcp_retrans.etl `
    maxSize=256

# === Трассировка NDIS (пакеты + drops) ===
netsh trace start `
    provider=Microsoft-Windows-NDIS-PacketCapture level=5 `
    tracefile=C:\traces\ndis_drops.etl `
    maxSize=512
```

### Анализ DPC/ISR — Когда "System" ест CPU

Частая жалоба: «Процесс System потребляет 30% CPU». System — это ядро. Внутри него — DPC (Deferred Procedure Calls) и ISR (Interrupt Service Routines) от сетевых драйверов.

```powershell
# === Шаг 1: Кто генерирует DPC? ===
# xperf (из Windows Performance Toolkit)
xperf -on PROC_THREAD+LOADER+INTERRUPT+DPC+PROFILE -stackwalk DPC+ISR

# Воспроизводим проблему (~30 секунд)

xperf -d dpc_analysis.etl

# Открываем в WPA (Windows Performance Analyzer):
# Graph: DPC/ISR → Summary Table → Stack → Module
# Ищем: какой .sys файл генерирует больше всего DPC

# Типичные виновники:
# ndis.sys / e1d65x64.sys → RSS misconfiguration
# tcpip.sys → TCP retransmissions flood
# storport.sys → Disk I/O (не сеть)
# dxgkrnl.sys → GPU (не сеть)
```

```powershell
# === Шаг 2: Быстрая проверка без xperf ===
# Sysinternals Process Explorer → System process → Threads tab
# Sort by CPU → Покажет, какой драйвер (.sys) потребляет CPU

# Или через PowerShell + Performance Counters
Get-Counter '\Processor Information(*)\% DPC Time' -SampleInterval 1 -MaxSamples 10 |
    ForEach-Object {
        $_.CounterSamples | Where-Object { $_.CookedValue -gt 10 } |
            Select-Object InstanceName,
                @{N="DPC%";E={[math]::Round($_.CookedValue,2)}}
    }
```

!!! tip "DPC Watchdog: Когда DPC длится слишком долго"
    Windows имеет DPC Watchdog — если один DPC выполняется дольше 100ms,
    система генерирует bugcheck (BSOD) **DPC_WATCHDOG_VIOLATION** (0x133).

    Частая причина: NIC miniport driver обрабатывает слишком много пакетов
    в одном DPC (RSS сломан → все пакеты на одном ядре).

    Диагностика (из crash dump):
    ```
    # WinDbg
    !analyze -v
    # Покажет: какой DPC routine, в каком драйвере

    # Stack trace:
    # nt!KiRetireDpcList
    # ndis!ndisMDpcX
    # e1d65x64!RxDpc        ← miniport driver Intel NIC
    ```

### Procdump + WinDbg: Анализ дампов

```powershell
# === Memory leak в сетевом стеке ===

# Снимаем дамп при высоком потреблении памяти
# Procdump (Sysinternals) — по порогу committed memory
procdump -ma -m 2048 System  # Дамп System при > 2GB committed

# Или полный kernel dump
# В WinDbg:
# !poolused 2       → какие тэги pool потребляют память
# !poolfind AfdC    → найти все AFD connection objects
# !ndiskd.miniport  → информация о NDIS miniport
# !ndiskd.nbl       → все NET_BUFFER_LIST в системе
# !ndiskd.filters   → NDIS filter chain
```

---

## Часть 9.10: Hardcore Lab — Chaos Engineering на Windows

### Сценарий: Диагностика «медленной сети» на Hyper-V хосте

**Симптомы:** VM app-01 (Windows Server, .NET API) показывает p99 latency 500ms+ при обращении к SQL Server в другой VM. Throughput упал с 8 Gbit/s до 2 Gbit/s. PerfMon «всё нормально».

### Шаг 1: Воспроизводим проблему

```powershell
# На Hyper-V хосте: Ломаем RSS

# Отключаем RSS на NIC (имитируем misconfiguration после обновления драйвера)
Set-NetAdapterRss -Name "Ethernet0" -Enabled $false

# Отключаем VMQ
Set-NetAdapterVmq -Name "Ethernet0" -Enabled $false

# Добавляем WFP filter, который тормозит (имитация тяжёлого антивируса)
# Создаём правило с аудитом каждого пакета
New-NetFirewallRule -DisplayName "CHAOS_AUDIT" `
    -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort 1433 `
    -Profile Any `
    -Enabled True
# + включаем WFP аудит
auditpol /set /subcategory:"Filtering Platform Connection" /success:enable
```

### Шаг 2: Наблюдаем деградацию

```powershell
# В VM app-01: измеряем baseline
iperf3 -c 172.16.2.20 -t 30 -P 4
# Ожидаем: ~2 Gbit/s (вместо 8 Gbit/s)

# Проверяем DPC distribution
Get-Counter '\Processor(*)\% DPC Time' -SampleInterval 1 -MaxSamples 5 |
    ForEach-Object {
        $_.CounterSamples | Sort-Object CookedValue -Descending |
            Select-Object -First 4 InstanceName,
                @{N="DPC%";E={[math]::Round($_.CookedValue,1)}}
    }
# РЕЗУЛЬТАТ: CPU 0 → 45% DPC, остальные → 0%
# ДИАГНОЗ: все сетевые DPC на одном ядре → RSS отключён
```

### Шаг 3: Находим root cause

```powershell
# Проверяем RSS
Get-NetAdapterRss -Name "Ethernet0"
# Enabled: False ← ROOT CAUSE #1

# Проверяем VMQ
Get-NetAdapterVmq -Name "Ethernet0"
# Enabled: False ← ROOT CAUSE #2

# Проверяем WFP overhead
Get-Counter '\WFP*\*' -SampleInterval 1 -MaxSamples 3
# Если WFP Classify Operations/sec > 100K — WFP добавляет overhead

# pktmon: смотрим latency по компонентам
pktmon start --capture --comp all --type all
Start-Sleep -Seconds 10
pktmon stop
pktmon format PktMon.etl -o stack_latency.txt
# Анализируем: на каком компоненте самая большая задержка?
```

### Шаг 4: Фиксим

```powershell
# Включаем RSS с правильной NUMA-привязкой
Enable-NetAdapterRss -Name "Ethernet0"
Set-NetAdapterRss -Name "Ethernet0" `
    -NumberOfReceiveQueues 8 `
    -Profile "NUMAStatic" `
    -BaseProcessorNumber 0 `
    -MaxProcessorNumber 7

# Включаем VMQ
Enable-NetAdapterVmq -Name "Ethernet0"

# Убираем chaos WFP rule
Remove-NetFirewallRule -DisplayName "CHAOS_AUDIT"
auditpol /set /subcategory:"Filtering Platform Connection" /success:disable

# Проверяем
iperf3 -c 172.16.2.20 -t 30 -P 4
# Ожидаем: ~8-9 Gbit/s (restored)
```

---

## Часть 9.11: Чеклист для Production Windows Server

### Сетевой стек — обязательные проверки

```powershell
# === Создаём функцию-аудитор ===
function Invoke-NetworkStackAudit {
    param([string]$AdapterName = (Get-NetAdapter | Where-Object Status -eq "Up" | Select-Object -First 1 -ExpandProperty Name))

    Write-Host "=== Network Stack Audit: $AdapterName ===" -ForegroundColor Cyan

    # 1. RSS
    $rss = Get-NetAdapterRss -Name $AdapterName
    if (-not $rss.Enabled) {
        Write-Host "[FAIL] RSS DISABLED — all packets on single CPU" -ForegroundColor Red
    } else {
        Write-Host "[OK] RSS Enabled, Queues: $($rss.NumberOfReceiveQueues), Profile: $($rss.Profile)" -ForegroundColor Green
    }

    # 2. NUMA alignment
    $numaNode = (Get-NetAdapterHardwareInfo -Name $AdapterName).NumaNode
    Write-Host "[INFO] NIC NUMA Node: $numaNode" -ForegroundColor Yellow
    if ($rss.BaseProcessorGroup -ne 0 -and $numaNode -eq 0) {
        Write-Host "[WARN] RSS processors may be on different NUMA node than NIC" -ForegroundColor Yellow
    }

    # 3. RSC
    $rsc = Get-NetAdapterRsc -Name $AdapterName
    Write-Host "[INFO] RSC IPv4: $($rsc.IPv4Enabled), IPv6: $($rsc.IPv6Enabled)" -ForegroundColor Cyan

    # 4. VMQ (если Hyper-V)
    try {
        $vmq = Get-NetAdapterVmq -Name $AdapterName -ErrorAction Stop
        if (-not $vmq.Enabled) {
            Write-Host "[WARN] VMQ DISABLED on Hyper-V host" -ForegroundColor Yellow
        } else {
            Write-Host "[OK] VMQ Enabled, Queues: $($vmq.NumberOfReceiveQueues)" -ForegroundColor Green
        }
    } catch {
        Write-Host "[INFO] VMQ not applicable (not Hyper-V)" -ForegroundColor Gray
    }

    # 5. SR-IOV
    try {
        $sriov = Get-NetAdapterSriov -Name $AdapterName -ErrorAction Stop
        if ($sriov.Enabled) {
            Write-Host "[OK] SR-IOV Enabled, VFs: $($sriov.NumVFs)" -ForegroundColor Green
        } else {
            Write-Host "[INFO] SR-IOV available but disabled" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "[INFO] SR-IOV not supported by this NIC" -ForegroundColor Gray
    }

    # 6. Offload settings
    $offload = Get-NetAdapterChecksumOffload -Name $AdapterName
    Write-Host "[INFO] Checksum Offload - Rx: $($offload.RxIPv4Checksum), Tx: $($offload.TxTCPv4Checksum)" -ForegroundColor Cyan

    # 7. TCP settings
    $tcp = Get-NetTCPSetting -SettingName "Internet"
    Write-Host "[INFO] Congestion: $($tcp.CongestionProvider), AutoTuning: $($tcp.AutoTuningLevelLocal)" -ForegroundColor Cyan
    if ($tcp.AutoTuningLevelLocal -eq "Disabled") {
        Write-Host "[FAIL] TCP AutoTuning DISABLED — max window 64KB!" -ForegroundColor Red
    }

    # 8. NDIS filter drivers count
    $filters = Get-NetAdapterBinding -Name $AdapterName | Where-Object Enabled
    Write-Host "[INFO] Active NDIS bindings: $($filters.Count)" -ForegroundColor Cyan
    if ($filters.Count -gt 10) {
        Write-Host "[WARN] Too many NDIS filters ($($filters.Count)) — potential latency" -ForegroundColor Yellow
    }

    # 9. DPC distribution
    Write-Host "`n--- DPC Distribution (3 sec sample) ---" -ForegroundColor Cyan
    $dpc = Get-Counter '\Processor(*)\% DPC Time' -SampleInterval 1 -MaxSamples 3
    $lastSample = $dpc[-1].CounterSamples |
        Where-Object { $_.InstanceName -ne "_total" -and $_.CookedValue -gt 1 } |
        Sort-Object CookedValue -Descending
    foreach ($cpu in $lastSample) {
        $bar = "#" * [math]::Min([int]$cpu.CookedValue, 50)
        Write-Host "  CPU $($cpu.InstanceName): $([math]::Round($cpu.CookedValue,1))% $bar"
    }
}

# Запуск:
Invoke-NetworkStackAudit -AdapterName "Ethernet0"
```

---

## Практические задания

### Задание 1: RSS Benchmark

1. Установите iperf3 на два Windows Server (или VM)
2. Измерьте throughput с RSS **отключённым** (`Disable-NetAdapterRss`)
3. Включите RSS с 1, 2, 4, 8 очередями
4. Постройте график: Queues → Throughput
5. Найдите точку diminishing returns (обычно = количество физических ядер на NUMA node)

### Задание 2: Найди filter driver

1. Установите WireGuard VPN на Windows Server
2. Запустите `pktmon list` — найдите новый NDIS filter
3. Измерьте latency (ping) и throughput до и после установки
4. Деактивируйте WireGuard binding через `Disable-NetAdapterBinding`
5. Сравните производительность — какой overhead добавил filter?

### Задание 3: WFP Forensics

1. Создайте 5 Windows Firewall rules (блокирующих и разрешающих)
2. Добавьте правило, блокирующее порт 5432 (PostgreSQL)
3. Из приложения — попытка подключения к БД
4. Используя **только** `netsh wfp show filters` и `auditpol` + Security Event Log — найдите, какое правило блокирует
5. Найдите Filter ID, Provider Key, и покажите полную цепочку вызовов

### Задание 4: DPC Storm

1. На загруженном сервере запустите `xperf -on DPC+ISR+PROFILE -stackwalk DPC`
2. Сгенерируйте 10 Gbit/s нагрузку через iperf3
3. Откройте ETL в WPA
4. Определите: какой драйвер генерирует больше всего DPC?
5. Какова средняя и максимальная длительность DPC? (> 100μs — проблема)

### Задание 5: SR-IOV Live Migration

1. Настройте SR-IOV для VM на Hyper-V Cluster (2 узла)
2. Внутри VM: запустите непрерывный iperf3 + ping
3. Выполните Live Migration
4. Измерьте: сколько пакетов потеряно? Какой максимальный gap?
5. Проверьте: переключилась ли VM обратно на VF после миграции?

---

**Связь с основной книгой:**
- RSS в Windows ≈ RSS/RPS в Linux (Модуль 1)
- WFP ≈ iptables/nftables + netfilter (Модуль 5, IP)
- TCP settings ≈ sysctl tuning (Модуль 4, TC/Tuning)
- DPC/ISR ≈ SoftIRQ/NAPI (Модуль 1)
- pktmon ≈ tcpdump + eBPF tracing (Модуль 4)
