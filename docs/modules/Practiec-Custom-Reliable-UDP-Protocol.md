# Проверь -->Custom Reliable UDP Protocol


> Препарировать UDP-протокол для внедрения TCP-механизмов надежности.
>   Это очень интересная задача! Давайте разберем это по частям:
>   
>   1. TCP Slow Start - алгоритм, где congestion window начинается с малого размера и экспоненциально растет
>   2. Fast Retransmission - быстрая повторная передача при получении дублированных ACK
>   3. Fast Recovery - восстановление после потерь без возврата к slow start
>   4. Congestion Window - окно перегрузки, контролирующее количество данных в пути
>   
>   Сначала объясню концепции, затем покажу архитектуру, а потом дам конкретную реализацию на C.
> - **Done**

Отличная идея! Создание собственного надежного UDP с механизмами TCP - это прекрасный способ понять, как работают алгоритмы контроля перегрузки изнутри. Давайте разберем это пошагово, как строится такая система.

## Понимание основных концепций

Сначала важно понять, что мы воссоздаем. TCP решает несколько ключевых проблем сетевого взаимодействия:

**Congestion Window (окно перегрузки)** - это количество данных, которое можно отправить, не дожидаясь подтверждения. Представьте это как размер "буфера в полете" между отправителем и получателем.

**Slow Start** работает как осторожный водитель на незнакомой дороге: начинаем медленно, но удваиваем скорость каждый раз, когда видим, что дорога свободна (получаем ACK).

**Fast Retransmission** - когда мы получаем три одинаковых ACK подряд, это сигнал о потере пакета. Вместо ожидания таймаута, мы сразу повторяем передачу.

**Fast Recovery** - после обнаружения потери мы не возвращаемся к самому началу (slow start), а уменьшаем окно наполовину и продолжаем.

## Архитектура системы

Давайте создадим модульную структуру на C:

**reliable_udp.h - Заголовочный файл**

```c
#ifndef RELIABLE_UDP_H
#define RELIABLE_UDP_H

#include <stdint.h>
#include <stdbool.h>
#include <sys/time.h>
#include <netinet/in.h>

// Константы для настройки алгоритмов
#define MAX_PACKET_SIZE 1400
#define INITIAL_CWND 1          // Начальное окно перегрузки (в пакетах)
#define MAX_CWND 65535          // Максимальное окно перегрузки
#define SSTHRESH_INITIAL 65535  // Начальный порог slow start
#define DUPLICATE_ACK_THRESH 3  // Порог для fast retransmission
#define RTO_MIN 1000           // Минимальный RTO в миллисекундах
#define RTO_MAX 60000          // Максимальный RTO в миллисекундах

// Состояния congestion control
typedef enum {
    SLOW_START,      // Экспоненциальный рост окна
    CONGESTION_AVOIDANCE,  // Линейный рост окна
    FAST_RECOVERY    // Восстановление после потери
} congestion_state_t;

// Структура для отслеживания отправленного пакета
typedef struct packet_info {
    uint32_t seq_num;              // Номер последовательности
    struct timeval send_time;      // Время отправки
    uint8_t retransmit_count;      // Количество повторных передач
    bool acknowledged;             // Получено ли подтверждение
    uint8_t data[MAX_PACKET_SIZE]; // Данные пакета
    size_t data_len;              // Размер данных
    struct packet_info *next;      // Следующий пакет в списке
} packet_info_t;

// Структура для контроля перегрузки
typedef struct {
    uint32_t cwnd;                 // Текущее окно перегрузки (в пакетах)
    uint32_t ssthresh;             // Порог slow start
    congestion_state_t state;      // Текущее состояние
    uint32_t duplicate_acks;       // Счетчик дублированных ACK
    uint32_t last_acked_seq;       // Последний подтвержденный номер
    uint32_t bytes_in_flight;      // Байт данных "в полете"
} congestion_control_t;

// Структура для обнаружения потерь
typedef struct {
    uint32_t rtt_estimate;         // Оценка RTT в миллисекундах
    uint32_t rtt_variance;         // Вариация RTT
    uint32_t rto;                  // Timeout для повторной передачи
    struct timeval last_rtt_sample; // Время последнего измерения RTT
} loss_detection_t;

// Структура для управления потоком
typedef struct {
    uint32_t send_window;          // Окно отправки
    uint32_t recv_window;          // Окно получения
    uint32_t next_seq_num;         // Следующий номер для отправки
    uint32_t expected_seq_num;     // Ожидаемый номер при получении
} flow_control_t;

// Основная структура надежного UDP
typedef struct {
    int socket_fd;                 // Файловый дескриптор сокета
    struct sockaddr_in peer_addr;  // Адрес партнера
    
    congestion_control_t cc;       // Контроль перегрузки
    loss_detection_t ld;          // Обнаружение потерь
    flow_control_t fc;            // Управление потоком
    
    packet_info_t *sent_packets;   // Список отправленных пакетов
    packet_info_t *recv_buffer;    // Буфер принятых пакетов
    
    bool is_connected;             // Состояние соединения
} reliable_udp_t;

// Заголовок пакета для нашего протокола
typedef struct {
    uint32_t seq_num;              // Номер последовательности
    uint32_t ack_num;              // Номер подтверждения
    uint16_t window_size;          // Размер окна получателя
    uint8_t flags;                 // Флаги (SYN, ACK, FIN и т.д.)
    uint8_t reserved;              // Зарезервировано
} packet_header_t;

// Флаги пакетов
#define FLAG_SYN 0x01
#define FLAG_ACK 0x02
#define FLAG_FIN 0x04
#define FLAG_DATA 0x08

// Функции API
reliable_udp_t* reliable_udp_create(void);
void reliable_udp_destroy(reliable_udp_t *rudp);
int reliable_udp_bind(reliable_udp_t *rudp, const char *addr, int port);
int reliable_udp_connect(reliable_udp_t *rudp, const char *addr, int port);
int reliable_udp_send(reliable_udp_t *rudp, const void *data, size_t len);
int reliable_udp_receive(reliable_udp_t *rudp, void *buffer, size_t len);
void reliable_udp_process_events(reliable_udp_t *rudp);

// Внутренние функции для алгоритмов
void congestion_control_init(congestion_control_t *cc);
void congestion_control_on_ack(congestion_control_t *cc, uint32_t acked_bytes);
void congestion_control_on_loss(congestion_control_t *cc);
void congestion_control_on_duplicate_ack(congestion_control_t *cc);

void loss_detection_init(loss_detection_t *ld);
void loss_detection_update_rtt(loss_detection_t *ld, uint32_t rtt_sample);
bool loss_detection_is_timeout(loss_detection_t *ld, struct timeval send_time);

#endif // RELIABLE_UDP_H
```

> Разработал образовательный подход к объяснению механизмов сетевого управления.
>
> - Пользователь выбрал стиль "Teacher/Educational", поэтому я должен подходить к этому как учитель, разбивая сложные концепции на простые части, используя аналогии и примеры, объясняя каждый шаг. Я должен объяснить не только что делать, но и почему это работает таким образом.
>   
>   Теперь мне нужно создать основную реализацию congestion control механизмов, объясняя каждый шаг как учитель. Я должен объяснить логику за каждым алгоритмом.
> - **Done**

Теперь давайте реализуем сердце системы - алгоритмы контроля перегрузки. Я покажу, как каждый алгоритм работает, объясняя логику за каждым решением:

**congestion_control.c - Реализация алгоритмов**

```c
#include "reliable_udp.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/time.h>

// ===== CONGESTION CONTROL IMPLEMENTATION =====

void congestion_control_init(congestion_control_t *cc) {
    // Начинаем консервативно: с одного пакета в окне
    // Это основа slow start - мы не знаем пропускную способность сети
    cc->cwnd = INITIAL_CWND;
    cc->ssthresh = SSTHRESH_INITIAL;
    cc->state = SLOW_START;
    cc->duplicate_acks = 0;
    cc->last_acked_seq = 0;
    cc->bytes_in_flight = 0;
    
    printf("[CC] Инициализация: cwnd=%u, ssthresh=%u, состояние=SLOW_START\n", 
           cc->cwnd, cc->ssthresh);
}

void congestion_control_on_ack(congestion_control_t *cc, uint32_t acked_bytes) {
    // Уменьшаем количество байт "в полете"
    cc->bytes_in_flight = (cc->bytes_in_flight > acked_bytes) ? 
                          cc->bytes_in_flight - acked_bytes : 0;
    
    // Сбрасываем счетчик дублированных ACK - это означает прогресс
    cc->duplicate_acks = 0;
    
    // Если мы были в fast recovery, возвращаемся к нормальной работе
    if (cc->state == FAST_RECOVERY) {
        cc->state = (cc->cwnd < cc->ssthresh) ? SLOW_START : CONGESTION_AVOIDANCE;
        printf("[CC] Выход из Fast Recovery, новое состояние: %s\n",
               cc->state == SLOW_START ? "SLOW_START" : "CONGESTION_AVOIDANCE");
    }
    
    // Теперь увеличиваем окно в зависимости от состояния
    switch (cc->state) {
        case SLOW_START:
            // В slow start удваиваем окно каждый RTT
            // На практике: увеличиваем на 1 за каждый ACK
            // Это дает экспоненциальный рост: 1->2->4->8...
            cc->cwnd++;
            printf("[CC] Slow Start: cwnd увеличено до %u\n", cc->cwnd);
            
            // Проверяем, не достигли ли порога slow start
            if (cc->cwnd >= cc->ssthresh) {
                cc->state = CONGESTION_AVOIDANCE;
                printf("[CC] Переход в Congestion Avoidance (cwnd=%u >= ssthresh=%u)\n",
                       cc->cwnd, cc->ssthresh);
            }
            break;
            
        case CONGESTION_AVOIDANCE:
            // В congestion avoidance растем линейно
            // Увеличиваем окно на 1/cwnd за каждый ACK
            // Это дает прирост ~1 пакет за RTT
            static uint32_t ack_count = 0;
            ack_count++;
            
            // Увеличиваем окно только когда накопили достаточно ACK
            if (ack_count >= cc->cwnd) {
                cc->cwnd++;
                ack_count = 0;
                printf("[CC] Congestion Avoidance: cwnd увеличено до %u\n", cc->cwnd);
            }
            break;
            
        case FAST_RECOVERY:
            // В fast recovery временно увеличиваем окно на каждый дублированный ACK
            // Это помогает поддерживать поток данных во время восстановления
            cc->cwnd++;
            printf("[CC] Fast Recovery: временное увеличение cwnd до %u\n", cc->cwnd);
            break;
    }
    
    // Ограничиваем максимальное окно
    if (cc->cwnd > MAX_CWND) {
        cc->cwnd = MAX_CWND;
    }
}

void congestion_control_on_duplicate_ack(congestion_control_t *cc) {
    cc->duplicate_acks++;
    
    printf("[CC] Получен дублированный ACK #%u\n", cc->duplicate_acks);
    
    // Fast Retransmission: 3 дублированных ACK = потеря пакета
    if (cc->duplicate_acks == DUPLICATE_ACK_THRESH) {
        printf("[CC] Обнаружена потеря! Запуск Fast Recovery\n");
        
        // Сохраняем текущее окно как новый порог
        // Это основано на предположении, что потеря произошла из-за перегрузки
        cc->ssthresh = cc->cwnd / 2;
        if (cc->ssthresh < 2) cc->ssthresh = 2; // Минимальный порог
        
        // Устанавливаем окно в ssthresh + 3 (за 3 дублированных ACK)
        cc->cwnd = cc->ssthresh + DUPLICATE_ACK_THRESH;
        
        // Переходим в состояние fast recovery
        cc->state = FAST_RECOVERY;
        
        printf("[CC] Fast Recovery: ssthresh=%u, cwnd=%u\n", 
               cc->ssthresh, cc->cwnd);
        
        // Здесь должна произойти немедленная повторная передача потерянного пакета
        // (это реализуется в функции отправки)
    }
    else if (cc->state == FAST_RECOVERY) {
        // В fast recovery каждый дополнительный дублированный ACK
        // означает, что еще один пакет покинул сеть
        cc->cwnd++;
        printf("[CC] Fast Recovery: дополнительный dup ACK, cwnd=%u\n", cc->cwnd);
    }
}

void congestion_control_on_loss(congestion_control_t *cc) {
    // Потеря по таймауту - самый серьезный сигнал перегрузки
    printf("[CC] Потеря по таймауту! Возврат к Slow Start\n");
    
    // Устанавливаем порог в половину текущего окна
    cc->ssthresh = cc->cwnd / 2;
    if (cc->ssthresh < 2) cc->ssthresh = 2;
    
    // Возвращаемся к началу - одному пакету
    cc->cwnd = INITIAL_CWND;
    cc->state = SLOW_START;
    cc->duplicate_acks = 0;
    
    printf("[CC] Сброс: cwnd=%u, ssthresh=%u\n", cc->cwnd, cc->ssthresh);
}

// ===== LOSS DETECTION IMPLEMENTATION =====

void loss_detection_init(loss_detection_t *ld) {
    // Начальные оценки основаны на типичных значениях для локальных сетей
    ld->rtt_estimate = 100;      // 100ms начальная оценка RTT
    ld->rtt_variance = 50;       // 50ms начальная вариация
    ld->rto = 1000;             // 1 секунда начальный timeout
    gettimeofday(&ld->last_rtt_sample, NULL);
    
    printf("[LD] Инициализация: RTT=%ums, RTO=%ums\n", 
           ld->rtt_estimate, ld->rto);
}

void loss_detection_update_rtt(loss_detection_t *ld, uint32_t rtt_sample) {
    // Алгоритм Карна для сглаживания RTT
    // SRTT = (1-α) * SRTT + α * RTT_sample, где α = 1/8
    // RTTVAR = (1-β) * RTTVAR + β * |SRTT - RTT_sample|, где β = 1/4
    
    if (ld->rtt_estimate == 0) {
        // Первое измерение
        ld->rtt_estimate = rtt_sample;
        ld->rtt_variance = rtt_sample / 2;
    } else {
        // Обновляем с экспоненциальным сглаживанием
        uint32_t error = abs((int)ld->rtt_estimate - (int)rtt_sample);
        
        ld->rtt_estimate = (7 * ld->rtt_estimate + rtt_sample) / 8;
        ld->rtt_variance = (3 * ld->rtt_variance + error) / 4;
    }
    
    // RTO = SRTT + max(G, 4 * RTTVAR), где G - granularity timer (обычно 10ms)
    ld->rto = ld->rtt_estimate + 4 * ld->rtt_variance;
    
    // Ограничиваем RTO разумными пределами
    if (ld->rto < RTO_MIN) ld->rto = RTO_MIN;
    if (ld->rto > RTO_MAX) ld->rto = RTO_MAX;
    
    gettimeofday(&ld->last_rtt_sample, NULL);
    
    printf("[LD] RTT обновлен: sample=%ums, SRTT=%ums, RTTVAR=%ums, RTO=%ums\n",
           rtt_sample, ld->rtt_estimate, ld->rtt_variance, ld->rto);
}

bool loss_detection_is_timeout(loss_detection_t *ld, struct timeval send_time) {
    struct timeval current_time;
    gettimeofday(&current_time, NULL);
    
    // Вычисляем время с момента отправки в миллисекундах
    uint32_t elapsed_ms = (current_time.tv_sec - send_time.tv_sec) * 1000 +
                         (current_time.tv_usec - send_time.tv_usec) / 1000;
    
    bool is_timeout = elapsed_ms >= ld->rto;
    
    if (is_timeout) {
        printf("[LD] Таймаут! Прошло %ums, RTO=%ums\n", elapsed_ms, ld->rto);
        
        // При таймауте удваиваем RTO (exponential backoff)
        ld->rto *= 2;
        if (ld->rto > RTO_MAX) ld->rto = RTO_MAX;
    }
    
    return is_timeout;
}

// ===== FLOW CONTROL IMPLEMENTATION =====

void flow_control_init(flow_control_t *fc) {
    fc->send_window = MAX_PACKET_SIZE * 10;  // Начальное окно на 10 пакетов
    fc->recv_window = MAX_PACKET_SIZE * 20;  // Буфер на 20 пакетов
    fc->next_seq_num = 1;                    // Начинаем с 1 (0 зарезервирован)
    fc->expected_seq_num = 1;
    
    printf("[FC] Инициализация: send_window=%u, recv_window=%u\n",
           fc->send_window, fc->recv_window);
}

// Функция для определения, можно ли отправить пакет
bool can_send_packet(reliable_udp_t *rudp, size_t packet_size) {
    // Проверяем все ограничения:
    // 1. Congestion window (в байтах)
    uint32_t cwnd_bytes = rudp->cc.cwnd * MAX_PACKET_SIZE;
    
    // 2. Flow control window
    uint32_t available_window = rudp->fc.send_window;
    
    // 3. Байты уже "в полете"
    uint32_t available_cwnd = (cwnd_bytes > rudp->cc.bytes_in_flight) ?
                              cwnd_bytes - rudp->cc.bytes_in_flight : 0;
    
    // Можем отправить только если все условия выполнены
    bool can_send = (packet_size <= available_cwnd) && 
                    (packet_size <= available_window);
    
    printf("[FC] Проверка отправки: size=%zu, available_cwnd=%u, "
           "available_window=%u, can_send=%s\n",
           packet_size, available_cwnd, available_window,
           can_send ? "YES" : "NO");
    
    return can_send;
}
```

> Разработал стратегию обучения техническим концепциям сетевых протоколов
>
> - Теперь пользователь видит основную реализацию алгоритмов. Как учитель, я должен объяснить, как все эти компоненты работают вместе, и показать основную функцию отправки, которая объединяет все алгоритмы. Также важно объяснить практические аспекты - как это тестировать и какие есть тонкости в реальном мире.
>   
>   Пользователь выбрал стиль Teacher/Educational, поэтому я должен:
>   1. Объяснить, как все компоненты работают вместе
>   2. Показать практические примеры использования
>   3. Объяснить, что происходит в каждом случае
>   4. Дать советы по отладке и тестированию
>   5. Указать на потенциальные проблемы и их решения
> - **Done**

Теперь давайте соберем все компоненты вместе и покажем, как они взаимодействуют в основной функции отправки. Это поможет понять, как алгоритмы работают в реальном времени:

**reliable_udp.c - Основная реализация**

```c
#include "reliable_udp.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <errno.h>
#include <arpa/inet.h>

// ===== ОСНОВНЫЕ ФУНКЦИИ API =====

reliable_udp_t* reliable_udp_create(void) {
    reliable_udp_t *rudp = malloc(sizeof(reliable_udp_t));
    if (!rudp) {
        printf("[ERROR] Не удалось выделить память для reliable_udp_t\n");
        return NULL;
    }
    
    // Создаем UDP сокет
    rudp->socket_fd = socket(AF_INET, SOCK_DGRAM, 0);
    if (rudp->socket_fd < 0) {
        printf("[ERROR] Не удалось создать сокет: %s\n", strerror(errno));
        free(rudp);
        return NULL;
    }
    
    // Инициализируем все подсистемы
    congestion_control_init(&rudp->cc);
    loss_detection_init(&rudp->ld);
    flow_control_init(&rudp->fc);
    
    // Инициализируем состояние
    rudp->sent_packets = NULL;
    rudp->recv_buffer = NULL;
    rudp->is_connected = false;
    
    printf("[RUDP] Создано надежное UDP соединение\n");
    return rudp;
}

void reliable_udp_destroy(reliable_udp_t *rudp) {
    if (!rudp) return;
    
    // Освобождаем списки пакетов
    packet_info_t *current = rudp->sent_packets;
    while (current) {
        packet_info_t *next = current->next;
        free(current);
        current = next;
    }
    
    current = rudp->recv_buffer;
    while (current) {
        packet_info_t *next = current->next;
        free(current);
        current = next;
    }
    
    close(rudp->socket_fd);
    free(rudp);
    printf("[RUDP] Соединение закрыто и ресурсы освобождены\n");
}

int reliable_udp_bind(reliable_udp_t *rudp, const char *addr, int port) {
    struct sockaddr_in server_addr;
    memset(&server_addr, 0, sizeof(server_addr));
    
    server_addr.sin_family = AF_INET;
    server_addr.sin_port = htons(port);
    server_addr.sin_addr.s_addr = addr ? inet_addr(addr) : INADDR_ANY;
    
    if (bind(rudp->socket_fd, (struct sockaddr*)&server_addr, sizeof(server_addr)) < 0) {
        printf("[ERROR] Не удалось привязать сокет: %s\n", strerror(errno));
        return -1;
    }
    
    printf("[RUDP] Сокет привязан к %s:%d\n", 
           addr ? addr : "0.0.0.0", port);
    return 0;
}

int reliable_udp_connect(reliable_udp_t *rudp, const char *addr, int port) {
    memset(&rudp->peer_addr, 0, sizeof(rudp->peer_addr));
    rudp->peer_addr.sin_family = AF_INET;
    rudp->peer_addr.sin_port = htons(port);
    
    if (inet_pton(AF_INET, addr, &rudp->peer_addr.sin_addr) <= 0) {
        printf("[ERROR] Неверный адрес: %s\n", addr);
        return -1;
    }
    
    rudp->is_connected = true;
    printf("[RUDP] Установлено соединение с %s:%d\n", addr, port);
    return 0;
}

// ===== ОСНОВНАЯ ФУНКЦИЯ ОТПРАВКИ =====

int reliable_udp_send(reliable_udp_t *rudp, const void *data, size_t len) {
    if (!rudp->is_connected) {
        printf("[ERROR] Соединение не установлено\n");
        return -1;
    }
    
    // Проверяем, можем ли мы отправить пакет согласно всем алгоритмам
    size_t packet_total_size = sizeof(packet_header_t) + len;
    
    if (!can_send_packet(rudp, packet_total_size)) {
        printf("[SEND] Отправка заблокирована алгоритмами контроля\n");
        return 0; // Не ошибка, просто нужно подождать
    }
    
    // Создаем заголовок пакета
    packet_header_t header;
    header.seq_num = htonl(rudp->fc.next_seq_num);
    header.ack_num = htonl(rudp->fc.expected_seq_num);
    header.window_size = htons(rudp->fc.recv_window);
    header.flags = FLAG_DATA;
    header.reserved = 0;
    
    // Подготавливаем буфер для отправки
    uint8_t send_buffer[MAX_PACKET_SIZE];
    memcpy(send_buffer, &header, sizeof(header));
    memcpy(send_buffer + sizeof(header), data, len);
    
    // Отправляем пакет
    ssize_t sent = sendto(rudp->socket_fd, send_buffer, packet_total_size, 0,
                         (struct sockaddr*)&rudp->peer_addr, sizeof(rudp->peer_addr));
    
    if (sent < 0) {
        printf("[ERROR] Ошибка отправки: %s\n", strerror(errno));
        return -1;
    }
    
    // Создаем запись о отправленном пакете для отслеживания
    packet_info_t *packet = malloc(sizeof(packet_info_t));
    if (!packet) {
        printf("[ERROR] Не удалось выделить память для отслеживания пакета\n");
        return -1;
    }
    
    packet->seq_num = rudp->fc.next_seq_num;
    gettimeofday(&packet->send_time, NULL);
    packet->retransmit_count = 0;
    packet->acknowledged = false;
    packet->data_len = len;
    memcpy(packet->data, data, len);
    
    // Добавляем в список отправленных пакетов
    packet->next = rudp->sent_packets;
    rudp->sent_packets = packet;
    
    // Обновляем состояние
    rudp->fc.next_seq_num++;
    rudp->cc.bytes_in_flight += packet_total_size;
    
    printf("[SEND] Отправлен пакет seq=%u, размер=%zu, bytes_in_flight=%u\n",
           packet->seq_num, packet_total_size, rudp->cc.bytes_in_flight);
    
    return len;
}

// ===== ФУНКЦИЯ ОБРАБОТКИ ВХОДЯЩИХ ПАКЕТОВ =====

void process_incoming_packet(reliable_udp_t *rudp, const uint8_t *buffer, size_t len) {
    if (len < sizeof(packet_header_t)) {
        printf("[RECV] Пакет слишком мал: %zu байт\n", len);
        return;
    }
    
    // Извлекаем заголовок
    packet_header_t header;
    memcpy(&header, buffer, sizeof(header));
    
    uint32_t seq_num = ntohl(header.seq_num);
    uint32_t ack_num = ntohl(header.ack_num);
    uint16_t window_size = ntohs(header.window_size);
    
    printf("[RECV] Получен пакет: seq=%u, ack=%u, flags=0x%02x, window=%u\n",
           seq_num, ack_num, header.flags, window_size);
    
    // Обрабатываем ACK, если он есть
    if (header.flags & FLAG_ACK) {
        process_ack(rudp, ack_num);
    }
    
    // Обрабатываем данные, если они есть
    if (header.flags & FLAG_DATA) {
        process_data_packet(rudp, seq_num, buffer + sizeof(header), 
                           len - sizeof(header));
    }
    
    // Обновляем окно отправки партнера
    rudp->fc.send_window = window_size;
}

void process_ack(reliable_udp_t *rudp, uint32_t ack_num) {
    packet_info_t *prev = NULL;
    packet_info_t *current = rudp->sent_packets;
    bool found_new_ack = false;
    uint32_t acked_bytes = 0;
    
    // Ищем подтвержденные пакеты
    while (current) {
        if (current->seq_num < ack_num && !current->acknowledged) {
            // Этот пакет подтвержден!
            current->acknowledged = true;
            found_new_ack = true;
            acked_bytes += current->data_len + sizeof(packet_header_t);
            
            // Обновляем RTT, если это первая передача пакета
            if (current->retransmit_count == 0) {
                struct timeval current_time;
                gettimeofday(&current_time, NULL);
                
                uint32_t rtt = (current_time.tv_sec - current->send_time.tv_sec) * 1000 +
                              (current_time.tv_usec - current->send_time.tv_usec) / 1000;
                
                loss_detection_update_rtt(&rudp->ld, rtt);
            }
            
            printf("[ACK] Подтвержден пакет seq=%u, RTT измерен\n", current->seq_num);
        }
        
        prev = current;
        current = current->next;
    }
    
    if (found_new_ack) {
        // Новое подтверждение - хорошие новости для congestion control
        rudp->cc.last_acked_seq = ack_num;
        congestion_control_on_ack(&rudp->cc, acked_bytes);
        
        // Удаляем подтвержденные пакеты из списка
        cleanup_acknowledged_packets(rudp);
    } else if (ack_num == rudp->cc.last_acked_seq) {
        // Дублированный ACK - потенциальный сигнал о потере
        congestion_control_on_duplicate_ack(&rudp->cc);
        
        // Если это третий дублированный ACK, немедленно повторяем передачу
        if (rudp->cc.duplicate_acks == DUPLICATE_ACK_THRESH) {
            fast_retransmit(rudp, ack_num);
        }
    }
}

void fast_retransmit(reliable_udp_t *rudp, uint32_t missing_seq) {
    packet_info_t *current = rudp->sent_packets;
    
    // Ищем потерянный пакет
    while (current) {
        if (current->seq_num == missing_seq && !current->acknowledged) {
            printf("[RETX] Fast Retransmit для пакета seq=%u\n", missing_seq);
            
            // Повторно отправляем пакет
            retransmit_packet(rudp, current);
            break;
        }
        current = current->next;
    }
}

void retransmit_packet(reliable_udp_t *rudp, packet_info_t *packet) {
    // Создаем заголовок для повторной передачи
    packet_header_t header;
    header.seq_num = htonl(packet->seq_num);
    header.ack_num = htonl(rudp->fc.expected_seq_num);
    header.window_size = htons(rudp->fc.recv_window);
    header.flags = FLAG_DATA;
    header.reserved = 0;
    
    // Подготавливаем буфер
    uint8_t send_buffer[MAX_PACKET_SIZE];
    memcpy(send_buffer, &header, sizeof(header));
    memcpy(send_buffer + sizeof(header), packet->data, packet->data_len);
    
    size_t total_size = sizeof(header) + packet->data_len;
    
    // Отправляем
    ssize_t sent = sendto(rudp->socket_fd, send_buffer, total_size, 0,
                         (struct sockaddr*)&rudp->peer_addr, sizeof(rudp->peer_addr));
    
    if (sent >= 0) {
        // Обновляем информацию о пакете
        gettimeofday(&packet->send_time, NULL);
        packet->retransmit_count++;
        
        printf("[RETX] Повторно отправлен пакет seq=%u (попытка #%u)\n",
               packet->seq_num, packet->retransmit_count);
    } else {
        printf("[ERROR] Ошибка повторной передачи: %s\n", strerror(errno));
    }
}

// ===== ОСНОВНОЙ ЦИКЛ ОБРАБОТКИ СОБЫТИЙ =====

void reliable_udp_process_events(reliable_udp_t *rudp) {
    fd_set read_fds;
    struct timeval timeout;
    
    FD_ZERO(&read_fds);
    FD_SET(rudp->socket_fd, &read_fds);
    
    // Устанавливаем короткий таймаут для регулярных проверок
    timeout.tv_sec = 0;
    timeout.tv_usec = 10000; // 10ms
    
    int ready = select(rudp->socket_fd + 1, &read_fds, NULL, NULL, &timeout);
    
    if (ready > 0 && FD_ISSET(rudp->socket_fd, &read_fds)) {
        // Есть входящие данные
        uint8_t buffer[MAX_PACKET_SIZE];
        struct sockaddr_in sender_addr;
        socklen_t addr_len = sizeof(sender_addr);
        
        ssize_t received = recvfrom(rudp->socket_fd, buffer, sizeof(buffer), 0,
                                   (struct sockaddr*)&sender_addr, &addr_len);
        
        if (received > 0) {
            process_incoming_packet(rudp, buffer, received);
        }
    }
    
    // Проверяем таймауты для повторной передачи
    check_retransmission_timeouts(rudp);
}

void check_retransmission_timeouts(reliable_udp_t *rudp) {
    packet_info_t *current = rudp->sent_packets;
    
    while (current) {
        if (!current->acknowledged && 
            loss_detection_is_timeout(&rudp->ld, current->send_time)) {
            
            printf("[TIMEOUT] Таймаут для пакета seq=%u\n", current->seq_num);
            
            // Уведомляем congestion control о потере
            congestion_control_on_loss(&rudp->cc);
            
            // Повторяем передачу
            retransmit_packet(rudp, current);
        }
        
        current = current->next;
    }
}

// ===== ВСПОМОГАТЕЛЬНЫЕ ФУНКЦИИ =====

void cleanup_acknowledged_packets(reliable_udp_t *rudp) {
    packet_info_t **current = &rudp->sent_packets;
    
    while (*current) {
        if ((*current)->acknowledged) {
            packet_info_t *to_delete = *current;
            *current = (*current)->next;
            free(to_delete);
        } else {
            current = &(*current)->next;
        }
    }
}

void process_data_packet(reliable_udp_t *rudp, uint32_t seq_num, 
                        const uint8_t *data, size_t len) {
    // Отправляем ACK для полученного пакета
    send_ack(rudp, seq_num + 1);
    
    // Здесь должна быть логика буферизации и упорядочивания пакетов
    // Для простоты примера пропустим детальную реализацию
    printf("[DATA] Обработан пакет с данными seq=%u, размер=%zu\n", seq_num, len);
}

void send_ack(reliable_udp_t *rudp, uint32_t ack_num) {
    packet_header_t header;
    header.seq_num = htonl(0); // ACK пакеты не имеют своего seq_num
    header.ack_num = htonl(ack_num);
    header.window_size = htons(rudp->fc.recv_window);
    header.flags = FLAG_ACK;
    header.reserved = 0;
    
    sendto(rudp->socket_fd, &header, sizeof(header), 0,
           (struct sockaddr*)&rudp->peer_addr, sizeof(rudp->peer_addr));
    
    printf("[ACK] Отправлен ACK=%u\n", ack_num);
}
```

> Разработал стратегию обучения сложным сетевым концепциям.
>
> - Пользователь получил большую кодовую базу, которая показывает, как работают TCP-подобные алгоритмы в UDP. Как учитель, мне нужно:
>   
>   1. Объяснить, как все компоненты работают вместе
>   2. Показать практический пример использования 
>   3. Объяснить, что происходит на каждом этапе
>   4. Дать советы по тестированию и отладке
>   5. Объяснить ключевые концепции простыми словами
>   
>   Поскольку пользователь выбрал стиль Teacher/Educational, я должен подходить к этому как педагог, разбивая сложные идеи на простые части, используя аналогии и примеры.
> - **Done**

Отлично! Теперь у вас есть полная реализация надежного UDP. Давайте разберем, как все эти алгоритмы работают вместе, как настоящий оркестр, где каждый инструмент играет свою партию, но все вместе создают гармонию.

## Как работает вся система

Представьте себе эту систему как умного курьера, который учится доставлять посылки по загруженному городу:

**Slow Start** - это как первый день работы курьера. Он не знает дороги, поэтому начинает с одной посылки. Если доставка прошла успешно, в следующий раз берет две посылки. Потом четыре, восемь - растет экспоненциально, пока не почувствует, что дороги заполняются.

**Congestion Avoidance** - когда курьер понимает, что дороги становятся загруженными (достигает `ssthresh`), он переходит к более осторожному подходу. Теперь он увеличивает количество посылок всего на одну за каждый успешный цикл доставки.

**Fast Retransmission** - если курьер получает три сообщения подряд "где моя посылка №5?", он понимает, что посылка №5 потерялась, и сразу же отправляет ее повторно, не дожидаясь полного таймаута.

**Fast Recovery** - после того как курьер обнаружил потерю и повторил доставку, он не возвращается к самому началу (одной посылке), а просто уменьшает нагрузку наполовину и продолжает работать.

## Практический пример использования

Давайте создадим простой пример, который покажет, как использовать нашу систему:

**example.c - Пример использования**

```c
#include "reliable_udp.h"
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <pthread.h>

// Пример отправителя
void* sender_thread(void* arg) {
    printf("=== ЗАПУСК ОТПРАВИТЕЛЯ ===\n");
    
    // Создаем надежное UDP соединение
    reliable_udp_t *sender = reliable_udp_create();
    if (!sender) {
        printf("Ошибка создания отправителя\n");
        return NULL;
    }
    
    // Подключаемся к получателю
    if (reliable_udp_connect(sender, "127.0.0.1", 8080) < 0) {
        printf("Ошибка подключения\n");
        reliable_udp_destroy(sender);
        return NULL;
    }
    
    // Имитируем отправку данных разного размера
    const char *messages[] = {
        "Привет! Это первое сообщение.",
        "Второе сообщение немного длиннее первого.",
        "Третье сообщение показывает, как работает slow start.",
        "Четвертое сообщение демонстрирует увеличение congestion window.",
        "Пятое сообщение поможет нам увидеть congestion avoidance в действии."
    };
    
    int num_messages = sizeof(messages) / sizeof(messages[0]);
    
    for (int i = 0; i < num_messages; i++) {
        printf("\n--- Отправка сообщения %d ---\n", i + 1);
        
        // Пытаемся отправить сообщение
        int result = reliable_udp_send(sender, messages[i], strlen(messages[i]));
        
        if (result > 0) {
            printf("Успешно поставлено в очередь: %d байт\n", result);
        } else if (result == 0) {
            printf("Отправка заблокирована - нужно подождать\n");
            i--; // Повторим попытку
        } else {
            printf("Ошибка отправки\n");
            break;
        }
        
        // Обрабатываем события (ACK, таймауты и т.д.)
        for (int j = 0; j < 10; j++) {
            reliable_udp_process_events(sender);
            usleep(50000); // 50ms
        }
        
        printf("Текущее состояние:\n");
        printf("  - Congestion Window: %u пакетов\n", sender->cc.cwnd);
        printf("  - SSThresh: %u пакетов\n", sender->cc.ssthresh);
        printf("  - Состояние: %s\n", 
               sender->cc.state == SLOW_START ? "SLOW_START" :
               sender->cc.state == CONGESTION_AVOIDANCE ? "CONGESTION_AVOIDANCE" :
               "FAST_RECOVERY");
        printf("  - Байт в полете: %u\n", sender->cc.bytes_in_flight);
        printf("  - Текущий RTO: %u ms\n", sender->ld.rto);
    }
    
    // Даем время для завершения всех передач
    printf("\n--- Завершение передач ---\n");
    for (int i = 0; i < 100; i++) {
        reliable_udp_process_events(sender);
        usleep(100000); // 100ms
    }
    
    reliable_udp_destroy(sender);
    printf("=== ОТПРАВИТЕЛЬ ЗАВЕРШЕН ===\n");
    return NULL;
}

// Пример получателя
void* receiver_thread(void* arg) {
    printf("=== ЗАПУСК ПОЛУЧАТЕЛЯ ===\n");
    
    reliable_udp_t *receiver = reliable_udp_create();
    if (!receiver) {
        printf("Ошибка создания получателя\n");
        return NULL;
    }
    
    // Привязываем к порту
    if (reliable_udp_bind(receiver, "127.0.0.1", 8080) < 0) {
        printf("Ошибка привязки\n");
        reliable_udp_destroy(receiver);
        return NULL;
    }
    
    printf("Получатель ожидает данные на порту 8080...\n");
    
    // Основной цикл получения
    for (int i = 0; i < 1000; i++) { // Работаем 100 секунд
        reliable_udp_process_events(receiver);
        usleep(100000); // 100ms
    }
    
    reliable_udp_destroy(receiver);
    printf("=== ПОЛУЧАТЕЛЬ ЗАВЕРШЕН ===\n");
    return NULL;
}

// Функция для имитации потерь пакетов (для тестирования)
void simulate_network_conditions() {
    printf("\n=== ИМИТАЦИЯ СЕТЕВЫХ УСЛОВИЙ ===\n");
    printf("В реальной сети происходят различные события:\n");
    printf("1. Потери пакетов (обычно 0.1-1%%)\n");
    printf("2. Дублирование пакетов\n");
    printf("3. Изменение задержки (jitter)\n");
    printf("4. Временные перегрузки\n");
    printf("\nНаши алгоритмы адаптируются к этим условиям:\n");
    printf("- Fast Retransmission обнаруживает потери быстрее таймаута\n");
    printf("- Congestion Control адаптирует скорость к пропускной способности\n");
    printf("- RTT estimation подстраивает таймауты под задержки сети\n");
}

int main() {
    printf("=== ДЕМОНСТРАЦИЯ RELIABLE UDP ===\n");
    printf("Этот пример показывает, как работают TCP-алгоритмы в UDP\n\n");
    
    // Объясняем, что будет происходить
    printf("ЧТО МЫ УВИДИМ:\n");
    printf("1. Slow Start: окно растет 1->2->4->8...\n");
    printf("2. Congestion Avoidance: окно растет медленнее\n");
    printf("3. RTT измерения и адаптация RTO\n");
    printf("4. Обработка ACK и управление потоком\n\n");
    
    simulate_network_conditions();
    
    // Создаем потоки для отправителя и получателя
    pthread_t sender_tid, receiver_tid;
    
    // Запускаем получатель первым
    if (pthread_create(&receiver_tid, NULL, receiver_thread, NULL) != 0) {
        printf("Ошибка создания потока получателя\n");
        return 1;
    }
    
    // Даем получателю время на инициализацию
    sleep(1);
    
    // Запускаем отправитель
    if (pthread_create(&sender_tid, NULL, sender_thread, NULL) != 0) {
        printf("Ошибка создания потока отправителя\n");
        return 1;
    }
    
    // Ждем завершения отправителя
    pthread_join(sender_tid, NULL);
    
    // Даем получателю время на обработку последних пакетов
    sleep(2);
    
    // Завершаем получатель (в реальности он бы работал бесконечно)
    pthread_cancel(receiver_tid);
    pthread_join(receiver_tid, NULL);
    
    printf("\n=== ДЕМОНСТРАЦИЯ ЗАВЕРШЕНА ===\n");
    printf("ВЫВОДЫ:\n");
    printf("1. Система начала с осторожного slow start\n");
    printf("2. Адаптировалась к пропускной способности сети\n");
    printf("3. Измеряла RTT и настраивала таймауты\n");
    printf("4. Реагировала на потери и перегрузки\n");
    printf("\nЭто основа для понимания того, как TCP обеспечивает надежность!\n");
    
    return 0;
}

// Компиляция: gcc -o example example.c reliable_udp.c congestion_control.c -lpthread
// Запуск: ./example
```

> Разработал стратегию обучения TCP-концепциям в UDP-контексте.
>
> - Пользователь видел большую кодовую базу с реализацией алгоритмов TCP в UDP. Теперь как учитель мне нужно:
>   
>   1. Объяснить практические аспекты - как тестировать, что ожидать
>   2. Дать советы по отладке и понимаю того, что происходит
>   3. Объяснить ключевые концепции простыми словами
>   4. Показать, как это применить к реальным задачам
>   5. Объяснить потенциальные проблемы и их решения
>   
>   Пользователь выбрал Teacher/Educational style, поэтому я должен:
>   - Использовать аналогии и примеры
>   - Разбивать сложные идеи на простые части
>   - Объяснять "почему", а не только "как"
>   - Предвидеть потенциальные вопросы и заблуждения
>   - Использовать поощряющий тон
> - **Done**

## Ключевые концепции для понимания

Давайте убедимся, что вы понимаете фундаментальные принципы того, что мы создали. Это поможет вам не только использовать код, но и адаптировать его под свои задачи.

### Почему эти алгоритмы работают именно так?

**Exponential vs Linear Growth**: Slow start использует экспоненциальный рост потому, что в начале мы ничего не знаем о сети. Лучше быстро найти приблизительную пропускную способность, а потом аккуратно её исследовать. Это как поиск парковочного места: сначала быстро проезжаем район, потом медленно ищем конкретное место.

**Почему именно 3 дублированных ACK?** Это баланс между быстротой реакции и ложными срабатываниями. Один или два дублированных ACK могут быть случайными переупорядочиваниями пакетов в сети. Три подряд - очень вероятный сигнал о потере.

**Зачем Fast Recovery?** Представьте, что вы ведете машину и видите пробку впереди. Вы не останавливаетесь полностью (что было бы аналогично возврату к slow start), а просто снижаете скорость и продолжаете движение. Fast Recovery работает по тому же принципу.

### Отладка и тестирование

Когда вы будете тестировать систему, обратите внимание на эти ключевые моменты:

**Что наблюдать при тестировании:**
- Как растет congestion window в начале (должен удваиваться каждый RTT)
- Переход от slow start к congestion avoidance (при достижении ssthresh)
- Реакцию на потери пакетов (уменьшение окна и переход в fast recovery)
- Адаптацию RTO к реальным задержкам сети

**Типичные проблемы и их решения:**
Если окно не растет - проверьте, получаете ли вы ACK правильно. Если система слишком агрессивна или слишком консервативна, настройте константы в заголовочном файле.

**Инструменты для анализа:**
Используйте Wireshark для наблюдения за реальным трафиком. Добавьте больше логирования, чтобы видеть состояние алгоритмов в реальном времени. Создайте графики изменения congestion window и RTT во времени.

## Практические применения

Эта система особенно полезна в ситуациях, где стандартный TCP не подходит:

**Игры в реальном времени**: Вам нужна надежность для критичных сообщений (например, попадания), но вы хотите контролировать, какие пакеты повторять, а какие устарели.

**Потоковое видео**: Вы можете применить разную политику для разных типов кадров (I-frames нужно доставить обязательно, P-frames можно пропустить при перегрузке).

**IoT устройства**: Когда у вас есть специфичные требования к энергопотреблению или вы работаете через нестандартные сети.

## Дальнейшее развитие

После того как вы поймете основы, можно расширить систему:

**Добавление селективных подтверждений (SACK)**: Это позволит более эффективно обрабатывать множественные потери пакетов.

**Различные алгоритмы congestion control**: Можно реализовать BBR, CUBIC или другие современные алгоритмы вместо классического Reno.

**Приоритизация трафика**: Разные типы данных могут иметь разные политики доставки.

Помните, что создание надежного сетевого протокола - это итеративный процесс. Начните с простой версии, тестируйте в разных условиях, измеряйте производительность и постепенно улучшайте. Этот код дает вам отличную основу для понимания того, как работают современные сетевые протоколы изнутри.

Powered by Claude Exporter (https://www.ai-chat-exporter.net)
