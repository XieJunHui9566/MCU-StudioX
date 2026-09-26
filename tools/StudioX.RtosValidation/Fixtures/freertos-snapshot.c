/*
 * Offline DWARF fixture: initialized data only; this is not executable firmware.
 * Field definitions follow FreeRTOS-Kernel V10.4.6 tasks.c, queue.c, list.h and
 * portable/MemMang/heap_4.c. No kernel source or user firmware is modified.
 * Sources: https://github.com/FreeRTOS/FreeRTOS-Kernel/tree/V10.4.6
 * FreeRTOS field declarations are covered by the upstream MIT license.
 */
#include <stdint.h>
#include <stddef.h>

typedef uint32_t UBaseType_t;
typedef uint32_t TickType_t;
typedef uint32_t StackType_t;
struct xLIST;
typedef struct xLIST_ITEM {
    TickType_t xItemValue;
    struct xLIST_ITEM *pxNext;
    struct xLIST_ITEM *pxPrevious;
    void *pvOwner;
    struct xLIST *pxContainer;
} ListItem_t;
typedef struct xMINI_LIST_ITEM {
    TickType_t xItemValue;
    ListItem_t *pxNext;
    ListItem_t *pxPrevious;
} MiniListItem_t;
typedef struct xLIST {
    volatile UBaseType_t uxNumberOfItems;
    ListItem_t *pxIndex;
    MiniListItem_t xListEnd;
} List_t;
typedef struct tskTaskControlBlock {
    volatile StackType_t *pxTopOfStack;
    ListItem_t xStateListItem;
    ListItem_t xEventListItem;
    UBaseType_t uxPriority;
    StackType_t *pxStack;
    char pcTaskName[16];
#ifndef FIXTURE_MINIMAL
    StackType_t *pxEndOfStack;
    UBaseType_t uxTCBNumber;
    UBaseType_t uxTaskNumber;
    UBaseType_t uxBasePriority;
    UBaseType_t uxMutexesHeld;
    uint32_t ulRunTimeCounter;
#endif
} TCB_t;
typedef TCB_t *TaskHandle_t;
typedef struct QueuePointers {
    int8_t *pcTail;
    int8_t *pcReadFrom;
} QueuePointers_t;
typedef struct SemaphoreData {
    TaskHandle_t xMutexHolder;
    UBaseType_t uxRecursiveCallCount;
} SemaphoreData_t;
typedef struct QueueDefinition {
    int8_t *pcHead;
    int8_t *pcWriteTo;
    union { QueuePointers_t xQueue; SemaphoreData_t xSemaphore; } u;
    List_t xTasksWaitingToSend;
    List_t xTasksWaitingToReceive;
    volatile UBaseType_t uxMessagesWaiting;
    UBaseType_t uxLength;
    UBaseType_t uxItemSize;
    volatile int8_t cRxLock;
    volatile int8_t cTxLock;
#ifndef FIXTURE_MINIMAL
    UBaseType_t uxQueueNumber;
    uint8_t ucQueueType;
#endif
} Queue_t;
typedef Queue_t *QueueHandle_t;
typedef struct QUEUE_REGISTRY_ITEM {
    const char *pcQueueName;
    QueueHandle_t xHandle;
} QueueRegistryItem_t;
typedef struct A_BLOCK_LINK {
    struct A_BLOCK_LINK *pxNextFreeBlock;
    size_t xBlockSize;
} BlockLink_t;

#define SENTINEL(list) ((ListItem_t *)&(list).xListEnd)
#define EMPTY_LIST(list) { 0, SENTINEL(list), { UINT32_MAX, SENTINEL(list), SENTINEL(list) } }
#define ONE_LIST(list, item) { 1, SENTINEL(list), { UINT32_MAX, &(item), &(item) } }
#define TASK_ITEM(task, list) { 0, SENTINEL(list), SENTINEL(list), &(task), &(list) }
#ifndef FIXTURE_MINIMAL
#define EXTRA_TASK_FIELDS(stack, number, priority, runtime) \
    .pxEndOfStack = &(stack)[63], .uxTCBNumber = number, .uxTaskNumber = number, \
    .uxBasePriority = priority, .uxMutexesHeld = 0, .ulRunTimeCounter = runtime,
#define QUEUE_TYPE(type) .ucQueueType = type,
#else
#define EXTRA_TASK_FIELDS(stack, number, priority, runtime)
#define QUEUE_TYPE(type)
#endif

StackType_t stack_a[64] = { [0 ... 63] = 0xa5a5a5a5U, [16] = 0 };
StackType_t stack_b[64] = { [0 ... 63] = 0xa5a5a5a5U, [24] = 0 };
StackType_t stack_c[64] = { [0 ... 63] = 0xa5a5a5a5U, [8] = 0 };
StackType_t stack_d[64] = { [0 ... 63] = 0xa5a5a5a5U, [4] = 0 };
StackType_t stack_idle[64] = { [0 ... 63] = 0xa5a5a5a5U, [32] = 0 };

extern List_t pxReadyTasksLists[5], xDelayedTaskList1, xDelayedTaskList2;
extern List_t xSuspendedTaskList, xTasksWaitingTermination, xPendingReadyList;
extern TCB_t task_a, task_b, task_c, task_d, task_idle;
extern Queue_t messages, mutex, binary;

TCB_t task_a = {
    .pxTopOfStack = &stack_a[56], .xStateListItem = TASK_ITEM(task_a, pxReadyTasksLists[2]),
    .uxPriority = 2, .pxStack = stack_a, .pcTaskName = "Sensor",
    EXTRA_TASK_FIELDS(stack_a, 1, 1, 100)
};
TCB_t task_b = {
    .pxTopOfStack = &stack_b[48], .xStateListItem = TASK_ITEM(task_b, xDelayedTaskList1),
    .xEventListItem = TASK_ITEM(task_b, messages.xTasksWaitingToReceive),
    .uxPriority = 1, .pxStack = stack_b, .pcTaskName = "Receiver",
    EXTRA_TASK_FIELDS(stack_b, 2, 1, 50)
};
TCB_t task_c = {
    .pxTopOfStack = &stack_c[56], .xStateListItem = TASK_ITEM(task_c, xSuspendedTaskList),
    .uxPriority = 3, .pxStack = stack_c, .pcTaskName = "Maintenance",
    EXTRA_TASK_FIELDS(stack_c, 3, 3, 25)
};
TCB_t task_d = {
    .pxTopOfStack = &stack_d[52], .xStateListItem = TASK_ITEM(task_d, xTasksWaitingTermination),
    .uxPriority = 0, .pxStack = stack_d, .pcTaskName = "Retired",
    EXTRA_TASK_FIELDS(stack_d, 4, 0, 0)
};
TCB_t task_idle = {
    .pxTopOfStack = &stack_idle[40], .xStateListItem = TASK_ITEM(task_idle, pxReadyTasksLists[0]),
    .uxPriority = 0, .pxStack = stack_idle, .pcTaskName = "IDLE",
    EXTRA_TASK_FIELDS(stack_idle, 5, 0, 10)
};

List_t pxReadyTasksLists[5] = {
    ONE_LIST(pxReadyTasksLists[0], task_idle.xStateListItem),
    EMPTY_LIST(pxReadyTasksLists[1]),
    ONE_LIST(pxReadyTasksLists[2], task_a.xStateListItem),
    EMPTY_LIST(pxReadyTasksLists[3]), EMPTY_LIST(pxReadyTasksLists[4])
};
List_t xDelayedTaskList1 = ONE_LIST(xDelayedTaskList1, task_b.xStateListItem);
List_t xDelayedTaskList2 = EMPTY_LIST(xDelayedTaskList2);
List_t xSuspendedTaskList = ONE_LIST(xSuspendedTaskList, task_c.xStateListItem);
List_t xTasksWaitingTermination = ONE_LIST(xTasksWaitingTermination, task_d.xStateListItem);
List_t xPendingReadyList = EMPTY_LIST(xPendingReadyList);
List_t *pxDelayedTaskList = &xDelayedTaskList1;
List_t *pxOverflowDelayedTaskList = &xDelayedTaskList2;
TCB_t *volatile pxCurrentTCB = &task_a;
volatile UBaseType_t uxCurrentNumberOfTasks = 5;
volatile UBaseType_t uxDeletedTasksWaitingCleanUp = 1;
volatile uint32_t xSchedulerRunning = 1;
volatile UBaseType_t uxSchedulerSuspended = 0;
volatile TickType_t xTickCount = 12345;
const char tskKERNEL_VERSION_NUMBER[] = "V10.4.6";

int8_t message_storage[32] = { 1 };
Queue_t messages = {
    .pcHead = message_storage, .pcWriteTo = &message_storage[12],
    .u.xQueue = { &message_storage[32], &message_storage[8] },
    .xTasksWaitingToSend = EMPTY_LIST(messages.xTasksWaitingToSend),
    .xTasksWaitingToReceive = ONE_LIST(messages.xTasksWaitingToReceive, task_b.xEventListItem),
    .uxMessagesWaiting = 3, .uxLength = 8, .uxItemSize = 4,
    .cRxLock = -1, .cTxLock = -1, QUEUE_TYPE(0)
};
Queue_t mutex = {
    .pcHead = NULL, .u.xSemaphore = { &task_a, 2 },
    .xTasksWaitingToSend = EMPTY_LIST(mutex.xTasksWaitingToSend),
    .xTasksWaitingToReceive = EMPTY_LIST(mutex.xTasksWaitingToReceive),
    .uxMessagesWaiting = 0, .uxLength = 1, .uxItemSize = 0,
    .cRxLock = -1, .cTxLock = -1, QUEUE_TYPE(4)
};
Queue_t binary = {
    .pcHead = (int8_t *)&binary, .uxMessagesWaiting = 1, .uxLength = 1, .uxItemSize = 0,
    .xTasksWaitingToSend = EMPTY_LIST(binary.xTasksWaitingToSend),
    .xTasksWaitingToReceive = EMPTY_LIST(binary.xTasksWaitingToReceive),
    .cRxLock = -1, .cTxLock = -1, QUEUE_TYPE(3)
};
QueueRegistryItem_t xQueueRegistry[4] = {
    { "Messages", &messages }, { "SPI lock", &mutex }, { "Wakeup", &binary }, { NULL, NULL }
};
QueueHandle_t externalQueue = &messages;
QueueHandle_t externalMutex = &mutex;

/* 块链接真实落在同一 4096 字节区域内；保留 uint8_t 总区间而不依赖目标布局偏移。 */
union HeapFixture {
    uint8_t bytes[4096];
    struct {
        BlockLink_t first;
        uint8_t pad1[1024 - sizeof(BlockLink_t)];
        BlockLink_t second;
        uint8_t pad2[4096 - 1024 - 2 * sizeof(BlockLink_t)];
        BlockLink_t end;
    } links;
};
extern union HeapFixture ucHeap;
union HeapFixture ucHeap = { .links = {
    .first = { &ucHeap.links.second, 1024 },
    .second = { &ucHeap.links.end, 1280 },
    .end = { NULL, 0 }
} };
BlockLink_t xStart = { &ucHeap.links.first, 0 };
BlockLink_t *pxEnd = &ucHeap.links.end;
size_t xFreeBytesRemaining = 2304;
size_t xMinimumEverFreeBytesRemaining = 1024;
#ifndef FIXTURE_MINIMAL
size_t xNumberOfSuccessfulAllocations = 9;
size_t xNumberOfSuccessfulFrees = 3;
#endif
size_t xBlockAllocatedBit = 0x80000000U;
const size_t xHeapStructSize = sizeof(BlockLink_t);

void _start(void) { /* 仅提供 ELF 入口；验证永远不会执行此函数。 */ }
