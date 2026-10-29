; Explicit MCS251 stack: link with --mcs251-stack; SPX derives from SSEG.
;--------------------------------------------------------------------------
;  crtstart.asm - C run-time: startup
;
;  Copyright (C) 2004, Erik Petrich
;
;  This library is free software; you can redistribute it and/or modify it
;  under the terms of the GNU General Public License as published by the
;  Free Software Foundation; either version 2, or (at your option) any
;  later version.
;
;  This library is distributed in the hope that it will be useful,
;  but WITHOUT ANY WARRANTY; without even the implied warranty of
;  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
;  GNU General Public License for more details.
;
;  You should have received a copy of the GNU General Public License 
;  along with this library; see the file COPYING. If not, write to the
;  Free Software Foundation, 51 Franklin Street, Fifth Floor, Boston,
;   MA 02110-1301, USA.
;
;  As a special exception, if you link this library with other files,
;  some of which are compiled with SDCC, to produce an executable,
;  this library does not by itself cause the resulting executable to
;  be covered by the GNU General Public License. This exception does
;  not however invalidate any other reasons why the executable file
;  might be covered by the GNU General Public License.
;--------------------------------------------------------------------------

	.area CSEG    (CODE)
	.area GSINIT0 (CODE)
	.area GSINIT1 (CODE)
	.area GSINIT2 (CODE)
	.area GSINIT3 (CODE)
	.area GSINIT4 (CODE)
	.area GSINIT5 (CODE)
	.area GSINIT  (CODE)
	.area GSFINAL (CODE)

	.globl __mcs251_stack_initial
	
	.area GSINIT0 (CODE)

__sdcc_gsinit_startup::
        ; STC32G USB BSL handoff: mask IE/IE2 before stack and C initialization.
        ; IE2.EUSB is write-only. Whole-byte writes do not depend on readback.
        ; Application owns other peripheral interrupt enables before setting EA.
        mov     0xa8,#0x00
        mov     0xaf,#0x00
        mov     spx,#__mcs251_stack_initial

	.area GSINIT2 (CODE)
	
        ecall   ___sdcc_external_startup
        mov     a,dpl
        jz      __sdcc_init_data
        ejmp    __sdcc_program_startup
__sdcc_init_data:
