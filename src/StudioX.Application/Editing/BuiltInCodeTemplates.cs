namespace StudioX.Application.Editing;

public static class BuiltInCodeTemplates
{
    public static IReadOnlyList<CodeTemplate> All
    {
        get;
    } = Array.AsReadOnly(new CodeTemplate[]
    {
        new("builtin-if", "条件判断", "sxif", "C/C++", "填写条件，可将选区放入语句体。", "if (${condition:condition})\n{\n    ${selection}${cursor}\n}"),
        new("builtin-for", "计数循环", "sxfor", "C/C++", "循环变量会在所有位置使用同一个填写值。", "for (int ${index:i} = 0; ${index} < ${count:count}; ++${index})\n{\n    ${selection}${cursor}\n}"),
        new("builtin-function", "函数定义", "sxfunc", "C/C++", "填写返回类型、函数名和参数。", "${returnType:void} ${functionName:function_name}(${arguments:void})\n{\n    ${selection}${cursor}\n}"),
        new("builtin-guard", "头文件保护", "sxguard", "C/C++", "填写项目自己的保护宏名。", "#ifndef ${guard:MY_HEADER_H}\n#define ${guard}\n\n${selection}${cursor}\n\n#endif /* ${guard} */"),
        new("builtin-pyfunc", "Python 函数", "sxdef", "Python", "保留当前缩进，填写函数名和参数。", "def ${functionName:function_name}(${arguments}):\n    ${cursor}pass"),
        new("builtin-cmake", "CMake 源文件列表", "sxsources", "CMake", "填写现有目标和要加入的源文件。", "target_sources(${target:app} PRIVATE\n    ${source:src/main.c}\n    ${cursor}\n)")
    });
}
