namespace StudioX.Application.Tools;

using StudioX.Engine;
using StudioX.Packages;

public sealed record ComponentMigrationPreview(string SourceDirectory, string DestinationDirectory, string SourceFingerprint,
    InstalledPack OriginalPack, InstalledPack TargetPack, ProjectManifest OriginalProject, ProjectManifest TargetProject,
    int Files, long Bytes, IReadOnlyList<string> Blockers)
{
    public bool CanCreate => Blockers.Count == 0;
    public string ToText() => $"原工程：{SourceDirectory}\n验证副本：{DestinationDirectory}\n"
        + $"器件包：{OriginalPack.Manifest.Id} {OriginalPack.Manifest.Version} → {TargetPack.Manifest.Version}\n"
        + $"器件 / 模板：{OriginalProject.DeviceId} / {OriginalProject.TemplateId}\n"
        + "目标开发环境组件：" + string.Join("、", ProjectDevelopmentComponents.FromSnapshot(TargetProject).Select(n => n.Id + "/" + n.Version))
        + $"\n保留 {Files:N0} 个用户文件，{Bytes:N0} 字节。复制应用源码、根 CMake 配置和编译设置；器件支持使用所选新包。\n"
        + "旧内容锁、构建缓存、调试会话和 Git 历史不复制。副本实际编译后建立新锁；原工程和原组件保留。\n"
        + (OriginalProject.Espressif is { } sdk ? $"SDK：{sdk.Framework} {sdk.SdkVersion} → {TargetProject.Espressif?.SdkVersion}，target={sdk.Target}。保留原 CMake、组件、sdkconfig 和 defaults；原始配置另存 migration-inputs，SDK 可能重写副本配置，API 差异须人工审阅。\n" : "")
        + (OriginalProject.PinMapping is not null ? "AGM：保留 VE、引脚命名、时钟、Verilog 和逻辑/仿真设置；重新生成系统支持及映射/逻辑镜像。本机 Supra 许可不随工程复制。\n" : "")
        + (CanCreate ? "可以创建并编译验证副本。编译结果不代表硬件验收。" : "尚不能自动创建：\n" + string.Join('\n', Blockers));
}
