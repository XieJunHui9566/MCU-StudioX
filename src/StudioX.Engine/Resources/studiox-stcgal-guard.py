"""StudioX's exact-model gate for an externally installed stcgal 1.10.

No serial port is opened until the image checksum and command are validated.
The model comparison runs after the bootloader identifies the silicon and
before stcgal's handshake, erase, program, or option-writing sequence.
"""

import argparse
import copy
import hashlib
import json
import functools
import re
import struct
import sys

from stcgal import options as option_types, protocols
from stcgal.frontend import StcGal
from stcgal.protocols import StcProtocolException, Stc15Protocol


def install_option_safety():
    """Apply StudioX compatibility repairs without changing the immutable runtime."""
    if getattr(protocols, "_studiox_option_safety", False):
        return

    def get_option(self, name):
        for option, getter, _ in self.options:
            if option == name:
                return getter()
        raise ValueError("unknown option: " + name)

    def uart2_mode(self, value):
        if value not in ("normal", "push-pull"):
            raise ValueError("uart2_pin_mode must be normal or push-pull")
        self.msr[2] = (self.msr[2] & ~0x20) | (0x20 if value == "push-pull" else 0)

    def output_level(value):
        # getter 返回 high/low；兼容已有明确布尔输入，但不把未知字符串默认为 low。
        if isinstance(value, bool):
            return value
        if value in ("high", "true", "1"):
            return True
        if value in ("low", "false", "0"):
            return False
        raise ValueError("rstout_por_state must be high or low")

    def p33_level(self, value):
        bit = 0x08 if output_level(value) else 0
        self.msr[2] = (self.msr[2] & ~0x08) | bit

    def p20_level(self, value):
        bit = 0x08 if output_level(value) else 0
        self.msr[1] = (self.msr[1] & ~0x08) | bit

    option_types.BaseOption.get_option = get_option
    option_types.Stc15Option.set_uart_pin_mode = uart2_mode
    option_types.Stc15Option.set_p33_state = p33_level
    option_types.Stc8Option.set_p20_state = p20_level

    original_split = protocols.Stc8dProtocol.set_option
    def set_partition(self, name, value):
        if name == "program_eeprom_split":
            # 先验证再改配置镜像；251 的地址空间尚无该后端的容量契约，拒绝套用 64 KiB。
            if self.model.mcs251:
                raise StcProtocolException("StudioX: MCS-251 partition requires a qualified address-space backend")
            if not self.model.iap:
                raise ValueError("program_eeprom_split is unsupported for this non-IAP target")
            size = option_types.Utils.to_int(value)
            if size < 512 or size > min(65024, self.model.total) or size % 512:
                raise ValueError("program_eeprom_split exceeds target capacity or alignment")
        return original_split(self, name, value)
    protocols.Stc8dProtocol.set_option = set_partition

    def unqualified_family(name):
        return bool(re.match(r"^(?:STC|IAP|IRC)(?:12H|15H)", name, re.IGNORECASE))

    original_selector = protocols.StcAutoProtocol.initialize_model
    def select_protocol(self):
        original_selector(self)
        if unqualified_family(self.model.name):
            self.protocol_name = None
            print("StudioX: new STC12H/STC15H protocol is not qualified; legacy fallback disabled")
    protocols.StcAutoProtocol.initialize_model = select_protocol

    def checked_call(original, minimum=None):
        @functools.wraps(original)
        def invoke(self, *args, **kwargs):
            packet = args[0] if args else getattr(self, "status_packet", None)
            if minimum is not None and (packet is None or len(packet) < minimum):
                raise StcProtocolException("StudioX: short status/options packet for " + original.__qualname__)
            try:
                return original(self, *args, **kwargs)
            except (IndexError, struct.error, AssertionError) as error:
                raise StcProtocolException("StudioX: malformed response for " + original.__qualname__) from error
        return invoke

    # 按协议字段边界检查，不按器件名猜测短包的缺失字段。
    status_lengths = {
        protocols.Stc89Protocol: 20, protocols.Stc89AProtocol: 23,
        protocols.Stc12AProtocol: 19, protocols.Stc12BaseProtocol: 19, protocols.Stc15AProtocol: 19,
        protocols.Stc15Protocol: 23, protocols.Stc8Protocol: 40,
    }
    classes = (protocols.StcBaseProtocol, protocols.StcAutoProtocol,
               protocols.Stc89Protocol, protocols.Stc89AProtocol,
               protocols.Stc12AOptionsMixIn, protocols.Stc12OptionsMixIn, protocols.Stc12BaseProtocol,
               protocols.Stc12AProtocol, protocols.Stc12BProtocol, protocols.Stc12Protocol,
               protocols.Stc15AProtocol, protocols.Stc15Protocol,
               protocols.Stc8Protocol, protocols.Stc8dProtocol, protocols.Stc8gProtocol)
    for cls in classes:
        for name in ("initialize_status", "initialize_options", "initialize_model", "get_status_packet", "program_options"):
            original = cls.__dict__.get(name)
            if original is None or getattr(original, "__isabstractmethod__", False):
                continue
            minimum = status_lengths.get(cls) if name == "initialize_status" else 23 if name == "initialize_model" else None
            setattr(cls, name, checked_call(original, minimum))

    original_read = protocols.StcBaseProtocol.read_packet
    @functools.wraps(original_read)
    def read_packet(self):
        data = checked_call(original_read)(self)
        if not data:
            raise StcProtocolException("StudioX: empty protocol response")
        return data
    protocols.StcBaseProtocol.read_packet = read_packet

    original_program = StcGal.program_mcu
    @functools.wraps(original_program)
    def program_mcu(self):
        if self.protocol.model.mcs251:
            raise StcProtocolException("StudioX: MCS-251 HEX/address-space programming is unsupported by this legacy backend; use a qualified backend")
        if unqualified_family(self.protocol.model.name):
            raise StcProtocolException("StudioX: STC12H/STC15H cannot use a legacy STC12/STC15 programming protocol")
        original_load = self.load_file_auto
        def load_checked(file):
            data = original_load(file)
            code = self.protocol.split_code if self.protocol.split_code is not None else self.protocol.model.code
            eeprom = self.protocol.split_eeprom if self.protocol.split_eeprom is not None else self.protocol.model.eeprom
            if min(code, eeprom) < 0 or code + eeprom > self.protocol.model.total:
                raise StcProtocolException("StudioX: invalid code/EEPROM partition")
            capacity = eeprom if file is self.opts.eeprom_image else code + eeprom
            if len(data) > capacity:
                raise StcProtocolException("StudioX: image exceeds target region; truncation refused")
            return data
        self.load_file_auto = load_checked
        try:
            return original_program(self)
        finally:
            self.load_file_auto = original_load
    StcGal.program_mcu = program_mcu
    protocols._studiox_option_safety = True


install_option_safety()

MONITOR_SETUP_SHA256 = "ABC080915DDDCA9589F184E9BE76FBCC757EDF001BE02AF7A70ECB57451106F9"
MONITOR_SETUP_HASHES = {
    "IAP15F2K61S2": MONITOR_SETUP_SHA256,
    "IAP15L2K61S2": "D2320E17FE8961813D8A012C46571390D6AA3BA683A594DD05697AD5B990D729",
}
STCGAL_FIRST_CALIBRATION = bytes.fromhex("000c00c080c0ffc000808080ff8000408040ff4000008000c000")
IAP15_FIRST_CALIBRATION = bytes.fromhex("000b00c080c0ffc000808080ff8000408040ff40000080000000")


ALLOWED_MODEL_NAMES = {
    "STC89C52RC": ("STC89C52RC/LE52RC",),
    "STC8G1K08": ("STC8G1K08-8PIN", "STC8G1K08-20/16PIN"),
    "STC8G1K17": ("STC8G1K17-8PIN", "STC8G1K17-20/16PIN"),
}


def configure_iap15_calibration(protocol):
    # 当前实测官方流程使用 66 校准脉冲；精确限定型号和 BSL，避免改变其他系列。
    # 第一轮按抓包使用 11 点和尾部填充；保留第二轮、超时、频率计算及应答校验。
    if (
        type(protocol) is not Stc15Protocol
        or protocol.model.name != "IAP15F2K61S2"
        or protocol.model.code != 62464
        or getattr(protocol, "mcu_bsl_version", None) != "7.2.5S"
        or len(getattr(protocol, "status_packet", b"")) < 46
        or protocol.status_packet[45] != 0x70
        or getattr(protocol, "external_clock", False)
    ):
        return False
    if getattr(protocol, "_studiox_calibration_profile", None) == "iap15-7.2.5S-66-11":
        return True
    original = protocol.pulse
    original_write = protocol.write_packet

    def pulse(character=b"\x7f", timeout=0):
        if character == b"\xfe":
            character = b"\x66"
            print("StudioX: IAP15F2K61S2 / 7.2.5S calibration pulse=66", flush=True)
        return original(character, timeout)

    def write_packet(data, *args, **kwargs):
        if data == STCGAL_FIRST_CALIBRATION:
            data = IAP15_FIRST_CALIBRATION
            print("StudioX: IAP15F2K61S2 / 7.2.5S first calibration trials=11", flush=True)
        return original_write(data, *args, **kwargs)

    protocol.pulse = pulse
    protocol.write_packet = write_packet
    protocol._studiox_calibration_profile = "iap15-7.2.5S-66-11"
    return True


class GuardedStcGal(StcGal):
    def __init__(
        self,
        options,
        expected_model,
        expected_code_bytes,
        expected_hash,
        clock_mode,
        monitor_setup=False,
        hardware_options=None,
    ):
        self.expected_model = expected_model
        self.expected_code_bytes = expected_code_bytes
        self.expected_hash = expected_hash
        self.clock_mode = clock_mode
        self.monitor_setup = monitor_setup
        self.hardware_options = hardware_options or {}
        super().__init__(options)

    def report_program_progress(self, current, written, maximum):
        # 协议在块写入应答核对后调用；最后的哨兵回调不再额外累计一个块。
        # 独立完整行便于 IDE 合并进度，避免 tqdm 在重定向输出中刷屏。
        if maximum <= 0 or current < 0 or current > maximum or written <= 0:
            return
        measurement = (min(current + written, maximum), maximum)
        if measurement != getattr(self, "_studiox_last_program_progress", None):
            self._studiox_last_program_progress = measurement
            print("STUDIOX_ISP_PROGRESS %d %d" % measurement, flush=True)

    def program_mcu(self):
        detected = self.protocol.model.name
        print("StudioX detected model: " + detected, flush=True)
        allowed = ALLOWED_MODEL_NAMES.get(
            self.expected_model.upper(), (self.expected_model.upper(),)
        )
        if detected.upper() not in allowed or self.protocol.model.code != self.expected_code_bytes:
            raise StcProtocolException(
                "StudioX exact-model guard: expected %s / %d bytes, detected %s / %d bytes; no erase/write"
                % (
                    self.expected_model,
                    self.expected_code_bytes,
                    detected,
                    self.protocol.model.code,
                )
            )
        self.opts.code_image.seek(0)
        actual_hash = hashlib.sha256(self.opts.code_image.read()).hexdigest().upper()
        self.opts.code_image.seek(0)
        if actual_hash != self.expected_hash:
            raise StcProtocolException("StudioX image checksum changed; no erase/write")
        if self.monitor_setup:
            self.program_monitor()
            return
        # 在同一次真实身份握手后、校准/擦除前核对选项类；未知字段不能静默忽略。
        hardware = getattr(self, "hardware_options", {})
        if hardware:
            available = {name for name, _, _ in self.protocol.options.options}
            checked_options = copy.deepcopy(self.protocol.options)
            for name, value in hardware.items():
                if name not in available:
                    raise StcProtocolException("StudioX: option unavailable on detected silicon: " + name)
                current = checked_options.get_option(name)
                if name == "cpu_core_voltage" and current == "unknown":
                    raise StcProtocolException("StudioX: CPU core voltage is not recognized on detected silicon; no erase/write")
                if isinstance(current, bool) and value not in ("true", "false"):
                    raise StcProtocolException("StudioX: invalid boolean option: " + name)
                try:
                    checked_options.set_option(name, value)
                except (ValueError, TypeError) as error:
                    raise StcProtocolException("StudioX: option invalid for detected silicon: " + name) from error
            self.opts.option = list(self.opts.option or []) + [
                name + "=" + value for name, value in sorted(hardware.items())
            ]
            print("StudioX hardware overrides: " + json.dumps(hardware, sort_keys=True), flush=True)
        # stcgal 1.10 在外部时钟路径中以 24 MHz 初始化 RC 校准。
        # 显式指定 RC 目标频率时，拒绝一步从外部切至内部时钟，
        # 否则该分支不会实际应用请求的频率。
        read_clock_source = getattr(self.protocol.options, "get_clock_source", None)
        if (
            self.clock_mode == "internal"
            and self.opts.trim > 0
            and callable(read_clock_source)
            and read_clock_source() == "external"
        ):
            raise StcProtocolException(
                "StudioX: external-to-internal RC trim needs a separate cold-start sequence; no erase/write"
            )
        if self.clock_mode != "preserve":
            # 先识别实际选项类再决定是否存在时钟源位；STC15A/STC8 只有 RC 校准，不能发送未知选项。
            available = {name for name, _, _ in self.protocol.options.options}
            if "clock_source" in available:
                self.opts.option = list(getattr(self.opts, "option", None) or []) + ["clock_source=" + self.clock_mode]
            elif self.clock_mode == "external" or not isinstance(self.protocol.options, (option_types.Stc15AOption, option_types.Stc8Option)):
                raise StcProtocolException("StudioX: clock source option unsupported on detected silicon; no erase/write")
        self._studiox_last_program_progress = None
        self.protocol.progress_cb = self.report_program_progress
        print("STUDIOX_TARGET_VERIFIED " + self.expected_model, flush=True)
        configure_iap15_calibration(self.protocol)
        super().program_mcu()
        print("STUDIOX_PROGRAM_COMPLETE", flush=True)

    def program_monitor(self):
        protocol = self.protocol
        data = self.opts.code_image.read()
        self.opts.code_image.seek(0)
        # 专用编码镜像不能走普通 program_flash；仅开放静态核对的型号、版本和内容。
        if (
            self.expected_model not in MONITOR_SETUP_HASHES
            or self.expected_hash != MONITOR_SETUP_HASHES[self.expected_model]
            or protocol.model.name != self.expected_model
            or protocol.model.code != 62464
            or hashlib.sha256(data).hexdigest().upper() != self.expected_hash
            or len(data) != 61440
            or type(protocol) is not Stc15Protocol
            or protocol.mcu_bsl_version != "7.2.5S"
            or self.clock_mode != "preserve"
            or len(protocol.status_packet) < 46
            or protocol.status_packet[45] != 0x70
        ):
            raise StcProtocolException(
                "StudioX monitor setup: unsupported target, BSL, or image; no erase/write"
            )
        self._studiox_last_program_progress = None
        protocol.progress_cb = self.report_program_progress
        print("STUDIOX_TARGET_VERIFIED " + self.expected_model, flush=True)
        print(
            "StudioX: factory monitor setup, dedicated 62/42 packets; exact image / 7.2.5S / status 70; target="
            + self.expected_model,
            flush=True,
        )
        if self.expected_model == "IAP15L2K61S2":
            print(
                "StudioX: IAP15L2K61S2 / 7.2.5S / status 70 hardware baseline verified on 2026-10-21",
                flush=True,
            )
        configure_iap15_calibration(protocol)
        protocol.handshake()
        protocol.erase_flash(len(data), protocol.model.code)
        for offset in range(0, len(data), protocol.PROGRAM_BLOCKSIZE):
            packet = bytes([0x62 if offset == 0 else 0x42]) + struct.pack(">H", offset)
            packet += bytes([0x5A, 0xA5]) + data[offset : offset + protocol.PROGRAM_BLOCKSIZE]
            protocol.write_packet(packet)
            response = protocol.read_packet()
            if len(response) < 2 or response[:2] != bytes([0x02, 0x54]):
                raise StcProtocolException(
                    "StudioX monitor setup: write acknowledgement failed at %04X" % offset
                )
            protocol.progress_cb(offset, protocol.PROGRAM_BLOCKSIZE, len(data))
        print("Finishing write:", flush=True)
        protocol.write_packet(bytes([0x07, 0, 0, 0x5A, 0xA5]))
        if protocol.read_packet()[:2] != bytes([0x07, 0x54]):
            raise StcProtocolException("StudioX monitor setup: finish acknowledgement failed")
        print("Finishing write: done", flush=True)
        protocol.program_options()
        protocol.disconnect()
        print("STUDIOX_PROGRAM_COMPLETE", flush=True)
        print("STUDIOX_MONITOR_SETUP_COMPLETE POWER_CYCLE_REQUIRED", flush=True)


def validate_hardware_options(text):
    if len(text) > 8192:
        raise ValueError("hardware options too large")
    def unique(pairs):
        values = {}
        for key, value in pairs:
            if key in values:
                raise ValueError("duplicate hardware option: " + key)
            values[key] = value
        return values
    values = json.loads(text, object_pairs_hook=unique)
    flags = {"reset_pin_enabled", "watchdog_por_enabled", "watchdog_stop_idle",
             "eeprom_lvd_inhibit", "eeprom_erase_enabled", "bsl_pindetect_enabled",
             "uart2_passthrough", "uart1_remap", "epwm_open_drain", "cpu_6t_enabled",
             "ale_enabled", "xram_enabled"}
    choices = {name: ("true", "false") for name in flags}
    choices.update({
        "clock_gain": ("low", "high"), "watchdog_prescale": tuple(str(2 ** i) for i in range(1, 9)),
        "low_voltage_reset": ("true", "false", "low", "high"),
        "low_voltage_threshold": tuple(str(i) for i in range(8)),
        "por_reset_delay": ("short", "long"), "rstout_por_state": ("low", "high"),
        "uart2_pin_mode": ("normal", "push-pull"), "cpu_core_voltage": ("low", "mid", "high"),
        "oscillator_stable_delay": ("4096", "8192", "16384", "32768"),
    })
    if not isinstance(values, dict):
        raise ValueError("hardware options must be an object")
    for key, value in values.items():
        if key not in choices or not isinstance(value, str) or value not in choices[key]:
            raise ValueError("invalid hardware option: " + key)
    return values


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", required=True)
    parser.add_argument("--expected-model", required=True)
    parser.add_argument("--expected-code-bytes", type=int, required=True)
    parser.add_argument("--expected-sha256", required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--baud", type=int, required=True)
    parser.add_argument("--clock-mode", choices=("preserve", "internal", "external"), required=True)
    parser.add_argument("--frequency-hz", type=int)
    parser.add_argument("--monitor-setup", action="store_true")
    parser.add_argument("--hardware-options", default="{}")
    args = parser.parse_args()
    try:
        hardware = validate_hardware_options(args.hardware_options)
    except (ValueError, TypeError) as error:
        parser.error(str(error))
    if args.monitor_setup and hardware:
        parser.error("monitor setup preserves hardware options; overrides refused")
    if not args.port.upper().startswith("COM") or not args.port[3:].isdigit():
        parser.error("invalid COM port")
    if args.expected_code_bytes <= 0 or args.expected_code_bytes > 65536:
        parser.error("invalid code capacity")
    if len(args.expected_sha256) != 64 or any(
        c not in "0123456789abcdefABCDEF" for c in args.expected_sha256
    ):
        parser.error("invalid image checksum")
    with open(args.image, "rb") as image:
        if hashlib.sha256(image.read()).hexdigest().upper() != args.expected_sha256.upper():
            parser.error("image checksum changed")
    if args.monitor_setup and (
        args.expected_model not in MONITOR_SETUP_HASHES
        or args.expected_code_bytes != 62464
        or args.expected_sha256.upper() != MONITOR_SETUP_HASHES[args.expected_model]
        or not 1200 <= args.baud <= 921600
        or args.clock_mode != "preserve"
        or args.frequency_hz is not None
    ):
        parser.error("unsupported monitor setup request; serial port not opened")
    options = argparse.Namespace(
        port=args.port,
        protocol="auto",
        handshake=2400,
        baud=args.baud,
        trim=(
            (args.frequency_hz / 1000.0)
            if args.clock_mode == "internal" and args.frequency_hz
            else 0.0
        ),
        option=None,
        debug=False,
        autoreset=False,
        resetcmd=None,
        resetpin="dtr",
        version=False,
        erase=False,
        eeprom_image=None,
        code_image=open(args.image, "rb"),
    )
    try:
        print(
            "StudioX: waiting for MCU; press and release the board's power/download button now.",
            flush=True,
        )
        return GuardedStcGal(
            options,
            args.expected_model,
            args.expected_code_bytes,
            args.expected_sha256.upper(),
            args.clock_mode,
            args.monitor_setup,
            hardware,
        ).run()
    finally:
        options.code_image.close()


if __name__ == "__main__":
    sys.exit(main())
