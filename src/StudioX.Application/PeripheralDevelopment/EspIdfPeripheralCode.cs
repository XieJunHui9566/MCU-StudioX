namespace StudioX.Application.PeripheralDevelopment;

internal static class EspIdfPeripheralCode
{
    public static string Generate(string id, IReadOnlyDictionary<string, string> v)
    {
        var n = v["name"];
        var recipe = EspIdfPeripheralRecipes.All.Single(r => r.Id == id);
        var intro = $$"""
            // StudioX 外设辅助：在文件顶层插入，由任务明确调用；不自动运行或占用硬件。
            #include <stdbool.h>
            #include "esp_err.h"
            #include "esp_log.h"
            #include "driver/gpio.h"
            #include "{{recipe.Header}}"

            // 回滚失败保留原始错误，并记录清理错误，便于排查资源仍占用的原因。
            static inline esp_err_t {{n}}_rollback(esp_err_t original, esp_err_t cleanup)
            {
                if (cleanup != ESP_OK) {
                    ESP_LOGE("{{n}}", "Cleanup: %s; initial: %s", esp_err_to_name(cleanup), esp_err_to_name(original));
                }
                return original;
            }

            """;
        var body = id switch
        {
            "gpio" => $$"""
                static bool {{n}}_ready;
                esp_err_t {{n}}_deinit(void)
                {
                    if (!{{n}}_ready) return ESP_OK;
                    esp_err_t err = gpio_reset_pin((gpio_num_t){{v["pin"]}});
                    if (err == ESP_OK) {{n}}_ready = false;
                    return err;
                }
                esp_err_t {{n}}_init(void)
                {
                    if ({{n}}_ready) return ESP_ERR_INVALID_STATE;
                    if (!GPIO_IS_VALID_OUTPUT_GPIO({{v["pin"]}})) return ESP_ERR_INVALID_ARG;
                    esp_err_t err = gpio_set_level((gpio_num_t){{v["pin"]}}, {{v["level"]}});
                    if (err != ESP_OK) return err;
                    gpio_config_t config = {0};
                    config.pin_bit_mask = 1ULL << {{v["pin"]}};
                    config.mode = GPIO_MODE_OUTPUT;
                    config.pull_up_en = GPIO_PULLUP_DISABLE;
                    config.pull_down_en = GPIO_PULLDOWN_DISABLE;
                    config.intr_type = GPIO_INTR_DISABLE;
                    err = gpio_config(&config);
                    if (err != ESP_OK) return {{n}}_rollback(err, gpio_reset_pin((gpio_num_t){{v["pin"]}}));
                    {{n}}_ready = true;
                    return ESP_OK;
                }
                """,
            "uart" => $$"""
                static bool {{n}}_ready;
                esp_err_t {{n}}_deinit(void)
                {
                    if (!{{n}}_ready) return ESP_OK;
                    esp_err_t err = uart_driver_delete((uart_port_t){{v["port"]}});
                    if (err == ESP_OK) {{n}}_ready = false;
                    return err;
                }
                esp_err_t {{n}}_init(void)
                {
                    if ({{n}}_ready || uart_is_driver_installed((uart_port_t){{v["port"]}})) return ESP_ERR_INVALID_STATE;
                    if (!GPIO_IS_VALID_OUTPUT_GPIO({{v["tx"]}}) || !GPIO_IS_VALID_GPIO({{v["rx"]}})) return ESP_ERR_INVALID_ARG;
                    uart_config_t config = {0};
                    config.baud_rate = {{v["baud"]}};
                    config.data_bits = UART_DATA_8_BITS;
                    config.parity = UART_PARITY_DISABLE;
                    config.stop_bits = UART_STOP_BITS_1;
                    config.flow_ctrl = UART_HW_FLOWCTRL_DISABLE;
                    config.source_clk = UART_SCLK_DEFAULT;
                    esp_err_t err = uart_param_config((uart_port_t){{v["port"]}}, &config);
                    if (err != ESP_OK) return err;
                    err = uart_driver_install((uart_port_t){{v["port"]}}, 1024, 0, 0, NULL, 0);
                    if (err != ESP_OK) return err;
                    {{n}}_ready = true;
                    err = uart_set_pin((uart_port_t){{v["port"]}}, {{v["tx"]}}, {{v["rx"]}}, UART_PIN_NO_CHANGE, UART_PIN_NO_CHANGE);
                    if (err != ESP_OK) return {{n}}_rollback(err, {{n}}_deinit());
                    return ESP_OK;
                }
                """,
            "i2c" => $$"""
                static i2c_master_bus_handle_t {{n}}_bus;
                esp_err_t {{n}}_deinit(void)
                {
                    if ({{n}}_bus == NULL) return ESP_OK;
                    esp_err_t err = i2c_del_master_bus({{n}}_bus);
                    if (err == ESP_OK) {{n}}_bus = NULL;
                    return err;
                }
                esp_err_t {{n}}_init(void)
                {
                    if ({{n}}_bus != NULL) return ESP_ERR_INVALID_STATE;
                    if (!GPIO_IS_VALID_OUTPUT_GPIO({{v["sda"]}}) || !GPIO_IS_VALID_OUTPUT_GPIO({{v["scl"]}})) return ESP_ERR_INVALID_ARG;
                    i2c_master_bus_config_t config = {0};
                    config.i2c_port = {{v["port"]}};
                    config.sda_io_num = (gpio_num_t){{v["sda"]}};
                    config.scl_io_num = (gpio_num_t){{v["scl"]}};
                    config.clk_source = I2C_CLK_SRC_DEFAULT;
                    config.glitch_ignore_cnt = 7;
                    // 外部上拉按板电压和总线电容选择；内部弱上拉不替代电阻。
                    config.flags.enable_internal_pullup = false;
                    return i2c_new_master_bus(&config, &{{n}}_bus);
                }
                """,
            "spi" => $$"""
                static bool {{n}}_ready;
                esp_err_t {{n}}_deinit(void)
                {
                    if (!{{n}}_ready) return ESP_OK;
                    esp_err_t err = spi_bus_free((spi_host_device_t)({{v["port"]}} - 1));
                    if (err == ESP_OK) {{n}}_ready = false;
                    return err;
                }
                esp_err_t {{n}}_init(void)
                {
                    if ({{n}}_ready) return ESP_ERR_INVALID_STATE;
                    if (!GPIO_IS_VALID_OUTPUT_GPIO({{v["mosi"]}}) || !GPIO_IS_VALID_GPIO({{v["miso"]}}) || !GPIO_IS_VALID_OUTPUT_GPIO({{v["sclk"]}})) return ESP_ERR_INVALID_ARG;
                    spi_bus_config_t config = {0};
                    config.mosi_io_num = {{v["mosi"]}};
                    config.miso_io_num = {{v["miso"]}};
                    config.sclk_io_num = {{v["sclk"]}};
                    config.quadwp_io_num = -1;
                    config.quadhd_io_num = -1;
                    config.data4_io_num = -1;
                    config.data5_io_num = -1;
                    config.data6_io_num = -1;
                    config.data7_io_num = -1;
                    config.max_transfer_sz = 4096;
                    esp_err_t err = spi_bus_initialize((spi_host_device_t)({{v["port"]}} - 1), &config, SPI_DMA_CH_AUTO);
                    if (err == ESP_OK) {{n}}_ready = true;
                    return err;
                }
                """,
            "adc" => $$"""
                static adc_oneshot_unit_handle_t {{n}}_unit;
                static adc_channel_t {{n}}_channel;
                esp_err_t {{n}}_deinit(void)
                {
                    if ({{n}}_unit == NULL) return ESP_OK;
                    esp_err_t err = adc_oneshot_del_unit({{n}}_unit);
                    if (err == ESP_OK) {{n}}_unit = NULL;
                    return err;
                }
                esp_err_t {{n}}_init(void)
                {
                    if ({{n}}_unit != NULL) return ESP_ERR_INVALID_STATE;
                    adc_unit_t unit;
                    esp_err_t err = adc_oneshot_io_to_channel({{v["pin"]}}, &unit, &{{n}}_channel);
                    if (err != ESP_OK) return err;
                    adc_oneshot_unit_init_cfg_t config = {0};
                    config.unit_id = unit;
                    config.ulp_mode = ADC_ULP_MODE_DISABLE;
                    err = adc_oneshot_new_unit(&config, &{{n}}_unit);
                    if (err != ESP_OK) return err;
                    adc_oneshot_chan_cfg_t channel_config = {0};
                    channel_config.bitwidth = ADC_BITWIDTH_DEFAULT;
                    channel_config.atten = ADC_ATTEN_DB_0;
                    err = adc_oneshot_config_channel({{n}}_unit, {{n}}_channel, &channel_config);
                    if (err != ESP_OK) return {{n}}_rollback(err, {{n}}_deinit());
                    return ESP_OK;
                }
                esp_err_t {{n}}_read(int *raw)
                {
                    if (raw == NULL) return ESP_ERR_INVALID_ARG;
                    if ({{n}}_unit == NULL) return ESP_ERR_INVALID_STATE;
                    return adc_oneshot_read({{n}}_unit, {{n}}_channel, raw);
                }
                """,
            "pwm" => $$"""
                static bool {{n}}_timer_ready;
                static bool {{n}}_channel_ready;
                esp_err_t {{n}}_deinit(void)
                {
                    if ({{n}}_channel_ready) {
                        esp_err_t err = ledc_stop(LEDC_LOW_SPEED_MODE, (ledc_channel_t){{v["channel"]}}, 0);
                        if (err != ESP_OK) return err;
                        {{n}}_channel_ready = false;
                    }
                    if (!{{n}}_timer_ready) return ESP_OK;
                    esp_err_t pause = ledc_timer_pause(LEDC_LOW_SPEED_MODE, (ledc_timer_t){{v["timer"]}});
                    if (pause != ESP_OK) return pause;
                    ledc_timer_config_t config = {0};
                    config.speed_mode = LEDC_LOW_SPEED_MODE;
                    config.timer_num = (ledc_timer_t){{v["timer"]}};
                    config.deconfigure = true;
                    esp_err_t err = ledc_timer_config(&config);
                    if (err == ESP_OK) {{n}}_timer_ready = false;
                    return err;
                }
                esp_err_t {{n}}_init(void)
                {
                    if ({{n}}_timer_ready) return ESP_ERR_INVALID_STATE;
                    if (!GPIO_IS_VALID_OUTPUT_GPIO({{v["pin"]}})) return ESP_ERR_INVALID_ARG;
                    ledc_timer_config_t timer = {0};
                    timer.speed_mode = LEDC_LOW_SPEED_MODE;
                    timer.duty_resolution = LEDC_TIMER_8_BIT;
                    timer.timer_num = (ledc_timer_t){{v["timer"]}};
                    timer.freq_hz = {{v["frequency"]}};
                    timer.clk_cfg = LEDC_AUTO_CLK;
                    esp_err_t err = ledc_timer_config(&timer);
                    if (err != ESP_OK) return err;
                    {{n}}_timer_ready = true;
                    ledc_channel_config_t channel = {0};
                    channel.gpio_num = {{v["pin"]}};
                    channel.speed_mode = LEDC_LOW_SPEED_MODE;
                    channel.channel = (ledc_channel_t){{v["channel"]}};
                    channel.timer_sel = (ledc_timer_t){{v["timer"]}};
                    channel.duty = {{v["duty"]}};
                    err = ledc_channel_config(&channel);
                    if (err != ESP_OK) return {{n}}_rollback(err, {{n}}_deinit());
                    {{n}}_channel_ready = true;
                    return ESP_OK;
                }
                """,
            "timer" => $$"""
                static esp_timer_handle_t {{n}}_handle;
                static bool {{n}}_running;
                static void {{n}}_callback(void *argument)
                {
                    (void)argument;
                    // 在此通知工作任务；不阻塞、不进行长时间外设读写。
                }
                esp_err_t {{n}}_deinit(void)
                {
                    if ({{n}}_handle == NULL) return ESP_OK;
                    if ({{n}}_running) {
                        esp_err_t err = esp_timer_stop({{n}}_handle);
                        if (err != ESP_OK) return err;
                        {{n}}_running = false;
                    }
                    esp_err_t err = esp_timer_delete({{n}}_handle);
                    if (err == ESP_OK) {{n}}_handle = NULL;
                    return err;
                }
                esp_err_t {{n}}_init(void)
                {
                    if ({{n}}_handle != NULL) return ESP_ERR_INVALID_STATE;
                    esp_timer_create_args_t config = {0};
                    config.callback = {{n}}_callback;
                    config.dispatch_method = ESP_TIMER_TASK;
                    config.name = "{{n}}";
                    esp_err_t err = esp_timer_create(&config, &{{n}}_handle);
                    if (err != ESP_OK) return err;
                    err = esp_timer_start_periodic({{n}}_handle, {{v["period"]}}ULL);
                    if (err != ESP_OK) return {{n}}_rollback(err, {{n}}_deinit());
                    {{n}}_running = true;
                    return ESP_OK;
                }
                """,
            "rmt" => $$"""
                #include "soc/soc_caps.h"
                static rmt_channel_handle_t {{n}}_channel;
                static bool {{n}}_enabled;
                esp_err_t {{n}}_deinit(void)
                {
                    if ({{n}}_channel == NULL) return ESP_OK;
                    if ({{n}}_enabled) {
                        // 有限等待；超时保留句柄，调用方可稍后重试释放。
                        esp_err_t err = rmt_tx_wait_all_done({{n}}_channel, 100);
                        if (err != ESP_OK) return err;
                        err = rmt_disable({{n}}_channel);
                        if (err != ESP_OK) return err;
                        {{n}}_enabled = false;
                    }
                    esp_err_t err = rmt_del_channel({{n}}_channel);
                    if (err == ESP_OK) {{n}}_channel = NULL;
                    return err;
                }
                esp_err_t {{n}}_init(void)
                {
                    if ({{n}}_channel != NULL) return ESP_ERR_INVALID_STATE;
                    if (!GPIO_IS_VALID_OUTPUT_GPIO({{v["pin"]}})) return ESP_ERR_INVALID_ARG;
                    rmt_tx_channel_config_t config = {0};
                    config.gpio_num = (gpio_num_t){{v["pin"]}};
                    config.clk_src = RMT_CLK_SRC_DEFAULT;
                    config.resolution_hz = 1000000;
                    config.mem_block_symbols = SOC_RMT_MEM_WORDS_PER_CHANNEL;
                    config.trans_queue_depth = 4;
                    esp_err_t err = rmt_new_tx_channel(&config, &{{n}}_channel);
                    if (err != ESP_OK) return err;
                    err = rmt_enable({{n}}_channel);
                    if (err != ESP_OK) return {{n}}_rollback(err, {{n}}_deinit());
                    {{n}}_enabled = true;
                    return ESP_OK;
                }
                """,
            _ => throw new InvalidOperationException("Unknown peripheral recipe")
        };
        return intro + "\n" + body + "\n";
    }
}
