namespace StudioX.Engine.Pic;

/// <summary>厂商 Java 后端的用户文件留在可写数据目录，发行组件保持只读。</summary>
internal static class PicJavaRuntime
{
    internal static readonly string[] AmbientVariables = [.. ToolsetEnvironment.AmbientVariables,
        "JAVA_HOME", "JRE_HOME", "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH", "MPLAB_LOG_OUTPUT_FILE"];

    internal static string[] Options(string userDirectory)
    {
        var logs = Path.Combine(userDirectory, "logs");
        var temporary = Path.Combine(userDirectory, "tmp");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(temporary);
        // MPLABLogger 默认按 JAR 位置写 MPLABXLog.xml，单独指定 user.home 或工作目录无效。
        // 使用厂商提供的日志目录属性；不忽略发行目录内的未知文件或放宽哈希检查。
        return ["-Duser.home=" + userDirectory, "-Dmplablogger.dir=" + logs,
            "-Djava.io.tmpdir=" + temporary, "-Duser.language=en", "-Duser.country=US", "-Dfile.encoding=UTF-8"];
    }
}
