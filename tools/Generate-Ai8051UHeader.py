"""从已核对的寄存器事实表生成 MX251 32 位头文件；不依赖 Keil 或全局环境。"""

from pathlib import Path
import argparse
import hashlib
import json


def generate(data_path):
    data = json.loads(data_path.read_text(encoding='utf8'))
    registers = data['registers']
    names = [
        x['name']
        for key in ['registers', 'bits', 'constants', 'usbOffsets', 'vectors', 'mathCommands']
        for x in data[key]
    ]
    if len(names) != len(set(names)):
        raise ValueError('重复名称必须先消除，禁止静默覆盖寄存器定义')
    lines = [
        '/* MCU StudioX: AI8051U 32位寄存器定义。由 Generate-Ai8051UHeader.py 生成。',
        ' * 来源、地址勘误和覆盖范围见 register-reference.json 与 README.txt。',
        ' * 只声明寄存器，不自动初始化GPIO、时钟、外设或中断。',
        ' * 端口名称不代表当前封装已引出对应引脚。',
        ' * 数据表 SHA-256: ' + hashlib.sha256(data_path.read_bytes()).hexdigest(),
        ' */',
        '#ifndef STUDIOX_AI8051U_32BIT_H',
        '#define STUDIOX_AI8051U_32BIT_H',
        '',
        '#include <stdint.h>',
        '',
        '#ifndef __SDCC_mcs251',
        '#error "AI8051U.h requires MX251/SDCC mcs251 in 32-bit source mode"',
        '#endif',
        '',
        '/* SFR：8位直接地址。别名保留同一地址，不占用RAM。 */',
    ]
    for x in registers:
        if x['kind'] == 'sfr':
            lines.append(f'__sfr __at(0x{x["address"]:02X}) {x["name"]};')
    lines += ['', '/* 可位寻址SFR的单个位。非位寻址寄存器请使用下方掩码。 */']
    for x in data['bits']:
        lines.append(f'__sbit __at(0x{x["address"]:02X}) {x["name"]};')
    lines += [
        '',
        '/* 扩展寄存器：必须先 P_SW2 |= EAXFR；使用完整24位地址和8位volatile访问。',
        ' * 高低字节有硬件访问顺序，不能随意合并为uint16_t/uint32_t指针。 */',
    ]
    previous = None
    for x in registers:
        if x['kind'] != 'xfr':
            continue
        page = x['address'] >> 8
        if page != previous:
            lines += ['', f'/* XFR 0x{page:04X}00 */']
            previous = page
        qualifiers = 'const volatile' if x['readOnly'] else 'volatile'
        lines.append(
            f'#define {x["name"]:<24} (*({qualifiers} __xdata uint8_t *)0x{x["address"]:06X}UL)'
        )
    lines += [
        '',
        '/* 芯片身份区只读；使用数组下标读取时仍保持volatile。 */',
        '#define CHIPID  ((const volatile __xdata uint8_t *)0x7EFDE0UL)',
        '#define CHIPIDX ((const volatile __xdata uint8_t *)0x7EFBD0UL)',
        '',
        '/* STC命名的位掩码：EAXFR为0x80掩码，不能写 EAXFR = 1。 */',
    ]
    for x in data['constants']:
        lines.append(f'#define {x["name"]:<24} 0x{x["value"]:02X}U')
    lines += [
        '',
        '/* USB内部寄存器是USBADR/USBDAT间接访问的偏移量，不是存储器左值。 */',
        '#define USBBASE 0U',
    ]
    for x in data['usbOffsets']:
        lines.append(f'#define {x["name"]:<24} 0x{x["value"]:02X}U')
    lines += ['', '/* 中断号是芯片定义；声明这些常量不等同于全部ISR ABI已通过实板验收。 */']
    for x in data['vectors']:
        lines.append(f'#define {x["name"]:<24} {x["value"]}U')
    lines += [
        '',
        '/* 数学单元命令值。DMAIR必须用MOV direct,#立即数触发。',
        ' * 操作数和结果使用CPU寄存器，不能把DMAIR赋值当作普通C数学函数。 */',
    ]
    for x in data['mathCommands']:
        lines.append(f'#define {x["name"]:<36} 0x{x["value"]:02X}U')
    lines += [
        '',
        '/* 显式启停XFR访问；do/while保证可安全放在if/else语句中。 */',
        '#define EAXSFR() do { P_SW2 |= EAXFR; } while (0)',
        '#define EAXRAM() do { P_SW2 &= (uint8_t)~EAXFR; } while (0)',
        '',
        '/* NOP数量是指令条数，不是微秒；使用MX251内联汇编，不依赖Keil intrins.h。 */',
        '#define NOP1() do { __asm nop __endasm; } while (0)',
    ]
    for n in range(2, 41):
        lines.append(f'#define NOP{n}() do {{ NOP{n-1}(); NOP1(); }} while (0)')
    lines += ['#define NOP(N) NOP##N()', '', '#include "AI8051U_Delay.h"', '', '#endif', '']
    return '\n'.join(lines)


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    target = root / 'examples/packs/stc.stc251/include/AI8051U.h'
    generated = generate(root / 'tools/data/ai8051u-registers.json')
    if args.check:
        if target.read_text(encoding='utf8') != generated:
            raise ValueError('头文件与事实表不一致，请重新生成')
    else:
        target.write_text(generated, encoding='utf8', newline='\n')
    print(target)


if __name__ == '__main__':
    main()
