# 工程目录与新增源文件

## 常见目录
- 根 `CMakeLists.txt`：用户维护的应用源码、包含目录、宏和库关系。
- `src/`：常见 C/C++ 应用目录；实际入口由模板决定。
- `device/`：创建时复制的器件支持和包清单。部分内部生成配置在编辑器中只读。
- `.studiox/`：工程身份、工具锁和编译等设置。
- `.build/`：缓存、日志与本次构建产物，不是应用源码目录。

## 添加 C/C++ 源码
在工程内创建文件只是第一步，还要把它加入相应 CMake 目标。例如普通固件模板可在根配置中维护：
```cmake
target_sources(firmware PRIVATE
    src/main.c
    src/uart.c
)
target_include_directories(firmware PRIVATE src include)
target_compile_definitions(firmware PRIVATE USE_UART=1)
```
`firmware` 只是普通模板中的示例目标名；自定义工程应使用自身真实目标。头文件搜索路径加入包含目录，不把头文件内容复制到每个 C 文件中。

## ESP-IDF 的区别
应用通常位于 `main/`，组件通过组件自己的 `CMakeLists.txt` 注册。新增源文件时维护组件的 `idf_component_register(SRCS ... INCLUDE_DIRS ...)`。不要把普通模板的 `firmware` 目标规则直接套到 ESP-IDF 上。

## 什么地方不建议改
不要把应用写进共享 SDK、工具安装目录或 `.build` 的生成文件。内部器件平台配置用于固定启动、CPU/ABI 和链接关系；修改应用依赖应从用户维护的配置入口完成。看到“只读”提示时先检查说明，不通过解除只读来绕开配置边界。

## 验证是否加入成功
保存源码和 CMake 后按 `F7`。若新函数出现 `undefined reference`，先确认文件是否被编译、声明与定义是否一致，以及所需库是否被链接。
