# Модуль 9: Windows Client Network Internals — Wi-Fi, DNS Client, VPN и «Нет интернета»

*«Жёлтый треугольник на иконке сети — это не диагноз. Это симптом. А причина может быть в любом из 47 компонентов между вашим браузером и роутером».*

---

## Введение и Суть (The "Why")

На серверах всё детерминировано: Ethernet, статический IP, стабильный канал. На клиентских машинах — хаос:

- **Wi-Fi**: roaming между точками, 802.1X аутентификация, driver bugs, power management отключает адаптер
- **VPN**: Always On VPN ломает split tunneling, DNS leak, MTU black holes
- **DNS Client**: кэш врёт, NRPT (Name Resolution Policy Table) перенаправляет запросы, DoH/DoT конфликтует с корпоративным DNS
- **NCSI**: Windows показывает «Нет интернета», хотя ping 8.8.8.8 работает
- **NLA**: Network Location Awareness решает, что вы в «Public network», и файрвол блокирует всё

Всё это невозможно диагностировать через GUI. Этот модуль — PowerShell + ETW + реестр. Zero GUI.

---

## Часть 9.1: Анатомия сетевого подключения на клиенте

### Стек компонентов (от пользователя до провода)

```
┌─────────────────────────────────────────────────────────┐
│ User Mode                                                │
│                                                          │
│  Browser/App → Winsock (ws2_32.dll)                     │
│       ↓                                                  │
│  DNS Client Service (svchost.exe → dnsrslvr.dll)        │
│       ↓                                                  │
│  Network Location Awareness (NlaSvc)                    │
│       ↓                                                  │
│  NCSI (Network Connectivity Status Indicator)           │
│       ↓                                                  │
│  Network List Manager (netprofm.dll)                    │
│       ↓                                                  │
│  VPN Client (RasMan / IKEv2 / WireGuard / 3rd party)   │
│                                                          │
├──────────────────────────────────────────────────────────┤
│ Kernel Mode                                              │
│                                                          │
│  AFD.sys → tcpip.sys → NDIS.sys                        │
│       ↓                                                  │
│  WFP (Windows Filtering Platform)                       │
│       ↓                                                  │
│  ┌─────────────────┐  ┌──────────────────────┐         │
│  │ Native Wi-Fi    │  │  NDIS Miniport       │         │
│  │ Framework       │  │  (Ethernet driver)   │         │
│  │ (nwifi.sys)     │  │                      │         │
│  │      ↓          │  │                      │         │
│  │ Wi-Fi Miniport  │  │                      │         │
│  │ (mrvlpcie8897)  │  │                      │         │
│  └────────┬────────┘  └──────────┬───────────┘         │
│           ↓                      ↓                      │
│        Wi-Fi NIC             Ethernet NIC               │
└─────────────────────────────────────────────────────────┘
```

**Ключевая разница с сервером:** на клиенте между tcpip.sys и NIC стоит **Native Wi-Fi Framework** (nwifi.sys), который добавляет 802.11 management (association, authentication, roaming). На сервере этого слоя нет.

---

## Часть 9.2: Wi-Fi Stack — 802.11 изнутри

### Архитектура Windows Wi-Fi

```
                    User Mode
┌─────────────────────────────────────────────┐
│  WLAN AutoConfig Service (WlanSvc)          │
│  svchost.exe → wlansvc.dll                 │
│  • Сканирование сетей                       │
│  • Выбор профиля                            │
│  • Управление 802.1X (EAP)                 │
│  • Roaming decisions                        │
└──────────────────┬──────────────────────────┘
                   │ WLAN API (wlanapi.dll)
┌──────────────────┴──────────────────────────┐
│           Kernel Mode                        │
│                                              │
│  nwifi.sys (Native Wi-Fi filter driver)     │
│  • 802.11 MAC state machine                 │
│  • Frame encryption/decryption (WPA3/WPA2)  │
│  • Power management                         │
│  • BSS transition (roaming)                 │
│           ↓                                  │
│  Wi-Fi Miniport Driver (vendor .sys)        │
│  • Hardware control                          │
│  • Firmware interface                        │
│  • Interrupt handling                        │
└──────────────────────────────────────────────┘
```

### Диагностика Wi-Fi через PowerShell

```powershell
# === Базовая информация ===

# Все Wi-Fi адаптеры
Get-NetAdapter -Physical | Where-Object { $_.MediaType -eq "802.11" }

# Текущее подключение
netsh wlan show interfaces
# Выдаст:
# State           : connected
# SSID            : CorpWiFi
# BSSID           : aa:bb:cc:dd:ee:ff  ← MAC точки доступа
# Channel         : 36 (5 GHz)
# Receive rate    : 866.7 Mbps (Wi-Fi 5, 80MHz, 2x2)
# Signal          : 78%
# Radio type      : 802.11ac

# Профили Wi-Fi (сохранённые сети)
netsh wlan show profiles

# Детали профиля (включая пароль!)
netsh wlan show profile name="CorpWiFi" key=clear
# Key Content : P@ssw0rd123  ← пароль в открытом виде
```

!!! danger "Безопасность: Wi-Fi пароли в открытом виде"
    `netsh wlan show profile key=clear` показывает WPA2-PSK пароль
    **любому локальному администратору**. Пароли хранятся в
    `%ProgramData%\Microsoft\Wlansvc\Profiles\` зашифрованные DPAPI,
    но SYSTEM может расшифровать. Это аргумент в пользу 802.1X (EAP-TLS)
    вместо PSK в корпоративной среде.

### Wi-Fi Roaming: Почему VoIP рвётся при переходе между этажами

```powershell
# === Диагностика Roaming ===

# WLAN AutoConfig Event Log — КАЖДОЕ событие Wi-Fi
Get-WinEvent -LogName "Microsoft-Windows-WLAN-AutoConfig/Operational" -MaxEvents 20 |
    Format-Table TimeCreated, Id, Message -Wrap

# Ключевые Event ID:
# 8001 — Successfully connected to wireless network
# 8002 — Failed to connect
# 8003 — Successfully disconnected
# 11001 — Wireless network association started
# 11002 — Wireless network association succeeded
# 11003 — Wireless network association FAILED ← проблема
# 11004 — 802.1X authentication started
# 11005 — 802.1X authentication succeeded
# 11006 — 802.1X authentication FAILED ← проблема
# 11010 — Wireless security started (4-way handshake)
# 11011 — Wireless security succeeded
# 12011 — Roaming started
# 12012 — Roaming completed
# 12013 — Roaming FAILED ← проблема
```

```powershell
# Анализ roaming: сколько длится переключение?
$roamEvents = Get-WinEvent -LogName "Microsoft-Windows-WLAN-AutoConfig/Operational" |
    Where-Object { $_.Id -in @(12011, 12012, 12013) }

for ($i = 0; $i -lt $roamEvents.Count - 1; $i++) {
    $current = $roamEvents[$i]
    $next = $roamEvents[$i + 1]
    if ($current.Id -eq 12011 -and $next.Id -eq 12012) {
        $duration = ($next.TimeCreated - $current.TimeCreated).TotalMilliseconds
        Write-Host "Roam: $($current.TimeCreated) → $([math]::Round($duration))ms"
        # Норма: < 50ms (802.11r FT)
        # Проблема: > 500ms (full re-auth)
        # Катастрофа: > 2000ms (VoIP рвётся)
    }
}
```

!!! warning "802.11r (Fast BSS Transition) — почему не работает"
    802.11r позволяет роуминг за < 50ms через pre-authentication.
    Но в Windows это работает ТОЛЬКО если:
    1. Все AP поддерживают 802.11r (FT)
    2. Wi-Fi драйвер поддерживает FT
    3. WPA3-Enterprise или WPA2-Enterprise с PMK caching

    Проверить поддержку:
    ```powershell
    netsh wlan show drivers
    # Поддерживаемые стандарты радио: 802.11a 802.11b 802.11g 802.11n 802.11ac
    # 802.11r                        : Не поддерживается ← ПРОБЛЕМА
    # Типы инфраструктуры            : BSS
    ```

### Power Management — Wi-Fi засыпает

Самая частая причина «рандомных отвалов Wi-Fi» на ноутбуках:

```powershell
# Проверить, разрешено ли Windows отключать Wi-Fi для экономии
Get-NetAdapterPowerManagement -Name "Wi-Fi" |
    Select-Object AllowComputerToTurnOffDevice,
                  DeviceSleepOnDisconnect,
                  WakeOnMagicPacket,
                  WakeOnPattern

# ЗАПРЕТИТЬ отключение (для корпоративных ноутбуков — обязательно)
Set-NetAdapterPowerManagement -Name "Wi-Fi" `
    -AllowComputerToTurnOffDevice Disabled `
    -DeviceSleepOnDisconnect Disabled

# Через реестр (для GPO / Intune):
$path = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}"
Get-ChildItem $path | ForEach-Object {
    $driverDesc = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DriverDesc
    if ($driverDesc -match "Wi-Fi|Wireless|WLAN") {
        # PnPCapabilities = 24 (0x18) = отключить и idle, и suspend power management
        Set-ItemProperty $_.PSPath -Name "PnPCapabilities" -Value 24 -Type DWord
        Write-Host "Disabled power management for: $driverDesc"
    }
}
# Требует перезагрузки
```

!!! tip "GPO для массового развёртывания"
    Для массовой настройки Wi-Fi power management через Intune/GPO:
    ```powershell
    # PowerShell script для Intune Remediation
    # Detection script:
    $adapter = Get-NetAdapter -Physical | Where-Object MediaType -eq "802.11"
    $pm = Get-NetAdapterPowerManagement -Name $adapter.Name
    if ($pm.AllowComputerToTurnOffDevice -ne "Disabled") { exit 1 } else { exit 0 }

    # Remediation script:
    $adapter = Get-NetAdapter -Physical | Where-Object MediaType -eq "802.11"
    Set-NetAdapterPowerManagement -Name $adapter.Name -AllowComputerToTurnOffDevice Disabled
    Restart-NetAdapter -Name $adapter.Name
    ```

### Modern Standby (S0ix) — Новая реальность Power Management

Всё вышесказанное про `AllowComputerToTurnOffDevice` работало в эпоху классического S3 Sleep. Но с ~2020 года **все** современные ноутбуки (Intel 10th gen+, ARM) используют **Modern Standby (S0 Low Power Idle)** — и правила игры полностью изменились.

!!! warning "Modern Standby (S0) убивает старый Power Management"
    В Modern Standby классический спящий режим (S3) мёртв. Вместо него работает
    **S0 Low Power Idle** — компьютер «спит», но ядро ОС продолжает работать.
    В этом режиме галочка «Разрешить отключение этого устройства для экономии
    энергии» **игнорируется**.

    ОС сама решает, отключить ли Wi-Fi во сне через механизм
    «Network Disconnected Standby». Результат: Always On VPN рвётся
    при закрытии крышки, Wi-Fi мучительно переподключается при открытии.

```powershell
# === Проверить: какой режим сна поддерживается? ===
powercfg /a
# Ищем в выводе:
# "Standby (S0 Low Power Idle) Network Connected"   ← Modern Standby, сеть жива
# "Standby (S0 Low Power Idle) Network Disconnected" ← Modern Standby, сеть рубится
# "Standby (S3)"                                     ← Классический S3

# Если видите "Network Disconnected" — Wi-Fi выключается при засыпании!

# === Заставить Windows держать сеть во сне ===
# GUID F15576E8-98B7-4186-B944-EAFA664402D9 = "Network Connectivity in Standby"
# Значение 1 = Enabled (держать сеть), 0 = Disabled (рубить)

# На батарее (DC):
powercfg /setdcvalueindex scheme_current sub_none F15576E8-98B7-4186-B944-EAFA664402D9 1

# От сети (AC):
powercfg /setacvalueindex scheme_current sub_none F15576E8-98B7-4186-B944-EAFA664402D9 1

# Применить
powercfg /setactive scheme_current

# Проверить текущее значение
powercfg /query scheme_current sub_none F15576E8-98B7-4186-B944-EAFA664402D9
# Current AC Power Setting Index: 0x00000001 ← 1 = сеть жива

# === Для массового развёртывания через Intune / GPO ===
# Реестр:
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\F15576E8-98B7-4186-B944-EAFA664402D9" `
    -Name "Attributes" -Value 2 -Type DWord
# Attributes=2 делает эту настройку видимой в powercfg GUI

# Или через PowerShell DSC / Intune Remediation:
# Detection: powercfg /query проверяет значение
# Remediation: powercfg /setdcvalueindex + /setacvalueindex
```

!!! danger "Modern Standby + Always On VPN = рвущийся туннель"
    Хронология при закрытии крышки на Modern Standby (без фикса):

    1. Крышка закрыта → S0 Low Power Idle
    2. Windows через ~30 сек решает отключить Wi-Fi (экономия батареи)
    3. IKEv2 tunnel рвётся (Dead Peer Detection timeout)
    4. Крышка открыта → Wi-Fi переподключается (~3-5 сек)
    5. VPN поднимается (~5-15 сек)
    6. NLA переоценивает сеть → Domain profile (ещё ~5 сек)
    7. **Итого: 15-25 секунд без корпоративной сети**

    С фиксом (Network Connected Standby):
    1. Крышка закрыта → S0 Low Power Idle, Wi-Fi жив
    2. IKEv2 keepalive проходит
    3. Крышка открыта → сеть уже есть
    4. **Итого: 0 секунд downtime**

    Цена: ~5-10% больше расход батареи в спящем режиме.

### ETW-трассировка Wi-Fi (тяжёлая артиллерия)

```powershell
# Полная Wi-Fi трассировка — для сложных проблем
netsh trace start `
    scenario=wlan `
    capture=yes `
    tracefile=C:\traces\wifi_diag.etl `
    maxSize=512

# Воспроизводим проблему (роуминг, отвал, медленность)

netsh trace stop

# Результат: ETL файл + cab с логами
# Открываем в WPA (Windows Performance Analyzer)
# или текстовый отчёт:
netsh trace convert input=C:\traces\wifi_diag.etl output=C:\traces\wifi_diag.txt

# Внутри: КАЖДЫЙ 802.11 frame, каждое решение WLAN AutoConfig,
# каждый EAP exchange, каждая смена канала
```

---

## Часть 9.3: DNS Client — Самый непонятый компонент Windows

### Как Windows резолвит DNS (реальный путь)

Это **не** просто «спросить DNS-сервер». Реальный путь:

```
Приложение вызывает getaddrinfo("example.com")
    ↓
1. Winsock (ws2_32.dll) → DNS Client API
    ↓
2. DNS Client Cache (in-memory)
   ├── Найдено? → Вернуть из кэша
   └── Не найдено? ↓
    ↓
3. HOSTS file (C:\Windows\System32\drivers\etc\hosts)
   ├── Найдено? → Вернуть
   └── Не найдено? ↓
    ↓
4. NRPT (Name Resolution Policy Table)
   ├── Совпадение? → Отправить на NRPT-specified DNS
   └── Нет? ↓
    ↓
5. DNS-серверы интерфейса (в порядке приоритета)
   ├── Есть VPN? → VPN DNS может перехватить ВСЁ
   ├── Wi-Fi + Ethernet? → Порядок определяется Interface Metric
   └── Split DNS? → Разные суффиксы → разные серверы
    ↓
6. DNS Query → DNS Server
   ├── UDP :53 (стандарт)
   ├── DoH (DNS over HTTPS) → Windows 11+
   └── Fallback: TCP :53 (если ответ > 512 байт)
    ↓
7. Ответ кешируется → TTL → возвращается приложению
```

### DNS Client Cache: Инструмент и ловушка

```powershell
# === DNS Cache ===

# Показать весь кэш
Get-DnsClientCache | Format-Table Entry, Type, TimeToLive, Data -AutoSize

# Размер кэша (сколько записей)
(Get-DnsClientCache).Count

# Очистить кэш (классика, но знаете ли вы, ПОЧЕМУ это помогает?)
Clear-DnsClientCache

# ПОЧЕМУ помогает: DNS Client кеширует и ПОЛОЖИТЕЛЬНЫЕ, и ОТРИЦАТЕЛЬНЫЕ ответы.
# Если DNS-сервер временно не отвечал, Windows кеширует NXDOMAIN с TTL
# и следующие 5-15 минут ВСЕ запросы к этому домену → failure из кэша.

# Проверить TTL отрицательного кэширования:
Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters" |
    Select-Object MaxNegativeCacheTtl, MaxCacheTtl, NegativeCacheTime
# MaxNegativeCacheTtl: 900 (секунд = 15 минут!)
# Уменьшить для динамичных сред:
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters" `
    -Name "MaxNegativeCacheTtl" -Value 30 -Type DWord
# Теперь негативный кэш живёт 30 секунд вместо 15 минут
```

### NRPT: Name Resolution Policy Table (тихий перехватчик)

NRPT — механизм, через который VPN-клиенты, DirectAccess и Intune **перенаправляют** DNS-запросы. Это самая частая причина «DNS работает странно после установки VPN».

```powershell
# === Показать NRPT правила ===

# Через реестр (единственный надёжный способ)
Get-ChildItem "HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig" -ErrorAction SilentlyContinue |
    ForEach-Object {
        $props = Get-ItemProperty $_.PSPath
        [PSCustomObject]@{
            Name           = $_.PSChildName
            Namespace      = $props.GenericDNSServers
            DnsServers     = $props.GenericDNSServers
            DirectAccess   = $props.DirectAccessEnabled
            Comment        = $props.Comment
        }
    } | Format-Table -AutoSize

# Через PowerShell (если модуль DnsClient доступен)
Get-DnsClientNrptPolicy | Format-Table Namespace, NameServers, Comment -AutoSize

# ТИПИЧНАЯ ПРОБЛЕМА:
# VPN-клиент добавляет NRPT-правило "." (точка = ВСЕ запросы)
# Результат: ВЕСЬ DNS идёт через VPN-туннель, даже для youtube.com
# Отключили VPN — правило осталось в реестре!

# Найти "catch-all" NRPT правила:
Get-DnsClientNrptPolicy | Where-Object { $_.Namespace -match "^\.$|^$" }
# Если есть — это причина "DNS не работает после VPN"
```

!!! danger "NRPT + Always On VPN = DNS Leak"
    При Always On VPN с split tunneling NRPT должен перенаправлять
    ТОЛЬКО корпоративные домены через VPN DNS. Но неправильная настройка
    NRPT часто приводит к одному из двух:

    1. **DNS Leak**: корпоративные запросы идут через ISP DNS (без NRPT)
    2. **All traffic через VPN DNS**: NRPT с namespace "." перехватывает всё

    Проверка:
    ```powershell
    # Какой DNS-сервер реально обрабатывает запрос?
    Resolve-DnsName -Name "internal.corp.com" -DnsOnly |
        Select-Object Name, Type, IPAddress, NameHost

    # Через какой сервер ушёл запрос?
    Resolve-DnsName -Name "internal.corp.com" -DnsOnly -Debug 2>&1 |
        Select-String "server"
    ```

### DNS over HTTPS (DoH) — Windows 11+

```powershell
# Проверить текущие DoH-настройки
Get-DnsClientDohServerAddress

# Добавить кастомный DoH-сервер
Add-DnsClientDohServerAddress -ServerAddress "1.1.1.1" `
    -DohTemplate "https://cloudflare-dns.com/dns-query" `
    -AllowFallbackToUdp $true `
    -AutoUpgrade $true

# Включить DoH для конкретного интерфейса
$adapter = Get-NetAdapter -Name "Wi-Fi"
Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex `
    -ServerAddresses "1.1.1.1","8.8.8.8"

# Через реестр (для GPO):
# HKLM:\SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters\
# {GUID}\DohInterfaceSettings\Doh\1.1.1.1
# DohFlags = 1 (auto-upgrade UDP→DoH)
```

!!! warning "DoH + корпоративный DNS = конфликт"
    Если пользователь включил DoH (1.1.1.1), а корпоративный DNS (10.0.0.53)
    резолвит internal.corp.com — произойдёт split:
    - internal.corp.com → 10.0.0.53 (через NRPT) → работает
    - Всё остальное → 1.1.1.1 через HTTPS → обходит корпоративный firewall/proxy

    Для контроля DoH в enterprise:
    ```powershell
    # GPO: запретить DoH
    # Computer Configuration → Administrative Templates →
    #   Network → DNS Client → Configure DNS over HTTPS (DoH) → Disabled
    # Или через реестр:
    Set-ItemProperty "HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient" `
        -Name "DoHPolicy" -Value 0 -Type DWord
    # 0 = Disabled, 1 = Allow, 2 = Require
    ```

### SMHNR: Smart Multi-Homed Name Resolution — Главная причина DNS Leaks

Даже с идеально настроенным NRPT, Windows 10/11 имеет ещё один механизм, который **сводит с ума безопасников**: Smart Multi-Homed Name Resolution.

!!! danger "SMHNR: DNS-запросы утекают мимо NRPT"
    **Суть:** Когда у машины несколько сетевых интерфейсов (Wi-Fi + Ethernet + VPN),
    Windows по умолчанию отправляет DNS-запросы на **все** интерфейсы параллельно.
    Если основной DNS не ответил за доли секунды — Windows берёт ответ от того, кто
    ответил первым.

    **Результат:** Внутренний корпоративный запрос `secret-server.corp.local`
    улетает на 8.8.8.8 через Wi-Fi провайдера. ISP видит, какие внутренние домены
    вы резолвите. Это колоссальная дыра в безопасности.

    NRPT **не спасает**, потому что SMHNR работает на уровне ниже — в DNS Client
    Service до применения NRPT policy.

```powershell
# === Проверить: включён ли SMHNR? ===
Get-ItemProperty "HKLM:\Software\Policies\Microsoft\Windows NT\DNSClient" `
    -Name "DisableSmartNameResolution" -ErrorAction SilentlyContinue
# Если свойство отсутствует или = 0 → SMHNR ВКЛЮЧЁН (по умолчанию!)

# === ОТКЛЮЧИТЬ SMHNR (обязательно для корпоративных машин с VPN!) ===

# Через реестр:
New-Item -Path "HKLM:\Software\Policies\Microsoft\Windows NT\DNSClient" -Force | Out-Null
Set-ItemProperty "HKLM:\Software\Policies\Microsoft\Windows NT\DNSClient" `
    -Name "DisableSmartNameResolution" -Value 1 -Type DWord

# Через GPO (рекомендуемый способ для enterprise):
# Computer Configuration → Administrative Templates → Network → DNS Client →
# "Turn off smart multi-homed name resolution" = Enabled

# === Проверить утечку: куда РЕАЛЬНО уходят DNS-запросы ===
# Включаем DNS Client ETW:
$session = New-EtwTraceSession -Name "DNSTrace" -LogFileMode 0x8
Add-EtwTraceProvider -SessionName "DNSTrace" `
    -Guid "{1C95126E-7EEA-49A9-A3FE-A378B03DDB4D}" -Level 5
# Это Microsoft-Windows-DNS-Client provider

# Генерируем DNS-запрос
Resolve-DnsName -Name "secret-server.corp.local"

# Через pktmon — видим, через какие интерфейсы ушли UDP:53 пакеты
pktmon start --capture --type all -p 53
Start-Sleep -Seconds 5
pktmon stop
pktmon format PktMon.etl -o dns_leak.txt
# Если видите UDP:53 на НЕСКОЛЬКИХ интерфейсах — SMHNR утекает
```

!!! tip "Дополнительная защита: DNS-only binding"
    Помимо отключения SMHNR, привяжите DNS-серверы к конкретным интерфейсам:
    ```powershell
    # Убрать DNS с Wi-Fi (оставить только на VPN):
    Set-DnsClientServerAddress -InterfaceAlias "Wi-Fi" -ServerAddresses @()
    # Теперь Wi-Fi не имеет DNS-серверов — запросы пойдут только через VPN
    # Минус: без VPN DNS не работает вообще
    ```

### Interface Metric и DNS Priority

Когда у клиента Wi-Fi + Ethernet + VPN — в каком порядке опрашиваются DNS-серверы?

```powershell
# Метрики интерфейсов (чем меньше — тем приоритетнее)
Get-NetIPInterface |
    Where-Object { $_.AddressFamily -eq "IPv4" -and $_.ConnectionState -eq "Connected" } |
    Sort-Object InterfaceMetric |
    Select-Object InterfaceAlias, InterfaceMetric, Dhcp |
    Format-Table -AutoSize

# Типичный результат:
# InterfaceAlias  InterfaceMetric  Dhcp
# Ethernet        25               Enabled   ← приоритет 1
# Wi-Fi           50               Enabled   ← приоритет 2
# VPN Tunnel      6                Disabled  ← приоритет 0 (VPN перехватывает!)

# VPN-клиенты часто ставят metric=1, перехватывая весь трафик.
# Проверить и поменять:
Set-NetIPInterface -InterfaceAlias "VPN Tunnel" -InterfaceMetric 100

# Привязать DNS к конкретному интерфейсу
Set-DnsClient -InterfaceAlias "Ethernet" `
    -RegisterThisConnectionsAddress $true `
    -UseSuffixWhenRegistering $true `
    -ConnectionSpecificSuffix "corp.local"
```

---

## Часть 9.4: NCSI — Почему Windows говорит «Нет интернета»

### Как NCSI определяет подключение

**NCSI (Network Connectivity Status Indicator)** — компонент, который рисует иконку сети в трее. Он выполняет **активные проверки**:

```
Шаг 1: DNS Lookup
  Запрос: dns.msftncsi.com → ожидает 131.107.255.255

Шаг 2: HTTP Probe
  GET http://www.msftconnecttest.com/connecttest.txt
  Ожидает: "Microsoft Connect Test" (plain text)

Шаг 3: HTTPS Probe (Windows 10+)
  GET https://www.msftconnecttest.com/connecttest.txt
  Проверяет: валидный TLS сертификат

Все три успешно → ✓ Интернет есть (белая иконка)
DNS работает, HTTP нет → ⚠ "No Internet Access" (жёлтый треугольник)
Ничего не работает → ✗ "No Network" (красный крест)
```

### Почему NCSI врёт

```powershell
# === Сценарий: "Нет интернета", но ping работает ===

# 1. Captive Portal (гостевой Wi-Fi)
# HTTP probe перехватывается captive portal → NCSI видит не тот ответ
# Решение: подключиться к captive portal

# 2. Корпоративный Proxy/Firewall
# Firewall блокирует HTTP к msftconnecttest.com
# DNS работает, HTTP нет → жёлтый треугольник
# Решение: разрешить в proxy/firewall:
#   - dns.msftncsi.com (DNS)
#   - www.msftconnecttest.com (HTTP/HTTPS)
#   - 131.107.255.255 (IP)

# 3. DNS возвращает неправильный IP
# Некоторые DNS-фильтры (Pi-hole, корпоративный DNS) резолвят
# dns.msftncsi.com в свой IP → NCSI думает, что DNS сломан
Resolve-DnsName -Name "dns.msftncsi.com"
# Должно быть: 131.107.255.255
# Если другое — корпоративный DNS перехватывает

# 4. HTTPS probe fails (сертификат)
# Если proxy делает SSL inspection — сертификат msftconnecttest.com
# будет от proxy CA, NCSI может не доверять → "No Internet"
```

### NCSI и IPv6: Параллельная проверка, о которой забывают

NCSI проверяет не только IPv4. Параллельно идут **IPv6-пробы**:

```
IPv4 Probe:                              IPv6 Probe (параллельно):
  DNS: dns.msftncsi.com                    DNS: dns.msftncsi.com (AAAA)
  Ожидает: 131.107.255.255                 Ожидает: fd3e:4f5a:5b81::1
  HTTP: www.msftconnecttest.com            HTTP: ipv6.msftconnecttest.com
```

!!! danger "IPv6 NCSI ломает метрики и DirectAccess"
    Если ваша сеть фильтрует AAAA-запросы или блокирует IPv6-трафик к
    Microsoft, NCSI отметит IPv6 как "No Internet", даже если IPv4 работает.

    Последствия:
    - **DirectAccess / Always On VPN (IPv6):** NCSI видит "No IPv6 Internet" →
      клиент считает, что корпоративный IPv6-туннель мёртв → переключается
      на fallback → рост latency
    - **Метрики интерфейсов:** Windows может увеличить metric для интерфейса
      с "No Internet" IPv6, сломав маршрутизацию
    - **Teredo/ISATAP:** Transition-технологии зависят от IPv6 NCSI status

    Диагностика:
    ```powershell
    # Проверить IPv6 connectivity status
    Get-NetConnectionProfile |
        Select-Object InterfaceAlias, IPv4Connectivity, IPv6Connectivity
    # Если IPv4Connectivity=Internet, IPv6Connectivity=NoTraffic — проблема

    # Проверить, резолвится ли IPv6 NCSI endpoint
    Resolve-DnsName -Name "dns.msftncsi.com" -Type AAAA
    # Должно быть: fd3e:4f5a:5b81::1

    # Если ваша сеть не поддерживает IPv6 — отключите IPv6 NCSI:
    # Или отключите IPv6 на интерфейсе, если он не используется:
    Disable-NetAdapterBinding -Name "Wi-Fi" -ComponentID "ms_tcpip6"
    # ВНИМАНИЕ: это отключит IPv6 полностью на Wi-Fi
    ```

    Для корпоративных сетей с IPv6 — убедитесь, что firewall/proxy пропускает:
    - `dns.msftncsi.com` (AAAA записи)
    - `ipv6.msftconnecttest.com` (HTTP/HTTPS по IPv6)
    - `fd3e:4f5a:5b81::1` (прямой IP)

### Настройка NCSI через реестр

```powershell
# === Кастомный NCSI endpoint (для air-gapped сетей) ===

$ncsiPath = "HKLM:\SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters\Internet"

# Показать текущие настройки
Get-ItemProperty $ncsiPath |
    Select-Object ActiveWebProbeHost, ActiveWebProbePath, ActiveWebProbeContent,
                  ActiveDnsProbeHost, ActiveDnsProbeContent, EnableActiveProbing

# Изменить на внутренний сервер (для сетей без интернета):
Set-ItemProperty $ncsiPath -Name "ActiveWebProbeHost" -Value "ncsi.corp.local"
Set-ItemProperty $ncsiPath -Name "ActiveWebProbePath" -Value "connecttest.txt"
Set-ItemProperty $ncsiPath -Name "ActiveWebProbeContent" -Value "Microsoft Connect Test"
Set-ItemProperty $ncsiPath -Name "ActiveDnsProbeHost" -Value "dns-ncsi.corp.local"
Set-ItemProperty $ncsiPath -Name "ActiveDnsProbeContent" -Value "10.0.0.1"

# Или полностью отключить NCSI (не рекомендуется — сломает UX):
Set-ItemProperty $ncsiPath -Name "EnableActiveProbing" -Value 0 -Type DWord
```

!!! tip "Air-gapped сеть: как убрать жёлтый треугольник"
    В air-gapped корпоративной сети без доступа в интернет жёлтый треугольник
    сбивает пользователей с толку. Решение:

    1. Поднять на внутреннем сервере endpoint:
       - DNS: `dns-ncsi.corp.local` → `10.0.0.1`
       - HTTP: `http://ncsi.corp.local/connecttest.txt` → "Microsoft Connect Test"
    2. Настроить реестр/GPO на корпоративный NCSI endpoint
    3. Результат: Windows считает, что интернет есть → белая иконка

---

## Часть 9.5: NLA — Network Location Awareness (Domain / Private / Public)

### Почему это критично

NLA определяет **профиль сети**: Domain, Private, или Public. Windows Firewall применяет **разные правила** для каждого профиля. Если NLA ошибочно определит корпоративную сеть как Public — файрвол заблокирует SMB, RDP, WinRM, и всё остальное.

### Как NLA определяет профиль

```
1. Domain Network:
   Компьютер member of AD → Netlogon пытается найти DC →
   → Если DC отвечает → Domain Network
   → Если DC не отвечает (VPN ещё не поднялся) → Public! ← ПРОБЛЕМА

2. Private Network:
   Пользователь вручную выбрал "Private" для этой сети
   Или: GPO назначил сеть как Private

3. Public Network:
   Всё, что не Domain и не Private → Public
   Первое подключение к новому Wi-Fi → Public (по умолчанию)
```

```powershell
# === Текущие сетевые профили ===
Get-NetConnectionProfile |
    Select-Object Name, InterfaceAlias, NetworkCategory, IPv4Connectivity, IPv6Connectivity |
    Format-Table -AutoSize

# NetworkCategory:
# Public  → Firewall максимально строгий (блокирует входящие)
# Private → Firewall мягче (разрешает file sharing)
# DomainAuthenticated → Firewall по корпоративной политике

# === Изменить профиль (если NLA ошиблось) ===
Set-NetConnectionProfile -InterfaceAlias "Ethernet" -NetworkCategory Private

# Для Domain — нельзя поставить вручную. Только если DC доступен.
```

### NLA и VPN: Гонка состояний

```powershell
# ПРОБЛЕМА: Always On VPN → NLA timing

# Хронология при загрузке:
# 1. Windows загружается
# 2. NLA проверяет сеть → DC недоступен (VPN ещё не поднят) → "Public"
# 3. Windows Firewall применяет Public rules → блокирует всё корпоративное
# 4. VPN поднимается (через 5-15 секунд)
# 5. NLA перепроверяет → DC доступен → "Domain"
# 6. Firewall переключает профиль → всё работает

# НО: в окне 5-15 секунд GPO не применяются, скрипты логона не работают,
# mapped drives не подключаются. Пользователи видят ошибки.

# РЕШЕНИЕ: Network Location Awareness service delay
# Заставить NLA ждать VPN перед определением профиля:
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters" `
    -Name "AlwaysExpectDomainController" -Value 1 -Type DWord
# NLA будет ждать DC ответа дольше перед назначением Public

# Или через GPO:
# Computer Configuration → Policies → Administrative Templates →
# Network → Network Connections →
# "Require domain authentication to change from public to domain network"
```

### Диагностика NLA через Event Log

```powershell
# NLA Event Log — показывает каждую смену профиля
Get-WinEvent -LogName "Microsoft-Windows-NetworkProfile/Operational" -MaxEvents 20 |
    Format-Table TimeCreated, Id, Message -Wrap

# Ключевые Event ID:
# 10000 — Network connected (показывает выбранный профиль!)
# 10001 — Network disconnected
# 4004 — DNS registration initiated (NLA triggered)

# Найти моменты, когда NLA менял профиль:
Get-WinEvent -LogName "Microsoft-Windows-NetworkProfile/Operational" |
    Where-Object { $_.Id -eq 10000 } |
    ForEach-Object {
        $xml = [xml]$_.ToXml()
        [PSCustomObject]@{
            Time     = $_.TimeCreated
            Name     = $xml.Event.EventData.Data[0].'#text'
            Category = $xml.Event.EventData.Data[2].'#text'
            # 0 = Public, 1 = Private, 2 = Domain
        }
    } | Format-Table -AutoSize
```

---

## Часть 9.6: VPN Stack — Always On VPN, IKEv2, Split Tunneling

### Архитектура VPN в Windows

```
┌─────────────────────────────────────────────┐
│ User Mode                                    │
│                                              │
│  RasMan (Remote Access Connection Manager)  │
│  svchost.exe → rasmans.dll                  │
│  • Управление VPN-профилями                 │
│  • Triggering (Always On VPN)               │
│  • Credential management                    │
│                                              │
│  RasDial API (rasdial.exe / PowerShell)     │
│                                              │
├──────────────────────────────────────────────┤
│ Kernel Mode                                  │
│                                              │
│  NDIS Virtual Adapter (WAN Miniport)        │
│  • IKEv2 → rasikev.sys                     │
│  • SSTP → sstpsvc.dll (user mode!)         │
│  • L2TP → rasl2tp.sys                      │
│  • PPTP → raspptp.sys (DON'T USE)          │
│                                              │
│  IPsec (ikeext.sys + ipsec.sys)             │
│  ESP encryption/decryption                   │
│                                              │
└──────────────────────────────────────────────┘
```

### Always On VPN: Диагностика

```powershell
# === Статус VPN ===
Get-VpnConnection | Format-List *
# Name              : Corp VPN
# ServerAddress     : vpn.corp.com
# TunnelType        : Ikev2
# SplitTunneling    : True
# ConnectionStatus  : Connected
# Routes            : {10.0.0.0/8, 172.16.0.0/12}

# === VPN Event Log ===
Get-WinEvent -LogName "Microsoft-Windows-RasClient/Operational" -MaxEvents 20 |
    Format-Table TimeCreated, Id, Message -Wrap

# Ключевые Event ID:
# 20222 — VPN connection attempt
# 20223 — VPN connected successfully
# 20224 — VPN connection failed (с кодом ошибки!)
# 20225 — VPN disconnected
# 20226 — VPN reconnecting (Always On triggered)

# === Расшифровка кодов ошибок VPN ===
# 691 — Wrong credentials (RADIUS reject)
# 789 — L2TP certificate validation failed
# 809 — IKEv2 connection failed (firewall blocks UDP 500/4500)
# 812 — RADIUS policy denied connection
# 853 — IKEv2 SA negotiation failed (mismatched crypto)
# 868 — DNS resolution of VPN server failed
```

### Split Tunneling: Что идёт через VPN

```powershell
# Показать маршруты VPN
(Get-VpnConnection -Name "Corp VPN").Routes
# DestinationPrefix  Metric
# 10.0.0.0/8         1
# 172.16.0.0/12      1
# Всё остальное → напрямую (split tunnel)

# Добавить маршрут в split tunnel
Add-VpnConnectionRoute -ConnectionName "Corp VPN" `
    -DestinationPrefix "192.168.100.0/24" -PassThru

# Удалить маршрут
Remove-VpnConnectionRoute -ConnectionName "Corp VPN" `
    -DestinationPrefix "192.168.100.0/24"

# === Force Tunnel vs Split Tunnel ===
# Force Tunnel: ВЕСЬ трафик через VPN (RemoteDefaultGateway = True)
# Split Tunnel: только корпоративный трафик через VPN

Set-VpnConnection -Name "Corp VPN" -SplitTunneling $true
# $true = split tunnel (рекомендуется)
# $false = force tunnel (безопаснее, но медленнее)
```

### MTU Black Hole на VPN

```powershell
# VPN добавляет IPsec/IKEv2 overhead (50-80 байт)
# Effective MTU: 1500 - overhead = 1400-1420
# Если PMTUD не работает (ICMP заблокирован) → пакеты > MTU дропаются

# Проверить текущий MTU VPN-интерфейса
Get-NetIPInterface -InterfaceAlias "Corp VPN" |
    Select-Object InterfaceAlias, NlMtu

# Обычно Windows ставит MTU 1400 автоматически.
# Если нет — задать вручную:
netsh interface ipv4 set subinterface "Corp VPN" mtu=1400 store=persistent

# Диагностика: найти правильный MTU
# Отправляем пакеты с DF-битом, уменьшая размер:
ping -f -l 1400 10.0.0.1    # Если проходит → MTU >= 1400 + 28 (ICMP header)
ping -f -l 1300 10.0.0.1    # Если не проходит при 1400, пробуем 1300
# Последний размер, при котором проходит = MTU - 28
```

---

## Часть 9.7: Сетевые сбросы и восстановление

### «Ядерная кнопка»: Полный сброс сетевого стека

```powershell
# === Уровень 1: Мягкий сброс ===
# Очистить DNS кэш
Clear-DnsClientCache

# Перерегистрировать DNS
Register-DnsClient

# Обновить DHCP lease
Invoke-CimMethod -ClassName Win32_NetworkAdapterConfiguration `
    -Filter "IPEnabled = 'True'" -MethodName "ReleaseDHCPLease"
Start-Sleep -Seconds 2
Invoke-CimMethod -ClassName Win32_NetworkAdapterConfiguration `
    -Filter "IPEnabled = 'True'" -MethodName "RenewDHCPLease"

# === Уровень 2: Средний сброс ===
# Сброс Winsock catalog (повреждённые LSP от VPN/антивируса)
netsh winsock reset

# Сброс TCP/IP стека
netsh int ip reset

# Сброс правил Windows Firewall
netsh advfirewall reset

# === Уровень 3: Жёсткий сброс (требует перезагрузки) ===
# Полный сброс всех сетевых компонентов
# ВНИМАНИЕ: Удаляет все профили Wi-Fi, сбрасывает все настройки,
# переустанавливает все сетевые адаптеры
Get-NetAdapter | ForEach-Object {
    Disable-NetAdapter -Name $_.Name -Confirm:$false
    Enable-NetAdapter -Name $_.Name -Confirm:$false
}

# Удалить и переустановить виртуальные адаптеры
Get-PnpDevice -Class Net -Status OK |
    Where-Object { $_.FriendlyName -match "WAN Miniport|Virtual" } |
    ForEach-Object {
        Disable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false
        Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false
    }
```

!!! danger "netsh winsock reset — Скрытый побочный эффект"
    `netsh winsock reset` пересоздаёт Winsock Catalog — реестр LSP
    (Layered Service Providers). Некоторые приложения регистрируют
    свои LSP (антивирусы, VPN, перехватчики трафика). После winsock reset
    эти LSP **удаляются**, и приложения могут сломаться.

    Проверить LSP перед сбросом:
    ```powershell
    netsh winsock show catalog
    # Если видите записи от вендоров (не Microsoft) — будьте осторожны
    ```

---

## Часть 9.8: ETW-диагностика для клиентских проблем

### Сценарий: «Интернет тормозит»

```powershell
# === Комплексная трассировка ===

# 1. Сетевая трассировка (все компоненты)
netsh trace start `
    scenario=InternetClient `
    capture=yes `
    tracefile=C:\traces\slow_internet.etl `
    maxSize=512 `
    persistent=yes  # Переживёт перезагрузку

# 2. Параллельно: Performance counters
$counters = @(
    '\Network Interface(*)\Bytes Total/sec',
    '\Network Interface(*)\Packets Received Errors',
    '\Network Interface(*)\Output Queue Length',
    '\TCPv4\Connections Reset',
    '\TCPv4\Segments Retransmitted/sec',
    '\DNS\Total Query Received/sec',
    '\Processor(_Total)\% DPC Time'
)

# Запускаем сбор на 5 минут
$job = Start-Job -ScriptBlock {
    param($counters)
    Get-Counter -Counter $counters -SampleInterval 1 -MaxSamples 300 |
        Export-Counter -Path "C:\traces\perf_counters.blg" -Force
} -ArgumentList (,$counters)

# 3. Воспроизводим проблему...

# 4. Останавливаем
netsh trace stop
$job | Stop-Job; $job | Remove-Job
```

### Анализ: На что смотреть

```powershell
# === TCP Retransmissions (главный индикатор проблем) ===
Get-Counter '\TCPv4\Segments Retransmitted/sec' -SampleInterval 1 -MaxSamples 10 |
    ForEach-Object {
        $_.CounterSamples | Select-Object Timestamp,
            @{N="Retrans/sec";E={[math]::Round($_.CookedValue,1)}}
    }
# Норма: < 1/sec
# Проблема: > 10/sec → потери пакетов на канале
# Катастрофа: > 100/sec → серьёзные проблемы с сетью

# === DNS Resolution Time ===
# Встроенного счётчика нет, используем Resolve-DnsName с замером
1..10 | ForEach-Object {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $null = Resolve-DnsName -Name "www.google.com" -DnsOnly
    $sw.Stop()
    [PSCustomObject]@{
        Attempt = $_
        TimeMs  = $sw.ElapsedMilliseconds
    }
}
# Норма: < 50ms (из кэша: < 1ms)
# Проблема: > 200ms → DNS-сервер далеко или перегружен
# Катастрофа: > 2000ms → DNS timeout, fallback на другой сервер

# === Wi-Fi Signal Quality ===
# Непрерывный мониторинг (каждую секунду)
while ($true) {
    $wlan = netsh wlan show interfaces | Select-String "Signal|BSSID|Channel|Receive"
    $timestamp = Get-Date -Format "HH:mm:ss"
    Write-Host "$timestamp $($wlan -join ' | ')"
    Start-Sleep -Seconds 1
}
# Signal < 50% → высокие потери на Wi-Fi
# BSSID меняется → частый roaming (плохое покрытие)
# Channel 1/6/11 → 2.4GHz (перегружено). Channel 36+ → 5GHz (лучше)
```

---

## Часть 9.9: Типичные клиентские проблемы — Cookbook

### Проблема 1: «Wi-Fi подключён, но страницы не грузятся»

```powershell
# Чеклист:
# 1. NCSI probe
Test-NetConnection -ComputerName www.msftconnecttest.com -Port 80
# Если TcpTestSucceeded: False → proxy/firewall блокирует

# 2. DNS
Resolve-DnsName -Name "www.google.com" -DnsOnly
# Если ошибка → DNS-сервер не отвечает
# Проверяем: какой DNS-сервер настроен?
Get-DnsClientServerAddress -InterfaceAlias "Wi-Fi" -AddressFamily IPv4
# Если 10.x.x.x → корпоративный DNS, нужен VPN
# Если 0.0.0.0 → DHCP не выдал DNS

# 3. Gateway
$gw = (Get-NetRoute -InterfaceAlias "Wi-Fi" -DestinationPrefix "0.0.0.0/0").NextHop
Test-Connection -TargetName $gw -Count 3
# Если timeout → gateway недоступен (Wi-Fi connected, но L3 нет)

# 4. DHCP lease
Get-NetIPAddress -InterfaceAlias "Wi-Fi" -AddressFamily IPv4
# Если 169.254.x.x (APIPA) → DHCP не работает

# 5. Captive Portal
Start-Process "http://captive.apple.com/hotspot-detect.html"
# Если перенаправляет → нужно пройти авторизацию
```

### Проблема 2: «После обновления Windows сеть пропала»

```powershell
# Частая причина: обновление сбросило/обновило драйвер NIC

# 1. Проверяем адаптер
Get-NetAdapter -Physical
# Если Status = "Disabled" или "Not Present" → драйвер слетел

# 2. Проверяем устройство
Get-PnpDevice -Class Net | Where-Object Status -ne "OK" |
    Select-Object FriendlyName, Status, Problem

# 3. Откатываем драйвер (если обновился)
$driver = Get-WmiObject Win32_PnpSignedDriver |
    Where-Object DeviceClass -eq "Net" |
    Select-Object DeviceName, DriverVersion, DriverDate
$driver | Format-Table -AutoSize
# Если DriverDate свежая → драйвер обновился

# 4. Проверяем, не отключился ли адаптер в Device Manager
Get-PnpDevice -Class Net |
    Where-Object { $_.Status -eq "Error" -or $_.Problem -ne 0 } |
    ForEach-Object {
        Write-Host "$($_.FriendlyName): Problem code $($_.Problem)"
        # Problem 22 = устройство отключено
        # Problem 28 = драйвер не установлен
        # Problem 31 = устройство не работает
        # Problem 43 = Windows остановило устройство (ошибка)
    }

# 5. Переустановить адаптер (без драйвера)
$adapter = Get-PnpDevice -Class Net | Where-Object FriendlyName -match "Wi-Fi"
Disable-PnpDevice -InstanceId $adapter.InstanceId -Confirm:$false
Start-Sleep -Seconds 3
Enable-PnpDevice -InstanceId $adapter.InstanceId -Confirm:$false
```

### Проблема 3: «Slow Wi-Fi» — Hidden Node и Channel Congestion

```powershell
# === Анализ Wi-Fi окружения ===

# Все видимые сети с каналами и силой сигнала
netsh wlan show networks mode=bssid | Select-String "SSID|Signal|Channel|Radio"

# Или через PowerShell (нужен Native Wi-Fi API):
$wlanReport = netsh wlan show wlanreport
Start-Process "$env:ProgramData\Microsoft\Windows\WlanReport\wlan-report-latest.html"
# Откроется HTML-отчёт с графиками сигнала, roaming events, ошибками

# === Канальная загруженность ===
# Если 10+ сетей на одном канале (1, 6, или 11 для 2.4GHz) — congestion
# Решение: переключить AP на менее загруженный канал 5GHz

# === Проверка negotiated rate ===
netsh wlan show interfaces | Select-String "Receive rate|Transmit rate"
# Receive rate (Mbps) : 866.7  ← 802.11ac, 80MHz, 2x2 MIMO
# Transmit rate (Mbps) : 866.7
# Если rate низкий (< 100 Mbps) при хорошем сигнале — проверить:
# - Wi-Fi драйвер (обновить)
# - Bandwidth 20MHz vs 40MHz vs 80MHz
# - MIMO (1x1 vs 2x2)
Get-NetAdapterAdvancedProperty -Name "Wi-Fi" |
    Where-Object DisplayName -match "Bandwidth|Channel Width|MIMO|802.11" |
    Select-Object DisplayName, DisplayValue
```

---

## Часть 9.10: Скрипт полной диагностики клиентской машины

```powershell
function Invoke-ClientNetworkDiag {
    <#
    .SYNOPSIS
    Полная сетевая диагностика клиентской Windows-машины.
    Собирает всё в один отчёт.
    #>

    $report = @()
    $report += "=== CLIENT NETWORK DIAGNOSTIC REPORT ==="
    $report += "Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    $report += "Computer: $env:COMPUTERNAME"
    $report += "User: $env:USERNAME"
    $report += ""

    # --- Adapters ---
    $report += "--- NETWORK ADAPTERS ---"
    Get-NetAdapter -Physical | ForEach-Object {
        $report += "  $($_.Name): $($_.Status), Speed=$($_.LinkSpeed), MAC=$($_.MacAddress)"
    }
    $report += ""

    # --- IP Config ---
    $report += "--- IP CONFIGURATION ---"
    Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.InterfaceAlias -notmatch "Loopback" } |
        ForEach-Object {
            $report += "  $($_.InterfaceAlias): $($_.IPAddress)/$($_.PrefixLength) ($($_.PrefixOrigin))"
        }
    $report += ""

    # --- DNS ---
    $report += "--- DNS SERVERS ---"
    Get-DnsClientServerAddress -AddressFamily IPv4 |
        Where-Object ServerAddresses |
        ForEach-Object {
            $report += "  $($_.InterfaceAlias): $($_.ServerAddresses -join ', ')"
        }
    $report += ""

    # --- Gateway ---
    $report += "--- DEFAULT GATEWAY ---"
    Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $metric = $_.RouteMetric + (Get-NetIPInterface -InterfaceIndex $_.InterfaceIndex -AddressFamily IPv4).InterfaceMetric
            $report += "  $($_.InterfaceAlias): $($_.NextHop) (Combined Metric=$metric)"
        }
    $report += ""

    # --- Network Profile (NLA) ---
    $report += "--- NETWORK PROFILE (NLA) ---"
    Get-NetConnectionProfile | ForEach-Object {
        $report += "  $($_.InterfaceAlias): $($_.Name) → $($_.NetworkCategory) (IPv4=$($_.IPv4Connectivity))"
    }
    $report += ""

    # --- Wi-Fi (если есть) ---
    $wlan = netsh wlan show interfaces 2>$null
    if ($wlan -match "connected") {
        $report += "--- WI-FI STATUS ---"
        $wlan | Select-String "State|SSID|BSSID|Signal|Channel|Radio|Receive rate|Authentication" |
            ForEach-Object { $report += "  $($_.Line.Trim())" }
        $report += ""
    }

    # --- NRPT ---
    $nrpt = Get-DnsClientNrptPolicy -ErrorAction SilentlyContinue
    if ($nrpt) {
        $report += "--- NRPT RULES (DNS Policy) ---"
        $nrpt | ForEach-Object {
            $report += "  Namespace=$($_.Namespace), DNS=$($_.NameServers -join ',')"
        }
        $report += ""
    }

    # --- VPN ---
    $vpn = Get-VpnConnection -ErrorAction SilentlyContinue
    if ($vpn) {
        $report += "--- VPN CONNECTIONS ---"
        $vpn | ForEach-Object {
            $report += "  $($_.Name): $($_.ConnectionStatus), Type=$($_.TunnelType), Split=$($_.SplitTunneling)"
        }
        $report += ""
    }

    # --- Connectivity Tests ---
    $report += "--- CONNECTIVITY TESTS ---"

    # Gateway ping
    $gw = (Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric | Select-Object -First 1).NextHop
    if ($gw) {
        $gwPing = Test-Connection -TargetName $gw -Count 2 -ErrorAction SilentlyContinue
        if ($gwPing) {
            $avgMs = [math]::Round(($gwPing | Measure-Object -Property Latency -Average).Average, 1)
            $report += "  Gateway ($gw): OK (${avgMs}ms)"
        } else {
            $report += "  Gateway ($gw): FAIL"
        }
    }

    # DNS resolution
    try {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        $dns = Resolve-DnsName -Name "www.google.com" -DnsOnly -ErrorAction Stop
        $sw.Stop()
        $report += "  DNS (www.google.com): OK ($($sw.ElapsedMilliseconds)ms → $($dns[0].IPAddress))"
    } catch {
        $report += "  DNS (www.google.com): FAIL ($($_.Exception.Message))"
    }

    # NCSI test
    try {
        $ncsi = Invoke-WebRequest -Uri "http://www.msftconnecttest.com/connecttest.txt" `
            -TimeoutSec 5 -UseBasicParsing -ErrorAction Stop
        if ($ncsi.Content -match "Microsoft Connect Test") {
            $report += "  NCSI Probe: OK"
        } else {
            $report += "  NCSI Probe: INTERCEPTED (captive portal?)"
        }
    } catch {
        $report += "  NCSI Probe: FAIL (proxy/firewall blocking?)"
    }

    # TCP connectivity
    $tcpTest = Test-NetConnection -ComputerName "8.8.8.8" -Port 443 -WarningAction SilentlyContinue
    $report += "  TCP 8.8.8.8:443: $($tcpTest.TcpTestSucceeded) (Latency=$($tcpTest.PingReplyDetails.RoundtripTime)ms)"

    $report += ""

    # --- TCP Stats ---
    $report += "--- TCP STATISTICS ---"
    $tcp = Get-Counter @(
        '\TCPv4\Connections Established',
        '\TCPv4\Connection Failures',
        '\TCPv4\Connections Reset',
        '\TCPv4\Segments Retransmitted/sec'
    ) -SampleInterval 1 -MaxSamples 1
    $tcp.CounterSamples | ForEach-Object {
        $name = $_.Path -replace "^.*\\", ""
        $report += "  $name: $([math]::Round($_.CookedValue, 1))"
    }
    $report += ""

    # --- NDIS Filter Drivers ---
    $report += "--- ACTIVE NDIS FILTERS ---"
    $mainAdapter = Get-NetAdapter -Physical | Where-Object Status -eq "Up" | Select-Object -First 1
    if ($mainAdapter) {
        Get-NetAdapterBinding -Name $mainAdapter.Name |
            Where-Object Enabled |
            ForEach-Object { $report += "  $($_.DisplayName) ($($_.ComponentID))" }
    }

    # Output
    $report | ForEach-Object { Write-Host $_ }

    # Save to file
    $outputPath = "$env:TEMP\NetworkDiag_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
    $report | Out-File -FilePath $outputPath -Encoding UTF8
    Write-Host "`nReport saved: $outputPath" -ForegroundColor Green

    return $outputPath
}

# Запуск:
Invoke-ClientNetworkDiag
```

---

## Практические задания

### Задание 1: Wi-Fi Roaming Analysis

1. Подключите ноутбук к корпоративному Wi-Fi
2. Включите сбор событий: `Get-WinEvent -LogName "Microsoft-Windows-WLAN-AutoConfig/Operational"`
3. Пройдитесь между этажами / точками доступа
4. Проанализируйте:
   - Сколько раз произошёл roaming?
   - Средняя длительность каждого roaming event?
   - Был ли хотя бы один roaming failure?
   - Поддерживает ли ваш адаптер 802.11r?

### Задание 2: DNS Path Tracing

1. Очистите DNS-кэш: `Clear-DnsClientCache`
2. Проверьте NRPT: есть ли правила, перехватывающие DNS?
3. Резолвите 10 доменов (корпоративных и публичных)
4. Для каждого определите: какой DNS-сервер ответил? Через какой интерфейс ушёл запрос?
5. Включите DoH для публичного DNS. Убедитесь, что корпоративные домены по-прежнему резолвятся через внутренний DNS

### Задание 3: VPN Split Tunnel Audit

1. Подключитесь к корпоративному VPN
2. Проверьте routing table: какие подсети идут через VPN?
3. Проверьте DNS: все ли запросы идут через VPN или только корпоративные?
4. Проверьте NRPT: есть ли catch-all правило (namespace = ".")?
5. Измерьте latency к корпоративным ресурсам через VPN vs без VPN
6. Проверьте MTU VPN-интерфейса: нет ли фрагментации?

### Задание 4: NCSI Troubleshooting

1. Заблокируйте www.msftconnecttest.com через файл hosts
2. Наблюдайте: через сколько секунд появится жёлтый треугольник?
3. Настройте кастомный NCSI endpoint (внутренний веб-сервер)
4. Уберите блокировку, проверьте, что кастомный endpoint работает
5. Задокументируйте: какие URL/IP нужно разрешить в proxy для корпоративной NCSI

### Задание 5: Полная диагностика «медленного ноутбука»

1. Запустите `Invoke-ClientNetworkDiag` на проблемном ноутбуке
2. Запустите ETW-трассировку: `netsh trace start scenario=InternetClient capture=yes`
3. Откройте браузер, загрузите тяжёлую страницу
4. Остановите трассировку, проанализируйте:
   - Есть ли TCP retransmissions? (Wi-Fi packet loss)
   - DNS resolution time > 100ms? (медленный DNS)
   - Есть ли DPC spikes? (плохой Wi-Fi драйвер)
5. Сформулируйте root cause и план исправления

---

**Связь с модулями книги:**
- Wi-Fi Power Management ≈ NIC power states (Модуль 1)
- DNS resolution path ≈ IP routing decision (Модуль 2)
- VPN IPsec/IKEv2 ≈ IPsec/XFRM (Модуль 2, часть 2.8)
- TCP retransmissions ≈ RTO/SACK/RACK (Модуль 3)
- Wi-Fi roaming ≈ Connection migration в QUIC (Модуль 7)
- NLA/NCSI ≈ Health checks / circuit breaker patterns (Модуль 10)
- NDIS filters ≈ netfilter/iptables chains (Модуль 5)

**Предыдущий модуль:** [Модуль 8: Windows Network Stack Internals](Module-08-Windows-Network-Stack.md) — серверный стек, NDIS, RSS, SR-IOV.
