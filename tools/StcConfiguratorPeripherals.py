"""Exact IAP15L2K61S2 profile 3 facts; no capabilities inherited from other STC15s."""


def extend(document: dict) -> None:
    definitions = document["definitions"]
    peripherals = definitions["peripherals"]
    pins = {pin["displayName"]: pin for pin in definitions["pins"]}

    def peripheral(identifier: str, kind: str, name: str) -> dict:
        item = {
            "id": identifier,
            "kind": kind,
            "displayName": name,
            "signals": [],
            "evidenceIds": ["datasheet", "registers"],
            "profileData": {},
        }
        peripherals.append(item)
        return item

    def function(owner: dict, pin: str, identifier: str, signal: str, direction: str) -> None:
        if not any(item["id"] == signal for item in owner["signals"]):
            owner["signals"].append(
                {
                    "id": signal,
                    "displayName": signal,
                    "direction": direction,
                    "profileData": {},
                }
            )
        pins[pin]["functions"].append(
            {
                "id": identifier,
                "displayName": signal,
                "peripheralId": owner["id"],
                "signalId": signal,
                "profileData": {},
            }
        )

    port5 = peripheral("GPIO5", "gpio", "P5 GPIO · P5.4 受 ISP 复位选项约束")
    for pin in ["P5.4", "P5.5"]:
        function(port5, pin, "gpio", pin, "bidirectional")
        pins[pin]["category"] = "io"
    peripheral("TIMER2", "timer", "Timer 2 · 16 位自动重装")
    for index, (count, output) in enumerate([("P3.4", "P3.5"), ("P3.5", "P3.4"), ("P3.1", "P3.0")]):
        owner = next(p for p in peripherals if p["id"] == f"TIMER{index}")
        function(owner, count, f"t{index}in", f"T{index} 外部计数", "input")
        function(owner, output, f"t{index}out", f"T{index}CLKO", "output")
    uart = peripheral("UART2", "uart", "UART2 · Timer2 波特率")
    for route, names in [("default", ["P1.0", "P1.1"]), ("p46", ["P4.6", "P4.7"])]:
        for pin, signal, direction in zip(names, ["RX", "TX"], ["input", "output"]):
            function(uart, pin, "uart2", f"UART2 {signal}-{route}", direction)
    adc = peripheral("ADC", "adc", "ADC · 8 路 10 位")
    for channel in range(8):
        function(adc, f"P1.{channel}", "adc", f"ADC{channel}", "input")
    spi = peripheral("SPI", "spi", "SPI · 主机 / 软件片选")
    for route, names in [
        ("p1", ["P1.3", "P1.4", "P1.5"]),
        ("p2", ["P2.3", "P2.2", "P2.1"]),
        ("p4", ["P4.0", "P4.1", "P4.3"]),
    ]:
        for pin, signal, direction in zip(
            names, ["MOSI", "MISO", "SCLK"], ["output", "input", "output"]
        ):
            function(spi, pin, "spi", f"SPI {signal}-{route}", direction)
    pca = peripheral("PCA", "timer", "PCA · CCP0 / CCP1 / CCP2")
    for route, names in [
        ("p1", ["P1.2", "P1.1", "P1.0", "P3.7"]),
        ("p3", ["P3.4", "P3.5", "P3.6", "P3.7"]),
        ("p2", ["P2.4", "P2.5", "P2.6", "P2.7"]),
    ]:
        for index, pin in enumerate(names):
            identifier = "eci" if index == 0 else f"ccp{index - 1}"
            # CCP2 的前两组共享 P3.7；一个物理功能只声明一次。
            if not any(f["id"] == identifier for f in pins[pin]["functions"]):
                function(
                    pca,
                    pin,
                    identifier,
                    identifier.upper(),
                    "input" if index == 0 else "bidirectional",
                )
    exti = peripheral("EXTI", "other", "外部中断 INT0–INT4")
    for index, pin in enumerate(["P3.2", "P3.3", "P3.6", "P3.7", "P3.0"]):
        function(exti, pin, f"int{index}", f"INT{index}", "input")
    for identifier, name in [
        ("WDT", "看门狗"),
        ("WKT", "掉电唤醒定时器"),
        ("LVD", "低压检测"),
        ("IAP", "IAP / EEPROM 待机初始化"),
    ]:
        peripheral(identifier, "other", name)
    mclk = peripheral("MCLKO", "other", "主时钟输出 · 仅内部 IRC")
    function(mclk, "P5.4", "mclko", "MCLKO", "output")
    definitions["profileData"]["peripheralInitialization"] = {
        "version": 1,
        "interrupts": "Configured but left disabled; application owns handlers and enabling",
        "spi": "Master only: vendor selection table restricts this family despite the old English SPI chapter",
        "adcClock": "Internal IRC before CLK_DIV; never infer ADC speed from external crystal",
        "mclko": "Internal IRC only, <=8 MHz, P5.4 ISP reset option must be disabled",
    }
    for index in range(3):
        nodes = definitions["clockTree"]["nodes"]
        next(n for n in nodes if n["id"] == f"T{index}")["inputIds"].append(f"EXT{index}")
        nodes.append(
            {
                "id": f"EXT{index}",
                "kind": "source",
                "displayName": f"T{index} 外部脉冲",
                "inputIds": [],
                "point": {"x": 480, "y": 400 + 100 * index},
                "evidenceIds": ["datasheet"],
                "profileData": {},
            }
        )
    definitions["evidence"][0][
        "section"
    ] = "p15–24 exact family/package; p237–238 WDT; p383–400 interrupts; p432–504 T0/1/2; p550–552 WKT; p572/605–608 UART; p627–630 IAP; p661–665 ADC; p685–700 PCA; p792–805 SPI"
    definitions["evidence"].append(
        {
            "id": "vendor-selection",
            "kind": "datasheet",
            "title": "STC15F2K60S2 厂商选型表：SPI 仅主机；C 版 MCLKO 仅 IRC",
            "version": "accessed 2026-10-22",
            "locator": "https://stcmicro.com/cn/stc/stc15f2k60s2.html",
            "sha256": None,
            "section": "系列选型表 / 主时钟输出限制",
        }
    )
    document["profile"]["version"] = "3.0.0"
    document["profile"]["dataSchema"]["version"] = "3.0.0"
    document["configuration"]["profileData"]["p54GpioConfirmed"] = False
