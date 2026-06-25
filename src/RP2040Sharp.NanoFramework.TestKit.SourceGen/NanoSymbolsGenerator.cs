using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RP2040Sharp.NanoFramework.TestKit.SourceGen
{
    /// <summary>
    /// Emits a strongly-typed symbols class for a nanoFramework app, so integration tests reference real
    /// type/field/method names instead of raw strings. The app's built managed assembly (.exe) is fed in
    /// as an AdditionalFile flagged NanoSymbols (see the package .targets); we read it through
    /// System.Reflection.Metadata and emit:
    ///   • <c>Assembly</c>     — the assembly name (use with FindAssembly / RunUntilStatic).
    ///   • <c>Fields.X</c>     — each static field's simple name (use with RunUntilStatic / ReadStatic).
    ///   • <c>Methods.X</c>    — "Assembly!Method" (use with RunUntilNativeCall-style helpers).
    /// </summary>
    [Generator]
    public sealed class NanoSymbolsGenerator : IIncrementalGenerator
    {
        private const string FlagKey = "build_metadata.AdditionalFiles.NanoSymbols";
        private const string NamespaceKey = "build_metadata.AdditionalFiles.NanoSymbolsNamespace";
        private const string ClassKey = "build_metadata.AdditionalFiles.NanoSymbolsClass";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var specs = context.AdditionalTextsProvider
                .Combine(context.AnalyzerConfigOptionsProvider)
                .Select((pair, ct) =>
                {
                    var options = pair.Right.GetOptions(pair.Left);
                    if (!options.TryGetValue(FlagKey, out var flag) ||
                        !string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        return (Spec?)null;
                    }

                    options.TryGetValue(NamespaceKey, out var ns);
                    options.TryGetValue(ClassKey, out var cls);
                    return new Spec(pair.Left.Path, ns ?? "", cls ?? "");
                })
                .Where(s => s.HasValue)
                .Select((s, ct) => s!.Value);

            context.RegisterSourceOutput(specs, static (spc, spec) => Emit(spc, spec));
        }

        private readonly struct Spec
        {
            public Spec(string assemblyPath, string ns, string className)
            {
                AssemblyPath = assemblyPath;
                Namespace = ns;
                ClassName = className;
            }

            public string AssemblyPath { get; }
            public string Namespace { get; }
            public string ClassName { get; }
        }

        private static readonly DiagnosticDescriptor CannotRead = new(
            id: "NANO001",
            title: "nanoFramework app assembly not readable",
            messageFormat: "Could not read the nanoFramework app assembly '{0}'. Build the .nfproj first.",
            category: "NanoSymbols",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        private static void Emit(SourceProductionContext spc, Spec spec)
        {
            string assemblyName;
            var methods = new SortedSet<string>(StringComparer.Ordinal);
            var staticFields = new SortedSet<string>(StringComparer.Ordinal);
            // Per-type structure extracted from the project: type name -> its readable fields
            // (static + instance, non-const), so tests can address instance fields by type.
            var types = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

            try
            {
                using var stream = File.OpenRead(spec.AssemblyPath);
                using var pe = new PEReader(stream);
                var mr = pe.GetMetadataReader();
                assemblyName = mr.GetString(mr.GetAssemblyDefinition().Name);

                foreach (var typeHandle in mr.TypeDefinitions)
                {
                    var type = mr.GetTypeDefinition(typeHandle);
                    var typeName = mr.GetString(type.Name);
                    if (!IsIdentifier(typeName))
                    {
                        continue; // skip compiler-generated types
                    }

                    var typeFields = new SortedSet<string>(StringComparer.Ordinal);

                    foreach (var mh in type.GetMethods())
                    {
                        var m = mr.GetMethodDefinition(mh);
                        if ((m.Attributes & (MethodAttributes.SpecialName | MethodAttributes.RTSpecialName)) != 0)
                        {
                            continue; // ctors/.cctor, accessors, operators
                        }

                        var name = mr.GetString(m.Name);
                        if (IsIdentifier(name))
                        {
                            methods.Add(name);
                        }
                    }

                    foreach (var fh in type.GetFields())
                    {
                        var f = mr.GetFieldDefinition(fh);
                        if ((f.Attributes & FieldAttributes.Literal) != 0)
                        {
                            continue; // const → not a runtime cell
                        }

                        var name = mr.GetString(f.Name);
                        if (!IsIdentifier(name))
                        {
                            continue;
                        }

                        typeFields.Add(name); // both static and instance fields are readable
                        if ((f.Attributes & FieldAttributes.Static) != 0)
                        {
                            staticFields.Add(name);
                        }
                    }

                    if (typeFields.Count > 0)
                    {
                        types[typeName] = typeFields;
                    }
                }
            }
            catch
            {
                spc.ReportDiagnostic(Diagnostic.Create(CannotRead, Location.None, spec.AssemblyPath));
                return;
            }

            var className = !string.IsNullOrEmpty(spec.ClassName) ? spec.ClassName : Sanitize(assemblyName) + "Symbols";
            spc.AddSource($"{className}.NanoSymbols.g.cs", Render(spec.Namespace, className, assemblyName, methods, staticFields, types));
        }

        private static string Render(string ns, string className, string assemblyName,
            SortedSet<string> methods, SortedSet<string> fields,
            SortedDictionary<string, SortedSet<string>> types)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/> Generated by RP2040Sharp.NanoFramework.TestKit (NanoSymbolsGenerator). Do not edit.");
            sb.AppendLine("#nullable enable");
            var indent = "";
            var hasNs = !string.IsNullOrEmpty(ns);
            if (hasNs)
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
                indent = "    ";
            }

            sb.AppendLine($"{indent}/// <summary>Strongly-typed symbols for the '{assemblyName}' nanoFramework app.</summary>");
            sb.AppendLine($"{indent}internal static class {className}");
            sb.AppendLine($"{indent}{{");
            sb.AppendLine($"{indent}    /// <summary>The app assembly name.</summary>");
            sb.AppendLine($"{indent}    public const string Assembly = \"{Escape(assemblyName)}\";");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Methods as \"{Escape(assemblyName)}!Method\".</summary>");
            sb.AppendLine($"{indent}    public static class Methods");
            sb.AppendLine($"{indent}    {{");
            foreach (var m in methods)
            {
                sb.AppendLine($"{indent}        public const string {Member(m)} = \"{Escape(assemblyName)}!{Escape(m)}\";");
            }

            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Static fields by simple name (use with RunUntilStatic / ReadStatic).</summary>");
            sb.AppendLine($"{indent}    public static class Fields");
            sb.AppendLine($"{indent}    {{");
            foreach (var f in fields)
            {
                sb.AppendLine($"{indent}        public const string {Member(f)} = \"{Escape(f)}\";");
            }

            sb.AppendLine($"{indent}    }}");
            sb.AppendLine();
            sb.AppendLine($"{indent}    /// <summary>Each type and its readable fields, extracted from the project structure.</summary>");
            sb.AppendLine($"{indent}    public static class Types");
            sb.AppendLine($"{indent}    {{");
            foreach (var t in types)
            {
                sb.AppendLine($"{indent}        /// <summary>Type '{t.Key}'.</summary>");
                sb.AppendLine($"{indent}        public static class {Member(t.Key)}");
                sb.AppendLine($"{indent}        {{");
                sb.AppendLine($"{indent}            public const string Name = \"{Escape(t.Key)}\";");
                sb.AppendLine($"{indent}            public static class Fields");
                sb.AppendLine($"{indent}            {{");
                foreach (var f in t.Value)
                {
                    sb.AppendLine($"{indent}                public const string {Member(f)} = \"{Escape(f)}\";");
                }

                sb.AppendLine($"{indent}            }}");
                sb.AppendLine($"{indent}        }}");
            }

            sb.AppendLine($"{indent}    }}");
            sb.AppendLine($"{indent}}}");

            if (hasNs)
            {
                sb.AppendLine("}");
            }

            return sb.ToString();
        }

        private static bool IsIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            if (!char.IsLetter(name[0]) && name[0] != '_')
            {
                return false;
            }

            for (var i = 1; i < name.Length; i++)
            {
                if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                {
                    return false;
                }
            }

            return true;
        }

        private static string Member(string name) =>
            SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ||
            SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

        private static string Sanitize(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            var s = new string(chars);
            return char.IsDigit(s[0]) ? "_" + s : s;
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
