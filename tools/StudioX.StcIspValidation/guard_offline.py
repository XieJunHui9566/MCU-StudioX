"""Exercise the bundled guard with a fake protocol. Never opens a port."""

import hashlib
import importlib.util
import io
import sys
from types import SimpleNamespace

from stcgal.frontend import StcGal
from stcgal.protocols import StcProtocolException

script, image_path = sys.argv[1:3]
spec = importlib.util.spec_from_file_location("studiox_guard", script)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
image = open(image_path, "rb").read()
digest = hashlib.sha256(image).hexdigest().upper()
calls = []
original = StcGal.program_mcu
StcGal.program_mcu = lambda self: calls.append("program")


def check(model, code, expected, expected_code=None, source="internal", mode="preserve", trim=0.0):
    obj = object.__new__(module.GuardedStcGal)
    obj.expected_model = expected
    obj.expected_code_bytes = expected_code if expected_code is not None else code
    obj.expected_hash = digest
    obj.clock_mode = mode
    obj.protocol = SimpleNamespace(
        model=SimpleNamespace(name=model, code=code),
        options=SimpleNamespace(get_clock_source=lambda: source),
    )
    obj.opts = SimpleNamespace(code_image=io.BytesIO(image), trim=trim)
    obj.program_mcu()


try:
    check("IAP15F2K61S2", 62464, "IAP15F2K61S2")
    assert calls == ["program"]
    for model, code, expected, expected_code in (
        ("IAP15L2K61S2", 62464, "IAP15F2K61S2", 62464),
        ("IAP15F2K61S2", 61440, "IAP15F2K61S2", 62464),
        ("STC8G1K08A-8PIN", 8192, "STC8G1K08", 8192),
    ):
        try:
            check(model, code, expected, expected_code)
            raise AssertionError("mismatch was accepted")
        except StcProtocolException:
            assert calls == ["program"]
    check("STC8G1K08-8PIN", 8192, "STC8G1K08")
    assert calls == ["program", "program"]
    try:
        check("IAP15F2K61S2", 62464, "IAP15F2K61S2", source="external", mode="internal", trim=11059.2)
        raise AssertionError("external-to-internal one-step trim was accepted")
    except StcProtocolException:
        assert calls == ["program", "program"]
    print("STUDIOX_GUARD_OFFLINE_OK")
finally:
    StcGal.program_mcu = original
