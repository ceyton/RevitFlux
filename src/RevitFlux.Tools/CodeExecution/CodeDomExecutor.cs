// CodeDom executor for .NET Framework 4.8 (Revit 2023/2024)
// On net8+ builds, Roslyn is used instead — this file is excluded via #if.
#if !REVIT2025_OR_GREATER
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Microsoft.CSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitFlux.Core.Results;
using RevitFlux.Core.Tools;

namespace RevitFlux.Tools.CodeExecution;

/// <summary>
/// Compiles and executes C# code using CSharpCodeProvider (CodeDom).
/// Fallback for Revit 2023/2024 where Roslyn Scripting is not available.
/// </summary>
public static class CodeDomExecutor
{
    public static CortexResult<object> Execute(
        string code,
        ScriptGlobals globals,
        ScriptTransactionMode transactionMode = ScriptTransactionMode.Auto)
    {
        try
        {
            // Wrap user code in a class with a static method
            var wrappedCode = WrapCode(code);

            // Compile
            var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                IncludeDebugInformation = false,
            };

            // Add references from loaded assemblies, deduplicated by simple
            // assembly name to avoid conflicts from other Revit add-ins.
            var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location))
                    {
                        var name = asm.GetName().Name ?? string.Empty;
                        if (seenReferences.Add(name))
                            parameters.ReferencedAssemblies.Add(asm.Location);
                    }
                }
                catch { }
            }

            var compileResult = provider.CompileAssemblyFromSource(parameters, wrappedCode);

            if (compileResult.Errors.HasErrors)
            {
                var errors = new List<string>();
                foreach (CompilerError err in compileResult.Errors)
                {
                    if (!err.IsWarning)
                        errors.Add($"Line {err.Line - CountPrefixLines()}: {err.ErrorText}");
                }
                return CortexResult<object>.Fail(
                    CortexErrorCode.InvalidInput,
                    $"Compilation error:\n{string.Join("\n", errors)}",
                    suggestion: "Revit 2023/2024 scripts compile as C# 5: no $\"\" interpolation, ?. operators, expression bodies, out var or pattern matching. Globals: document (Document), uiDocument (UIDocument), app (Application).");
            }

            // Execute
            var assembly = compileResult.CompiledAssembly;
            var type = assembly.GetType("RevitFlux.DynamicScript.ScriptRunner");
            var method = type!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);

            object? result;

            var failure = ScriptTransactionRunner.Run(globals.document, transactionMode,
                () => method!.Invoke(null, new object[] { globals.document, globals.uiDocument, globals.app }),
                out result);
            if (failure != null) return failure;

            return CortexResult<object>.Ok(SerializeResult(result));
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            return CortexResult<object>.Fail(
                CortexErrorCode.Unknown,
                $"Runtime error: {ex.InnerException}",
                suggestion: "Check variable names, null references, and Revit API usage. Full stack trace above.");
        }
        catch (Exception ex)
        {
            return CortexResult<object>.Fail(
                CortexErrorCode.Unknown,
                $"Execution error: {ex.Message}");
        }
    }

    private static int CountPrefixLines() => 12; // Number of lines before user code in WrapCode()

    private static string WrapCode(string userCode)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using Autodesk.Revit.DB;");
        sb.AppendLine("using Autodesk.Revit.UI;");
        sb.AppendLine("using Newtonsoft.Json.Linq;");
        sb.AppendLine("namespace RevitFlux.DynamicScript {");
        sb.AppendLine("  public static class ScriptRunner {");
        sb.AppendLine("    public static object Run(");
        sb.AppendLine("      Autodesk.Revit.DB.Document document,");
        sb.AppendLine("      Autodesk.Revit.UI.UIDocument uiDocument,");
        sb.AppendLine("      Autodesk.Revit.ApplicationServices.Application app) {");
        sb.AppendLine(userCode);
        sb.AppendLine("      return null;");
        sb.AppendLine("    }");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static object SerializeResult(object? result)
    {
        if (result == null)
            return new { result = (object?)null };

        if (result is string || result is int || result is long || result is double || result is float || result is bool)
            return new { result };

        if (result is Element elem)
            // H10: elem.Id.IntegerValue truncates 64-bit IDs on R2024 (net48). Use the
            // version-guarded helper (Value on R2024+, IntegerValue cast on R2023).
            return new { elementId = Utilities.ToolHelpers.GetElementIdValue(elem.Id), name = elem.Name, category = elem.Category?.Name };

        try
        {
            var json = JsonConvert.SerializeObject(result, new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                MaxDepth = 5,
                Error = (sender, args) => args.ErrorContext.Handled = true
            });
            var token = JToken.Parse(json);
            // The result envelope expects a JObject. A script that returns a collection
            // (List/array) parses to a JArray — wrap it so it doesn't fail downstream with
            // "Object serialized to Array. JObject instance expected."
            return token is JObject ? token : new JObject { ["result"] = token };
        }
        catch
        {
            return new { result = result.ToString() };
        }
    }
}
#endif
