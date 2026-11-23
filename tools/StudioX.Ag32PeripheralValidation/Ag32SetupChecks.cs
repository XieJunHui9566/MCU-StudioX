using StudioX.Engine;
using StudioX.Foundation;

internal static class Ag32SetupChecks
{
    internal static void Run()
    {
        var settings = new Ag32Setup();
        const string device = "AG32VF303CCT6";
        Ag32PinAssignment[] pins = [new("UART4_UARTRXD", 10)];
        Ag32BusSettings[] buses = [new("SPI0") { TxDmaChannel = 0 }];
        void Validate() => settings.Validate(device, pins, new(), [new(4, 115200, false)], buses,
            [new("GPTIMER0", 100), new("GPTIMER1", 100)], new(), new(8, 120, 60));
        void Reject(string diagnostic)
        {
            try
            {
                Validate();
            }
            catch (StudioXException ex) when (ex.Message.Contains(diagnostic, StringComparison.Ordinal))
            {
                return;
            }
            throw new InvalidOperationException("Expected configuration diagnostic: " + diagnostic);
        }

        settings.Units["DMA_CH1"] = new()
        {
            ["Request"] = "UART4_RX"
        };
        Reject("硬件 DMA 请求 6");
        buses = [new("SPI0") { RxDmaChannel = 1 }];
        Reject("DMA 通道 1");
        buses = [new("SPI0") { RxDmaChannel = 0 }];
        Validate();

        settings.Units["GPTIMER0"] = new()
        {
            ["Output"] = "UPDATE",
            ["Slave"] = "触发启动",
            ["Input"] = "GPTIMER1_TRGO"
        };
        settings.Units["GPTIMER1"] = new()
        {
            ["Output"] = "UPDATE",
            ["Slave"] = "复位",
            ["Input"] = "GPTIMER0_TRGO"
        };
        Reject("形成循环");
        settings.Units["GPTIMER1"].Remove("Slave");
        Validate();

        settings.Units["USB0"] = new()
        {
            ["EP1IN"] = "Bulk",
            ["Size1IN"] = "65"
        };
        Reject("EP1IN");
        settings.Units["USB0"]["Size1IN"] = "64";
        Validate();
        pins = [.. pins, new("GPIO0_1", Ag32Setup.UsbDmPin(device))];
        Reject("D− / D+");
        settings.Units.Clear();
        settings.Units["CRC0"] = new()
        {
            ["Width"] = "7",
            ["Polynomial"] = "09",
            ["Seed"] = "FF"
        };
        Reject("位宽");
        settings.Units["CRC0"]["Seed"] = "7F";
        settings.Units["BKP"] = new()
        {
            ["First"] = "3",
            ["Last"] = "2"
        };
        Reject("范围倒置");
        settings.Units.Remove("BKP");
        settings.Units["DMA_CH2"] = new()
        {
            ["Request"] = "MEMORY"
        };
        settings.Units["DMA_CH3"] = new()
        {
            ["Request"] = "MEMORY"
        };
        Validate();
        var dma = settings.Units["DMA_CH2"];
        dma["Layout"] = "分段缓冲区";
        dma["SrcBurst"] = "4";
        dma["DstBurst"] = "16";
        dma["SegmentSizes"] = "4093，7";
        Validate();
        if (settings.GetDmaSegmentUsage("DMA_CH2") != (2, 4100, 3))
        {
            throw new InvalidOperationException("DMA segment split");
        }
        dma["SegmentSizes"] = "130944,1";
        Reject("描述符");
        dma["SegmentSizes"] = "4,,2";
        Reject("第 2 段");
        dma["SegmentSizes"] = "4092";
        dma["DstBurst"] = "2";
        Reject("目标突发长度");
        dma.Remove("DstBurst");

        var timer = new Ag32TimerSettings("GPTIMER0", 1000, new()
        {
            CompareMode = "TOGGLE",
            CompareTicks = 99
        });
        Ag32PinAssignment[] comparePins = [new("GPTIMER0_CH0", 13)];
        var code = Ag32TimerInitialization.InitCode(timer, comparePins, 100000, out _);
        if (!code.Contains("GPTIMER_OCMODE_TOGGLE") || !code.Contains("oc.CompareValue = 99u;"))

        {
            throw new InvalidOperationException("Raw compare at ARR");
        }
        var center = new Ag32Setup();
        center.Units["GPTIMER0"] = new()
        {
            ["Direction"] = "中心对齐"
        };
        try
        {
            Ag32TimerInitialization.InitCode(timer, comparePins, 100000, out _, center);
            throw new InvalidOperationException("Compare exceeding center-aligned ARR was accepted");
        }
        catch (StudioXException ex) when (ex.Message.Contains("0～50"))
        {
        }
        settings.Units["GPTIMER0"] = new()
        {
            ["Repeat"] = "256"
        };
        Reject("重复计数");
        Console.WriteLine("PASS AG32 shared DMA requests and channels, timer trigger cycles, endpoint sizes and fixed USB pads");
    }
}
