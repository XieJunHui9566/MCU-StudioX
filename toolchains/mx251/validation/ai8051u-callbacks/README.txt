AI8051U-34K64-32Bit（STC80251）固定回调应用

此工程原样来自 V0.0115/ai8051u/application，实际编译输入与该版一致。
CMakeLists.txt 选择 UART 传输和 ai8051u_test.h，不编译 usb_poll.c；保留的
usb_poll.c/stc32g_test.h 是原对照资料，不构成AI固件输入或外设支持声明。
算法、输入与断言没有变更。small/large 由统一 RunAcceptance.py 构建；
无需复制AI专属构建适配器，使用精确 AI8051U-34K64-32 profile。

原有22函数应用，128组输入，普通/混合与显式ABI4各10项结果；原生分支采用
实际函数指针，并有六项边界以及Timer0。共2560结果+6边界/模型。
计时10操作，每项64次调用，3样本；Timer0 SYSCLK/12，计时段关闭中断。
AI样品原IRC 39,943,200Hz，未修改；不能直接与STC的11,059,200Hz原始ticks比较。
逐位参考见 oracle.py；接口支持范围同 stc32g-callbacks/README.txt。

两模型实板证据在 V0.0115/ai8051u/hardware-results.json；本轮仅重建相同HEX，
不计为新硬件运行。原数学恢复证据保留，历史硬件NaN132差异未被隐藏。
不自动发送串口数据或下载，未做Flash回读。任意外设、嵌套ISR、全Flash仍未资格化。
