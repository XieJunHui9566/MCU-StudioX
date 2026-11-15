Core observer / indexed two-use safety regression (standalone entry)

This directory is permanent evidence. It is not registered in the common SDK
gate yet and does not change its README or existing execution contract.

Run with explicit tools and a fresh output directory on the Windows toolchain:
  python -B -X utf8 run.py --compiler <sdcc.exe> --assembler <sdas251.exe>
    --toolchain <SDCC_HOME> --source-lock <current-input-lock.json>
    --output <new-output-directory> --timeout 30
The source lock is an array of {path, sha256} build-input pins. All pins and both
tool binaries are checked before and after current execution. No IDE service,
default installed compiler, ISP host, device or network is used.

The four C cases differ only in the core SFR observed after two ordinary word
reads and a shared comparison. The object is seven bytes, the word is at +4,
and the tag is at +6. The explicit ABI4 argument is DR12; the return is R7.
Current original and mechanically renamed cases are compiled in small and large
models, OFF and ON, and assembled by the supplied real assembler. Pass decisions
use machine bytes, ABI output and memory events, never emission comments or
function names. All relative branches must already be resolved by the assembler.
The REL/LST binder rejects external references, nonempty relocation entries,
unexpected payload areas, sparse code or unaccounted instruction bytes.

ACC/B is executed by an explicit raw-opcode whitelist. Unknown opcodes fail.
All modeled return/core values and ordered memory events must match OFF versus
ON. PSW/PSW1 require exact encoded conservative fallback. Twelve malformed/core
capabilities per model must fail real compilation. The historical default OFF
ACC/B encoding is also fenced; intentional unrelated emitter changes require
independent review instead of silently replacing this evidence.

Historical-v5 contains original C/ASM/REL/LST plus raw machine samples, initial
state and captured results. They are copied from the failed V5 run, not generated
with the current compiler. Replaying them must detect four wrong results:
ACC 34H versus 56H and B 12H versus 78H, in both memory models. Memory transactions
remain identical. Original paths and SHA-256 are in source-provenance.json;
execution is self-contained and does not need those original paths or V5 binary.

The distinct values supplied to the two ordinary word reads deliberately detect
an accidental removed/reused transaction in this transformation. This is a raw
machine/event contract, not a claim that asynchronous mutation of a nonvolatile
C object is generally defined. No cycles or board timing are inferred.

This test does not replace final complete-object placement, caller/ISR/entry and
library closure, SDK16, actual V136/V135 cost qualification or target hardware
qualification. The typed object record is synthetic and must not grant arbitrary
peripheral or external pointer access. Default OFF and missing/unknown capability
remain conservative; a capability cannot turn core state into a peripheral.
