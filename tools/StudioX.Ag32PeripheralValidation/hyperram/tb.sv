`timescale 1ns / 1ps

module tb;
    parameter real PERIOD = 12.5;
    parameter FIXED = 0;
    localparam FIRST_DATA_EDGE = FIXED ? 30 : 16;
    localparam CR0 = FIXED ? 16'h8f2f : 16'h8f27;

    reg clk = 0, clk90 = 0, bus_clock = 0, resetn = 0;
    always #(PERIOD / 2.0) clk = ~clk;
    always #PERIOD bus_clock = ~bus_clock;
    initial begin
        #(PERIOD / 4.0);
        forever #(PERIOD / 2.0) clk90 = ~clk90;
    end

    reg [1:0] trans = 0;
    reg [31:0] addr = 0, write_data = 0;
    reg write_enable = 0;
    wire [31:0] read_data;
    wire cs, ck, ram_resetn, ready, error;
    tri [7:0] dq;
    tri rwds;
    reg peer_enabled = 0, strobe = 0;
    reg [7:0] peer_data = 0;
    integer edges = 0, transactions = 0, byte_count = 0;
    reg [47:0] ca;
    reg [31:0] received, memory_word;

    assign dq = peer_enabled ? peer_data : 8'hzz;
    assign rwds = peer_enabled ? strobe : ((FIXED && cs === 0 && edges < 10) ? 1'b1 : 1'bz);
    pulldown(rwds);

    hram_ip dut(
        .hbus_adq(dq), .hbus_clk_out(ck), .hbus_csn(cs), .hbus_resetn(ram_resetn), .hbus_rwds(rwds),
        .hbus_clk90_out_data(clk90), .hbus_clk_out_data(clk), .sys_clock(clk), .bus_clock(bus_clock),
        .resetn(resetn), .stop(1'b0), .mem_ahb_htrans(trans), .mem_ahb_hready(1'b1),
        .mem_ahb_hwrite(write_enable), .mem_ahb_haddr(addr), .mem_ahb_hsize(addr[23] ? 3'd1 : 3'd2),
        .mem_ahb_hburst(3'd0), .mem_ahb_hwdata(write_data), .mem_ahb_hrdata(read_data),
        .mem_ahb_hreadyout(ready), .mem_ahb_hresp(error), .slave_ahb_hreadyout(1'b1),
        .slave_ahb_hresp(1'b0), .slave_ahb_hrdata(32'd0)
    );

    // 存储器模型只观察封装接口。把实际写入的四个字节沿 RWDS 两个边沿送回。
    always @(negedge cs) begin
        edges = 0;
        byte_count = 0;
        ca = 0;
        received = 0;
    end
    always @(posedge ck or negedge ck) if (cs === 0) begin
        if (edges < 6) begin
            ca = {ca[39:0], dq};
        end else if (dq !== 8'hzz && !peer_enabled) begin
            received = {received[23:0], dq};
            byte_count = byte_count + 1;
        end
        if (ca[47] && edges >= FIRST_DATA_EDGE) begin
            peer_enabled = 1;
            case (edges - FIRST_DATA_EDGE)
                0: peer_data = memory_word[31:24];
                1: peer_data = memory_word[23:16];
                2: peer_data = memory_word[15:8];
                3: peer_data = memory_word[7:0];
                default: peer_data = 8'h55;
            endcase
            #0.15;
            strobe = ~strobe;
        end
        edges = edges + 1;
    end
    always @(posedge cs) if (edges >= 6) begin
        if (!ca[47] && !ca[46]) memory_word = received;
        peer_enabled = 0;
        strobe = 0;
        transactions = transactions + 1;
    end

    task write_word(input [31:0] address, input [31:0] data);
        @(negedge clk);
        addr = address;
        write_enable = 1;
        trans = 2;
        @(negedge clk);
        trans = 0;
        write_data = data;
        wait (ready === 1);
        @(posedge clk);
        if (error !== 0) $fatal(1, "AHB write error");
    endtask

    initial begin
        $dumpfile("hyperram.vcd");
        $dumpvars(1, tb);
        repeat (10) @(negedge clk);
        if (ram_resetn !== 0) $fatal(1, "HyperRAM reset not asserted");
        resetn = 1;
        repeat (160) @(negedge clk);
        if (ram_resetn !== 1) $fatal(1, "HyperRAM reset not released");

        write_word('h801000, CR0);
        wait (transactions == 1);
        if (ca !== 48'h600001000000 || byte_count != 2 || received[15:0] !== CR0)
            $fatal(1, "CR0 address/data mismatch %h %h", ca, received);
        write_word('h124, 32'h1234cdef);
        wait (transactions == 2);
        if (ca !== 48'h200000120002 || byte_count != 4 || received !== 32'hcdef1234)
            $fatal(1, "HyperBus write mismatch %h %h", ca, received);

        @(negedge clk);
        addr = 'h124;
        write_enable = 0;
        trans = 2;
        @(negedge clk);
        trans = 0;
        wait (ready === 1);
        @(negedge clk);
        if (read_data !== 32'h1234cdef || error !== 0) $fatal(1, "AHB read mismatch %h", read_data);
        wait (transactions == 3);
        if (ca !== 48'ha00000120002) $fatal(1, "HyperBus read address mismatch %h", ca);
        $display("PASS reset, CR0=%h, HyperBus write, AHB readback=%h", CR0, read_data);
        $finish;
    end
    initial begin
        #100000;
        $fatal(1, "Timeout ready=%b cs=%b", ready, cs);
    end
endmodule
