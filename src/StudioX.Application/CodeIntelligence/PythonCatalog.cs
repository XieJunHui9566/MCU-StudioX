namespace StudioX.Application.CodeIntelligence;

/// <summary>无需解释器即可提供的 Python 3 关键字和常用内置函数。</summary>
internal static class PythonCatalog
{
    internal const string Keywords = "False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield";
    internal static readonly HashSet<string> KeywordSet = new(Keywords.Split(' '), StringComparer.Ordinal);

    internal static readonly IReadOnlyList<Entry> Entries =
    [
        Function("print", "*objects|sep=' '|end='\\n'|file=None|flush=False", "输出对象的文本表示。"),
        Function("len", "obj", "返回对象包含的元素数。"),
        Function("range", "start|stop|step=1", "生成整数序列；单参数写法为 range(stop)。"),
        Function("enumerate", "iterable|start=0", "迭代索引与元素组成的二元组。"),
        Function("zip", "*iterables|strict=False", "按位置组合多个可迭代对象。"),
        Function("sum", "iterable|start=0", "累加元素，可指定初始值。"),
        Function("min", "iterable|key=None|default=…", "返回最小项；也支持 min(arg1, arg2, ...) 写法。"),
        Function("max", "iterable|key=None|default=…", "返回最大项；也支持 max(arg1, arg2, ...) 写法。"),
        Function("sorted", "iterable|key=None|reverse=False", "返回排序后的新列表。"),
        Function("reversed", "seq", "返回逆序迭代器。"),
        Function("abs", "x", "返回绝对值。"),
        Function("round", "number|ndigits=None", "按指定精度舍入数值。"),
        Function("pow", "base|exp|mod=None", "计算乘方，可指定模数。"),
        Function("all", "iterable", "检查所有元素是否为真。"),
        Function("any", "iterable", "检查是否存在真值元素。"),
        Function("isinstance", "object|classinfo", "检查对象是否属于指定类型。"),
        Function("issubclass", "class|classinfo", "检查类的继承关系。"),
        Function("getattr", "object|name|default=…", "按名称读取属性。"),
        Function("hasattr", "object|name", "检查是否存在属性。"),
        Function("setattr", "object|name|value", "按名称设置属性。"),
        Function("input", "prompt=''", "读取一行输入。"),
        Function("open", "file|mode='r'|buffering=-1|encoding=None|errors=None|newline=None|closefd=True|opener=None", "打开文件；文本编码可通过 encoding 指定。"),
        Function("iter", "object|sentinel=…", "创建迭代器；第二参数用于可调用对象形式。"),
        Function("next", "iterator|default=…", "读取迭代器的下一项。"),
        Function("map", "function|iterable|*iterables", "迭代应用函数所得的结果。"),
        Function("filter", "function|iterable", "迭代保留满足条件的元素。"),
        Function("int", "string='0'|base=10", "转换为整数；数值转换形式为 int(number)。"),
        Function("float", "x=0.0", "转换为浮点数。"),
        Function("str", "object=''", "转换为字符串；字节解码另有 encoding/errors 参数。"),
        Function("bool", "object=False", "转换为布尔值。"),
        Function("list", "iterable=()", "创建列表。"),
        Function("tuple", "iterable=()", "创建元组。"),
        Function("set", "iterable=()", "创建集合。"),
        Function("dict", "*args|**kwargs", "从映射、键值对或关键字参数创建字典。"),
        Function("bytes", "source=…|encoding=…|errors='strict'", "创建不可变字节序列；字符串输入需要指定编码。"),
        Function("bytearray", "source=…|encoding=…|errors='strict'", "创建可变字节序列。"),
        Function("type", "object", "返回对象类型；动态建类使用三参数形式。"),
        Function("super", "type=…|object_or_type=…", "访问继承链中的后续实现；方法内常用 super()。"),
        Function("repr", "object", "返回对象的调试文本表示。"),
        Function("hex", "x", "返回整数的十六进制文本。"),
        Function("bin", "x", "返回整数的二进制文本。"),
        Function("oct", "x", "返回整数的八进制文本。"),
        Function("chr", "i", "将 Unicode 码点转换为字符。"),
        Function("ord", "c", "返回字符的 Unicode 码点。"),
        new("property", 7, "Python 3 内置装饰器", null),
        new("classmethod", 7, "Python 3 内置装饰器", null),
        new("staticmethod", 7, "Python 3 内置装饰器", null),
        new("Exception", 7, "Python 3 异常基类", null),
        new("ValueError", 7, "Python 3 值错误", null),
        new("TypeError", 7, "Python 3 类型错误", null),
        new("RuntimeError", 7, "Python 3 运行错误", null)
    ];

    private static Entry Function(string name, string parameters, string description)
    {
        var items = parameters.Split('|');
        return new(name, 3, "Python 3 常用调用形式（离线）",
            new(name + "(" + string.Join(", ", items) + ")", description, items, 0));
    }

    internal sealed record Entry(string Name, int Kind, string Detail, CodeSignature? Signature);
}
