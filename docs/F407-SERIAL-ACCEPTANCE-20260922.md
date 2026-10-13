# F407 文字串口实机界面验收（2026-09-22）

用户恢复联调后，通过 computer-use 操作实际 MCU StudioX 窗口完成编译、ST-Link 下载、串口连接、发送、格式切换、导出和断开重连。后台只用于只读 Flash 备份、检查原始日志和核对导出数据，没有使用命令行串口客户端代替界面收发。

## 环境与备份

- IDE：`src/StudioX.Desktop/bin/Debug/net10.0-windows/MCU StudioX.exe`，独立 IDE 0.1.0 开发预览；本轮未重新生成安装包。
- 工程：`examples/F407-Serial-Terminal`，STM32F407ZG，HSE 8 MHz / SYSCLK 168 MHz，USART1 PA9/PA10。
- ST-Link V2J46S7，SWD 请求 2000 kHz、实际 1800 kHz；芯片 ID `0x10076413`，Flash 1024 KiB。
- 串口：USB-SERIAL CH340，COM15；8 数据位、无校验、1 停止位、无流控，DTR/RTS 始终关闭。
- 写入前完整备份：`.artifacts/serial-checks/flash-before-serial-20260922-041322.bin`，1,048,576 字节。
- 备份 SHA-256：`7C9F8772AFE4D1F08CB3D4A8420C078D117C812EDD358178F5D4164A57F70B62`。
- 只读备份结束执行 reset run / shutdown；随后由 IDE 下载测试固件。未修改选项字节、OTP，未做整片擦除。
- 下载 BIN 7432 字节，SHA-256 `69C2E782D5A64B31D26E9D053DF959DD09D3394BD379B8E7B86D1BD62B611914`。

## 实际结果

| 项目 | 结果 |
| --- | --- |
| IDE 编译与下载 | 编译退出 0；ST-Link 写入、回读校验并复位运行，下载退出 0 |
| 串口连接 | COM15 / 115200 / 8N1 连接成功，收到真实板端 Logo 与 Ready 信息 |
| ANSI 彩色接收 | Logo、16 色、256 色橙色、RGB 天蓝、粗体、下划线正常 |
| UTF-8 | 固定中文、主机发送中文回显正确；`.split` 每字节延时 2 ms 的多字节中文完整显示 |
| GB2312 / GBK | 接收固定 GB2312 字节样例及主机发送的“中文回显测试”均正确 |
| ANSI 发送 | 转义序列 `\e[35m紫色文字 StudioX\e[0m` 经板端原样回送，显示紫色中文 |
| HEX 双向 | 无行尾发送 `00 01 7F 80 A5 FF`，TX/RX 各增加 6 字节，原始记录包含完全一致的序列 |
| 全字节范围 | `.hex` 返回的 00–FF 256 字节在导出原始记录中连续、完整、顺序一致 |
| 定时发送 | 1000 ms 周期发送六字节 HEX，多轮 RX/TX 同步增加；取消勾选后停止 |
| 暂停与恢复 | 暂停期间画面不刷新，RX/TX 从 730/159 增至 772/201；恢复后显示缓存内容 |
| 参数切换 | 板端 `.baud 57600`，界面断开、手动输入 57600、重连，状态返回 `BAUD=57600 OVERRUN=0 ERR=0` |
| 连续日志 | 57600 下收到 BURST 0000–1999，共 100,000 字节；原始 BIN 与全部 2000 行预期内容连续完全匹配 |
| 有界显示与时间戳 | 连续输出可正常操作界面与滚动；2000 行显示上限生效，提示旧显示行淘汰；时间戳正常 |
| JSONL / BIN 导出 | 从界面保存成功；第一轮 JSONL 累计 RX 1036 B / TX 209 B，BIN 累计 RX 101199 B，与界面一致 |
| 清空显示 | 清除已导出的显示与历史缓存，保留 RX/TX 计数 |
| 关闭标签后重连 | 关闭串口标签完成断开，重新打开显示“已断开”；改回 115200 后重新连接成功，无需插拔或重新上电 |
| 最终恢复 | UTF-8 / CRLF / 115200 / 8N1；彩色 Logo、中文、状态返回正常，板端 SYSCLK=168000000、BAUD=115200、OVERRUN=0、ERR=0 |

验收结束保留 IDE 串口页面打开、COM15 连接，定时发送关闭，测试固件仍在板上。原固件仅备份，未自动恢复。

## 证据文件

- `.artifacts/serial-checks/flash-backup.log`。
- `examples/F407-Serial-Terminal/.build/studiox-build.log`。
- `examples/F407-Serial-Terminal/.build/download-c06b6c71fd72405db8cd4e7caec96e01/openocd.log`。
- `.artifacts/serial-checks/hardware-round1.jsonl` 与 `hardware-round1-verification.json`。
- `.artifacts/serial-checks/hardware-burst.bin` 与 `hardware-burst-verification.json`。
- 界面过程截图在本次 computer-use 操作记录中；未用后台串口测试替代这些操作。

## 验证边界与观察

- 本板 RTS/DTR 接 RESET/BOOT0，CTS 没有用于 USART 握手，因此没有实测 RTS/CTS 硬件流控；本轮也未测 XON/XOFF、其他校验位/数据位/停止位组合。
- 未做实际拔线重插、串口被其他软件占用、高波特率极限或长时间稳定性试验。已有模拟掉线、超时和缓存边界的后台回归不能替代这些硬件组合。
- 首次连接收到少量非 UTF-8 启动字节，原始 JSONL 保留这些字节；随后明确发送的中文、ANSI、全字节范围和连续日志核对通过。串口工具没有隐瞒或自动删除启动杂字节。
- 修改波特率时两端切换的短暂不同步可能产生杂字节；修改后重新连接并查询状态正常。测试固件复位恢复 115200。
- 本轮验证的是文本串口，不包含绘图、协议仿真或完整 VT/xterm 终端。
- 用户随后停止电脑操控，并提出透明背景及全软件滚轮修正。相应界面改动已加入源码和 Release 构建；这些后续改动尚未重新进行真实界面验收，不与上面的串口收发验收混为一项。
