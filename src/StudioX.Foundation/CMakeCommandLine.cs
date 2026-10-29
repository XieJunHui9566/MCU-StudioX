namespace StudioX.Foundation;

using System.Text;

/// <summary>拆分 CMake 导出的带引号命令，不经过 shell，也不执行命令替换。</summary>
public static class CMakeCommandLine
{
    public static string[] Split(string command)
    {
        var result = new List<string>();
        var part = new StringBuilder();
        var quoted = false;
        var started = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '\\')
            {
                var start = i;
                while (i < command.Length && command[i] == '\\')
                {
                    i++;
                }
                var count = i - start;
                if (i < command.Length && command[i] == '"')
                {
                    part.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        part.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else
                {
                    part.Append('\\', count);
                    i--;
                }
                started = true;
            }
            else if (c == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (started)
                {
                    result.Add(part.ToString());
                    part.Clear();
                    started = false;
                }
            }
            else
            {
                part.Append(c);
                started = true;
            }
        }
        if (quoted)
        {
            throw new StudioXException("CUBEMX_COMMAND", "编译数据库中有未闭合的参数引号。");
        }
        if (started)
        {
            result.Add(part.ToString());
        }
        return result.ToArray();
    }
}
