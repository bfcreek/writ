using System.Text.Json;
using Writ.Compiler.Capabilities;
using Writ.Compiler.Diagnostics;
using Writ.Compiler.Ir;
using Writ.Compiler.Semantics;
using Writ.Compiler.Syntax;

namespace Writ.Compiler.Binding;

public sealed class Binder
{
    private readonly string? _filePath;
    private readonly DiagnosticBag _diagnostics;
    private readonly ISemanticResolver _semanticResolver;
    private readonly ICapabilityLoader _capabilities;
    private readonly ILanguageModel _model;
    private readonly IntentProposalLowerer _proposalLowerer;
    private readonly Dictionary<string, IrRecordType> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionSignature> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LoadedCapability> _loadedCapabilities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpServerDecl> _mcpServers = new(StringComparer.Ordinal);
    private readonly List<IrTypeDecl> _capabilityTypes = [];
    private readonly HashSet<EffectKind> _grantedEffects = [];
    private readonly CmdletCatalog _cmdlets;

    /// <summary>Capabilities discovered while binding, in declaration order. Used by lockfiles and `writ tools`.</summary>
    public IReadOnlyList<LoadedCapability> Capabilities => _loadedCapabilities.Values.ToList();

    /// <summary>Effects granted by program-level "Allow …" sentences.</summary>
    public IReadOnlyCollection<EffectKind> GrantedEffects => _grantedEffects;

    public Binder(
        string? filePath,
        DiagnosticBag diagnostics,
        ISemanticResolver? semanticResolver = null,
        ICapabilityLoader? capabilities = null,
        ILanguageModel? model = null,
        CmdletCatalog? cmdlets = null)
    {
        _filePath = filePath;
        _diagnostics = diagnostics;
        _semanticResolver = semanticResolver ?? new RuleBasedSemanticResolver();
        _capabilities = capabilities ?? new FileCapabilityLoader();
        _model = model ?? new InnateLanguageModel();
        _cmdlets = cmdlets ?? CmdletCatalog.Default;
        _proposalLowerer = new IntentProposalLowerer(filePath, diagnostics);
    }

    public IrModule Bind(ProgramAst program, string moduleName)
    {
        var fullPath = string.IsNullOrEmpty(_filePath) ? null : Path.GetFullPath(_filePath);
        if (fullPath is not null && !ScriptCompileStack.Enter(fullPath))
        {
            _diagnostics.Error("E4012", $"\"{Path.GetFileName(fullPath)}\" calls itself.", _filePath, program.Span);
            return new IrModule(moduleName, IrVersions.SchemaVersion, [], [], program.Span, IrVoidType.Instance);
        }

        try
        {
            return BindModule(program, moduleName);
        }
        finally
        {
            if (fullPath is not null)
            {
                ScriptCompileStack.Exit(fullPath);
            }
        }
    }

    private IrModule BindModule(ProgramAst program, string moduleName)
    {
        foreach (var decl in program.Declarations.OfType<TypeDeclAst>())
        {
            BindTypeDecl(decl);
        }

        foreach (var use in program.Declarations.OfType<UseDeclAst>())
        {
            BindUse(use);
        }

        BindDefaultMcp(program.Span);

        foreach (var allow in program.Declarations.OfType<AllowDeclAst>())
        {
            BindAllow(allow);
        }

        // Second pass: function signatures
        foreach (var fn in program.Declarations.OfType<FunctionAst>())
        {
            var parameters = fn.Parameters
                .Select(p => new IrParameter(p.Name, ResolveType(p.Type)))
                .ToList();
            var returnType = ResolveType(fn.ReturnType);
            if (!_functions.TryAdd(fn.Name, new FunctionSignature(fn.Name, parameters, returnType, IsBuiltin: false)))
            {
                _diagnostics.Error("E2001", $"Duplicate function '{fn.Name}'.", _filePath, fn.Span);
            }
        }

        RegisterBuiltins();

        var types = program.Declarations.OfType<TypeDeclAst>()
            .Select(t => new IrTypeDecl(
                t.Name,
                _records[t.Name].Fields,
                t.Span,
                _records[t.Name]))
            .Concat(_capabilityTypes)
            .ToList();

        var functions = program.Declarations.OfType<FunctionAst>()
            .Select(BindFunction)
            .ToList();

        return new IrModule(
            moduleName,
            IrVersions.SchemaVersion,
            types,
            functions,
            program.Span,
            IrVoidType.Instance);
    }

    private void BindTypeDecl(TypeDeclAst decl)
    {
        if (_records.ContainsKey(decl.Name))
        {
            _diagnostics.Error("E2002", $"Duplicate type '{decl.Name}'.", _filePath, decl.Span);
            return;
        }

        var inferred = InferFieldTypes(decl);
        var fields = new List<IrField>();
        foreach (var field in decl.Fields)
        {
            IrType type;
            if (field.Type is not null)
            {
                type = ResolveType(field.Type);
            }
            else if (inferred is not null && inferred.TryGetValue(field.Name, out var chosen))
            {
                type = chosen;
            }
            else if (inferred is null)
            {
                type = IrErrorType.Instance;
            }
            else
            {
                _diagnostics.Error("E2103",
                    $"Could not tell what type '{field.Name}' is. Name it, for example 'which is a decimal'.",
                    _filePath, field.Span);
                type = IrErrorType.Instance;
            }

            fields.Add(new IrField(field.Name, type));
        }

        _records[decl.Name] = new IrRecordType(decl.Name, fields);
    }

    private Dictionary<string, IrType>? InferFieldTypes(TypeDeclAst decl)
    {
        var untyped = decl.Fields.Where(field => field.Type is null).ToList();
        if (untyped.Count == 0)
        {
            return [];
        }

        var rules = decl.Fields
            .Where(field => field.Type is not null)
            .Select(field => $"{field.Name} is {DescribeAst(field.Type!)}")
            .ToList();
        var request = new ModelRequest(
            $"{decl.Name} has {string.Join(" and ", untyped.Select(field => field.Name))}",
            LanguageCatalog.Types,
            Operations: [],
            untyped.Select(field => field.Name).ToList(),
            rules,
            Symbols: [],
            Records: []);
        var accepted = ReadModel(request, decl.Span);
        if (accepted is null)
        {
            return null;
        }

        var types = new Dictionary<string, IrType>(StringComparer.Ordinal);
        foreach (var selection in accepted)
        {
            if (selection.Subject is null || untyped.All(field => field.Name != selection.Subject))
            {
                _diagnostics.Error("E2105",
                    $"The model returned '{selection.Id}' for '{selection.Subject}', which the text did not ask about.",
                    _filePath, decl.Span);
                continue;
            }

            var type = Primitive(selection.Id);
            if (type is null)
            {
                _diagnostics.Error("E2105",
                    $"The model returned '{selection.Id}' for '{selection.Subject}', which is not a type.",
                    _filePath, decl.Span);
                continue;
            }

            types[selection.Subject] = type;
        }

        return types;
    }

    private IrNode? BindImpliedOperation(string name, SourceSpan span, Scope scope)
    {
        EnsureMentionedMcp(name, span);
        var request = new ModelRequest(
            name,
            Types: [],
            OperationCatalog(),
            Subjects: [],
            scope.AllSymbols().Select(symbol => $"{symbol.Name} is defined").ToList(),
            scope.AllSymbols().Select(symbol => new SymbolInfo(symbol.Name, Describe(symbol.Type))).ToList(),
            _records.Values.Select(record => new RecordSchemaInfo(
                record.Name,
                record.Fields.Select(field => new RecordFieldInfo(field.Name, Describe(field.Type))).ToList())).ToList());
        var reading = Read(request, span);
        if (reading is null)
        {
            return new IrLiteral(null, span, IrErrorType.Instance, Provenance.ResolvedByModel);
        }

        if (reading.Selections.Count == 0)
        {
            if (reading.FailureReason is null)
            {
                return null;
            }

            _diagnostics.Error("E2103", reading.FailureReason, _filePath, span);
            return new IrLiteral(null, span, IrErrorType.Instance, Provenance.ResolvedByModel);
        }

        var accepted = ModelReadings.Accept(reading, request, _diagnostics, _filePath, span);
        RecordReading(request, accepted, span);
        if (accepted is null)
        {
            return new IrLiteral(null, span, IrErrorType.Instance, Provenance.ResolvedByModel);
        }

        return LowerSelections(accepted, span, scope)
            ?? new IrLiteral(null, span, IrErrorType.Instance, Provenance.ResolvedByModel);
    }

    private void BindAllow(AllowDeclAst allow)
    {
        foreach (var phrase in allow.Phrases)
        {
            var text = phrase.ToLowerInvariant();
            var matched = false;
            if (text.Contains("file") || text.Contains("folder") || text.Contains("disk"))
            {
                if (text.Contains("read") || text.Contains("list") || text.Contains("look"))
                {
                    _grantedEffects.Add(EffectKind.FileSystemRead);
                    matched = true;
                }

                if (text.Contains("writ") || text.Contains("delet") || text.Contains("creat") || text.Contains("chang") || text.Contains("remov"))
                {
                    _grantedEffects.Add(EffectKind.FileSystemWrite);
                    _grantedEffects.Add(EffectKind.FileSystemRead);
                    matched = true;
                }
            }

            if (text.Contains("network") || text.Contains("web") || text.Contains("internet") || text.Contains("http"))
            {
                _grantedEffects.Add(EffectKind.NetworkRead);
                if (text.Contains("writ") || text.Contains("post") || text.Contains("send") || text.Contains("upload"))
                {
                    _grantedEffects.Add(EffectKind.NetworkWrite);
                }

                matched = true;
            }

            if (text.Contains("script"))
            {
                _grantedEffects.Add(EffectKind.Script);
                matched = true;
            }

            if (text.Contains("process") || text.Contains("service") || text.Contains("system") || text.Contains("external") || text.Contains("tool"))
            {
                _grantedEffects.Add(EffectKind.NetworkRead);
                if (text.Contains("chang") || text.Contains("stop") || text.Contains("start") || text.Contains("kill") || text.Contains("writ") || text.Contains("control"))
                {
                    _grantedEffects.Add(EffectKind.ExternalState);
                }

                matched = true;
            }

            if (!matched)
            {
                _diagnostics.Error("E4007",
                    $"'{phrase}' is not a permission this compiler knows. Try 'reading files', 'writing files', 'network access', 'changing processes', or 'running scripts'.",
                    _filePath, allow.Span);
            }
        }
    }

    private List<CatalogEntry> OperationCatalog()
    {
        var operations = new List<CatalogEntry>(LanguageCatalog.Operations);
        operations.AddRange(_cmdlets.AsCatalogEntries());
        foreach (var capability in _loadedCapabilities.Values)
        {
            foreach (var operation in capability.Operations.Values)
            {
                if (capability.AllowedOperations.Count > 0 &&
                    !capability.AllowedOperations.Contains(operation.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                operations.Add(new CatalogEntry(
                    $"{capability.Alias}.{operation.Name}",
                    $"{capability.Alias} {operation.Name.Replace('_', ' ')}",
                    operation.Arguments.Select(argument => argument.Name).ToList()));
            }
        }

        return operations;
    }

    private IrNode? LowerSelections(IReadOnlyList<ModelSelection> selections, SourceSpan span, Scope scope)
    {
        var steps = new List<IrNode>();
        foreach (var selection in selections.OrderBy(item => item.Order))
        {
            var step = LowerSelection(selection, span, scope);
            if (step is null)
            {
                return null;
            }

            steps.Add(step);
        }

        if (steps.Count == 1)
        {
            return steps[0];
        }

        return new IrSequence(steps, span, steps[^1].Type, Provenance.ResolvedByModel);
    }

    private IrNode? LowerSelection(ModelSelection selection, SourceSpan span, Scope scope)
    {
        IrType? Lookup(string symbol) => scope.TryLookup(symbol, out var type) ? type : null;
        switch (selection.Id)
        {
            case "sumProjection":
            {
                if (!TryBinding(selection, "collection", span, out var collection) ||
                    !TryBinding(selection, "iterator", span, out var iterator) ||
                    !TryBinding(selection, "left", span, out var left) ||
                    !TryBinding(selection, "right", span, out var right))
                {
                    return null;
                }

                return _proposalLowerer.Lower(
                    new SumProjectionProposal(
                        collection,
                        iterator,
                        new ProposalBinary(new ProposalField(left), "*", new ProposalField(right))),
                    span,
                    Lookup);
            }
            case "filter":
            {
                if (!TryBinding(selection, "collection", span, out var collection) ||
                    !TryBinding(selection, "iterator", span, out var iterator) ||
                    !TryBinding(selection, "field", span, out var field) ||
                    !TryBinding(selection, "operator", span, out var op) ||
                    !TryBinding(selection, "value", span, out var rawValue))
                {
                    return null;
                }

                object literal = long.TryParse(rawValue, out var number) ? number : rawValue;
                return _proposalLowerer.Lower(
                    new FilterProposal(
                        collection,
                        iterator,
                        new ProposalBinary(new ProposalField(field), op, new ProposalLiteral(literal))),
                    span,
                    Lookup);
            }
            default:
                return selection.Id.StartsWith("pwsh.", StringComparison.Ordinal)
                    ? LowerCmdletSelection(selection, span, scope)
                    : LowerCapabilitySelection(selection, span, scope);
        }
    }

    private IrNode? LowerCmdletSelection(ModelSelection selection, SourceSpan span, Scope scope)
    {
        var entry = _cmdlets.Find(selection.Id["pwsh.".Length..]);
        if (entry is null)
        {
            _diagnostics.Error("E4002", $"'{selection.Id}' is not in the pinned command catalog.", _filePath, span);
            return null;
        }

        if (entry.Effect != EffectKind.Pure && !_grantedEffects.Contains(entry.Effect))
        {
            _diagnostics.Error("E4003",
                $"'{entry.Name}' requires effect '{DescribeEffect(entry.Effect)}', which this program does not allow. Add 'Allow {AllowPhrase(entry.Effect)}'.",
                _filePath, span);
            return null;
        }

        var arguments = new List<IrNamedArgument>();
        foreach (var parameter in entry.Parameters)
        {
            if (!selection.Bindings.TryGetValue(parameter.Name, out var bound))
            {
                if (parameter.Required)
                {
                    _diagnostics.Error("E4005", $"'{entry.Name}' needs '{parameter.Name}', and the sentence does not say which value that is.", _filePath, span);
                }

                continue;
            }

            if (parameter.IsSwitch)
            {
                arguments.Add(new IrNamedArgument(parameter.Name, new IrLiteral(true, span, IrPrimitiveType.Boolean, Provenance.ResolvedByModel)));
                continue;
            }

            IrNode value;
            if (scope.TryLookup(bound, out var symbolType) && symbolType is not null)
            {
                value = new IrVar(bound, span, symbolType, Provenance.ResolvedByModel);
            }
            else if (bound.StartsWith('"') && bound.EndsWith('"') && bound.Length >= 2)
            {
                value = new IrLiteral(bound[1..^1], span, IrPrimitiveType.String, Provenance.ResolvedByModel);
            }
            else
            {
                _diagnostics.Error("E4005", $"'{entry.Name}' parameter '{parameter.Name}' was bound to '{bound}', which is not in scope.", _filePath, span);
                continue;
            }

            if (parameter.Type is not IrDynamicType && !TypesCompatible(parameter.Type, value.Type))
            {
                _diagnostics.Error("E4005",
                    $"'{entry.Name}' parameter '{parameter.Name}' expects '{Describe(parameter.Type)}', got '{Describe(value.Type)}'.",
                    _filePath, span);
            }

            arguments.Add(new IrNamedArgument(parameter.Name, value));
        }

        foreach (var fixedArgument in entry.Fixed)
        {
            arguments.Add(new IrNamedArgument(fixedArgument.Key, new IrLiteral(fixedArgument.Value, span, IrPrimitiveType.Boolean, Provenance.ResolvedByModel)));
        }

        return new IrCommand(
            entry.Name,
            arguments,
            entry.Effect == EffectKind.Pure ? [EffectKind.Pure] : [entry.Effect],
            $"pwsh:{entry.Name}@{_cmdlets.Hash}",
            span,
            entry.Output,
            Provenance.ResolvedByModel);
    }

    private static string AllowPhrase(EffectKind effect) => effect switch
    {
        EffectKind.FileSystemRead => "reading files",
        EffectKind.FileSystemWrite => "writing files",
        EffectKind.NetworkRead or EffectKind.NetworkWrite => "network access",
        EffectKind.ExternalState => "changing processes",
        EffectKind.Script => "running scripts",
        _ => effect.ToString()
    };

    private IrNode? LowerCapabilitySelection(ModelSelection selection, SourceSpan span, Scope scope)
    {
        var dot = selection.Id.IndexOf('.');
        if (dot <= 0 || dot == selection.Id.Length - 1)
        {
            _diagnostics.Error("E2105",
                $"The model returned '{selection.Id}', which this compiler cannot apply.",
                _filePath, span);
            return null;
        }

        var alias = selection.Id[..dot];
        var operationName = selection.Id[(dot + 1)..];
        if (!_loadedCapabilities.TryGetValue(alias, out var capability) ||
            !capability.Operations.TryGetValue(operationName, out var operation) ||
            (capability.AllowedOperations.Count > 0 &&
             !capability.AllowedOperations.Contains(operationName, StringComparer.Ordinal)))
        {
            _diagnostics.Error("E4002",
                $"Capability '{alias}' has no allowed operation '{operationName}'.",
                _filePath, span);
            return null;
        }

        CheckCapabilityEffects(capability, operation, span);

        var arguments = new List<IrNamedArgument>();
        foreach (var argument in operation.Arguments)
        {
            if (!selection.Bindings.TryGetValue(argument.Name, out var symbolName) ||
                !scope.TryLookup(symbolName, out var symbolType) ||
                symbolType is null)
            {
                if (argument.Required)
                {
                    _diagnostics.Error("E4005",
                        $"Missing argument '{argument.Name}' for '{operationName}'.",
                        _filePath, span);
                }

                continue;
            }

            if (!TypesCompatible(argument.Type, symbolType))
            {
                _diagnostics.Error("E4005",
                    $"Argument '{argument.Name}' expects '{Describe(argument.Type)}', got '{Describe(symbolType)}'.",
                    _filePath, span);
            }

            arguments.Add(new IrNamedArgument(
                argument.Name,
                new IrVar(symbolName, span, symbolType, Provenance.ResolvedByModel)));
        }

        return new IrCapabilityCall(
            alias,
            operationName,
            $"{alias}@{capability.VersionHash}",
            capability.ManifestPath,
            arguments,
            operation.Effects,
            span,
            operation.ResultType,
            Provenance.ResolvedByModel)
        {
            Provider = capability.Provider,
            Endpoint = capability.Endpoint,
            Transport = capability.Transport,
            WorkingDirectory = capability.WorkingDirectory
        };
    }

    /// <summary>
    /// An operation's effects must be granted either by the capability manifest or by a
    /// program-level "Allow …" sentence (the only option for a bare MCP server).
    /// </summary>
    private void CheckCapabilityEffects(LoadedCapability capability, BoundOperation operation, SourceSpan span)
    {
        foreach (var effect in operation.Effects)
        {
            if (effect == EffectKind.Pure || capability.GrantedEffects.Contains(effect) || _grantedEffects.Contains(effect))
            {
                continue;
            }

            _diagnostics.Error("E4003",
                $"Operation '{capability.Alias}.{operation.Name}' requires effect '{DescribeEffect(effect)}', which was not granted. Add 'Allow {AllowPhrase(effect)}' or grant it in the manifest.",
                _filePath, span);
        }
    }

    private bool TryBinding(ModelSelection selection, string key, SourceSpan span, out string value)
    {
        if (selection.Bindings.TryGetValue(key, out value!))
        {
            return true;
        }

        _diagnostics.Error("E2105",
            $"The model returned '{selection.Id}' without a '{key}' binding.",
            _filePath, span);
        value = "";
        return false;
    }

    private IReadOnlyList<ModelSelection>? ReadModel(ModelRequest request, SourceSpan span)
    {
        var reading = Read(request, span);
        if (reading is null)
        {
            return null;
        }

        if (reading.Selections.Count == 0)
        {
            return [];
        }

        var accepted = ModelReadings.Accept(reading, request, _diagnostics, _filePath, span);
        RecordReading(request, accepted, span);
        return accepted;
    }

    private readonly List<Compilation.LockedReading> _readings = [];

    /// <summary>Accepted model readings in source order, for the lockfile and `writ explain`.</summary>
    public IReadOnlyList<Compilation.LockedReading> Readings => _readings;

    private void RecordReading(ModelRequest request, IReadOnlyList<ModelSelection>? accepted, SourceSpan span)
    {
        if (accepted is null)
        {
            return;
        }

        _readings.Add(new Compilation.LockedReading
        {
            Where = $"{span.StartLine}:{span.StartColumn}",
            Text = request.Text,
            Selections = accepted
                .Select(s => s.Order + ":" + s.Id + (string.IsNullOrEmpty(s.Subject) ? "" : "(" + s.Subject + ")")
                              + (s.Bindings.Count == 0 ? "" : "{" + string.Join(",", s.Bindings.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => b.Key + "=" + b.Value)) + "}"))
                .ToList()
        });
    }

    private ModelReading? Read(ModelRequest request, SourceSpan span)
    {
        try
        {
            return _model.ReadAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _diagnostics.Error("E2106", $"Language model failed: {ex.Message}", _filePath, span);
            return null;
        }
    }

    private static IrType? Primitive(string id) => id switch
    {
        "integer" => IrPrimitiveType.Integer,
        "decimal" => IrPrimitiveType.Decimal,
        "string" => IrPrimitiveType.String,
        "boolean" => IrPrimitiveType.Boolean,
        _ => null
    };

    private static string DescribeAst(TypeAst type) => type switch
    {
        NamedTypeAst named => named.Name,
        ListTypeAst list => $"list of {DescribeAst(list.ElementType)}",
        _ => "unknown"
    };

    private void BindUse(UseDeclAst use)
    {
        if (_loadedCapabilities.ContainsKey(use.Alias))
        {
            _diagnostics.Error("E4004", $"Capability '{use.Alias}' is already in use.", _filePath, use.Span);
            return;
        }

        var loaded = _capabilities.Load(use.Alias, new CapabilitySource(use.SourceKind, use.ManifestPath), _filePath, _diagnostics, use.Span);
        if (loaded is null)
        {
            return;
        }

        AdoptCapability(loaded, use.Span);
    }

    /// <summary>
    /// Servers in writ.mcp.json are available when a program names them.
    /// Closer files win, the user file is the fallback, and an explicit "Use" wins over both.
    /// Discovery still only lists tools, and only for an alias the program requires.
    /// </summary>
    private void BindDefaultMcp(SourceSpan span)
    {
        var merged = McpDefaults.Merge(_filePath);
        foreach (var error in merged.Errors)
        {
            _diagnostics.Error(error.Code, error.Message, _filePath, span);
        }

        foreach (var server in merged.Servers)
        {
            if (_loadedCapabilities.ContainsKey(server.Alias))
            {
                continue;
            }

            _mcpServers[server.Alias] = server;
        }
    }

    /// <summary>
    /// Query a defaults server when the sentence names its alias, so the model can select a tool.
    /// Servers the sentence does not name are left alone.
    /// </summary>
    private void EnsureMentionedMcp(string text, SourceSpan span)
    {
        foreach (var server in _mcpServers.Values)
        {
            if (ContainsWord(text, server.Alias))
            {
                EnsureMcpServer(server.Alias, span);
            }
        }
    }

    private bool EnsureMcpServer(string alias, SourceSpan span)
    {
        if (_loadedCapabilities.ContainsKey(alias))
        {
            return true;
        }

        if (!_mcpServers.TryGetValue(alias, out var server))
        {
            return false;
        }

        var loaded = _capabilities.Load(alias, CapabilitySource.Mcp(server.Endpoint), _filePath, _diagnostics, span, server.WorkingDirectory);
        if (loaded is null)
        {
            return false;
        }

        return AdoptCapability(loaded, span);
    }

    private static bool ContainsWord(string text, string word)
    {
        if (word.Length == 0)
        {
            return false;
        }

        var index = 0;
        while (index < text.Length)
        {
            var at = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return false;
            }

            var before = at == 0 || !IsNameChar(text[at - 1]);
            var end = at + word.Length;
            var after = end >= text.Length || !IsNameChar(text[end]);
            if (before && after)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private bool AdoptCapability(LoadedCapability loaded, SourceSpan span)
    {
        foreach (var type in loaded.Types)
        {
            if (_records.ContainsKey(type.Name))
            {
                _diagnostics.Error("E4001", $"Capability type '{type.Name}' conflicts with an existing type.", _filePath, span);
                return false;
            }
        }

        foreach (var type in loaded.Types)
        {
            if (type.Type is IrRecordType record)
            {
                _records[type.Name] = record;
            }

            _capabilityTypes.Add(type);
        }

        _loadedCapabilities[loaded.Alias] = loaded;
        return true;
    }

    private IrNode BindRun(RunExprAst expr, Scope scope)
    {
        var directory = string.IsNullOrEmpty(_filePath)
            ? Directory.GetCurrentDirectory()
            : Path.GetDirectoryName(Path.GetFullPath(_filePath)) ?? Directory.GetCurrentDirectory();
        var resolved = ScriptCalls.Resolve(
            expr.Path,
            directory,
            _filePath,
            _diagnostics,
            expr.Span,
            _semanticResolver,
            _capabilities,
            _model,
            _cmdlets);
        if (resolved is null)
        {
            return new IrLiteral(null, expr.Span, IrErrorType.Instance);
        }

        foreach (var effect in resolved.Effects)
        {
            if (effect != EffectKind.Pure && !_grantedEffects.Contains(effect))
            {
                _diagnostics.Error("E4003",
                    $"Running \"{Path.GetFileName(resolved.FullPath)}\" requires '{DescribeEffect(effect)}', which this program does not allow. Add 'Allow {AllowPhrase(effect)}'.",
                    _filePath, expr.Span);
            }
        }

        var arguments = BindScriptArguments(expr, resolved, scope);
        return new IrScriptCall(resolved.FullPath, resolved.Kind, arguments, resolved.Effects, expr.Span, resolved.ResultType)
        {
            CompiledScript = resolved.CompiledScript
        };
    }

    private List<IrNamedArgument> BindScriptArguments(RunExprAst expr, ResolvedScript script, Scope scope)
    {
        var file = Path.GetFileName(script.FullPath);
        var named = expr.Arguments.Any(a => a.Name is not null);
        if (expr.Arguments.Count > 0 && expr.Arguments.Any(a => a.Name is null) && named)
        {
            _diagnostics.Error("E4005", $"Arguments to \"{file}\" must all be named or all be positional.", _filePath, expr.Span);
            return [];
        }

        var result = new List<IrNamedArgument>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(ScriptParameter parameter, AskArgAst argument)
        {
            if (!seen.Add(parameter.Name))
            {
                _diagnostics.Error("E4005", $"Parameter '{parameter.Name}' of \"{file}\" was given twice.", _filePath, argument.Span);
                return;
            }

            var value = BindExpression(argument.Value, scope);
            if (!TypesCompatible(parameter.Type, value.Type))
            {
                _diagnostics.Error("E4005",
                    $"Parameter '{parameter.Name}' of \"{file}\" expects '{Describe(parameter.Type)}', got '{Describe(value.Type)}'.",
                    _filePath, argument.Span);
            }

            result.Add(new IrNamedArgument(parameter.Name, value));
        }

        if (!named)
        {
            if (expr.Arguments.Count > script.Parameters.Count)
            {
                _diagnostics.Error("E4005",
                    $"\"{file}\" takes {script.Parameters.Count} arguments, and the sentence gives {expr.Arguments.Count}.",
                    _filePath, expr.Span);
            }

            for (var i = 0; i < expr.Arguments.Count && i < script.Parameters.Count; i++)
            {
                Add(script.Parameters[i], expr.Arguments[i]);
            }
        }
        else
        {
            foreach (var argument in expr.Arguments)
            {
                var parameter = script.Parameters.FirstOrDefault(p => string.Equals(p.Name, argument.Name, StringComparison.OrdinalIgnoreCase));
                if (parameter is null)
                {
                    _diagnostics.Error("E4005", $"\"{file}\" has no parameter '{argument.Name}'.", _filePath, argument.Span);
                    continue;
                }

                Add(parameter, argument);
            }
        }

        foreach (var parameter in script.Parameters.Where(p => p.Required && !seen.Contains(p.Name)))
        {
            _diagnostics.Error("E4005", $"Missing argument '{parameter.Name}' for \"{file}\".", _filePath, expr.Span);
        }

        return result;
    }

    private IrFunction BindFunction(FunctionAst fn)
    {
        var sig = _functions[fn.Name];
        var scope = new Scope(null);
        foreach (var param in sig.Parameters)
        {
            scope.Define(param.Name, param.Type, fn.Span, _diagnostics, _filePath);
        }

        var body = fn.Body.Select(s => BindStatement(s, scope, sig.ReturnType)).ToList();
        body = RefineEmptyListDeclarations(body, scope);
        return new IrFunction(fn.Name, sig.Parameters, sig.ReturnType, body, fn.Span, sig.ReturnType)
        {
            IsEntry = fn.IsEntry,
            SourceFile = fn.SourceFile
        };
    }

    private static List<IrNode> RefineEmptyListDeclarations(List<IrNode> body, Scope scope)
    {
        return body.Select(node => RefineNode(node, scope)).ToList();
    }

    private static IrNode RefineNode(IrNode node, Scope scope) =>
        node switch
        {
            IrAssign assign when assign.IsDeclaration &&
                                 scope.TryLookup(assign.Name, out var current) &&
                                 current is IrListType { ElementType: not IrErrorType } &&
                                 assign.Value is IrListLiteral { Elements.Count: 0 } list =>
                assign with
                {
                    Type = current,
                    Value = list with { Type = current }
                },
            IrIf iff => iff with
            {
                ThenBody = iff.ThenBody.Select(n => RefineNode(n, scope)).ToList(),
                OtherwiseBody = iff.OtherwiseBody?.Select(n => RefineNode(n, scope)).ToList()
            },
            IrForEach loop => loop with
            {
                Body = loop.Body.Select(n => RefineNode(n, scope)).ToList()
            },
            _ => node
        };

    private IrNode BindStatement(StmtAst stmt, Scope scope, IrType returnType) =>
        stmt switch
        {
            AssignStmtAst a => BindAssign(a, scope),
            IfStmtAst i => BindIf(i, scope, returnType),
            ForEachStmtAst f => BindForEach(f, scope, returnType),
            ReturnStmtAst r => BindReturn(r, scope, returnType),
            SayStmtAst s => new IrSay(s.Parts.Select(p => BindExpression(p, scope)).ToList(), s.Span, IrVoidType.Instance),
            WhileStmtAst w => BindWhile(w, scope, returnType),
            TryStmtAst t => BindTry(t, scope, returnType),
            ExpressionStmtAst e => BindExpression(e.Value, scope),
            _ => ErrorNode(stmt.Span, $"Unsupported statement.")
        };

    private IrTry BindTry(TryStmtAst stmt, Scope scope, IrType returnType)
    {
        // Names made inside "try to" are visible afterwards, so "the result" assigned in the
        // try block can be read after the failure handler ran; the handler itself sees "the error".
        var body = stmt.Body.Select(s => BindStatement(s, scope, returnType)).ToList();
        List<IrNode>? onFailure = null;
        if (stmt.OnFailure is not null)
        {
            var failureScope = new Scope(scope);
            failureScope.Define("error", IrPrimitiveType.String, stmt.Span, _diagnostics, _filePath);
            onFailure = stmt.OnFailure.Select(s => BindStatement(s, failureScope, returnType)).ToList();
        }

        var finallyBody = stmt.Finally?.Select(s => BindStatement(s, new Scope(scope), returnType)).ToList();
        return new IrTry(body, onFailure, finallyBody, "error", stmt.Span, IrVoidType.Instance);
    }

    private IrWhile BindWhile(WhileStmtAst stmt, Scope scope, IrType returnType)
    {
        var condition = BindExpression(stmt.Condition, scope);
        if (!IsBoolean(condition.Type) && condition.Type is not IrErrorType)
        {
            _diagnostics.Error("E2004", "Loop condition must be boolean.", _filePath, stmt.Condition.Span);
        }

        if (stmt.Until)
        {
            condition = new IrUnary("not", condition, condition.Span, IrPrimitiveType.Boolean);
        }

        var bodyScope = new Scope(scope);
        var body = stmt.Body.Select(s => BindStatement(s, bodyScope, returnType)).ToList();
        return new IrWhile(condition, body, stmt.Span, IrVoidType.Instance);
    }

    private IrNode BindTreat(TreatExprAst expr, Scope scope)
    {
        var value = BindExpression(expr.Target, scope);
        var type = ResolveType(expr.Type);
        if (value.Type is not IrDynamicType and not IrErrorType && !ContainsDynamic(value.Type))
        {
            if (!TypesCompatible(type, value.Type))
            {
                _diagnostics.Error("E2003",
                    $"'{Describe(value.Type)}' is already typed and is not a '{Describe(type)}'.",
                    _filePath, expr.Span);
            }
        }

        return new IrValidate(value, expr.Span, type, value.Provenance);
    }

    private static bool ContainsDynamic(IrType type) => type switch
    {
        IrDynamicType => true,
        IrListType list => ContainsDynamic(list.ElementType),
        _ => false
    };

    private IrNode BindSort(SortExprAst expr, Scope scope)
    {
        var target = BindExpression(expr.Target, scope);
        if (target.Type is not IrListType list)
        {
            if (target.Type is not IrErrorType)
            {
                _diagnostics.Error("E2005", $"Only a list can be sorted, found '{Describe(target.Type)}'.", _filePath, expr.Span);
            }

            return new IrSort(target, expr.Field, expr.Descending, expr.Span, IrErrorType.Instance);
        }

        CheckElementField(list, expr.Field, expr.Span);
        return new IrSort(target, expr.Field, expr.Descending, expr.Span, list, target.Provenance);
    }

    private IrNode BindTake(TakeExprAst expr, Scope scope)
    {
        var target = BindExpression(expr.Target, scope);
        var count = BindExpression(expr.Count, scope);
        if (!IsInteger(count.Type) && count.Type is not IrErrorType)
        {
            _diagnostics.Error("E2024", "'the first N of' needs a whole number.", _filePath, expr.Count.Span);
        }

        if (target.Type is not IrListType && target.Type is not IrErrorType)
        {
            _diagnostics.Error("E2005", $"'the first N of' needs a list, found '{Describe(target.Type)}'.", _filePath, expr.Span);
            return new IrTake(target, count, expr.Span, IrErrorType.Instance);
        }

        return new IrTake(target, count, expr.Span, target.Type, target.Provenance);
    }

    private IrType? CheckElementField(IrListType list, string field, SourceSpan span)
    {
        switch (list.ElementType)
        {
            case IrRecordType record:
            {
                var found = record.Fields.FirstOrDefault(f => f.Name == field);
                if (found is null)
                {
                    _diagnostics.Error("E2017", $"Type '{record.Name}' has no field '{field}'.", _filePath, span);
                    return null;
                }

                return found.Type;
            }
            case IrDynamicType:
                return IrDynamicType.Instance;
            case IrErrorType:
                return IrErrorType.Instance;
            default:
                _diagnostics.Error("E2018", $"Items of type '{Describe(list.ElementType)}' have no field '{field}'.", _filePath, span);
                return null;
        }
    }

    private IrAssign BindAssign(AssignStmtAst assign, Scope scope)
    {
        var value = BindExpression(assign.Value, scope);
        var isDecl = !scope.TryLookup(assign.Name, out _);
        if (isDecl)
        {
            scope.Define(assign.Name, value.Type, assign.Span, _diagnostics, _filePath);
        }
        else
        {
            var existing = scope.Lookup(assign.Name);
            if (existing is IrListType { ElementType: IrErrorType } && value.Type is IrListType refined)
            {
                // Refine empty-list placeholder to the first concrete element type.
                scope.Replace(assign.Name, refined);
                value = value with { Type = refined };
            }
            else if (!TypesCompatible(existing, value.Type))
            {
                _diagnostics.Error("E2003",
                    $"Cannot assign '{Describe(value.Type)}' to '{assign.Name}' of type '{Describe(existing)}'.",
                    _filePath, assign.Span);
            }
        }

        var assignType = isDecl ? value.Type : scope.Lookup(assign.Name);
        var provenance = value.Provenance == Provenance.ResolvedByModel
            ? Provenance.ResolvedByModel
            : Provenance.Deterministic;
        return new IrAssign(assign.Name, value, isDecl, assign.Span, assignType, provenance);
    }

    private IrIf BindIf(IfStmtAst stmt, Scope scope, IrType returnType)
    {
        var condition = BindExpression(stmt.Condition, scope);
        if (!IsBoolean(condition.Type) && condition.Type is not IrErrorType)
        {
            _diagnostics.Error("E2004", "If condition must be boolean.", _filePath, stmt.Condition.Span);
        }

        var thenScope = new Scope(scope);
        var thenBody = stmt.ThenBody.Select(s => BindStatement(s, thenScope, returnType)).ToList();
        List<IrNode>? otherwiseBody = null;
        if (stmt.OtherwiseBody is not null)
        {
            var elseScope = new Scope(scope);
            otherwiseBody = stmt.OtherwiseBody.Select(s => BindStatement(s, elseScope, returnType)).ToList();
        }

        return new IrIf(condition, thenBody, otherwiseBody, stmt.Span, IrVoidType.Instance);
    }

    private IrForEach BindForEach(ForEachStmtAst stmt, Scope scope, IrType returnType)
    {
        var collection = BindExpression(stmt.Collection, scope);
        IrType elementType = IrErrorType.Instance;
        if (collection.Type is IrListType list)
        {
            elementType = list.ElementType;
        }
        else if (collection.Type is not IrErrorType)
        {
            _diagnostics.Error("E2005",
                $"For-each requires a list, found '{Describe(collection.Type)}'.",
                _filePath, stmt.Collection.Span);
        }

        var bodyScope = new Scope(scope);
        bodyScope.Define(stmt.Iterator, elementType, stmt.Span, _diagnostics, _filePath);
        var body = stmt.Body.Select(s => BindStatement(s, bodyScope, returnType)).ToList();
        return new IrForEach(stmt.Iterator, collection, body, stmt.Span, IrVoidType.Instance);
    }

    private IrReturn BindReturn(ReturnStmtAst stmt, Scope scope, IrType returnType)
    {
        var value = BindExpression(stmt.Value, scope);
        if (!TypesCompatible(returnType, value.Type))
        {
            _diagnostics.Error("E2006",
                $"Return type '{Describe(value.Type)}' does not match expected '{Describe(returnType)}'.",
                _filePath, stmt.Span);
        }

        return new IrReturn(value, stmt.Span, value.Type);
    }

    private IrNode BindExpression(ExprAst expr, Scope scope) =>
        expr switch
        {
            LiteralExprAst lit => BindLiteral(lit),
            NameExprAst name => BindName(name, scope),
            OfExprAst ofExpr => BindOf(ofExpr, scope),
            CallExprAst call => BindCall(call, scope),
            FieldAccessExprAst field => BindFieldAccess(field, scope),
            ListLiteralExprAst list => BindListLiteral(list, scope),
            UnaryExprAst unary => BindUnary(unary, scope),
            BinaryExprAst binary => BindBinary(binary, scope),
            IntentExprAst intent => BindIntent(intent, scope),
            AskExprAst ask => BindAsk(ask, scope),
            RunExprAst run => BindRun(run, scope),
            SortExprAst sort => BindSort(sort, scope),
            TakeExprAst take => BindTake(take, scope),
            TreatExprAst treat => BindTreat(treat, scope),
            _ => ErrorNode(expr.Span, "Unsupported expression.")
        };

    private IrNode BindIntent(IntentExprAst intent, Scope scope)
    {
        var symbols = scope.AllSymbols()
            .Select(s => new SymbolInfo(s.Name, Describe(s.Type)))
            .ToList();
        var records = _records.Values
            .Select(r => new RecordSchemaInfo(
                r.Name,
                r.Fields.Select(f => new RecordFieldInfo(f.Name, Describe(f.Type))).ToList()))
            .ToList();

        var request = new SemanticResolutionRequest(
            intent.Phrase,
            ExpectedTypeDescription: null,
            symbols,
            records,
            intent.Span);

        SemanticResolutionResult resolution;
        try
        {
            resolution = _semanticResolver.ResolveAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _diagnostics.Error("E2106", $"Semantic resolver failed: {ex.Message}", _filePath, intent.Span);
            return new IrLiteral(null, intent.Span, IrErrorType.Instance, Provenance.ResolvedByModel);
        }

        if (resolution.Candidates.Count == 0)
        {
            var implied = BindImpliedOperation(intent.Phrase, intent.Span, scope);
            if (implied is not null)
            {
                return implied;
            }

            _diagnostics.Error("E2103",
                resolution.FailureReason ?? $"Unresolved intent: \"{intent.Phrase}\".",
                _filePath, intent.Span);
            return new IrLiteral(null, intent.Span, IrErrorType.Instance, Provenance.ResolvedByModel);
        }

        if (resolution.Candidates.Count > 1)
        {
            var alternatives = string.Join(Environment.NewLine,
                resolution.Candidates.Select(c => $"  - {c.Rationale}"));
            _diagnostics.Error("E2104",
                $"\"{intent.Phrase}\" has {resolution.Candidates.Count} valid interpretations.{Environment.NewLine}{alternatives}{Environment.NewLine}Define the term or select one interpretation.",
                _filePath, intent.Span);
            return new IrLiteral(null, intent.Span, IrErrorType.Instance, Provenance.ResolvedByModel);
        }

        var candidate = resolution.Candidates[0];
        var lowered = _proposalLowerer.Lower(
            candidate.Proposal,
            intent.Span,
            name => scope.TryLookup(name, out var type) ? type : null);

        return lowered ?? new IrLiteral(null, intent.Span, IrErrorType.Instance, Provenance.ResolvedByModel);
    }

    private static IrLiteral BindLiteral(LiteralExprAst lit)
    {
        IrType type = lit.Value switch
        {
            long => IrPrimitiveType.Integer,
            decimal => IrPrimitiveType.Decimal,
            string => IrPrimitiveType.String,
            bool => IrPrimitiveType.Boolean,
            _ => IrErrorType.Instance
        };
        return new IrLiteral(lit.Value, lit.Span, type);
    }

    private IrNode BindAsk(AskExprAst ask, Scope scope)
    {
        if (!_loadedCapabilities.TryGetValue(ask.Capability, out var capability))
        {
            if (!_mcpServers.ContainsKey(ask.Capability))
            {
                _diagnostics.Error("E4004", $"Unknown capability '{ask.Capability}'.", _filePath, ask.Span);
                return new IrLiteral(null, ask.Span, IrErrorType.Instance);
            }

            if (!EnsureMcpServer(ask.Capability, ask.Span) || !_loadedCapabilities.TryGetValue(ask.Capability, out capability))
            {
                return new IrLiteral(null, ask.Span, IrErrorType.Instance);
            }
        }

        if (!capability.Operations.TryGetValue(ask.Operation, out var operation) ||
            (capability.AllowedOperations.Count > 0 &&
             !capability.AllowedOperations.Contains(ask.Operation, StringComparer.Ordinal)))
        {
            _diagnostics.Error("E4002",
                $"Capability '{ask.Capability}' has no allowed operation '{ask.Operation}'.",
                _filePath, ask.Span);
            return new IrLiteral(null, ask.Span, IrErrorType.Instance);
        }

        CheckCapabilityEffects(capability, operation, ask.Span);

        var bound = BindCapabilityArguments(ask, operation, scope);
        IrNode call = new IrCapabilityCall(
            ask.Capability,
            ask.Operation,
            $"{ask.Capability}@{capability.VersionHash}",
            capability.ManifestPath,
            bound,
            operation.Effects,
            ask.Span,
            operation.ResultType)
        {
            Provider = capability.Provider,
            Endpoint = capability.Endpoint,
            Transport = capability.Transport,
            WorkingDirectory = capability.WorkingDirectory
        };

        if (ask.Retries > 0)
        {
            // Retrying a write could repeat it. Only operations that merely read may be retried.
            if (operation.Effects.Any(e => e is EffectKind.FileSystemWrite or EffectKind.NetworkWrite or EffectKind.ExternalState or EffectKind.RuntimeAi))
            {
                _diagnostics.Error("E4006",
                    $"'{ask.Capability}.{ask.Operation}' changes something, so it cannot be retried automatically. Retry only reads, or handle failure with 'try to … if that fails'.",
                    _filePath, ask.Span);
            }

            call = new IrRetry(call, ask.Retries, ask.Span, operation.ResultType);
        }

        return call;
    }

    private List<IrNamedArgument> BindCapabilityArguments(AskExprAst ask, BoundOperation operation, Scope scope)
    {
        var required = operation.Arguments.Where(a => a.Required).ToList();
        var named = ask.Arguments.Any(a => a.Name is not null);
        if (ask.Arguments.Any(a => a.Name is null) && named)
        {
            _diagnostics.Error("E4005",
                $"Arguments to '{ask.Operation}' must all be named or all be positional.",
                _filePath, ask.Span);
            return [];
        }

        if (!named)
        {
            if (ask.Arguments.Count != operation.Arguments.Count)
            {
                _diagnostics.Error("E4005",
                    $"Operation '{ask.Operation}' expects {operation.Arguments.Count} argument(s), got {ask.Arguments.Count}.",
                    _filePath, ask.Span);
            }

            var positional = new List<IrNamedArgument>();
            for (var i = 0; i < Math.Min(ask.Arguments.Count, operation.Arguments.Count); i++)
            {
                var value = BindExpression(ask.Arguments[i].Value, scope);
                if (!TypesCompatible(operation.Arguments[i].Type, value.Type))
                {
                    _diagnostics.Error("E4005",
                        $"Argument '{operation.Arguments[i].Name}' expects '{Describe(operation.Arguments[i].Type)}', got '{Describe(value.Type)}'.",
                        _filePath, ask.Arguments[i].Span);
                }

                positional.Add(new IrNamedArgument(operation.Arguments[i].Name, value));
            }

            return positional;
        }

        var byName = operation.Arguments.ToDictionary(a => a.Name, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IrNamedArgument>();
        foreach (var argument in ask.Arguments)
        {
            if (argument.Name is null || !byName.TryGetValue(argument.Name, out var expected))
            {
                _diagnostics.Error("E4005",
                    $"Operation '{ask.Operation}' has no argument '{argument.Name}'.",
                    _filePath, argument.Span);
                continue;
            }

            if (!seen.Add(argument.Name))
            {
                _diagnostics.Error("E4005", $"Argument '{argument.Name}' was supplied more than once.", _filePath, argument.Span);
                continue;
            }

            var value = BindExpression(argument.Value, scope);
            if (!TypesCompatible(expected.Type, value.Type))
            {
                _diagnostics.Error("E4005",
                    $"Argument '{argument.Name}' expects '{Describe(expected.Type)}', got '{Describe(value.Type)}'.",
                    _filePath, argument.Span);
            }

            result.Add(new IrNamedArgument(argument.Name, value));
        }

        foreach (var argument in required)
        {
            if (!seen.Contains(argument.Name))
            {
                _diagnostics.Error("E4005", $"Missing argument '{argument.Name}' for '{ask.Operation}'.", _filePath, ask.Span);
            }
        }

        return result;
    }

    private static string DescribeEffect(EffectKind effect) => effect switch
    {
        EffectKind.NetworkRead => "external-read",
        EffectKind.ExternalState => "external-write",
        EffectKind.NetworkWrite => "network-write",
        EffectKind.FileSystemRead => "filesystem-read",
        EffectKind.FileSystemWrite => "filesystem-write",
        EffectKind.Pure => "pure",
        EffectKind.RuntimeAi => "runtime-ai",
        EffectKind.Script => "script",
        _ => effect.ToString()
    };

    private IrNode BindName(NameExprAst name, Scope scope)
    {
        if (scope.TryLookup(name.Name, out var type))
        {
            return new IrVar(name.Name, name.Span, type!);
        }

        if (WellKnownPlace(name.Name) is { } place)
        {
            return new IrCall(place, [], true, [EffectKind.Pure], name.Span, IrPrimitiveType.String);
        }

        if (!string.IsNullOrWhiteSpace(name.RawPhrase))
        {
            return BindIntent(new IntentExprAst(name.RawPhrase, name.Span), scope);
        }

        var implied = BindImpliedOperation(name.Name, name.Span, scope);
        if (implied is not null)
        {
            return implied;
        }

        _diagnostics.Error("E2007", $"Undeclared name '{name.Name}'.", _filePath, name.Span);
        return new IrVar(name.Name, name.Span, IrErrorType.Instance);
    }

    /// <summary>"the home folder", "the current folder", "the temp folder" — places every script needs.</summary>
    private static string? WellKnownPlace(string name) => name switch
    {
        "homeFolder" or "homeDirectory" or "userProfile" => "homeFolder",
        "currentFolder" or "currentDirectory" or "workingFolder" or "workingDirectory" => "currentFolder",
        "tempFolder" or "temporaryFolder" or "tempDirectory" => "tempFolder",
        "scriptFolder" or "scriptDirectory" => "scriptFolder",
        _ => null
    };

    private IrNode BindOf(OfExprAst expr, Scope scope)
    {
        if (_functions.ContainsKey(expr.Name))
        {
            return BindCall(new CallExprAst(expr.Name, [expr.Target], expr.Span), scope);
        }

        var target = BindExpression(expr.Target, scope);
        if (target.Type is IrListType list && expr.Name.EndsWith('s') && expr.Name.Length > 1)
        {
            var singular = expr.Name[..^1];
            var fieldType = list.ElementType switch
            {
                IrRecordType record => record.Fields.FirstOrDefault(f => f.Name == singular)?.Type,
                IrDynamicType => IrDynamicType.Instance,
                _ => null
            };
            if (fieldType is not null)
            {
                return new IrSelectField(target, singular, expr.Span, new IrListType(fieldType), target.Provenance);
            }
        }

        return BindFieldAccessOn(target, expr.Name, expr.Span);
    }

    private IrNode BindCall(CallExprAst call, Scope scope)
    {
        var args = call.Arguments.Select(a => BindExpression(a, scope)).ToList();
        if (!_functions.TryGetValue(call.Name, out var sig))
        {
            _diagnostics.Error("E2008", $"Unknown function '{call.Name}'.", _filePath, call.Span);
            return new IrCall(call.Name, args, false, [EffectKind.Pure], call.Span, IrErrorType.Instance);
        }

        if (sig.IsBuiltin)
        {
            return BindBuiltinCall(sig, args, call);
        }

        if (args.Count != sig.Parameters.Count)
        {
            _diagnostics.Error("E2009",
                $"Function '{call.Name}' expects {sig.Parameters.Count} argument(s), got {args.Count}.",
                _filePath, call.Span);
        }
        else
        {
            for (var i = 0; i < args.Count; i++)
            {
                if (!TypesCompatible(sig.Parameters[i].Type, args[i].Type))
                {
                    _diagnostics.Error("E2010",
                        $"Argument {i + 1} of '{call.Name}' expects '{Describe(sig.Parameters[i].Type)}', got '{Describe(args[i].Type)}'.",
                        _filePath, call.Arguments[i].Span);
                }
            }
        }

        return new IrCall(call.Name, args, false, [EffectKind.Pure], call.Span, sig.ReturnType);
    }

    private IrNode BindBuiltinCall(FunctionSignature sig, List<IrNode> args, CallExprAst call)
    {
        return sig.Name switch
        {
            "len" when args.Count == 1 && args[0].Type is IrDynamicType =>
                new IrFieldAccess(args[0], "length", call.Span, IrDynamicType.Instance, args[0].Provenance),
            "len" => BindLen(args, call),
            "round" => BindRound(args, call),
            "sum" => BindSum(args, call),
            "append" => BindAppend(args, call),
            "str" => BindCast(args, call, IrPrimitiveType.String),
            "int" => BindCast(args, call, IrPrimitiveType.Integer),
            "decimal" => BindCast(args, call, IrPrimitiveType.Decimal),
            "bool" => BindCast(args, call, IrPrimitiveType.Boolean),
            "json" or "csv" => BindParse(sig.Name, args, call),
            "env" => new IrCall("env", args, true, [EffectKind.Pure], call.Span, IrPrimitiveType.String),
            _ => new IrCall(sig.Name, args, true, [EffectKind.Pure], call.Span, IrErrorType.Instance)
        };
    }

    private IrCall BindLen(List<IrNode> args, CallExprAst call)
    {
        ExpectArity(call, args, 1);
        if (args.Count == 1 && args[0].Type is not IrListType and not IrPrimitiveType { Name: "string" } and not IrErrorType)
        {
            _diagnostics.Error("E2011", "len expects a list or string.", _filePath, call.Span);
        }

        return new IrCall("len", args, true, [EffectKind.Pure], call.Span, IrPrimitiveType.Integer);
    }

    private IrCall BindRound(List<IrNode> args, CallExprAst call)
    {
        ExpectArity(call, args, 2);
        if (args.Count >= 1 && !IsNumeric(args[0].Type) && args[0].Type is not IrErrorType)
        {
            _diagnostics.Error("E2012", "round expects a numeric value.", _filePath, call.Arguments[0].Span);
        }

        if (args.Count >= 2 && !IsInteger(args[1].Type) && args[1].Type is not IrErrorType)
        {
            _diagnostics.Error("E2013", "round digits must be an integer.", _filePath, call.Arguments[1].Span);
        }

        return new IrCall("round", args, true, [EffectKind.Pure], call.Span, IrPrimitiveType.Decimal);
    }

    private IrCall BindSum(List<IrNode> args, CallExprAst call)
    {
        ExpectArity(call, args, 1);
        IrType result = IrPrimitiveType.Decimal;
        if (args.Count == 1)
        {
            if (args[0].Type is IrListType { ElementType: var el } && IsNumeric(el))
            {
                result = IsInteger(el) ? IrPrimitiveType.Integer : IrPrimitiveType.Decimal;
            }
            else if (args[0].Type is not IrErrorType)
            {
                _diagnostics.Error("E2014", "sum expects a list of numbers.", _filePath, call.Span);
                result = IrErrorType.Instance;
            }
            else
            {
                result = IrErrorType.Instance;
            }
        }

        return new IrCall("sum", args, true, [EffectKind.Pure], call.Span, result);
    }

    private IrCall BindAppend(List<IrNode> args, CallExprAst call)
    {
        ExpectArity(call, args, 2);
        IrType result = IrErrorType.Instance;
        if (args.Count == 2)
        {
            if (args[0].Type is IrListType list)
            {
                if (list.ElementType is IrErrorType)
                {
                    result = new IrListType(args[1].Type);
                }
                else if (!TypesCompatible(list.ElementType, args[1].Type) && args[1].Type is not IrErrorType)
                {
                    _diagnostics.Error("E2015",
                        $"Cannot append '{Describe(args[1].Type)}' to list<{Describe(list.ElementType)}>.",
                        _filePath, call.Span);
                    result = list;
                }
                else
                {
                    result = list;
                }
            }
            else if (args[0].Type is not IrErrorType)
            {
                _diagnostics.Error("E2016", "append expects a list as the first argument.", _filePath, call.Span);
            }
        }

        return new IrCall("append", args, true, [EffectKind.Pure], call.Span, result);
    }

    private IrCall BindCast(List<IrNode> args, CallExprAst call, IrType target)
    {
        ExpectArity(call, args, 1);
        return new IrCall(call.Name, args, true, [EffectKind.Pure], call.Span, target);
    }

    private void ExpectArity(CallExprAst call, List<IrNode> args, int expected)
    {
        if (args.Count != expected)
        {
            _diagnostics.Error("E2009",
                $"Function '{call.Name}' expects {expected} argument(s), got {args.Count}.",
                _filePath, call.Span);
        }
    }

    private IrFieldAccess BindFieldAccess(FieldAccessExprAst expr, Scope scope) =>
        BindFieldAccessOn(BindExpression(expr.Target, scope), expr.FieldName, expr.Span);

    private IrFieldAccess BindFieldAccessOn(IrNode target, string fieldName, SourceSpan span)
    {
        if (target.Type is IrRecordType record)
        {
            var field = record.Fields.FirstOrDefault(f => f.Name == fieldName);
            if (field is null)
            {
                _diagnostics.Error("E2017",
                    $"Type '{record.Name}' has no field '{fieldName}'.",
                    _filePath, span);
                return new IrFieldAccess(target, fieldName, span, IrErrorType.Instance);
            }

            return new IrFieldAccess(target, fieldName, span, field.Type);
        }

        if (target.Type is IrDynamicType)
        {
            return new IrFieldAccess(target, fieldName, span, IrDynamicType.Instance, target.Provenance);
        }

        if (target.Type is not IrErrorType)
        {
            _diagnostics.Error("E2018",
                $"Cannot access field '{fieldName}' on '{Describe(target.Type)}'.",
                _filePath, span);
        }

        return new IrFieldAccess(target, fieldName, span, IrErrorType.Instance);
    }

    private IrListLiteral BindListLiteral(ListLiteralExprAst expr, Scope scope)
    {
        var elements = expr.Elements.Select(e => BindExpression(e, scope)).ToList();
        IrType elementType;
        if (elements.Count == 0)
        {
            // Empty list: element type inferred later from assignment context is out of scope;
            // use a flexible empty list of error/unknown — prefer decimal as neutral for Milestone A samples.
            // Better: use a special empty list typed as list<error> and allow assignment widening.
            elementType = IrErrorType.Instance;
        }
        else
        {
            elementType = elements[0].Type;
            foreach (var el in elements.Skip(1))
            {
                if (!TypesCompatible(elementType, el.Type))
                {
                    // Allow int/decimal promotion
                    if (IsNumeric(elementType) && IsNumeric(el.Type))
                    {
                        elementType = IrPrimitiveType.Decimal;
                    }
                    else if (elementType is not IrErrorType && el.Type is not IrErrorType)
                    {
                        _diagnostics.Error("E2019", "List elements must share a common type.", _filePath, el.Span);
                    }
                }
            }
        }

        // Empty list gets list with error element; assignment will specialize.
        var listType = new IrListType(elementType);
        return new IrListLiteral(elements, expr.Span, listType);
    }

    private IrUnary BindUnary(UnaryExprAst expr, Scope scope)
    {
        var operand = BindExpression(expr.Operand, scope);
        var op = expr.Operator switch
        {
            TokenKind.Not => "not",
            TokenKind.Minus => "-",
            _ => "?"
        };

        IrType type = IrErrorType.Instance;
        if (op == "not")
        {
            if (!IsBoolean(operand.Type) && operand.Type is not IrErrorType)
            {
                _diagnostics.Error("E2020", "'not' requires a boolean operand.", _filePath, expr.Span);
            }

            type = IrPrimitiveType.Boolean;
        }
        else if (op == "-")
        {
            if (!IsNumeric(operand.Type) && operand.Type is not IrErrorType)
            {
                _diagnostics.Error("E2021", "Unary minus requires a numeric operand.", _filePath, expr.Span);
            }

            type = operand.Type is IrErrorType ? IrErrorType.Instance : operand.Type;
        }

        return new IrUnary(op, operand, expr.Span, type);
    }

    private IrBinary BindBinary(BinaryExprAst expr, Scope scope)
    {
        var left = BindExpression(expr.Left, scope);
        var right = BindExpression(expr.Right, scope);
        var op = OperatorText(expr.Operator);
        var type = InferBinaryType(op, left.Type, right.Type, expr.Span);
        return new IrBinary(left, op, right, expr.Span, type);
    }

    private IrType InferBinaryType(string op, IrType left, IrType right, SourceSpan span)
    {
        if (left is IrErrorType || right is IrErrorType)
        {
            return IrErrorType.Instance;
        }

        if (left is IrDynamicType || right is IrDynamicType)
        {
            return op is "and" or "or" or "==" or "!=" or "<" or "<=" or ">" or ">="
                ? IrPrimitiveType.Boolean
                : IrDynamicType.Instance;
        }

        switch (op)
        {
            case "and" or "or":
                if (!IsBoolean(left) || !IsBoolean(right))
                {
                    _diagnostics.Error("E2022", $"Operator '{op}' requires boolean operands.", _filePath, span);
                }

                return IrPrimitiveType.Boolean;
            case "==" or "!=":
                return IrPrimitiveType.Boolean;
            case "<" or "<=" or ">" or ">=":
                if (!IsNumeric(left) || !IsNumeric(right))
                {
                    _diagnostics.Error("E2023", $"Operator '{op}' requires numeric operands.", _filePath, span);
                }

                return IrPrimitiveType.Boolean;
            case "+" when IsString(left) || IsString(right):
                return IrPrimitiveType.String;
            case "+" or "-" or "*" or "/" or "%":
                if (!IsNumeric(left) || !IsNumeric(right))
                {
                    _diagnostics.Error("E2024", $"Operator '{op}' requires numeric operands.", _filePath, span);
                    return IrErrorType.Instance;
                }

                if (op is "/" || IsDecimal(left) || IsDecimal(right))
                {
                    return IrPrimitiveType.Decimal;
                }

                return IrPrimitiveType.Integer;
            default:
                _diagnostics.Error("E2025", $"Unknown operator '{op}'.", _filePath, span);
                return IrErrorType.Instance;
        }
    }

    private IrType ResolveType(TypeAst type) =>
        type switch
        {
            ListTypeAst list => new IrListType(ResolveType(list.ElementType)),
            NamedTypeAst named => named.Name switch
            {
                "integer" => IrPrimitiveType.Integer,
                "decimal" => IrPrimitiveType.Decimal,
                "string" => IrPrimitiveType.String,
                "boolean" => IrPrimitiveType.Boolean,
                "nothing" or "void" => IrVoidType.Instance,
                var n when _records.TryGetValue(n, out var rec) => rec,
                var n => UnknownType(n, type.Span)
            },
            _ => IrErrorType.Instance
        };

    private IrType UnknownType(string name, SourceSpan span)
    {
        _diagnostics.Error("E2026", $"Unknown type '{name}'.", _filePath, span);
        return IrErrorType.Instance;
    }

    private void RegisterBuiltins()
    {
        void Add(string name, IrType returnType, params IrParameter[] parameters) =>
            _functions[name] = new FunctionSignature(name, parameters, returnType, IsBuiltin: true);

        // Signatures are checked in BindBuiltinCall; placeholders here for name lookup.
        Add("len", IrPrimitiveType.Integer, new IrParameter("value", IrErrorType.Instance));
        Add("round", IrPrimitiveType.Decimal,
            new IrParameter("value", IrPrimitiveType.Decimal),
            new IrParameter("digits", IrPrimitiveType.Integer));
        Add("sum", IrPrimitiveType.Decimal, new IrParameter("values", new IrListType(IrPrimitiveType.Decimal)));
        Add("append", new IrListType(IrErrorType.Instance),
            new IrParameter("list", new IrListType(IrErrorType.Instance)),
            new IrParameter("element", IrErrorType.Instance));
        Add("str", IrPrimitiveType.String, new IrParameter("value", IrErrorType.Instance));
        Add("int", IrPrimitiveType.Integer, new IrParameter("value", IrErrorType.Instance));
        Add("decimal", IrPrimitiveType.Decimal, new IrParameter("value", IrErrorType.Instance));
        Add("bool", IrPrimitiveType.Boolean, new IrParameter("value", IrErrorType.Instance));
        Add("env", IrPrimitiveType.String, new IrParameter("name", IrPrimitiveType.String));
        Add("json", IrDynamicType.Instance, new IrParameter("text", IrPrimitiveType.String));
        Add("csv", new IrListType(IrDynamicType.Instance), new IrParameter("text", IrPrimitiveType.String));
    }

    private IrCall BindParse(string format, List<IrNode> args, CallExprAst call)
    {
        ExpectArity(call, args, 1);
        if (args.Count == 1 && !IsString(args[0].Type) && args[0].Type is not IrErrorType and not IrDynamicType)
        {
            _diagnostics.Error("E2011", $"'as {format.ToUpperInvariant()}' needs text, found '{Describe(args[0].Type)}'.", _filePath, call.Span);
        }

        IrType type = format == "csv" ? new IrListType(IrDynamicType.Instance) : IrDynamicType.Instance;
        return new IrCall(format, args, true, [EffectKind.Pure], call.Span, type);
    }

    private IrNode ErrorNode(SourceSpan span, string message)
    {
        _diagnostics.Error("E2099", message, _filePath, span);
        return new IrLiteral(null, span, IrErrorType.Instance);
    }

    private static string OperatorText(TokenKind kind) => kind switch
    {
        TokenKind.Plus => "+",
        TokenKind.Minus => "-",
        TokenKind.Star => "*",
        TokenKind.Slash => "/",
        TokenKind.Percent => "%",
        TokenKind.EqualEqual => "==",
        TokenKind.BangEqual => "!=",
        TokenKind.Less => "<",
        TokenKind.LessEqual => "<=",
        TokenKind.Greater => ">",
        TokenKind.GreaterEqual => ">=",
        TokenKind.And => "and",
        TokenKind.Or => "or",
        _ => "?"
    };

    private static bool TypesCompatible(IrType expected, IrType actual)
    {
        if (expected is IrErrorType || actual is IrErrorType)
        {
            return true;
        }

        return (expected, actual) switch
        {
            (IrDynamicType, _) => true,
            (IrPrimitiveType, IrDynamicType) => true, // checked by cast when the script runs
            (IrRecordType, IrDynamicType) => false,  // needs "treat ... as a ..."
            (IrPrimitiveType e, IrPrimitiveType a) when e.Name == a.Name => true,
            (IrPrimitiveType { Name: "decimal" }, IrPrimitiveType { Name: "integer" }) => true,
            (IrListType e, IrListType a) when a.ElementType is IrErrorType => true, // empty list
            (IrListType e, IrListType a) => TypesCompatible(e.ElementType, a.ElementType),
            (IrRecordType e, IrRecordType a) when e.Name == a.Name => true,
            _ => false
        };
    }

    // Special-case assignment of empty list: refine variable type from annotation context is not available;
    // allow empty list assignment by treating list<error> as compatible when assigning TO a list.
    // Handled above via IrErrorType element.

    private static bool IsBoolean(IrType type) => type is IrPrimitiveType { Name: "boolean" };
    private static bool IsString(IrType type) => type is IrPrimitiveType { Name: "string" };
    private static bool IsInteger(IrType type) => type is IrPrimitiveType { Name: "integer" };
    private static bool IsDecimal(IrType type) => type is IrPrimitiveType { Name: "decimal" };
    private static bool IsNumeric(IrType type) => IsInteger(type) || IsDecimal(type);

    private static string Describe(IrType type) => type switch
    {
        IrPrimitiveType p => p.Name,
        IrListType l => $"list<{Describe(l.ElementType)}>",
        IrRecordType r => r.Name,
        IrVoidType => "void",
        IrErrorType => "<error>",
        IrDynamicType => "dynamic",
        _ => "?"
    };

    private sealed class FunctionSignature(
        string Name,
        IReadOnlyList<IrParameter> Parameters,
        IrType ReturnType,
        bool IsBuiltin)
    {
        public string Name { get; } = Name;
        public IReadOnlyList<IrParameter> Parameters { get; } = Parameters;
        public IrType ReturnType { get; } = ReturnType;
        public bool IsBuiltin { get; } = IsBuiltin;
    }

    private sealed class Scope(Scope? parent)
    {
        private readonly Scope? _parent = parent;
        private readonly Dictionary<string, IrType> _symbols = new(StringComparer.Ordinal);

        public void Define(string name, IrType type, SourceSpan span, DiagnosticBag diagnostics, string? filePath)
        {
            if (!_symbols.TryAdd(name, type))
            {
                diagnostics.Error("E2027", $"Variable '{name}' is already declared in this scope.", filePath, span);
            }
        }

        public void Replace(string name, IrType type)
        {
            if (_symbols.ContainsKey(name))
            {
                _symbols[name] = type;
                return;
            }

            _parent?.Replace(name, type);
        }

        public bool TryLookup(string name, out IrType? type)
        {
            if (_symbols.TryGetValue(name, out type))
            {
                return true;
            }

            if (_parent is not null)
            {
                return _parent.TryLookup(name, out type);
            }

            type = null;
            return false;
        }

        public IrType Lookup(string name) => TryLookup(name, out var type) ? type! : IrErrorType.Instance;

        public IEnumerable<(string Name, IrType Type)> AllSymbols()
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (Scope? s = this; s is not null; s = s._parent)
            {
                foreach (var (name, type) in s._symbols)
                {
                    if (seen.Add(name))
                    {
                        yield return (name, type);
                    }
                }
            }
        }
    }
}
