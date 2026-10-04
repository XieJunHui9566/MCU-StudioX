namespace StudioX.Application.PeripheralDevelopment;

internal static class EspIdfPeripheralRecipes
{
    private static PeripheralParameter Name(string name) => new("name", "实例名称（同一文件须唯一）", name);
    // 不预选板级引脚；空值强制用户依据当前板原理图填写。
    private static PeripheralParameter Pin(string id, string label) => new(id, label + " GPIO", "", 0, 63);
    private static PeripheralParameter Number(string id, string label, string value, int min, int max) => new(id, label, value, min, max);
    public static IReadOnlyList<EspIdfPeripheralRecipe> All
    {
        get;
    } = new EspIdfPeripheralRecipe[]
    {
        new("gpio", "GPIO 输出", "driver/gpio.h", "esp_driver_gpio", null,
            [Name("output"), Pin("pin", "输出"), Number("level", "初始电平（0 / 1）", "0", 0, 1)],
            "输出配置默认关闭上下拉和中断。先预置电平再启用输出；芯片复位到初始化之前的电平仍取决于板级电路。", "peripherals/gpio.html"),
        new("uart", "UART 串口", "driver/uart.h", "esp_driver_uart", null,
            [Name("serial"), Number("port", "UART 控制器编号", "1", 0, 2), Pin("tx", "TX"), Pin("rx", "RX"), Number("baud", "波特率", "115200", 1200, 2000000)],
            "8N1、无流控，RX 缓冲 1024 字节。避免与控制台共用控制器；接收使用有限超时并检查实际长度。", "peripherals/uart.html"),
        new("i2c", "I2C 主机总线", "driver/i2c_master.h", "esp_driver_i2c", "SOC_I2C_SUPPORTED",
            [Name("i2c"), Number("port", "I2C 控制器编号", "0", 0, 1), Pin("sda", "SDA"), Pin("scl", "SCL")],
            "使用新主机驱动；总线初始化与设备地址/时钟注册分开。请外接合适上拉电阻；添加设备后，释放总线前先移除设备。不要与旧 driver/i2c.h 驱动混用。", "peripherals/i2c.html"),
        new("spi", "SPI 主机总线", "driver/spi_master.h", "esp_driver_spi", null,
            [Name("spi"), Number("port", "SPI 主机编号（2 / 3，避开 Flash 总线）", "2", 2, 3), Pin("mosi", "MOSI"), Pin("miso", "MISO"), Pin("sclk", "SCLK")],
            "初始化通用 SPI 总线，自动选择 DMA。之后用 spi_bus_add_device 配置片选、模式和频率；释放总线前先移除所有设备。", "peripherals/spi_master.html"),
        new("adc", "ADC 单次采样", "esp_adc/adc_oneshot.h", "esp_adc", "SOC_ADC_SUPPORTED",
            [Name("adc"), Pin("pin", "模拟输入")],
            "SDK 将 GPIO 映射到实际 ADC 单元和通道，默认位宽、0 dB 衰减。输出原始值，不冒充电压；量程、校准和 ADC2/Wi-Fi 限制请核对目标文档。", "peripherals/adc_oneshot.html"),
        new("pwm", "LEDC PWM", "driver/ledc.h", "esp_driver_ledc", "SOC_LEDC_SUPPORTED",
            [Name("pwm"), Pin("pin", "PWM 输出"), Number("frequency", "频率 Hz（8 位分辨率）", "1000", 1, 100000), Number("duty", "占空计数（0–255 / 256）", "128", 0, 255),
                Number("timer", "LEDC 定时器编号", "0", 0, 3), Number("channel", "LEDC 通道编号", "0", 0, 5)],
            "低速模式、8 位分辨率。实际时钟是否可产生所需频率由 SDK 返回值确认；一个定时器被多个通道共享时不可随意释放。", "peripherals/ledc.html"),
        new("timer", "软件周期定时器", "esp_timer.h", "esp_timer", null,
            [Name("timer"), Number("period", "周期 μs", "1000000", 1000, 2000000000)],
            "回调由 ESP_TIMER_TASK 调度，示例回调为空。仅做短时非阻塞操作，将工作通知给任务；此模板不保证硬件定时精度。", "system/esp_timer.html"),
        new("rmt", "RMT 发送通道", "driver/rmt_tx.h", "esp_driver_rmt", "SOC_RMT_SUPPORTED",
            [Name("rmt"), Pin("pin", "RMT 输出")],
            "1 MHz 分辨率、单通道内存，初始化并启用发送通道。发送前另建 encoder，检查 rmt_transmit 返回值；释放前等待发送完成并删除 encoder。", "peripherals/rmt.html")
    };
}
