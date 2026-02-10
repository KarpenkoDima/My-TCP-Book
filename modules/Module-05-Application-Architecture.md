# Модуль 5: Архитектура приложений (Пишем код, который летает)

*«Самый быстрый syscall — тот, который вы не сделали».*

Мы разобрали ядро изнутри. Теперь поднимаемся в user space и смотрим, как писать сетевые приложения, которые не убивают ту производительность, которую ядро так старательно обеспечивает.

---

## Часть 5.1: Zero-Copy — Убираем лишние копии

### Проблема классического пути данных

Когда Nginx отдаёт файл через обычный `read()` + `write()`:

```
Диск → [DMA] → Kernel Page Cache → [CPU copy] → User Buffer
User Buffer → [CPU copy] → Kernel Socket Buffer → [DMA] → NIC
```

Четыре копирования данных. Четыре переключения контекста (user→kernel→user→kernel). На 10 Gbps это сжирает CPU.

```
┌──────────────┐       ┌──────────────┐       ┌──────────────┐
│   Диск       │──DMA──│  Page Cache  │──copy──│ User Buffer  │
│              │       │  (kernel)    │       │ (user space) │
└──────────────┘       └──────────────┘       └──────┬───────┘
                                                      │ copy
                                               ┌──────▼───────┐
┌──────────────┐       ┌──────────────┐       │Socket Buffer │
│   NIC        │──DMA──│  Socket Buf  │───────│  (kernel)    │
│              │       │  (kernel)    │       └──────────────┘
└──────────────┘       └──────────────┘
```

### sendfile(): Классика Zero-Copy

```c
#include <sys/sendfile.h>

// Отправляем файл в сокет без копирования в user space
ssize_t sendfile(int out_fd, int in_fd, off_t *offset, size_t count);
```

Путь данных:

```
Диск → [DMA] → Kernel Page Cache → [DMA] → NIC
```

Два DMA, ноль CPU-копий. Данные никогда не покидают kernel space.

**Nginx** использует `sendfile` по умолчанию для статических файлов. Директива `sendfile on;` в конфиге — это именно эта оптимизация.

**Ограничение:** нельзя модифицировать данные «на лету» (нет доступа из user space). Для динамического контента sendfile бесполезен.

### splice() и tee(): Pipe-based Zero-Copy

`splice()` перемещает данные между файловым дескриптором и pipe без копирования:

```c
// Проксирование данных между двумя сокетами через pipe
int pipefd[2];
pipe(pipefd);

// Сокет → Pipe (zero-copy)
splice(client_fd, NULL, pipefd[1], NULL, 65536, SPLICE_F_MOVE);

// Pipe → Сокет (zero-copy)
splice(pipefd[0], NULL, server_fd, NULL, 65536, SPLICE_F_MOVE);
```

Kernel pipe buffers служат посредником. Данные не копируются — передаются ссылки на страницы памяти.

`tee()` — дублирует данные в pipe. Полезно для логирования: один поток идёт клиенту, копия — в лог.

### MSG_ZEROCOPY: Для произвольных данных (Linux 4.14+)

Когда нужно отправить данные из user-space буфера без копирования:

```c
// Включаем zero-copy на сокете
int val = 1;
setsockopt(fd, SOL_SOCKET, SO_ZEROCOPY, &val, sizeof(val));

// Отправляем с флагом MSG_ZEROCOPY
send(fd, buf, len, MSG_ZEROCOPY);

// Ядро маппит user pages для DMA
// Когда NIC закончил, приходит уведомление через errqueue
struct msghdr msg = {};
struct sock_extended_err *serr;
recvmsg(fd, &msg, MSG_ERRQUEUE);
// serr->ee_origin == SO_EE_ORIGIN_ZEROCOPY — данные отправлены
```

**Когда использовать:** только для больших сообщений (>10 KB). Для маленьких overhead от completion notification превышает выигрыш от zero-copy.

---

## Часть 5.2: IO Models — Эволюция от блокировки до io_uring

### Blocking I/O и проблема C10K

Классический подход: один поток на соединение.

```c
while (1) {
    int client = accept(server_fd, ...);
    pthread_create(&thread, NULL, handle_client, client);
}
```

**Почему не масштабируется:**
- Стек потока = 8 MB (по умолчанию). 10K потоков = 80 GB RAM.
- Переключение контекста между потоками стоит ~1-5 мкс. При 10K потоков — это проценты CPU.
- Это **C10K problem**: как обслужить 10,000 соединений на одном сервере. Решена в начале 2000-х через мультиплексирование.

### epoll: O(1) Event Notification

```c
// Создаём epoll instance
int epfd = epoll_create1(0);

// Добавляем сокет
struct epoll_event ev;
ev.events = EPOLLIN | EPOLLET;  // Edge-triggered
ev.data.fd = client_fd;
epoll_ctl(epfd, EPOLL_CTL_ADD, client_fd, &ev);

// Event loop
struct epoll_event events[MAX_EVENTS];
while (1) {
    int n = epoll_wait(epfd, events, MAX_EVENTS, -1);
    for (int i = 0; i < n; i++) {
        if (events[i].events & EPOLLIN) {
            handle_read(events[i].data.fd);
        }
    }
}
```

**Edge-triggered vs Level-triggered:**
- **Level-triggered** (по умолчанию): epoll_wait возвращает fd, пока есть данные. Проще, но может генерировать лишние wakeups.
- **Edge-triggered** (`EPOLLET`): возвращает fd только при **изменении** состояния. Эффективнее, но нужно читать до `EAGAIN`.

**Внутри ядра:** epoll использует red-black tree для хранения отслеживаемых fd и ready list для готовых. Добавление/удаление: O(log n). Получение событий: O(1) на событие.

### io_uring: Новая эра (Linux 5.1+)

io_uring — это **shared memory** между user space и kernel. Два кольцевых буфера:
- **Submission Queue (SQ)**: приложение кладёт запросы
- **Completion Queue (CQ)**: ядро кладёт результаты

```
┌─────────────────────────────────────────┐
│              User Space                  │
│                                          │
│   SQE → SQE → SQE     CQE ← CQE ← CQE │
│   [Submission Queue]   [Completion Queue]│
│         ↓                    ↑           │
├─────────┼────────────────────┼───────────┤
│         ↓     Kernel         ↑           │
│   Process SQEs ──────→ Post CQEs         │
│                                          │
└─────────────────────────────────────────┘
```

**Революция:** в SQPOLL-режиме ядро само опрашивает SQ. Приложение **вообще не делает syscall** для отправки запросов. Это убирает overhead переключения контекста полностью.

```c
#include <liburing.h>

struct io_uring ring;
io_uring_queue_init(256, &ring, 0);  // 256 entries

// Подготавливаем read операцию
struct io_uring_sqe *sqe = io_uring_get_sqe(&ring);
io_uring_prep_recv(sqe, client_fd, buf, buf_size, 0);
sqe->user_data = client_fd;  // Метка для идентификации

// Отправляем batch запросов в ядро (один syscall на всё!)
io_uring_submit(&ring);

// Получаем результаты
struct io_uring_cqe *cqe;
io_uring_wait_cqe(&ring, &cqe);
int bytes_read = cqe->res;
int fd = cqe->user_data;
io_uring_cqe_seen(&ring, cqe);
```

**Батчинг:** можно подготовить десятки SQE и отправить одним `io_uring_submit()`. Один syscall вместо десятков.

**Производительность:** io_uring показывает 20-30% меньше латентности и 50-100% больше IOPS по сравнению с epoll на высоких нагрузках.

**Предупреждение:** io_uring отключён в некоторых container environments (Docker, Kubernetes) из-за уязвимостей безопасности. Проверяйте перед использованием.

---

## Часть 5.3: Прикладной уровень (.NET / C# Context)

### Почему стандартный Socket API медленный

```csharp
// Каждый вызов — аллокация byte[]
byte[] buffer = new byte[4096];
int bytesRead = await socket.ReceiveAsync(buffer, SocketFlags.None);
// buffer → GC → паузы
```

Каждый `ReceiveAsync` создаёт Task, каждый буфер — аллокация в managed heap. При 100K соединений GC паузы убивают tail latency.

### System.IO.Pipelines: Управление буферами без аллокаций

Pipelines — это абстракция, разработанная командой ASP.NET для Kestrel. Основная идея: **переиспользуемые буферы из пула**.

```csharp
var pipe = new Pipe(new PipeOptions(
    pool: MemoryPool<byte>.Shared,           // Буферы из пула
    pauseWriterThreshold: 64 * 1024,         // Backpressure: стоп при 64KB
    resumeWriterThreshold: 32 * 1024         // Возобновить при 32KB
));

// Writer (получение данных из сокета)
async Task FillPipeAsync(Socket socket, PipeWriter writer)
{
    while (true)
    {
        // Получаем буфер из пула (без аллокации!)
        Memory<byte> memory = writer.GetMemory(4096);
        int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None);
        if (bytesRead == 0) break;

        writer.Advance(bytesRead);
        FlushResult result = await writer.FlushAsync();
        if (result.IsCompleted) break;
    }
    await writer.CompleteAsync();
}

// Reader (парсинг данных)
async Task ReadPipeAsync(PipeReader reader)
{
    while (true)
    {
        ReadResult result = await reader.ReadAsync();
        ReadOnlySequence<byte> buffer = result.Buffer;

        // Парсим данные без копирования
        while (TryParseMessage(ref buffer, out var message))
        {
            ProcessMessage(message);
        }

        // Сообщаем, сколько обработали
        reader.AdvanceTo(buffer.Start, buffer.End);
        if (result.IsCompleted) break;
    }
}
```

**Ключевые идеи:**
- `GetMemory()` возвращает буфер из пула, а не аллоцирует новый
- `ReadOnlySequence<byte>` — чтение **без копирования** из цепочки буферов
- Backpressure: если reader не успевает, writer блокируется на `FlushAsync()`
- Kestrel (веб-сервер ASP.NET Core) построен на Pipelines — поэтому он быстрый

### SocketAsyncEventArgs (SAEA): Allocation-free сетевой код

Pre-allocated объекты операций. Паттерн пула:

```csharp
class SaeaPool
{
    private readonly ConcurrentStack<SocketAsyncEventArgs> _pool;

    public SaeaPool(int capacity)
    {
        _pool = new ConcurrentStack<SocketAsyncEventArgs>();
        for (int i = 0; i < capacity; i++)
        {
            var saea = new SocketAsyncEventArgs();
            saea.SetBuffer(new byte[4096], 0, 4096);  // Один раз при создании
            saea.Completed += OnCompleted;
            _pool.Push(saea);
        }
    }

    public SocketAsyncEventArgs Rent()
    {
        _pool.TryPop(out var saea);
        return saea;
    }

    public void Return(SocketAsyncEventArgs saea) => _pool.Push(saea);

    private void OnCompleted(object sender, SocketAsyncEventArgs e)
    {
        // Обработка без аллокаций
        int bytesRead = e.BytesTransferred;
        if (bytesRead > 0)
            ProcessData(e.Buffer, e.Offset, bytesRead);

        Return(e);  // Возвращаем в пул
    }
}
```

### Memory<T> и Span<T> для сетевых буферов

```csharp
// Берём буфер из пула вместо new byte[]
byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
try
{
    // Span<byte> — stack-allocated view, zero overhead
    Span<byte> span = buffer.AsSpan(0, bytesRead);

    // Парсим бинарный протокол без аллокаций
    int messageType = BinaryPrimitives.ReadInt32BigEndian(span);
    int length = BinaryPrimitives.ReadInt32BigEndian(span.Slice(4));
    ReadOnlySpan<byte> payload = span.Slice(8, length);
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```

---

## Часть 5.4: Практические паттерны

### Nagle's Algorithm и TCP_NODELAY

Nagle's algorithm буферизирует маленькие writes, ожидая заполнения MSS или получения ACK. Это хорошо для bulk transfer (меньше маленьких пакетов), но катастрофа для интерактивных протоколов.

**Проблема Nagle + Delayed ACK:**
1. Клиент шлёт 100 байт (Nagle буферизирует, ждёт ACK)
2. Сервер получил данные, обрабатывает, хочет ответить
3. Сервер включает Delayed ACK (ждёт 200 мс перед отправкой ACK без данных)
4. Только через 200 мс ACK доходит до клиента
5. Nagle наконец отправляет следующую порцию

Итог: **200 мс задержки** на каждое сообщение. Решение:

```c
// C
int flag = 1;
setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &flag, sizeof(flag));
```

```csharp
// C#
socket.NoDelay = true;
```

**Правило:** для интерактивных протоколов (игры, чаты, API) — всегда `TCP_NODELAY = true`. Для bulk transfer (бэкапы, файлы) — оставить Nagle.

### SO_REUSEPORT: Multi-worker приём соединений

Позволяет нескольким процессам слушать один и тот же порт:

```c
int val = 1;
setsockopt(fd, SOL_SOCKET, SO_REUSEPORT, &val, sizeof(val));
bind(fd, ...);
listen(fd, backlog);
```

Ядро балансирует входящие соединения между workers. Nginx использует это с `reuseport` директивой:

```nginx
listen 80 reuseport;
```

Результат: каждый worker имеет свой accept queue, нет contention на одном lock.

### TCP Keep-Alive

```bash
# Через сколько секунд idle начинать probes
net.ipv4.tcp_keepalive_time = 600    # 10 минут

# Интервал между probes
net.ipv4.tcp_keepalive_intvl = 60    # 1 минута

# Сколько проб до признания мёртвым
net.ipv4.tcp_keepalive_probes = 5    # 5 проб
```

**Когда использовать application-level heartbeat вместо TCP keepalive:**
- Когда нужна быстрая реакция (TCP keepalive — минуты, heartbeat — секунды)
- Когда нужно проверить не только сеть, но и работоспособность приложения
- В протоколах с load balancer/proxy (keepalive может не пройти через middleware)

### Connection Pooling

Создание TCP-соединения дорого: 3-way handshake (1 RTT) + TLS handshake (1-2 RTT). При RTT=50мс это 100-150мс на каждое соединение.

```csharp
// .NET HttpClient с пулом соединений (по умолчанию)
var handler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    MaxConnectionsPerServer = 100,
    EnableMultipleHttp2Connections = true
};
var client = new HttpClient(handler);
```

HTTP/2 мультиплексирует множество запросов через одно TCP-соединение, что снижает потребность в большом пуле.

---

## Практическое задание

### Задача 1: TCP-прокси с splice()

Напишите TCP-прокси на C, который пересылает данные между клиентом и сервером через `splice()` (zero-copy). Измерьте throughput по сравнению с наивным `read()` + `write()` прокси.

### Задача 2: Echo-сервер с io_uring

Напишите echo-сервер на C с liburing. Реализуйте accept → recv → send цикл через io_uring SQE. Сравните P99 латентность с epoll-версией при 10K concurrent connections.

### Задача 3: .NET TCP-сервер на Pipelines

Напишите TCP-сервер на C# с System.IO.Pipelines. Реализуйте простой line-based протокол. Проверьте: при 10K соединений сколько GC пауз (Gen2) за минуту? Сравните с наивным async Socket подходом.

### Задача 4: Профилирование GC

Запустите два варианта сервера (наивный и Pipelines) под нагрузкой. Используйте:
```bash
dotnet-counters monitor --process-id <pid> --counters System.Runtime
```
Сравните `Gen 0/1/2 GC Count`, `GC Heap Size`, `% Time in GC`.

### Задача 5: Эффект Nagle + Delayed ACK

Напишите клиент, который отправляет 1-байтовые сообщения каждые 10 мс. Измерьте RTT с `TCP_NODELAY=false` и `TCP_NODELAY=true`. Ожидаемая разница: 200+ мс vs <1 мс.

---

**Следующий модуль:** QUIC и HTTP/3 — почему TCP становится legacy для Web, и что приходит ему на смену.
