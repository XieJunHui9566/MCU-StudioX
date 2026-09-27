/* StudioX 的 LVGL 8 Win32 宿主；只模拟显示与输入，不访问 MCU 外设。 */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <windowsx.h>
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <inttypes.h>
#include "lvgl.h"

#if defined(PREVIEW_COLOR_DEPTH) && LV_COLOR_DEPTH != PREVIEW_COLOR_DEPTH
#error Preview color depth must agree with the project lv_conf.h
#endif

#ifndef PREVIEW_WIDTH
#define PREVIEW_WIDTH 240
#endif
#ifndef PREVIEW_HEIGHT
#define PREVIEW_HEIGHT 320
#endif
#ifndef PREVIEW_DRAW_ROWS
#ifdef DRAW_ROWS
#define PREVIEW_DRAW_ROWS DRAW_ROWS
#else
#define PREVIEW_DRAW_ROWS 10
#endif
#endif
#ifndef PREVIEW_DRAW_COUNT
#ifdef DRAW_COUNT
#define PREVIEW_DRAW_COUNT DRAW_COUNT
#else
#define PREVIEW_DRAW_COUNT 1
#endif
#endif
#ifndef PREVIEW_ZOOM
#ifdef ZOOM
#define PREVIEW_ZOOM ZOOM
#else
#define PREVIEW_ZOOM 2
#endif
#endif
#ifndef STUDIOX_UI_ENTRY
#error STUDIOX_UI_ENTRY must identify the shared UI function
#endif

extern void STUDIOX_UI_ENTRY(void);
/* Win32 消息、输入回调和 LVGL 定时器均由主线程驱动，避免跨线程调用 LVGL。 */
static HWND preview_window;
static bool running = true;
static uint32_t *framebuffer;
static BITMAPINFO bitmap_info;
static lv_color_t *draw_first;
static lv_color_t *draw_second;
static lv_disp_draw_buf_t draw_buffer;
static lv_disp_drv_t display_driver;
/* LVGL 8 保留驱动指针；三个输入驱动必须独立且覆盖整个会话生命周期。 */
static lv_indev_drv_t pointer_driver;
static lv_indev_drv_t keyboard_driver;
static lv_indev_drv_t encoder_driver;
static lv_indev_t *pointer_device;
static lv_indev_t *keyboard_device;
static lv_indev_t *encoder_device;
static int16_t pointer_x, pointer_y;
static bool pointer_pressed, key_pressed;
static uint32_t key_value;
static int16_t wheel_delta;
static ULONGLONG started_at;
static uint64_t frames, flush_pixels, warning_count, error_count;
static size_t observed_peak;
static char command_buffer[4096];
static size_t command_length;
static bool dropping_command;

uint32_t lv_port_millis(void)
{
    /* 真实单调时钟：渲染再慢也不按循环次数伪造 LVGL 时间。 */
    return (uint32_t)(GetTickCount64() - started_at);
}

static void json_string(const char *value)
{
    fputc('"', stdout);
    for (const unsigned char *p = (const unsigned char *)value; *p; ++p)
    {
        if (*p == '"' || *p == '\\')
        {
            fputc('\\', stdout);
            fputc(*p, stdout);
        }
        else if (*p < 32)
            fprintf(stdout, "\\u%04x", (unsigned)*p);
        else
            fputc(*p, stdout);
    }
    fputc('"', stdout);
}

static void emit_error(const char *message)
{
    ++error_count;
    fputs("{\"version\":1,\"kind\":\"error\",\"message\":", stdout);
    json_string(message);
    fputs("}\n", stdout);
}

void lv_port_panic(void)
{
    emit_error("LVGL assertion failed; inspect the original LVGL log.");
    ExitProcess(3);
}

#if LV_USE_LOG
static void lvgl_log(const char *message)
{
    /* 保留原始诊断在 stderr；stdout 只传版本化遥测。 */
    fputs(message, stderr);
    if (strstr(message, "Warn") || strstr(message, "WARN"))
        ++warning_count;
    if (strstr(message, "Error") || strstr(message, "ERROR"))
        ++error_count;
}
#endif

static void paint_rectangle(RECT *destination)
{
    RECT client;
    GetClientRect(preview_window, &client);
    int width = client.right, height = client.bottom;
    if ((int64_t)width * PREVIEW_HEIGHT > (int64_t)height * PREVIEW_WIDTH)
        width = height * PREVIEW_WIDTH / PREVIEW_HEIGHT;
    else
        height = width * PREVIEW_HEIGHT / PREVIEW_WIDTH;
    destination->left = (client.right - width) / 2;
    destination->top = (client.bottom - height) / 2;
    destination->right = destination->left + width;
    destination->bottom = destination->top + height;
}

static void set_pointer(LPARAM coordinates)
{
    RECT destination;
    paint_rectangle(&destination);
    int width = destination.right - destination.left, height = destination.bottom - destination.top;
    if (width <= 0 || height <= 0)
        return;
    int x = (GET_X_LPARAM(coordinates) - destination.left) * PREVIEW_WIDTH / width;
    int y = (GET_Y_LPARAM(coordinates) - destination.top) * PREVIEW_HEIGHT / height;
    if (x < 0)
        x = 0;
    if (x >= PREVIEW_WIDTH)
        x = PREVIEW_WIDTH - 1;
    if (y < 0)
        y = 0;
    if (y >= PREVIEW_HEIGHT)
        y = PREVIEW_HEIGHT - 1;
    pointer_x = (int16_t)x;
    pointer_y = (int16_t)y;
}

static uint32_t translate_key(WPARAM key)
{
    switch (key)
    {
    case VK_UP:
        return LV_KEY_UP;
    case VK_DOWN:
        return LV_KEY_DOWN;
    case VK_LEFT:
        return LV_KEY_LEFT;
    case VK_RIGHT:
        return LV_KEY_RIGHT;
    case VK_ESCAPE:
        return LV_KEY_ESC;
    case VK_DELETE:
        return LV_KEY_DEL;
    case VK_BACK:
        return LV_KEY_BACKSPACE;
    case VK_RETURN:
        return LV_KEY_ENTER;
    case VK_TAB:
        return (GetKeyState(VK_SHIFT) & 0x8000) ? LV_KEY_PREV : LV_KEY_NEXT;
    case VK_HOME:
        return LV_KEY_HOME;
    case VK_END:
        return LV_KEY_END;
    default:
        return key <= 255 ? (uint32_t)key : 0;
    }
}

static LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
    switch (message)
    {
    case WM_PAINT:
    {
        PAINTSTRUCT paint;
        HDC dc = BeginPaint(window, &paint);
        RECT destination;
        paint_rectangle(&destination);
        FillRect(dc, &paint.rcPaint, (HBRUSH)GetStockObject(BLACK_BRUSH));
        SetStretchBltMode(dc, COLORONCOLOR);
        StretchDIBits(dc, destination.left, destination.top, destination.right - destination.left,
                      destination.bottom - destination.top, 0, 0, PREVIEW_WIDTH, PREVIEW_HEIGHT,
                      framebuffer, &bitmap_info, DIB_RGB_COLORS, SRCCOPY);
        EndPaint(window, &paint);
        return 0;
    }
    case WM_SIZE:
        InvalidateRect(window, NULL, FALSE);
        return 0;
    case WM_LBUTTONDOWN:
        set_pointer(lparam);
        pointer_pressed = true;
        SetCapture(window);
        if (pointer_device)
            lv_indev_read_timer_cb(pointer_device->driver->read_timer);
        return 0;
    case WM_LBUTTONUP:
        set_pointer(lparam);
        pointer_pressed = false;
        ReleaseCapture();
        if (pointer_device)
            lv_indev_read_timer_cb(pointer_device->driver->read_timer);
        return 0;
    case WM_MOUSEMOVE:
        set_pointer(lparam);
        return 0;
    case WM_CAPTURECHANGED:
        pointer_pressed = false;
        return 0;
    case WM_KILLFOCUS:
        pointer_pressed = false;
        key_pressed = false;
        return 0;
    case WM_MOUSEWHEEL:
        wheel_delta = (int16_t)(wheel_delta - GET_WHEEL_DELTA_WPARAM(wparam) / WHEEL_DELTA);
        return 0;
    case WM_KEYDOWN:
        key_value = translate_key(wparam);
        key_pressed = key_value != 0;
        return 0;
    case WM_KEYUP:
        key_pressed = false;
        return 0;
    case WM_CLOSE:
        running = false;
        return 0;
    }
    return DefWindowProcW(window, message, wparam, lparam);
}

static void flush_display(lv_disp_drv_t *driver, const lv_area_t *area, lv_color_t *pixels)
{
    /* 保留项目分块缓冲；完整 BGRA 展示面是 PC 宿主开销，不占 LVGL 池。 */
    for (int y = area->y1; y <= area->y2; ++y)
    {
        for (int x = area->x1; x <= area->x2; ++x, ++pixels)
        {
            if (x >= 0 && x < PREVIEW_WIDTH && y >= 0 && y < PREVIEW_HEIGHT)
                framebuffer[(size_t)y * PREVIEW_WIDTH + x] = lv_color_to32(*pixels) & 0x00ffffffu;
        }
    }
    flush_pixels += (uint64_t)lv_area_get_size(area);
    if (lv_disp_flush_is_last(driver))
    {
        ++frames;
        InvalidateRect(preview_window, NULL, FALSE);
    }
    lv_disp_flush_ready(driver);
}

static void read_pointer(lv_indev_drv_t *driver, lv_indev_data_t *data)
{
    (void)driver;
    data->point.x = pointer_x;
    data->point.y = pointer_y;
    data->state = pointer_pressed ? LV_INDEV_STATE_PR : LV_INDEV_STATE_REL;
}

static void read_keyboard(lv_indev_drv_t *driver, lv_indev_data_t *data)
{
    (void)driver;
    data->key = key_value;
    data->state = key_pressed ? LV_INDEV_STATE_PR : LV_INDEV_STATE_REL;
}

static void read_encoder(lv_indev_drv_t *driver, lv_indev_data_t *data)
{
    (void)driver;
    data->enc_diff = wheel_delta;
    wheel_delta = 0;
    data->state = LV_INDEV_STATE_REL;
}

static bool save_frame(void)
{
    /* 固定输出名，IPC 不接受任意文件路径；临时文件完成后才原子替换。 */
    const uint32_t bytes = PREVIEW_WIDTH * PREVIEW_HEIGHT * 4u;
    BITMAPFILEHEADER header;
    memset(&header, 0, sizeof(header));
    header.bfType = 0x4d42;
    header.bfOffBits = sizeof(header) + sizeof(BITMAPINFOHEADER);
    header.bfSize = header.bfOffBits + bytes;
    FILE *file = fopen("frame.tmp", "wb");
    if (!file)
        return false;
    bool ok = fwrite(&header, sizeof(header), 1, file) == 1 &&
              fwrite(&bitmap_info.bmiHeader, sizeof(BITMAPINFOHEADER), 1, file) == 1 &&
              fwrite(framebuffer, bytes, 1, file) == 1;
    if (fclose(file) != 0)
        ok = false;
    if (!ok)
    {
        remove("frame.tmp");
        return false;
    }
    return MoveFileExW(L"frame.tmp", L"frame.bmp",
                       MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH) != 0;
}

/* 命令只包含固定协议的短 ASCII 字段；拒绝转义、长字段和缺失数值。 */
static const char *field_value(const char *json, const char *field)
{
    char needle[80];
    snprintf(needle, sizeof(needle), "\"%s\"", field);
    const char *search = json;
    const char *found;
    while ((found = strstr(search, needle)) != NULL)
    {
        search = found + strlen(needle);
        const char *value = search;
        while (*value == ' ' || *value == '\t')
            ++value;
        /* type 的字符串值也可能为 "key"；字段名必须紧跟冒号。 */
        if (*value != ':')
            continue;
        ++value;
        while (*value == ' ' || *value == '\t')
            ++value;
        return value;
    }
    return NULL;
}

static bool read_string(const char *json, const char *field, char *value, size_t capacity)
{
    const char *p = field_value(json, field);
    if (!p || *p++ != '"')
        return false;
    size_t count = 0;
    while (*p && *p != '"')
    {
        if ((unsigned char)*p < 32 || *p == '\\' || count + 1 >= capacity)
            return false;
        value[count++] = *p++;
    }
    value[count] = 0;
    return *p == '"';
}

static bool read_number(const char *json, const char *field, long *value)
{
    const char *p = field_value(json, field);
    if (!p || (*p != '-' && (*p < '0' || *p > '9')))
        return false;
    char *end;
    long result = strtol(p, &end, 10);
    if (*end != ',' && *end != '}' && *end != ' ' && *end != '\t')
        return false;
    if (result < -32768 || result > 32767)
        return false;
    *value = result;
    return true;
}

static bool read_pressed(const char *json, bool *value)
{
    const char *p = field_value(json, "pressed");
    if (!p)
        return false;
    if (strncmp(p, "true", 4) == 0)
    {
        *value = true;
        return true;
    }
    if (strncmp(p, "false", 5) == 0)
    {
        *value = false;
        return true;
    }
    return false;
}

static void handle_command(const char *json)
{
    char command[32];
    if (!read_string(json, "command", command, sizeof(command)))
    {
        emit_error("Invalid command.");
        return;
    }
    if (strcmp(command, "stop") == 0)
    {
        running = false;
        return;
    }
    if (strcmp(command, "screenshot") == 0)
    {
        char request[65];
        if (!read_string(json, "requestId", request, sizeof(request)))
        {
            emit_error("Invalid capture request.");
            return;
        }
        for (const char *p = request; *p; ++p)
            if (!((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') ||
                  (*p >= '0' && *p <= '9') || *p == '_' || *p == '-'))
            {
                emit_error("Invalid capture request identifier.");
                return;
            }
        if (!save_frame())
        {
            emit_error("Unable to write preview screenshot.");
            return;
        }
        fputs("{\"version\":1,\"kind\":\"screenshot\",\"requestId\":", stdout);
        json_string(request);
        fputs(",\"file\":\"frame.bmp\"}\n", stdout);
        return;
    }
    if (strcmp(command, "input") != 0)
    {
        emit_error("Unknown command.");
        return;
    }
    char type[20];
    if (!read_string(json, "type", type, sizeof(type)))
    {
        emit_error("Invalid input type.");
        return;
    }
    long x, y, number;
    bool pressed;
    if (strcmp(type, "pointer") == 0)
    {
        if (!read_number(json, "x", &x) || !read_number(json, "y", &y) ||
            !read_pressed(json, &pressed) || x < 0 || x >= PREVIEW_WIDTH || y < 0 ||
            y >= PREVIEW_HEIGHT)
        {
            emit_error("Pointer out of range.");
            return;
        }
        pointer_x = (int16_t)x;
        pointer_y = (int16_t)y;
        pointer_pressed = pressed;
        /* 单线程立即消费每个事件，快速 press/release 不会在一次 IPC 读取中被覆盖。 */
        lv_indev_read_timer_cb(pointer_device->driver->read_timer);
    }
    else if (strcmp(type, "wheel") == 0)
    {
        if (!read_number(json, "delta", &number) || number < -100 || number > 100)
        {
            emit_error("Invalid wheel delta.");
            return;
        }
        wheel_delta = (int16_t)number;
        if (encoder_device)
            lv_indev_read_timer_cb(encoder_device->driver->read_timer);
    }
    else if (strcmp(type, "key") == 0)
    {
        if (!read_number(json, "key", &number) || number < 1 || number > 255 ||
            !read_pressed(json, &pressed))
        {
            emit_error("Invalid LVGL key.");
            return;
        }
        key_value = (uint32_t)number;
        key_pressed = pressed;
        if (keyboard_device)
            lv_indev_read_timer_cb(keyboard_device->driver->read_timer);
    }
    else
        emit_error("Unknown input type.");
}

static void poll_commands(void)
{
    HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    DWORD available = 0;
    if (input == INVALID_HANDLE_VALUE)
        return;
    if (!PeekNamedPipe(input, NULL, 0, NULL, &available, NULL))
    {
        if (GetLastError() == ERROR_BROKEN_PIPE)
            running = false;
        return;
    }
    /* 限制每次处理字节，连续输入不能饿死窗口消息与 LVGL。 */
    if (available > 8192)
        available = 8192;
    while (available > 0)
    {
        char bytes[1024];
        DWORD got;
        DWORD wanted = available > sizeof(bytes) ? (DWORD)sizeof(bytes) : available;
        if (!ReadFile(input, bytes, wanted, &got, NULL) || !got)
            break;
        available -= got;
        for (DWORD i = 0; i < got; ++i)
        {
            if (bytes[i] == '\n')
            {
                if (!dropping_command)
                {
                    command_buffer[command_length] = 0;
                    handle_command(command_buffer);
                }
                command_length = 0;
                dropping_command = false;
            }
            else if (bytes[i] != '\r' && !dropping_command)
            {
                if (command_length + 1 < sizeof(command_buffer))
                    command_buffer[command_length++] = bytes[i];
                else
                {
                    dropping_command = true;
                    emit_error("Command exceeds protocol limit.");
                }
            }
        }
    }
}

static void heap_sample(lv_mem_monitor_t *monitor)
{
    memset(monitor, 0, sizeof(*monitor));
    /* 自定义分配器的实际内存未知；保留 heapAvailable=false，不把清零值当作真实用量。 */
#if !LV_MEM_CUSTOM
    lv_mem_monitor(monitor);
    size_t used = monitor->total_size - monitor->free_size;
    if (used > observed_peak)
        observed_peak = used;
#endif
}

static void emit_stats(uint32_t elapsed, uint64_t previous_frames, uint32_t interval,
                       const lv_mem_monitor_t *memory)
{
    /* 8.3 realloc 的 max_used 不完整，因此报告 pool walk 得到的观测峰值。 */
    fprintf(stdout,
            "{\"version\":1,\"kind\":\"stats\",\"uptimeMs\":%" PRIu32 ",\"frames\":%" PRIu64
            ",\"fps\":%.2f,\"flushPixels\":%" PRIu64
            ",\"heapUsedBytes\":%zu,\"heapFreeBytes\":%zu,\"heapLargestFreeBytes\":%zu"
            ",\"heapObservedPeakBytes\":%zu,\"heapFragmentationPercent\":%u"
            ",\"warningCount\":%" PRIu64 ",\"errorCount\":%" PRIu64 ",\"heapAvailable\":%s}\n",
            elapsed, frames,
            interval ? (double)(frames - previous_frames) * 1000.0 / interval : 0.0, flush_pixels,
            (size_t)(memory->total_size - memory->free_size), (size_t)memory->free_size,
            (size_t)memory->free_biggest_size, observed_peak, (unsigned)memory->frag_pct,
            warning_count, error_count, LV_MEM_CUSTOM ? "false" : "true");
}

int main(void)
{
    /* 标准输出承载逐行 JSON；禁用缓冲，让界面及时收到日志、统计和错误。 */
    setvbuf(stdout, NULL, _IONBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    started_at = GetTickCount64();
    if (PREVIEW_WIDTH < 1 || PREVIEW_WIDTH > 4096 || PREVIEW_HEIGHT < 1 || PREVIEW_HEIGHT > 4096 ||
        PREVIEW_DRAW_ROWS < 1 || PREVIEW_DRAW_ROWS > PREVIEW_HEIGHT ||
        (PREVIEW_DRAW_COUNT != 1 && PREVIEW_DRAW_COUNT != 2))
    {
        emit_error("Invalid preview display configuration.");
        return 2;
    }
    framebuffer = calloc((size_t)PREVIEW_WIDTH * PREVIEW_HEIGHT, sizeof(uint32_t));
    draw_first = calloc((size_t)PREVIEW_WIDTH * PREVIEW_DRAW_ROWS, sizeof(lv_color_t));
    if (PREVIEW_DRAW_COUNT == 2)
        draw_second = calloc((size_t)PREVIEW_WIDTH * PREVIEW_DRAW_ROWS, sizeof(lv_color_t));
    if (!framebuffer || !draw_first || (PREVIEW_DRAW_COUNT == 2 && !draw_second))
    {
        emit_error("PC display buffer allocation failed.");
        return 2;
    }
    memset(&bitmap_info, 0, sizeof(bitmap_info));
    bitmap_info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bitmap_info.bmiHeader.biWidth = PREVIEW_WIDTH;
    bitmap_info.bmiHeader.biHeight = -PREVIEW_HEIGHT;
    bitmap_info.bmiHeader.biPlanes = 1;
    bitmap_info.bmiHeader.biBitCount = 32;
    bitmap_info.bmiHeader.biCompression = BI_RGB;
    WNDCLASSW window_class;
    memset(&window_class, 0, sizeof(window_class));
    window_class.lpfnWndProc = window_proc;
    window_class.hInstance = GetModuleHandleW(NULL);
    window_class.hCursor = LoadCursor(NULL, IDC_ARROW);
    window_class.lpszClassName = L"StudioX_LVGL_Preview_v1";
    if (!RegisterClassW(&window_class))
    {
        emit_error("Win32 window class registration failed.");
        return 2;
    }
    RECT bounds = {0, 0, PREVIEW_WIDTH * PREVIEW_ZOOM, PREVIEW_HEIGHT * PREVIEW_ZOOM};
    AdjustWindowRect(&bounds, WS_OVERLAPPEDWINDOW, FALSE);
    preview_window =
        CreateWindowW(window_class.lpszClassName, L"MCU StudioX - LVGL PC Preview",
                      WS_OVERLAPPEDWINDOW, CW_USEDEFAULT, CW_USEDEFAULT, bounds.right - bounds.left,
                      bounds.bottom - bounds.top, NULL, NULL, window_class.hInstance, NULL);
    if (!preview_window)
    {
        emit_error("Win32 preview window creation failed.");
        return 2;
    }
    lv_init();
#if LV_USE_LOG
    lv_log_register_print_cb(lvgl_log);
#endif
    lv_disp_draw_buf_init(&draw_buffer, draw_first, draw_second, PREVIEW_WIDTH * PREVIEW_DRAW_ROWS);
    lv_disp_drv_init(&display_driver);
    display_driver.hor_res = PREVIEW_WIDTH;
    display_driver.ver_res = PREVIEW_HEIGHT;
    display_driver.draw_buf = &draw_buffer;
    display_driver.flush_cb = flush_display;
#if LV_COLOR_SCREEN_TRANSP
    display_driver.screen_transp = 0;
#endif
    if (!lv_disp_drv_register(&display_driver))
    {
        emit_error("LVGL display registration failed.");
        return 2;
    }
    lv_group_t *group = lv_group_create();
    lv_group_set_default(group);
    lv_indev_drv_init(&pointer_driver);
    pointer_driver.type = LV_INDEV_TYPE_POINTER;
    pointer_driver.read_cb = read_pointer;
    pointer_device = lv_indev_drv_register(&pointer_driver);
    if (!pointer_device)
    {
        emit_error("LVGL pointer registration failed.");
        return 2;
    }
    lv_indev_drv_init(&keyboard_driver);
    keyboard_driver.type = LV_INDEV_TYPE_KEYPAD;
    keyboard_driver.read_cb = read_keyboard;
    keyboard_device = lv_indev_drv_register(&keyboard_driver);
    if (keyboard_device)
        lv_indev_set_group(keyboard_device, group);
    lv_indev_drv_init(&encoder_driver);
    encoder_driver.type = LV_INDEV_TYPE_ENCODER;
    encoder_driver.read_cb = read_encoder;
    encoder_device = lv_indev_drv_register(&encoder_driver);
    if (encoder_device)
        lv_indev_set_group(encoder_device, group);
    ShowWindow(preview_window, SW_SHOWNORMAL);
    STUDIOX_UI_ENTRY();
    fprintf(stdout,
            "{\"version\":1,\"kind\":\"ready\",\"width\":%d,\"height\":%d,\"colorDepth\":%d,"
            "\"drawBufferBytes\":%zu,\"pointerBits\":%zu,\"lvglVersion\":\"%d.%d.%d\"}\n",
            PREVIEW_WIDTH, PREVIEW_HEIGHT, LV_COLOR_DEPTH,
            (size_t)PREVIEW_WIDTH * PREVIEW_DRAW_ROWS * PREVIEW_DRAW_COUNT * sizeof(lv_color_t),
            sizeof(void *) * 8u, LVGL_VERSION_MAJOR, LVGL_VERSION_MINOR, LVGL_VERSION_PATCH);
    uint32_t last_stats = 0, last_sample = 0;
#if !LV_TICK_CUSTOM
    uint32_t last_tick = 0;
#endif
    uint64_t previous_frames = 0;
    lv_mem_monitor_t memory;
    heap_sample(&memory);
    while (running)
    {
        MSG message;
        while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_QUIT)
            {
                running = false;
                break;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        poll_commands();
        if (!running)
            break;
#if !LV_TICK_CUSTOM
        uint32_t tick_now = lv_port_millis();
        lv_tick_inc(tick_now - last_tick);
        last_tick = tick_now;
#endif
        uint32_t delay = lv_timer_handler();
        uint32_t now = lv_port_millis();
        if (now - last_sample >= 200)
        {
            heap_sample(&memory);
            last_sample = now;
        }
        if (now - last_stats >= 1000)
        {
            emit_stats(now, previous_frames, now - last_stats, &memory);
            previous_frames = frames;
            last_stats = now;
        }
        if (delay < 1)
            delay = 1;
        if (delay > 5)
            delay = 5;
        Sleep(delay);
    }
    fputs("{\"version\":1,\"kind\":\"stopped\"}\n", stdout);
    DestroyWindow(preview_window);
    free(draw_second);
    free(draw_first);
    free(framebuffer);
    return 0;
}
