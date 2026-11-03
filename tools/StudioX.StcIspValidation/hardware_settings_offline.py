"""Cross-check the compiled C# UI contract against stcgal's real option encoders."""
import contextlib
import copy
import hashlib
import importlib.util
import io
import json
import sys
from types import SimpleNamespace

import serial
from stcgal import options
from stcgal.frontend import StcGal
from stcgal.protocols import StcProtocolException


def forbidden(*args, **kwargs):
    raise AssertionError("hardware access forbidden")


serial.Serial = forbidden
spec = importlib.util.spec_from_file_location("settings_guard", sys.argv[1])
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
contract = json.load(open(sys.argv[2], encoding="utf-8-sig"))
classes = {"89": (options.Stc89Option, 1), "12a": (options.Stc12AOption, 4),
           "12": (options.Stc12Option, 4), "15a": (options.Stc15AOption, 13),
           "15": (options.Stc15Option, 5), "8": (options.Stc8Option, 5)}
checks = 0
original = StcGal.program_mcu
programmed = []
StcGal.program_mcu = lambda self: programmed.append(list(self.opts.option or []))


def execute(raw, overrides, mode="preserve"):
    program = object.__new__(guard.GuardedStcGal)
    program.expected_model = "IAP15L2K61S2"
    program.expected_code_bytes = 62464
    image = b"offline-only"
    program.expected_hash = hashlib.sha256(image).hexdigest().upper()
    program.monitor_setup = False
    program.clock_mode = mode
    program.hardware_options = overrides
    program.protocol = SimpleNamespace(model=SimpleNamespace(name="IAP15L2K61S2", code=62464), options=raw)
    program.opts = SimpleNamespace(code_image=io.BytesIO(image), option=None, trim=0)
    with contextlib.redirect_stdout(io.StringIO()):
        program.program_mcu()


try:
    for entry in contract:
        cls, length = classes[entry["profile"]]
        for seed in range(256):
            raw = cls(seed if length == 1 else bytes([seed]) * length)
            available = {name for name, _, _ in raw.options}
            for field in entry["fields"]:
                assert field["id"] in available, (entry["target"], field["id"])
                for choice in field["values"]:
                    value = choice["value"]
                    with contextlib.redirect_stdout(io.StringIO()):
                        changed = copy.deepcopy(raw)
                        changed.set_option(field["id"], value)
                    actual = changed.get_option(field["id"])
                    assert str(actual).lower() == value, (entry["target"], field["id"], value, actual)
                    checks += 1
    for invalid in ("[]", "null", '{"a":"true"}', '{"watchdog_por_enabled":true}',
                    '{"watchdog_por_enabled":"yes"}', '{"uart2_pin_mode":"normal","uart2_pin_mode":"push-pull"}'):
        try:
            guard.validate_hardware_options(invalid)
            raise AssertionError("invalid JSON accepted: " + invalid)
        except (ValueError, TypeError):
            checks += 1
    raw = options.Stc15Option(bytes.fromhex("F7BA9FFDFF"))
    before = raw.get_msr()
    execute(raw, {"uart2_pin_mode": "normal", "rstout_por_state": "high"})
    assert raw.get_msr() == before  # dry run is isolated; actual application occurs in parent backend.
    assert programmed[-1] == ["rstout_por_state=high", "uart2_pin_mode=normal"]
    checks += 2
    for raw in (options.Stc15AOption(b"\xff" * 13), options.Stc8Option(b"\xff" * 5)):
        execute(raw, {}, "internal")
        assert programmed[-1] == []
        checks += 1
    for raw in (options.Stc12Option(b"\xff" * 4), options.Stc15Option(b"\xff" * 5)):
        execute(raw, {}, "internal")
        assert programmed[-1] == ["clock_source=internal"]
        checks += 1
    count = len(programmed)
    try:
        execute(options.Stc8Option(b"\xff" * 5), {}, "external")
        raise AssertionError("unsupported external clock accepted")
    except StcProtocolException:
        assert len(programmed) == count
        checks += 1
    for raw, overrides in ((options.Stc89Option(0xff), {"uart2_pin_mode": "normal"}),
                           (options.Stc8Option(b"\xff" * 5), {"low_voltage_threshold": "7"}),
                           (options.Stc15Option(b"\xff" * 4), {"cpu_core_voltage": "high"}),
                           (options.Stc15Option(b"\xff" * 5), {"cpu_core_voltage": "high"}),
                           (options.Stc15Option(b"\xff" * 5), {"low_voltage_reset": "low"})):
        count = len(programmed)
        try:
            execute(raw, overrides)
            raise AssertionError("unsupported field reached programmer")
        except StcProtocolException:
            assert len(programmed) == count
            checks += 1
    json.dump({"passed": True, "hardware": False, "checks": checks}, open(sys.argv[3], "w"), indent=2)
    print("PASS", checks, "backend option encodings and no-write guards")
finally:
    StcGal.program_mcu = original
