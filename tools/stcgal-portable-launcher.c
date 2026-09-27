/* 内置隔离 Python 的 Windows 启动入口。
 * 从程序自身位置解析运行时，避免写入包作者机器上的 Python 绝对路径。
 */
#include <windows.h>
#include <stdio.h>
#include <wchar.h>

static int append(wchar_t **cursor, wchar_t *end, wchar_t value)
{
    if (*cursor >= end)
        return 0;
    *(*cursor)++ = value;
    return 1;
}

/* 按 Windows 命令行规则逐个引用参数，保留空参数、引号与末尾反斜杠。 */
static int quote_argument(wchar_t **cursor, wchar_t *end, const wchar_t *argument)
{
    size_t backslashes;
    if (!append(cursor, end, L'"'))
        return 0;
    while (*argument != L'\0')
    {
        backslashes = 0;
        while (*argument == L'\\')
        {
            ++backslashes;
            ++argument;
        }
        if (*argument == L'"')
        {
            while (backslashes-- > 0)
                if (!append(cursor, end, L'\\') || !append(cursor, end, L'\\'))
                    return 0;
            if (!append(cursor, end, L'\\') || !append(cursor, end, L'"'))
                return 0;
            ++argument;
        }
        else if (*argument == L'\0')
        {
            while (backslashes-- > 0)
                if (!append(cursor, end, L'\\') || !append(cursor, end, L'\\'))
                    return 0;
        }
        else
        {
            while (backslashes-- > 0)
                if (!append(cursor, end, L'\\'))
                    return 0;
            if (!append(cursor, end, *argument++))
                return 0;
        }
    }
    return append(cursor, end, L'"');
}

int wmain(int argc, wchar_t **argv)
{
    wchar_t path[32768];
    wchar_t python[32768];
    wchar_t command[32768] = L"python.exe -B -m stcgal";
    const wchar_t suffix[] = L"\\..\\python.exe";
    wchar_t *separator;
    wchar_t *cursor = command + wcslen(command);
    wchar_t *end = command + 32767;
    STARTUPINFOW startup = {0};
    PROCESS_INFORMATION process = {0};
    DWORD status = 127;
    DWORD length = GetModuleFileNameW(NULL, path, 32768);
    int i;

    if (length == 0 || length >= 32768)
    {
        fputws(L"Cannot locate stcgal launcher.\n", stderr);
        return 127;
    }
    separator = wcsrchr(path, L'\\');
    if (separator == NULL ||
        (size_t)(separator - path) + (sizeof(suffix) / sizeof(suffix[0])) >= 32768)
    {
        fputws(L"Cannot locate bundled Python.\n", stderr);
        return 127;
    }
    separator[0] = L'\0';
    wcscat(path, suffix);
    if (_wfullpath(python, path, 32768) == NULL ||
        GetFileAttributesW(python) == INVALID_FILE_ATTRIBUTES)
    {
        fputws(L"Bundled Python is missing.\n", stderr);
        return 127;
    }

    for (i = 1; i < argc; ++i)
    {
        if (!append(&cursor, end, L' ') || !quote_argument(&cursor, end, argv[i]))
        {
            fputws(L"stcgal command line is too long.\n", stderr);
            return 127;
        }
    }
    *cursor = L'\0';
    startup.cb = sizeof(startup);
    if (!CreateProcessW(python, command, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &process))
    {
        fwprintf(stderr, L"Could not start bundled Python (Windows error %lu).\n", GetLastError());
        return 127;
    }
    WaitForSingleObject(process.hProcess, INFINITE);
    GetExitCodeProcess(process.hProcess, &status);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    return (int)status;
}
