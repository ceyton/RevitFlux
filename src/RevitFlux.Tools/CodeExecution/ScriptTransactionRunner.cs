using System;
using Autodesk.Revit.DB;
using RevitFlux.Core.Results;
using RevitFlux.Core.Tools;
using RevitFlux.Tools.Utilities;

namespace RevitFlux.Tools.CodeExecution;

/// <summary>Runs a compiled script under the requested transaction mode.</summary>
internal static class ScriptTransactionRunner
{
    /// <summary>Returns null on success (with <paramref name="result"/> set), or the failure.</summary>
    public static CortexResult<object>? Run(
        Document document, ScriptTransactionMode mode, Func<object?> script, out object? result)
    {
        result = null;
        switch (mode)
        {
            case ScriptTransactionMode.Manual:
                result = script();
                return null;

            case ScriptTransactionMode.ReadOnly:
                // Rolling back the group also undoes transactions the script commits itself.
                using (var group = new TransactionGroup(document, "RevitFlux: Read-only Script"))
                {
                    group.Start();
                    try { result = script(); }
                    finally
                    {
                        if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                    }
                }
                return null;

            case ScriptTransactionMode.Group:
                using (var group = new TransactionGroup(document, "RevitFlux: Script Group"))
                {
                    group.Start();
                    try
                    {
                        result = script();
                        if (group.GetStatus() == TransactionStatus.Started
                            && group.Assimilate() != TransactionStatus.Committed)
                        {
                            return CortexResult<object>.Fail(CortexErrorCode.TransactionFailed,
                                "Revit rolled back the script transaction group on commit.",
                                suggestion: "Fix the reported model errors and retry.");
                        }
                    }
                    catch
                    {
                        if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                        throw;
                    }
                }
                return null;

            default:
                using (var tx = new Transaction(document, "RevitFlux: Script"))
                {
                    var failures = TransactionFailureHandling.SuppressWarnings(tx);
                    tx.Start();
                    try
                    {
                        result = script();
                        if (tx.GetStatus() == TransactionStatus.Started
                            && tx.Commit() != TransactionStatus.Committed)
                        {
                            return CortexResult<object>.Fail(CortexErrorCode.TransactionFailed,
                                $"Revit rolled back the script transaction: {TransactionFailureHandling.Describe(failures)}",
                                suggestion: "The script triggered a Revit error during commit. Fix the reported model errors and retry.");
                        }
                    }
                    catch
                    {
                        if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                        throw;
                    }
                }
                return null;
        }
    }
}
