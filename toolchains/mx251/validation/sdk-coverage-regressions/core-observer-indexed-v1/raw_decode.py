def decode(ins):
    b = bytes.fromhex(ins['bytes'])
    op = b[0]
    a = b[1] if len(b) > 1 else 0
    if op == 127 and len(b) == 2:
        return ('copy', (a >> 4) * 4, (a & 15) * 4, 4)
    if op == 125 and len(b) == 2:
        return ('copy', (a >> 4) * 2, (a & 15) * 2, 2)
    if op == 124 and len(b) == 2:
        return ('copy', a >> 4, a & 15, 1)
    if op == 229 and b == bytes.fromhex('e5f0'):
        return ('copy', 11, 10, 1)
    if op == 10 and len(b) == 2:
        return ('extend', (a >> 4) * 2, a & 15)
    if op == 11 and len(b) == 2 and (a & 15 in (12, 13, 14)):
        return ('inc', (a >> 4) * 4, {12: 1, 13: 2, 14: 4}[a & 15])
    if op == 46 and len(b) == 4 and (a & 15 == 8):
        return ('add', (a >> 4) * 4, int.from_bytes(b[2:], 'big'))
    if op == 94 and len(b) == 4 and (a & 15 == 4):
        return ('and', (a >> 4) * 2, int.from_bytes(b[2:], 'big'))
    if op in (78, 94, 110) and len(b) == 3 and (a & 15 == 0):
        return ('logic8', a >> 4, b[2], op)
    if op == 11 and len(b) == 3 and (a & 15 == 10) and (not b[2] & 15):
        return ('load', (b[2] >> 4) * 2, (a >> 4) * 4, 0, 2)
    if op in (41, 105) and len(b) == 4:
        return ('load', (a >> 4) * (2 if op == 105 else 1), (a & 15) * 4, int.from_bytes(b[2:], 'big', signed=True), 2 if op == 105 else 1)
    if op == 126 and len(b) == 3 and (a & 15 == 11) and (not b[2] & 15):
        return ('load', b[2] >> 4, (a >> 4) * 4, 0, 1)
    if op == 126 and len(b) == 4 and (a & 15 == 4):
        return ('imm', (a >> 4) * 2, int.from_bytes(b[2:], 'big'), 2)
    if op == 126 and len(b) == 3 and (a & 15 == 0):
        return ('imm', a >> 4, b[2], 1)
    if op == 126 and len(b) == 3 and (a & 15 == 1) and (b[2] in (224, 240)):
        return ('copy', a >> 4, 11 if b[2] == 224 else 10, 1)
    if op == 122 and len(b) == 3 and (a & 15 == 11) and (not b[2] & 15):
        return ('store', (a >> 4) * 4, b[2] >> 4)
    if op == 122 and len(b) == 3 and (a & 15 == 1):
        return ('sfrstore', b[2], a >> 4)
    if op == 190 and len(b) == 3 and (a & 15 == 0):
        return ('cmp', a >> 4, b[2])
    if op == 190 and len(b) == 4 and (a & 15 == 4):
        return ('cmpword', (a >> 4) * 2, int.from_bytes(b[2:], 'big'))
    raise AssertionError(('unknown raw region', ins))
