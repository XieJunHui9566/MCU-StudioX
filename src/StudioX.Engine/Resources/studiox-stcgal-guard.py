"""StudioX's exact-model gate for an externally installed stcgal 1.10.

No serial port is opened until the image checksum and command are validated.
The model comparison runs after the bootloader identifies the silicon and
before stcgal's handshake, erase, program, or option-writing sequence.
"""

import argparse
import hashlib
import sys

from stcgal.frontend import StcGal
from stcgal.protocols import StcProtocolException


ALLOWED_MODEL_NAMES = {
    "STC89C52RC": ("STC89C52RC/LE52RC",),
    "STC8G1K08": ("STC8G1K08-8PIN", "STC8G1K08-20/16PIN"),
    "STC8G1K17": ("STC8G1K17-8PIN", "STC8G1K17-20/16PIN"),
}


class GuardedStcGal(StcGal):
    def __init__(self, options, expected_model, expected_code_bytes, expected_hash, clock_mode):
        self.expected_model = expected_model
        self.expected_code_bytes = expected_code_bytes
        self.expected_hash = expected_hash
        self.clock_mode = clock_mode
        super().__init__(options)

    def program_mcu(self):
        detected = self.protocol.model.name
        print("StudioX detected model: " + detected, flush=True)
        allowed = ALLOWED_MODEL_NAMES.get(self.expected_model.upper(), (self.expected_model.upper(),))
        if detected.upper() not in allowed or self.protocol.model.code != self.expected_code_bytes:
            raise StcProtocolException(
                "StudioX exact-model guard: expected %s / %d bytes, detected %s / %d bytes; no erase/write"
                % (self.expected_model, self.expected_code_bytes, detected, self.protocol.model.code)
            )
        self.opts.code_image.seek(0)
        actual_hash = hashlib.sha256(self.opts.code_image.read()).hexdigest().upper()
        self.opts.code_image.seek(0)
        if actual_hash != self.expected_hash:
            raise StcProtocolException("StudioX image checksum changed; no erase/write")
        # stcgal 1.10's external-clock path seeds the RC trim with 24 MHz.
        # Refuse a one-step external->internal transition with an explicit RC
        # target, because otherwise the requested frequency is not applied.
        read_clock_source = getattr(self.protocol.options, "get_clock_source", None)
        if (self.clock_mode == "internal" and self.opts.trim > 0 and
                callable(read_clock_source) and read_clock_source() == "external"):
            raise StcProtocolException(
                "StudioX: external-to-internal RC trim needs a separate cold-start sequence; no erase/write"
            )
        print("STUDIOX_TARGET_VERIFIED " + self.expected_model, flush=True)
        super().program_mcu()
        print("STUDIOX_PROGRAM_COMPLETE", flush=True)


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
    args = parser.parse_args()
    if not args.port.upper().startswith("COM") or not args.port[3:].isdigit():
        parser.error("invalid COM port")
    if args.expected_code_bytes <= 0 or args.expected_code_bytes > 65536:
        parser.error("invalid code capacity")
    if len(args.expected_sha256) != 64 or any(c not in "0123456789abcdefABCDEF" for c in args.expected_sha256):
        parser.error("invalid image checksum")
    with open(args.image, "rb") as image:
        if hashlib.sha256(image.read()).hexdigest().upper() != args.expected_sha256.upper():
            parser.error("image checksum changed")
    options = argparse.Namespace(
        port=args.port, protocol="auto", handshake=2400, baud=args.baud,
        trim=(args.frequency_hz / 1000.0) if args.clock_mode == "internal" and args.frequency_hz else 0.0,
        option=(["clock_source=" + args.clock_mode] if args.clock_mode != "preserve" and
                not args.expected_model.upper().startswith(("STC8G", "STC8H")) else None),
        debug=False, autoreset=False, resetcmd=None, resetpin="dtr",
        version=False, erase=False, eeprom_image=None,
        code_image=open(args.image, "rb"),
    )
    try:
        print("StudioX: waiting for MCU; press and release the board's power/download button now.", flush=True)
        return GuardedStcGal(options, args.expected_model, args.expected_code_bytes,
                             args.expected_sha256.upper(), args.clock_mode).run()
    finally:
        options.code_image.close()


if __name__ == "__main__":
    sys.exit(main())
