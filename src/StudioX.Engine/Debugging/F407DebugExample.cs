namespace StudioX.Engine.Debugging;

/// <summary>固定离线示例；不是任意 C 程序的解释器，也不代表芯片实际读数。</summary>
public static class F407DebugExample
{
    public const string RelativeFile = "src/main.c";
    public const string Source = """
        #include "system_config.h"

        /* STM32F407 离线调试示例。
         * 用 F9 设置断点，F10 逐过程，F11 进入 ComputeOutput。
         * 当前调试窗口中的数值来自固定模拟模型，不读取芯片。
         */
        volatile uint32_t app_counter = 0;
        volatile uint32_t app_input = 0;
        volatile uint32_t app_output = 0;

        static uint32_t ComputeOutput(uint32_t input)
        {
            uint32_t doubled = input * 2;
            return doubled + 1;
        }

        int main(void)
        {
            System_Init();

            for (;;) {
                ++app_counter;
                app_input = app_counter & 0xff;
                app_output = ComputeOutput(app_input);
                System_Delay(100);
            }
        }
        """;
    public static int Line(string text) => Array.FindIndex(Source.Split('\n'), line => line.Trim() == text) + 1;
    public static int Entry => Line("System_Init();");
    public static int Increment => Line("++app_counter;");
    public static int Input => Line("app_input = app_counter & 0xff;");
    public static int Call => Line("app_output = ComputeOutput(app_input);");
    public static int Delay => Line("System_Delay(100);");
    public static int Function => Line("uint32_t doubled = input * 2;");
    public static int Return => Line("return doubled + 1;");
    public static int[] ExecutableLines => [Entry, Increment, Input, Call, Delay, Function, Return];
}
