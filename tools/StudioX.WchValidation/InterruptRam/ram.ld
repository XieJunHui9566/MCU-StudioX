OUTPUT_ARCH(riscv)
ENTRY(_start)
MEMORY { RAM (rwx) : ORIGIN = 0x20000000, LENGTH = 48K }
SECTIONS
{
    .vectors : { KEEP(*(.vectors)) } > RAM
    .text : { KEEP(*(.text.start)) *(.text*) *(.rodata*) } > RAM
    .data : ALIGN(4) { __global_pointer$ = . + 0x800; *(.sdata*) *(.data*) } > RAM
    .bss (NOLOAD) : ALIGN(4) { _sbss = .; *(.sbss*) *(.bss*) *(COMMON) . = ALIGN(4); _ebss = .; } > RAM
    ASSERT(_ebss < 0x2000c000, "RAM fixture overlaps reserved result/snapshot/stack space")
}
