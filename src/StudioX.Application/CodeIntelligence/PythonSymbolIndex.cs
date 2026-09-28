namespace StudioX.Application.CodeIntelligence;

/// <summary>缩进作用域与显式绑定索引；动态属性、继承和运行时导入保持未解析。</summary>
internal sealed class PythonSymbolIndex
{
    private sealed class Scope(Scope? parent, int indent, bool isClass = false)
    {
        internal Scope? Parent { get; } = parent;
        internal int Indent { get; } = indent;
        internal bool IsClass { get; } = isClass;
        internal Dictionary<string, Symbol> Names { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Document(string path, string text, CancellationToken token)
    {
        internal string Path { get; } = path;
        internal string Text { get; } = text;
        internal List<PythonSyntax.Token> Tokens { get; } = PythonSyntax.Scan(text, -1, token).Tokens;
        internal Scope Root { get; } = new(null, -1);
        internal Dictionary<int, Scope> Scopes { get; } = [];
        internal Dictionary<int, Symbol> Bindings { get; } = [];
    }

    private sealed class Symbol(Document document, PythonSyntax.Token at, Scope owner)
    {
        internal Document Document { get; } = document;
        internal PythonSyntax.Token At { get; } = at;
        internal Scope Owner { get; } = owner;
        internal Scope? Body
        {
            get; set;
        }
        internal string? Module
        {
            get; set;
        }
        internal string? Imported
        {
            get; set;
        }
        internal string[]? Value
        {
            get; set;
        }
        internal string Key => Document.Path + ":" + At.Start;
    }

    private readonly Dictionary<string, Document> documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Symbol> modules = new(StringComparer.Ordinal);
    private readonly CancellationToken token;

    internal PythonSymbolIndex(IReadOnlyDictionary<string, string> sources, CancellationToken cancellation)
    {
        token = cancellation;
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var doc = new Document(source.Key, source.Value, token);
            documents.Add(source.Key, doc);
            var module = new Symbol(doc, new(ModuleName(source.Key), 0, 0, true), doc.Root) { Body = doc.Root };
            modules.TryAdd(ModuleName(source.Key), module);
            Parse(doc);
        }
    }

    internal IReadOnlyList<CodeLocation> Find(string path, int offset, bool references)
    {
        if (!documents.TryGetValue(path, out var doc))
        {
            return [];
        }
        var index = doc.Tokens.FindIndex(item => item.Name && item.Start <= offset && offset < item.End);
        if (index < 0)
        {
            index = doc.Tokens.FindIndex(item => item.Name && item.End == offset);
        }
        if (index < 0 || ResolveAt(doc, index, 0) is not { } symbol)
        {
            return [];
        }
        if (!references)
        {
            return [Location(symbol.Document, symbol.At)];
        }
        var result = new List<CodeLocation>();
        foreach (var source in documents.Values.Where(item => !item.Path.StartsWith(MicroPythonApiDocuments.Prefix, StringComparison.Ordinal)))
        {
            for (var i = 0; i < source.Tokens.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (source.Tokens[i].Name && ResolveAt(source, i, 0)?.Key == symbol.Key)
                {
                    result.Add(Location(source, source.Tokens[i]));
                }
            }
        }
        return result.Distinct().OrderBy(item => item.DocumentPath, StringComparer.Ordinal).ThenBy(item => item.Range.Start.Line)
            .ThenBy(item => item.Range.Start.Character).ToArray();
    }

    private static CodeLocation Location(Document doc, PythonSyntax.Token at) => new(doc.Path,
        new(CodePositions.FromOffset(doc.Text, at.Start), CodePositions.FromOffset(doc.Text, at.End)),
        doc.Path.StartsWith(MicroPythonApiDocuments.Prefix, StringComparison.Ordinal) ? "MicroPython API/" + doc.Path[MicroPythonApiDocuments.Prefix.Length..] : doc.Path);

    private static string ModuleName(string path)
    {
        var name = path.StartsWith(MicroPythonApiDocuments.Prefix, StringComparison.Ordinal) ? path[MicroPythonApiDocuments.Prefix.Length..] : path;
        if (name.StartsWith("lib/", StringComparison.Ordinal))
        {
            name = name[4..];
        }
        name = name[..^System.IO.Path.GetExtension(name).Length].Replace('/', '.');
        return name.EndsWith(".__init__", StringComparison.Ordinal) ? name[..^9] : name;
    }

    private void Parse(Document doc)
    {
        var scope = doc.Root;
        var line = new List<PythonSyntax.Token>();
        var depth = 0;
        foreach (var item in doc.Tokens.Append(new("\n", doc.Text.Length, doc.Text.Length)))
        {
            token.ThrowIfCancellationRequested();
            if (item.Value == "\n" && depth == 0)
            {
                if (line.Count > 0)
                {
                    ParseLine();
                    line.Clear();
                }
                continue;
            }
            if (item.Value is "(" or "[" or "{")
            {
                depth++;
            }
            else if (item.Value is ")" or "]" or "}")
            {
                depth = Math.Max(0, depth - 1);
            }
            if (item.Value != "\n")
            {
                line.Add(item);
            }
        }
        void ParseLine()
        {
            var lineStart = doc.Text.LastIndexOf('\n', Math.Max(0, line[0].Start - 1)) + 1;
            var indent = 0;
            foreach (var c in doc.Text[lineStart..line[0].Start])
            {
                indent += c == '\t' ? 8 - indent % 8 : 1;
            }
            while (scope.Parent is not null && indent <= scope.Indent)
            {
                scope = scope.Parent;
            }
            foreach (var item in line)
            {
                doc.Scopes[item.Start] = scope;
            }
            var first = line[0].Value == "async" ? 1 : 0;
            if (first >= line.Count)
            {
                return;
            }
            if (line[first].Value is "def" or "class" && first + 1 < line.Count && line[first + 1].Name)
            {
                var declaration = Bind(line[first + 1], scope);
                var isClass = line[first].Value == "class";
                var child = new Scope(scope, indent, isClass);
                declaration.Body = child;
                var nesting = 0;
                var parameter = true;
                var colon = -1;
                for (var i = first + 2; i < line.Count; i++)
                {
                    var part = line[i];
                    if (part.Value is "(" or "[" or "{")
                    {
                        nesting++;
                    }
                    else if (part.Value is ")" or "]" or "}")
                    {
                        nesting--;
                    }
                    else if (nesting == 0 && part.Value == ":")
                    {
                        colon = i;
                        break;
                    }
                    else if (!isClass && nesting == 1)
                    {
                        if (part.Value == ",")
                        {
                            parameter = true;
                        }
                        else if (parameter && part.Name)
                        {
                            Bind(part, child);
                            parameter = false;
                        }
                    }
                }
                if (colon >= 0 && colon + 1 < line.Count)
                {
                    foreach (var part in line.Skip(colon + 1))
                    {
                        doc.Scopes[part.Start] = child;
                    }
                }
                else
                {
                    scope = child;
                }
                return;
            }
            if (line[first].Value is "global" or "nonlocal")
            {
                foreach (var part in line.Skip(first + 1).Where(item => item.Name))
                {
                    scope.Redirects[part.Value] = line[first].Value;
                }
                return;
            }
            var import = line.FindIndex(item => item.Value == "import");
            if (line[first].Value is "import" or "from" && import >= 0)
            {
                var from = line[first].Value == "from" ? string.Concat(line.Skip(first + 1).Take(import - first - 1).Select(item => item.Value)) : null;
                for (var i = import + 1; i < line.Count; i++)
                {
                    if (!line[i].Name)
                    {
                        continue;
                    }
                    var original = line[i];
                    var name = original.Value;
                    while (i + 2 < line.Count && line[i + 1].Value == "." && line[i + 2].Name)
                    {
                        name += "." + line[i + 2].Value;
                        i += 2;
                    }
                    var alias = original;
                    var explicitAlias = i + 2 < line.Count && line[i + 1].Value == "as";
                    if (explicitAlias)
                    {
                        alias = line[i + 2];
                        i += 2;
                    }
                    var binding = Bind(alias, scope);
                    binding.Module = from ?? (explicitAlias ? name : name.Split('.')[0]);
                    binding.Imported = from is null ? null : name;
                    doc.Bindings[original.Start] = binding;
                }
                return;
            }
            for (var i = first; i < line.Count; i++)
            {
                var part = line[i];
                if (part.Name && i + 1 < line.Count && line[i + 1].Value == "=" &&
                    (i == first || line[i - 1].Value is "," or ";") && (i + 2 == line.Count || line[i + 2].Value != "="))
                {
                    var binding = Bind(part, BindingScope(scope, part.Value));
                    var chain = new List<string>();
                    var j = i + 2;
                    if (j < line.Count && line[j].Name)
                    {
                        chain.Add(line[j++].Value);
                        while (j + 1 < line.Count && line[j].Value == "." && line[j + 1].Name)
                        {
                            chain.Add(line[j + 1].Value);
                            j += 2;
                        }
                    }
                    // 只推断直接构造或名称赋值，不把算术表达式当成对象类型。
                    binding.Value = chain.Count > 0 && (j == line.Count || line[j].Value is "(" or ";") ? chain.ToArray() : null;
                }
                if (part.Value is "for" or "as" && i + 1 < line.Count && line[i + 1].Name)
                {
                    Bind(line[i + 1], BindingScope(scope, line[i + 1].Value));
                }
            }
        }
        Symbol Bind(PythonSyntax.Token at, Scope owner)
        {
            if (!owner.Names.TryGetValue(at.Value, out var symbol))
            {
                symbol = new Symbol(doc, at, owner);
                owner.Names.Add(at.Value, symbol);
            }
            doc.Bindings[at.Start] = symbol;
            doc.Scopes[at.Start] = owner;
            return symbol;
        }
    }

    private static Scope BindingScope(Scope scope, string name)
    {
        if (!scope.Redirects.TryGetValue(name, out var kind))
        {
            return scope;
        }
        if (kind == "global")
        {
            while (scope.Parent is not null)
            {
                scope = scope.Parent;
            }
            return scope;
        }
        for (var parent = scope.Parent; parent?.Parent is not null; parent = parent.Parent)
        {
            if (!parent.IsClass && parent.Names.ContainsKey(name))
            {
                return parent;
            }
        }
        return scope;
    }

    private Symbol? ResolveAt(Document doc, int index, int depth)
    {
        if (depth > 24 || !doc.Tokens[index].Name || PythonCatalog.KeywordSet.Contains(doc.Tokens[index].Value))
        {
            return null;
        }
        var at = doc.Tokens[index];
        if (doc.Bindings.TryGetValue(at.Start, out var bound))
        {
            return FollowImport(bound, depth + 1);
        }
        if (index > 0 && index + 1 < doc.Tokens.Count && doc.Tokens[index + 1].Value == "=" &&
            doc.Tokens[index - 1].Value is "(" or "," && (index + 2 == doc.Tokens.Count || doc.Tokens[index + 2].Value != "="))
        {
            // 关键字参数名称属于被调用函数，不能按当前作用域的同名变量算作引用。
            return null;
        }
        var parts = new List<string> { at.Value };
        while (index >= 2 && doc.Tokens[index - 1].Value == "." && doc.Tokens[index - 2].Name)
        {
            parts.Insert(0, doc.Tokens[index - 2].Value);
            index -= 2;
        }
        return ResolveChain(doc.Scopes.GetValueOrDefault(at.Start) ?? doc.Root, parts, depth + 1);
    }

    private Symbol? ResolveChain(Scope scope, IReadOnlyList<string> parts, int depth)
    {
        if (depth > 24 || parts.Count == 0)
        {
            return null;
        }
        Symbol? symbol = null;
        var initial = BindingScope(scope, parts[0]);
        for (var current = initial; current is not null; current = current.Parent)
        {
            if ((ReferenceEquals(current, initial) || !current.IsClass) && current.Names.TryGetValue(parts[0], out symbol))
            {
                break;
            }
        }
        if (symbol is null)
        {
            return null;
        }
        symbol = FollowImport(symbol, depth + 1);
        for (var i = 1; symbol is not null && i < parts.Count; i++)
        {
            var body = symbol.Body;
            if (body is null && symbol.Value is { } value)
            {
                var inferred = ResolveChain(symbol.Owner, value, depth + 1);
                body = inferred?.Body;
            }
            if (body is null && symbol.At.Value is "self" or "cls" && symbol.Owner.Parent?.IsClass == true)
            {
                body = symbol.Owner.Parent;
            }
            symbol = body?.Names.GetValueOrDefault(parts[i]);
            if (symbol is not null)
            {
                symbol = FollowImport(symbol, depth + 1);
            }
        }
        return symbol;
    }

    private Symbol? FollowImport(Symbol symbol, int depth)
    {
        if (depth > 24)
        {
            return null;
        }
        if (symbol.Module is not { } name)
        {
            return symbol;
        }
        if (name.StartsWith('.'))
        {
            var dots = name.TakeWhile(c => c == '.').Count();
            var path = ModuleName(symbol.Document.Path).Split('.').ToList();
            var levels = symbol.Document.Path.EndsWith("/__init__.py", StringComparison.Ordinal) ? dots - 1 : dots;
            if (levels > path.Count)
            {
                return null;
            }
            path.RemoveRange(path.Count - levels, levels);
            name = string.Join('.', path.Concat(name[dots..].Split('.', StringSplitOptions.RemoveEmptyEntries)));
        }
        var module = modules.GetValueOrDefault(name);
        if (symbol.Imported is not { } member)
        {
            return module;
        }
        var exported = module?.Body?.Names.GetValueOrDefault(member);
        return exported is not null ? FollowImport(exported, depth + 1) : modules.GetValueOrDefault(name + "." + member);
    }
}
