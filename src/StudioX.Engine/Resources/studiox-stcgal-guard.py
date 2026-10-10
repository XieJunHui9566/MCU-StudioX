"""StudioX's exact-model gate for an externally installed stcgal 1.10.

No serial port is opened until the image checksum and command are validated.
The model comparison runs after the bootloader identifies the silicon and
before stcgal's handshake, erase, program, or option-writing sequence.
"""

import argparse
import hashlib
import struct
import sys

from stcgal.frontend import StcGal
from stcgal.protocols import StcProtocolException, Stc15Protocol

MONITOR_SETUP_SHA256 = "ABC080915DDDCA9589F184E9BE76FBCC757EDF001BE02AF7A70ECB57451106F9"
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
    if (type(protocol) is not Stc15Protocol or protocol.model.name != "IAP15F2K61S2"
        or protocol.model.code != 62464 or getattr(protocol, "mcu_bsl_version", None) != "7.2.5S"
        or len(getattr(protocol, "status_packet", b"")) < 46 or protocol.status_packet[45] != 0x70
        or getattr(protocol, "external_clock", False)):
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
    def __init__(self, options, expected_model, expected_code_bytes, expected_hash, clock_mode, monitor_setup=False):
        self.expected_model = expected_model
        self.expected_code_bytes = expected_code_bytes
        self.expected_hash = expected_hash
        self.clock_mode = clock_mode
        self.monitor_setup = monitor_setup
        super().__init__(options)

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
        print("STUDIOX_TARGET_VERIFIED " + self.expected_model, flush=True)
        configure_iap15_calibration(self.protocol)
        super().program_mcu()
        print("STUDIOX_PROGRAM_COMPLETE", flush=True)

    def program_monitor(self):
        protocol = self.protocol
        data = self.opts.code_image.read()
        self.opts.code_image.seek(0)
        # 专用编码镜像不能走普通 program_flash；仅开放静态核对的型号、版本和内容。
        if (self.expected_model != "IAP15F2K61S2" or self.expected_hash != MONITOR_SETUP_SHA256
            or len(data) != 61440 or type(protocol) is not Stc15Protocol
            or protocol.mcu_bsl_version != "7.2.5S" or self.clock_mode != "preserve"
            or len(protocol.status_packet) < 46 or protocol.status_packet[45] != 0x70):
            raise StcProtocolException("StudioX monitor setup: unsupported target, BSL, or image; no erase/write")
        print("STUDIOX_TARGET_VERIFIED IAP15F2K61S2", flush=True)
        print("StudioX: factory monitor setup, dedicated 62/42 packets; validated on IAP15F2K61S2 / 7.2.5S / status 70", flush=True)
        configure_iap15_calibration(protocol)
        protocol.handshake()
        protocol.erase_flash(len(data), protocol.model.code)
        for offset in range(0, len(data), protocol.PROGRAM_BLOCKSIZE):
            packet = bytes([0x62 if offset == 0 else 0x42]) + struct.pack(">H", offset)
            packet += bytes([0x5a, 0xa5]) + data[offset:offset + protocol.PROGRAM_BLOCKSIZE]
            protocol.write_packet(packet)
            response = protocol.read_packet()
            if len(response) < 2 or response[:2] != bytes([0x02, 0x54]):
                raise StcProtocolException("StudioX monitor setup: write acknowledgement failed at %04X" % offset)
            protocol.progress_cb(offset, protocol.PROGRAM_BLOCKSIZE, len(data))
        protocol.write_packet(bytes([0x07, 0, 0, 0x5a, 0xa5]))
        if protocol.read_packet()[:2] != bytes([0x07, 0x54]):
            raise StcProtocolException("StudioX monitor setup: finish acknowledgement failed")
        protocol.program_options()
        protocol.disconnect()
        print("STUDIOX_PROGRAM_COMPLETE", flush=True)
        print("STUDIOX_MONITOR_SETUP_COMPLETE POWER_CYCLE_REQUIRED", flush=True)


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
    args = parser.parse_args()
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
    if args.monitor_setup and (args.expected_model != "IAP15F2K61S2" or args.expected_code_bytes != 62464
        or args.expected_sha256.upper() != MONITOR_SETUP_SHA256 or args.baud != 115200
        or args.clock_mode != "preserve" or args.frequency_hz is not None):
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
        option=(
            ["clock_source=" + args.clock_mode]
            if args.clock_mode != "preserve"
            and not args.expected_model.upper().startswith(("STC8G", "STC8H"))
            else None
        ),
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
        ).run()
    finally:
        options.code_image.close()


if __name__ == "__main__":
    sys.exit(main())
