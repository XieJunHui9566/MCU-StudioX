namespace StudioX.Application.CodeIntelligence;

using StudioX.Engine;
using StudioX.Foundation;

/// <summary>从已校验的官方寄存器头生成通用 C 分析入口；不模拟 PIC ABI 或替代 XC8。</summary>
internal sealed record PicAnalysisProfile(string[] Flags, string HeaderRoot)
{
    public static async Task<PicAnalysisProfile> ReadAsync(string runtime, string data, ProjectManifest project, CancellationToken token)
    {
        var tools = await new ToolsetCatalog(Path.Combine(runtime, "toolsets"), data)
            .ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId, token);
        if (tools.Manifest.Purpose != "pic-xc8" || project.Pic is null || project.DeviceId != "PIC" + project.Pic.CompilerTarget)
        {
            throw new StudioXException("PIC_ANALYSIS", "PIC 编辑配置与固定工具组件不一致。");
        }
        var dfp = tools.ResourceDirectory("pic-dfp");
        var header = PathBoundary.Resolve(dfp, "pic/include/proc/" + project.DeviceId.ToLowerInvariant() + ".h").Replace('\\', '/');
        if (!File.Exists(header))
        {
            throw new StudioXException("PIC_ANALYSIS", "锁定的 DFP 缺少该型号的官方寄存器头。");
        }
        var directory = PathBoundary.Resolve(data, "language-cache/pic/" + tools.Fingerprint + "/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // 仅分析器读取此包装入口；真实编译始终使用官方 xc.h 与 DFP，保留全部 XC8 约束。
        await File.WriteAllTextAsync(Path.Combine(directory, "xc.h"),
            "#pragma once\n#define _XC_H_ 1\n#define _LIB_BUILD 1\n#define __at(address)\n#define __bit unsigned char\n" +
            "#define __interrupt(...)\n#define CLRWDT() ((void)0)\n#define NOP() ((void)0)\n" +
            "#define __delay_ms(value) ((void)0)\n#define __delay_us(value) ((void)0)\n" +
            "#include \"" + header + "\"\n", token);
        return new(["--target=i386-unknown-elf", "-std=c99", "-ffreestanding", "-ferror-limit=0", "-Wno-unknown-pragmas",
            "-I" + directory, "-isystem", tools.ResourceDirectory("pic-headers")], tools.RootDirectory);
    }
}
