# AI8051U transport regression

Run `dotnet run --project tools/StudioX.Mx251TransportChecks -- tools/StudioX.Mx251TransportChecks/fixtures` from the repository root.

The compact frames come from successful user-operated AiCube-ISP v6.97C downloads on 2026-11-03. Original API records and capture integrity summaries are in `.artifacts/ai8051-download-capture-20261103`. Both streams contain the same 215-byte MX251 0.0170 image (HEX SHA-256 `039518C398E54C57338DF212319CF4A10FA09BCA388D5E23B7247FBF429877CE`).

USB keeps this chip's current IRC calibration instead of copying the official tool's new trim; the comparison therefore substitutes only the seven current clock bytes. UART uses the existing bridge calibration algorithm, whose fine window starts one point later than the official tool. Eleven values are real captured measurements; the extra high endpoint is an explicitly synthetic non-winning value. The chosen trim, measured Hz, mode change, program packets and option packet match the successful serial capture. Fragmented byte reads include boot noise. Wrong-model, failed-ACK, clock-mismatch and full 64 KiB USB-address-window cases exercise only the new code. Boundary ACKs are synthetic.

These checks open no hardware. They do not establish StudioX hardware success, configuration readback, Flash readback or application correctness.
