namespace StudioX.Engine.Hdl;

/// <summary>固定的厂商原生布局布线流程；工程只提供 HDL 与 SDC，不替换下载布局。</summary>
internal static class Ag32NativeScripts
{
    internal const string PlaceAndRoute = """
        set sh_continue_on_error false
        set sh_echo_on_source true
        set cc_critical_as_fatal true
        set ::alta_work alta_db
        set_global_assignment -name ON_CHIP_BITSTREAM_DECOMPRESSION OFF
        load_architect -type $DEVICE
        alta::convert_pio_settings_cmd pins.vex "" alta_db/alta0.asf alta_db/alta0.apf alta_db/alta0.aqf
        read_design_and_pack -top pins -type vqm -ve pins.vex -sdc studiox-clocks.sdc -gclk_level 2 pins.vqm
        set_mode -skew basic -effort high -fitting auto -fitter full
        source studiox-gpio.asf
        place_pseudo -user_io -place_io -place_pll -place_gclk -warn_io
        place_and_route_design -quiet -retry 3
        report_timing -setup -brief -file setup.rpt
        report_timing -hold -brief -file hold.rpt
        report_timing -fmax -file fmax.rpt
        report_timing -coverage -file coverage.rpt
        write_routed_design pins_routed.v
        bitgen normal -bin pins.bin
        exit
        """;
}
