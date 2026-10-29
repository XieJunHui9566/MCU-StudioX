STC251 器件包

独立包标识 stc.stc251，厂商 STC。与常规8位STC器件包分开。
本版器件：AI8051U-34K64-32Bit（STC80251）。
内部物理器件ID仍为AI8051U-34K64；显示名称明确区分32位模式，未改变型号或存储容量。
编译器：MX251 R89，studiox.mx251/0.1.89；不包含Keil C251。
构建链固定为MX251 + CMake + Ninja；另需studiox.cmake/4.4.0组件，由IDE按工程锁定。
CMakeLists.txt管理target_sources、target_include_directories及target_compile_definitions。
mx251.json仅保留目标、内存模型、ABI、运行库和IRC频率，不重复登记源文件。
新增C文件可使用源码登记面板；include目录已加入搜索路径，可直接包含AI8051U_Delay.h。
成功固件固定为.build/firmware.hex；构建文件只保留.build/mx251/latest，失败不保留旧固件入口。

目前只提供“寄存器开发”模板，不含HAL、SPL或RTOS。
默认small；在工程详情的编译参数中选择small或large，保存后下次编译生效。
内存模型不是芯片位数，两个模型均为32位模式。
main.c关闭总中断，初始化P2.0为高阻输入，读取电平并更新内存心跳。
输入悬空时电平不确定；它不是LED闪烁演示，接线和上拉由具体电路决定。
AI8051U.h覆盖116个SFR名称、112个位定义和676个XFR名称（包含同址别名）。
覆盖GPIO、时钟/复位、UART1-4/USART、Timer0-4/11、ADC、比较器、SPI/QSPI、I2C/I2S、USB、RTC、PWMA/B、高速外设、DMA、LCM、IAP及芯片身份区。
另提供258个STC命名掩码、29个USB内部寄存器偏移、56个中断号及119个MDU32/TFPU/DPU32命令值。
完整范围以register-reference.json中的名称/地址清单为准；这是寄存器声明，不包含HAL、自动初始化或全部外设驱动。
依据STC ISP附带AI8051U.H的寄存器事实及2026-10-09手册PDF第699-719页等，来源哈希见provenance.json。
总表X32KCR地址7FFE08H与专章冲突，按PDF第586页及STC头文件使用0x7EFE08。
手册RTCWEEK/RTCYEAR等名称与STC的WEEK/YEAR等名称均提供。S2ADDR名称去除总表误加句点。
厂商参考头文件内涉及AUXR2/P_SW3保留位的CAN等8个掩码未开放，明细见register-reference.json。
仅在STC头文件中出现、未在寄存器总表列出的名称已在清单中注明来源；不能据声明推定引脚或外设在该封装可用。

使用注意：
1. 本头文件仅供MX251的mcs251/32位源代码模式，不允许用于8位mcs51。
2. 使用XFR前执行EAXSFR()或P_SW2 |= EAXFR。EAXFR是0x80掩码，不能写EAXFR = 1。
3. XFR用完整0x7E段地址和8位volatile访问；高低字节按手册顺序操作，不提供可能改变访问顺序的宽指针别名。
4. CHIPID/CHIPIDX为const volatile只读。USB内部寄存器名是USBADR/USBDAT访问偏移，不是C左值。
5. DMAIR数学指令要求立即数MOV，并且使用CPU寄存器传参/返回；命令常量不能替代具有正确ABI的数学调用。
6. NOP1()至NOP40()/NOP(N)表示指令数量，不代表微秒。
7. AI8051U.h已包含stdint.h，支持uint8_t/uint16_t/uint32_t及对应有符号类型。
8. Delay_MS(uint16_t milliseconds)按1ms为基准阻塞延时，0直接返回，范围0..65535，不占用定时器。
   例如Delay_MS(500)。配套实现放入独立静态库，不调用时不会进入固件。
   内部IRC作为系统时钟、程序未改CLKDIV/PLL/IRC调节值时有效；中断和调用开销会延长延时。
   函数临时使用WTST=0并恢复原值，保留DR0，不修改中断开关或引脚。
   每毫秒循环量化到最接近的4个系统时钟，另有固定调用开销；不是精密硬件定时器。

新建工程后F7生成HEX。mx251.json显式登记files/units/link及ABI/运行库。
头文件以相对路径包含；启动由锁定工具链提供。工程详情保存的IRC频率同时供编译与下载使用。
mx251.json中的MX251_IRC_HZ是Hz整数，默认40000000；MX251_DELAY_TICKS是自动生成并编译期校验的基准。
改频率会使旧构建失效，点击下载自动重编译。工程迁到另一台电脑仍保留频率，USB设备选择保持本机独立。
编译器支持范围与限制仍按R89说明。本次模板只有离线编译验证，没有新增实板验收。
IDE已接入既有AI8H2K12G白名单USB下载器，固定32位，可设置1–48MHz，默认40MHz；源码调试尚未接入。
当前无任意ISR、公共bit ABI、未审计手写汇编或完整外设库的验收承诺。

制作：tools/New-Stc251Pack.py生成独立包源目录，再用StudioX.Cli pack写入格式1归档。
模板与MX251声明生成器由本项目编写；寄存器名称/地址为厂商公开硬件接口事实，不包含厂商商业编译器、闭源库或原始SDK源码。
生成器tools/Generate-Ai8051UHeader.py及事实表tools/data/ai8051u-registers.json可复现头文件。
