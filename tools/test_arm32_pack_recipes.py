"""Boundary tests for vendor metadata/startup conversion; no hardware or downloads."""
import importlib.util
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

spec=importlib.util.spec_from_file_location('recipe',Path(__file__).with_name('New-Arm32ExpansionPacks.py'))
r=importlib.util.module_from_spec(spec);spec.loader.exec_module(r)

class RecipeTests(unittest.TestCase):
    def test_paths_reject_traversal_and_absolute_paths(self):
        for value in ('../secret','./../secret','/root/file','C:/file','a/../../b'):
            with self.subTest(value=value),self.assertRaises(ValueError):r.cleanpath(value)
        self.assertEqual(r.cleanpath('./Device\\x.h'),'Device/x.h')

    def test_device_memory_override_and_contiguous_bank(self):
        family=ET.fromstring('<family><memory name="FLASH_Bank1" start="0x08000000" size="0x10000" startup="1"/><memory name="FLASH_Bank2" start="0x08010000" size="0x10000"/><memory id="IRAM1" start="0x20000000" size="0x8000"/></family>')
        device=ET.fromstring('<device><memory id="IRAM1" start="0x20000000" size="0x1000"/></device>')
        self.assertEqual(r.layout([family,device])[:4],(0x08000000,0x20000,0x20000000,0x1000))
        family[1].set('start','0x08100000')
        with self.assertRaisesRegex(ValueError,'Discontinuous'):r.layout([family,device])

    def test_multicore_never_infers_cpu(self):
        node=ET.fromstring('<device><processor Dcore="Cortex-M7" Pname="CM7"/></device>')
        with self.assertRaisesRegex(ValueError,'Multi-core'):r.attrs([node],'processor')

    def test_conditions_match_selected_variant_as_well_as_inherited_base(self):
        root=ET.fromstring('<package><conditions><condition id="part"><accept Dname="LPC824M201JHI33"/></condition><condition id="base"><accept Dname="LPC824"/></condition></conditions></package>')
        chain=[ET.Element('device',Dname='LPC824'),ET.Element('variant',Dvariant='LPC824M201JHI33')]
        c=r.Conditions(root,chain)
        self.assertTrue(c.test('part'));self.assertTrue(c.test('base'))

    def test_condition_require_accept_deny_and_recursive(self):
        root=ET.fromstring('<package><conditions><condition id="a"><require Dfamily="X"/><accept Dname="X1*"/><accept Dname="X2*"/><deny Dname="X20"/></condition><condition id="loop"><require condition="loop"/></condition></conditions></package>')
        node=ET.Element('device',Dfamily='X',Dname='X21');c=r.Conditions(root,[node])
        self.assertTrue(c.test('a'));c.values['Dname']='X20';self.assertFalse(c.test('a'))
        with self.assertRaises(ValueError):c.test('loop')

    def test_vector_parser_does_not_append_flash_configuration(self):
        src='__Vectors:\n.long _estack\n.long Reset_Handler\n'+'.long Default_Handler\n'*14+'.size __Vectors, . - __Vectors\n.section .FlashConfig,"a"\n.long 0xFFFFFFFF\n'
        entries=r.vectors(src);self.assertEqual(len(entries),16)
        body=r.startup_text(entries,vendor_reset=True)
        self.assertNotIn('void Default_Handler(void) __attribute__',body)
        self.assertIn('void StudioX_CRuntime(void)',body)
        self.assertNotIn('    SystemInit();',body)

    def test_reset_preserves_vendor_erratum_and_initialization(self):
        src='Reset_Handler PROC\n IMPORT __main\n IMPORT System_Initializes\n LDR R0, =0x40000EE4 ; vendor erratum\n LDR R2, [R0]\n ORRS R2,R2,R1\n STR R2,[R0]\n LDR R0,=System_Initializes\n BLX R0\n LDR R0,=__main\n BX R0\n ENDP'
        result=r.arm_reset(src)
        for instruction in ('LDR R0, =0x40000EE4','STR R2,[R0]','LDR R0,=System_Initializes','LDR R0,=StudioX_CRuntime'):self.assertIn(instruction,result)

    def test_unknown_reset_directive_is_not_silently_dropped(self):
        with self.assertRaises(ValueError):r.arm_reset('Reset_Handler PROC\n IF unknown\n LDR R0, =__main\n BX R0\n ENDP')

    def test_gnu_armcc_reset_preserves_vtor(self):
        text='Reset_Handler:\n cpsid i\n .equ VTOR,0xE000ED08\n ldr r0,=VTOR\n ldr r1,=__Vectors\n str r1,[r0]\n ldr r0,=SystemInit\n blx r0\n cpsie i\n ldr r0,=__main\n bx r0\n .pool\n'
        self.assertIn('str r1,[r0]',r.arm_reset(text))

if __name__=='__main__':unittest.main()
