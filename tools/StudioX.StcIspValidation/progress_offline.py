"""回放真实 STC15 写入协议的假应答，验证进度与错误边界；不打开串口。"""
import contextlib
import hashlib
import importlib.util
import io
import sys
from types import SimpleNamespace

from stcgal.frontend import StcGal
from stcgal.protocols import Stc15Protocol, StcProtocolException

spec = importlib.util.spec_from_file_location("studiox_guard", sys.argv[1])
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
image = bytes(range(256)) * 2
original = StcGal.program_mcu
checks = []


def fixture(failure=None, bad_model=False, bad_hash=False):
    protocol = object.__new__(Stc15Protocol)
    protocol.model = SimpleNamespace(name="IAP15L2K61S2" if bad_model else "IAP15F2K61S2", code=62464)
    protocol.options = SimpleNamespace(get_clock_source=lambda: "internal")
    protocol.bsl_version = 0x72
    protocol.PROGRAM_BLOCKSIZE = 64
    packets = []
    protocol.write_packet = lambda packet: packets.append(packet)

    def response():
        packet = packets[-1]
        if failure == "block" and len(packets) == 4:
            return b"\x02\x00"
        if packet[0] == 0x07:
            return b"\x07\x00" if failure == "finish" else b"\x07\x54"
        return b"\x02\x54"

    protocol.read_packet = response
    protocol.progress_cb = lambda *args: (_ for _ in ()).throw(AssertionError("original tqdm callback still in use"))
    obj = object.__new__(guard.GuardedStcGal)
    obj.protocol = protocol
    obj.expected_model = "IAP15F2K61S2"
    obj.expected_code_bytes = 62464
    obj.expected_hash = "invalid" if bad_hash else hashlib.sha256(image).hexdigest().upper()
    obj.clock_mode = "preserve"
    obj.monitor_setup = False
    obj.opts = SimpleNamespace(code_image=io.BytesIO(image), trim=0.0)
    return obj, packets


def run(failure=None, **kwargs):
    obj, packets = fixture(failure, **kwargs)
    output = io.StringIO()
    caught = None
    with contextlib.redirect_stdout(output):
        try:
            obj.program_mcu()
        except StcProtocolException as error:
            caught = error
    lines = output.getvalue().splitlines()
    progress = [tuple(map(int, line.split()[1:])) for line in lines if line.startswith("STUDIOX_ISP_PROGRESS ")]
    return lines, progress, caught, packets


try:
    # 保留真实分块/结束应答检查，只替换顶层流程中的串口准备、擦除与选项步骤。
    StcGal.program_mcu = lambda self: self.protocol.program_flash(image)
    lines, progress, error, packets = run()
    assert error is None and progress == [(count, 512) for count in range(64, 513, 64)]
    assert lines.count("STUDIOX_ISP_PROGRESS 512 512") == 1 and "STUDIOX_PROGRAM_COMPLETE" in lines
    assert len(packets) == 9 and packets[0][:5] == b"\x22\x00\x00\x5a\xa5" and packets[-1] == b"\x07\x00\x00\x5a\xa5"
    checks.append("ACK-confirmed counts, final sentinel deduplication and write packets preserved")

    lines, progress, error, packets = run("block")
    assert error is not None and progress == [(64, 512), (128, 512), (192, 512)] and len(packets) == 4
    assert "STUDIOX_PROGRAM_COMPLETE" not in lines
    checks.append("rejected block ACK neither advances byte count nor completes operation")

    lines, progress, error, packets = run("finish")
    assert error is not None and progress[-1] == (512, 512) and "STUDIOX_PROGRAM_COMPLETE" not in lines
    checks.append("100 percent acknowledged bytes cannot hide failed finish ACK")

    for values in [dict(bad_model=True), dict(bad_hash=True)]:
        lines, progress, error, packets = run(**values)
        assert error is not None and not packets and not progress and "STUDIOX_PROGRAM_COMPLETE" not in lines
    checks.append("model and checksum gates precede any write or byte progress")

    obj, _ = fixture()
    output = io.StringIO()
    with contextlib.redirect_stdout(output):
        for values in [(0, 64, 0), (-1, 64, 512), (513, 64, 512), (0, 0, 512)]:
            obj.report_program_progress(*values)
        obj.report_program_progress(448, 128, 512)
        obj.report_program_progress(512, 128, 512)
    assert output.getvalue().splitlines() == ["STUDIOX_ISP_PROGRESS 512 512"]
    checks.append("invalid callback values ignored and partial final blocks stay at 100 percent")
    print("PASS %d STC ISP progress checks; no serial or hardware access." % len(checks))
    for item in checks:
        print(item)
finally:
    StcGal.program_mcu = original
