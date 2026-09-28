# 探索者 STM32F407ZG Zephyr 实验工程

目标板为用户确认的探索者 V3.5；所参考的 `EXPLORER_V3.5.pdf` 页内修订栏却写 V3.4。PF9/LED0 已在所测实板上经闪烁观察与 GPIO 寄存器核验；PF10、按键、USART1 等其余连接仍待实板核验。

此工程固定 Zephyr v4.4.2，构建目标为 `alientek_explorer_f407zg`：

```text
west build -b alientek_explorer_f407zg .
```

`src/main.c` 每 500 ms 切换 PF9 板载 LED0。控制台选 USART1（PA9/PA10，115200）；要连到板载 CH340C USB 串口，P10 的两组跳线必须正确接通。构建命令不会下载程序。调试器使用外接 ST-Link 的 SWD 线；此实验包的 `west attach` 需要 NRST 接线，并会复位、暂停目标，但不自动烧录。

板级 DTS 和 Kconfig 等文件改写自 Zephyr v4.4.2 的 `black_f407zg_pro` 定义，保留原作者署名及 Apache-2.0 声明；完整许可证见本工程 `LICENSE`。
