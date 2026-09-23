# 串口后台检查

在项目根运行 `dotnet run --project tools/StudioX.SerialChecks --no-launch-profile`。

此程序只用受控内存传输，不打开真实 COM 端口，不启动 GUI，不控制或烧录开发板。覆盖实际 Application 服务的跨分片解码、ANSI 状态、显示格式重建、字节计数、发送失败、原始/JSONL 导出、连接边界、模拟拔出及释放/重连；另外检查编码器、ANSI 行处理及设备唯一所有者。

导出与偏好夹具写入输出中打印的临时目录，供检查，不修改正常用户的串口设置。

实机界面检查要求见 `docs/SERIAL_TERMINAL.md`；历史测试固件已清理，追溯位置见 `docs/DEVELOPMENT-CLEANUP-20260922.md`。后台检查不能替代实机验收。
