AI8051U-34K64-32Bit（STC80251）受限启动配置

内部精确target ID：AI8051U-34K64-32。startup.asm 原样取自已封存R89启动，
V0.0114/V0.0115 AI双模型使用相同源码；保留SDCC启动许可证及链接例外。
进入 GSINIT0 时 CLR bit AF(EA)，再设置链接器提供的SPX，调用外部启动钩子，
继续原有全局/静态初始化或skip-init路径。bit AF不是STC32G的字节SFR AF(IE2)。
本文件不清零IE2，也不重置全部外设，不改变时钟或芯片选项。

唯一构建规则：../../build/qualified/TargetContracts.json 中的同名条目。
保持原编译器默认向量策略，不启用STC的62向量预留；应用负责自身外设允许位。
仅standard运行库纳入当前双器件统一入口，不能据此断言AI没有MDU/数学外设。
当前验证布局与STC实验采用相同窗口，不表示两颗器件的全部容量/外设相同。
固定回调两模型证据见 validation/ai8051u-callbacks 与 V0.0115 封存报告。
