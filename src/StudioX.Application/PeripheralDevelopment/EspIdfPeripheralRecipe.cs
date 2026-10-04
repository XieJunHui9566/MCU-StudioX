namespace StudioX.Application.PeripheralDevelopment;

internal sealed record EspIdfPeripheralRecipe(string Id, string Name, string Header, string Component,
    string? Capability, PeripheralParameter[] Parameters, string Notes, string Documentation)
{
    public string[] EvidenceFiles => [$"components/{Component}/include/{Header}", $"components/{Component}/CMakeLists.txt",
        "components/esp_driver_gpio/include/driver/gpio.h", "components/esp_driver_gpio/CMakeLists.txt",
        "components/esp_common/include/esp_err.h", "components/log/include/esp_log.h",
        .. (Id == "spi" ? new[] { "components/esp_driver_spi/include/driver/spi_common.h" } : Array.Empty<string>())];
    public string InitializationApi => Id switch
    {
        "gpio" => "gpio_config",
        "uart" => "uart_driver_install",
        "i2c" => "i2c_new_master_bus",
        "spi" => "spi_bus_add_device",
        "adc" => "adc_oneshot_new_unit",
        "pwm" => "ledc_timer_config",
        "timer" => "esp_timer_create",
        "rmt" => "rmt_new_tx_channel",
        _ => throw new InvalidOperationException("Unknown peripheral recipe")
    };
}
