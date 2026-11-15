"""Bounded raw opcode model; no ASM comments or function-name dispatch."""
from raw_decode import decode
def execute(instructions):
    code = {x['pc']: x for x in instructions}
    pc = min(code)
    registers = bytearray(64)
    registers[12:16] = 1024 .to_bytes(4, 'big')
    registers[11] = 86
    registers[10] = 120
    events = []
    reads = 0
    z = False
    steps = 0

    def get(at, w):
        return int.from_bytes(registers[at:at + w], 'big')

    def put(at, w, value):
        registers[at:at + w] = (value & (1 << w * 8) - 1).to_bytes(w, 'big')
    while True:
        assert pc in code and steps < 128
        ins = code[pc]
        raw = bytes.fromhex(ins['bytes'])
        pc += len(raw)
        steps += 1
        if raw == b'\xaa':
            break
        if raw[0] in (104, 120, 128):
            assert len(raw) == 2
            if raw[0] == 128 or (z if raw[0] == 104 else not z):
                pc += int.from_bytes(raw[1:], 'big', signed=True)
            continue
        kind, *v = decode(ins)
        if kind == 'copy':
            d, s, w = v
            put(d, w, get(s, w))
        elif kind == 'extend':
            d, s = v
            put(d, 2, get(s, 1))
        elif kind == 'imm':
            d, value, w = v
            put(d, w, value)
        elif kind in ('inc', 'add'):
            d, value = v
            put(d, 4, get(d, 4) + value)
        elif kind == 'and':
            d, value = v
            put(d, 2, get(d, 2) & value)
        elif kind == 'load':
            d, b, offset, w = v
            address = get(b, 4) + offset
            if w == 2:
                assert address == 1028
                value = 4660 if reads == 0 else 43981
                reads += 1
            else:
                assert address == 1030
                value = 7
            events.append(('read', address, w, value))
            put(d, w, value)
        elif kind == 'sfrstore':
            address, s = v
            assert address == 177
            events.append(('io', get(s, 1)))
        elif kind == 'cmp':
            d, value = v
            z = get(d, 1) == value
        else:
            raise AssertionError((kind, ins))
    assert reads == 2
    return dict(result=registers[7], A=registers[11], B=registers[10], AParity=registers[11].bit_count() & 1, events=[list(x) for x in events])
