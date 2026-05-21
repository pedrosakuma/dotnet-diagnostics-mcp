using Microsoft.Diagnostics.Runtime;

namespace DotnetDiagnosticsMcp.Core.Dump;

internal static class ClrMdAsyncStateMachineWalker
{
    private const string AsyncStateMachineInterfaceName = "System.Runtime.CompilerServices.IAsyncStateMachine";
    private const string TaskTypeName = "System.Threading.Tasks.Task";
    private const int MaxTrackedAsyncOperations = 4096;
    private const int SnapshotAsyncOperations = 256;
    private const int MaxContinuationDepth = 8;
    private const int MaxContinuationReferences = 32;

    public static IReadOnlyList<AsyncOperationStat> WalkPendingAsyncOperations(
        ClrRuntime runtime,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(warnings);

        var operations = new List<RawAsyncOperation>();
        var dedupe = new HashSet<ulong>();
        var typeCache = new Dictionary<ulong, bool>();
        var truncated = false;
        long observedOrder = 0;

        try
        {
            foreach (var obj in runtime.Heap.EnumerateObjects())
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedOrder++;

                if (obj.IsNull || !obj.IsValid || obj.Type is null || obj.IsFree)
                {
                    continue;
                }

                if (!TryCreateAsyncOperation(obj, observedOrder, typeCache, out var operation) || operation is null)
                {
                    continue;
                }

                var dedupeKey = operation.TaskAddress ?? operation.StateMachineAddress;
                if (dedupeKey == 0 || !dedupe.Add(dedupeKey))
                {
                    continue;
                }

                operations.Add(operation);
                if (operations.Count >= MaxTrackedAsyncOperations)
                {
                    truncated = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Async-state walk aborted partway through: {ex.GetType().Name} ({ex.Message}).");
        }

        if (truncated)
        {
            warnings.Add($"Async-state walk hit cap of {MaxTrackedAsyncOperations:N0} tracked operations — the oldest pending async chains were retained and newer ones were dropped.");
        }

        if (operations.Count == 0)
        {
            return Array.Empty<AsyncOperationStat>();
        }

        var byTaskAddress = operations
            .Where(op => op.TaskAddress.HasValue && op.TaskAddress.Value != 0)
            .GroupBy(op => op.TaskAddress!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var byStateMachineAddress = operations
            .GroupBy(op => op.StateMachineAddress)
            .ToDictionary(g => g.Key, g => g.First());

        return operations
            .Where(op => op.State != -2)
            .OrderBy(op => op.ObservedOrder)
            .ThenByDescending(op => op.DirectSizeBytes)
            .Take(SnapshotAsyncOperations)
            .Select(op => op.ToStat(BuildAsyncStack(op, byTaskAddress, byStateMachineAddress)))
            .ToArray();
    }

    private static List<AsyncChainFrame> BuildAsyncStack(
        RawAsyncOperation root,
        IReadOnlyDictionary<ulong, RawAsyncOperation> byTaskAddress,
        IReadOnlyDictionary<ulong, RawAsyncOperation> byStateMachineAddress)
    {
        var stack = new List<AsyncChainFrame>(MaxContinuationDepth + 1) { root.ToFrame() };
        var seen = new HashSet<ulong> { root.TaskAddress ?? root.StateMachineAddress };
        var current = root;

        for (var depth = 0; depth < MaxContinuationDepth; depth++)
        {
            var next = ResolveContinuation(current, byTaskAddress, byStateMachineAddress);
            if (next is null)
            {
                break;
            }

            var key = next.TaskAddress ?? next.StateMachineAddress;
            if (key == 0 || !seen.Add(key))
            {
                break;
            }

            stack.Add(next.ToFrame());
            current = next;
        }

        return stack;
    }

    private static RawAsyncOperation? ResolveContinuation(
        RawAsyncOperation operation,
        IReadOnlyDictionary<ulong, RawAsyncOperation> byTaskAddress,
        IReadOnlyDictionary<ulong, RawAsyncOperation> byStateMachineAddress)
    {
        if (operation.ContinuationObjectAddress is ulong continuationAddress && continuationAddress != 0)
        {
            if (byTaskAddress.TryGetValue(continuationAddress, out var byTask))
            {
                return byTask;
            }

            if (byStateMachineAddress.TryGetValue(continuationAddress, out var byStateMachine))
            {
                return byStateMachine;
            }
        }

        foreach (var referencedAddress in operation.ContinuationReferences)
        {
            if (byTaskAddress.TryGetValue(referencedAddress, out var byTask))
            {
                return byTask;
            }

            if (byStateMachineAddress.TryGetValue(referencedAddress, out var byStateMachine))
            {
                return byStateMachine;
            }
        }

        return null;
    }

    private static bool TryCreateAsyncOperation(
        ClrObject obj,
        long observedOrder,
        Dictionary<ulong, bool> typeCache,
        out RawAsyncOperation? operation)
    {
        operation = null;
        var type = obj.Type;
        if (type is null)
        {
            return false;
        }

        if (IsAsyncStateMachineType(type, typeCache) &&
            TryBuildFromObject(obj, observedOrder, taskOverride: default, continuationSourceOverride: default, out operation))
        {
            return true;
        }

        var stateMachineField = FindEmbeddedAsyncStateMachineField(type, typeCache);
        if (stateMachineField is null)
        {
            return false;
        }

        var taskOverride = IsTaskLike(type) ? obj : default;
        var continuationSource = taskOverride.IsValid ? taskOverride : obj;

        try
        {
            if (stateMachineField.IsObjectReference)
            {
                var stateMachineObject = stateMachineField.ReadObject(obj.Address, interior: false);
                if (stateMachineObject.IsNull || !stateMachineObject.IsValid)
                {
                    return false;
                }

                return TryBuildFromObject(stateMachineObject, observedOrder, taskOverride, continuationSource, out operation);
            }

            if (!stateMachineField.IsValueType)
            {
                return false;
            }

            var stateMachineValue = stateMachineField.ReadStruct(obj.Address, interior: false);
            if (!stateMachineValue.IsValid)
            {
                return false;
            }

            return TryBuildFromValueType(stateMachineValue, observedOrder, taskOverride, continuationSource, out operation);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildFromObject(
        ClrObject stateMachineObject,
        long observedOrder,
        ClrObject taskOverride,
        ClrObject continuationSourceOverride,
        out RawAsyncOperation? operation)
    {
        operation = null;
        var type = stateMachineObject.Type;
        if (type is null)
        {
            return false;
        }

        var stateField = type.GetFieldByName("<>1__state");
        if (stateField is null)
        {
            return false;
        }

        try
        {
            var state = stateField.Read<int>(stateMachineObject.Address, interior: false);
            var awaiterType = TryReadAwaiterType(type.Fields, stateMachineObject.Address, interior: false);
            var taskObject = TryResolveTask(type, stateMachineObject.Address, interior: false, taskOverride);
            var hasBuilderField = FindFieldByName(type, "<>t__builder") is not null;
            if (!hasBuilderField && awaiterType is null && !taskObject.IsValid && !continuationSourceOverride.IsValid)
            {
                return false;
            }

            var continuationSource = continuationSourceOverride.IsValid
                ? continuationSourceOverride
                : (taskObject.IsValid ? taskObject : stateMachineObject);
            var continuation = ReadContinuation(continuationSource);

            operation = new RawAsyncOperation(
                StateMachineTypeFullName: type.Name ?? "<unknown>",
                StateMachineAddress: stateMachineObject.Address,
                State: state,
                AwaiterTypeFullName: awaiterType,
                TaskAddress: taskObject.IsValid ? taskObject.Address : null,
                TaskTypeFullName: taskObject.IsValid ? taskObject.Type?.Name : null,
                TaskId: taskObject.IsValid ? TryReadTaskId(taskObject) : null,
                ContinuationObjectAddress: continuation.Address,
                ContinuationObjectTypeFullName: continuation.TypeFullName,
                ContinuationReferences: continuation.References,
                ObservedOrder: observedOrder,
                DirectSizeBytes: (long)Math.Max(stateMachineObject.Size, taskObject.IsValid ? taskObject.Size : 0UL));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBuildFromValueType(
        ClrValueType stateMachineValue,
        long observedOrder,
        ClrObject taskOverride,
        ClrObject continuationSourceOverride,
        out RawAsyncOperation? operation)
    {
        operation = null;
        var type = stateMachineValue.Type;
        if (type is null)
        {
            return false;
        }

        try
        {
            var state = stateMachineValue.ReadField<int>("<>1__state");
            var awaiterType = TryReadAwaiterType(type.Fields, stateMachineValue.Address, interior: true);
            var taskObject = TryResolveTask(stateMachineValue, taskOverride);
            var hasBuilderField = FindFieldByName(type, "<>t__builder") is not null;
            if (!hasBuilderField && awaiterType is null && !taskObject.IsValid && !continuationSourceOverride.IsValid)
            {
                return false;
            }

            var continuationSource = continuationSourceOverride.IsValid ? continuationSourceOverride : taskObject;
            var continuation = continuationSource.IsValid
                ? ReadContinuation(continuationSource)
                : EmptyContinuation;

            operation = new RawAsyncOperation(
                StateMachineTypeFullName: type.Name ?? "<unknown>",
                StateMachineAddress: stateMachineValue.Address,
                State: state,
                AwaiterTypeFullName: awaiterType,
                TaskAddress: taskObject.IsValid ? taskObject.Address : null,
                TaskTypeFullName: taskObject.IsValid ? taskObject.Type?.Name : null,
                TaskId: taskObject.IsValid ? TryReadTaskId(taskObject) : null,
                ContinuationObjectAddress: continuation.Address,
                ContinuationObjectTypeFullName: continuation.TypeFullName,
                ContinuationReferences: continuation.References,
                ObservedOrder: observedOrder,
                DirectSizeBytes: (long)Math.Max((ulong)(type.StaticSize > 0 ? type.StaticSize : 0), taskObject.IsValid ? taskObject.Size : 0UL));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ClrObject TryResolveTask(ClrType type, ulong ownerAddress, bool interior, ClrObject taskOverride)
    {
        if (taskOverride.IsValid)
        {
            return taskOverride;
        }

        var builderField = type.GetFieldByName("<>t__builder");
        if (builderField is null)
        {
            return default;
        }

        try
        {
            if (builderField.IsObjectReference)
            {
                var builderObject = builderField.ReadObject(ownerAddress, interior);
                if (builderObject.IsValid && builderObject.Type is not null && IsTaskLike(builderObject.Type))
                {
                    return builderObject;
                }

                return default;
            }

            if (!builderField.IsValueType)
            {
                return default;
            }

            var builder = builderField.ReadStruct(ownerAddress, interior);
            return TryResolveTask(builder, default);
        }
        catch
        {
            return default;
        }
    }

    private static ClrObject TryResolveTask(ClrValueType stateMachineValue, ClrObject taskOverride)
    {
        if (taskOverride.IsValid)
        {
            return taskOverride;
        }

        ClrValueType builder;
        try
        {
            builder = stateMachineValue.ReadValueTypeField("<>t__builder");
        }
        catch
        {
            return default;
        }

        if (!builder.IsValid)
        {
            return default;
        }

        try
        {
            return builder.ReadObjectField("m_task");
        }
        catch
        {
            return default;
        }
    }

    private static string? TryReadAwaiterType(
        IEnumerable<ClrInstanceField> fields,
        ulong ownerAddress,
        bool interior)
    {
        string? fallback = null;
        foreach (var field in fields)
        {
            if (field.Name is null || !field.Name.StartsWith("<>u__", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (field.IsObjectReference)
                {
                    var awaiterObject = field.ReadObject(ownerAddress, interior);
                    if (!awaiterObject.IsNull && awaiterObject.IsValid)
                    {
                        return awaiterObject.Type?.Name ?? field.Type?.Name;
                    }

                    fallback ??= field.Type?.Name;
                    continue;
                }

                if (!field.IsValueType)
                {
                    fallback ??= field.Type?.Name;
                    continue;
                }

                var awaiterValue = field.ReadStruct(ownerAddress, interior);
                if (!awaiterValue.IsValid)
                {
                    fallback ??= field.Type?.Name;
                    continue;
                }

                if (HasReachableObject(awaiterValue))
                {
                    return awaiterValue.Type?.Name ?? field.Type?.Name;
                }

                fallback ??= awaiterValue.Type?.Name ?? field.Type?.Name;
            }
            catch
            {
                fallback ??= field.Type?.Name;
            }
        }

        return fallback;
    }

    private static bool HasReachableObject(ClrValueType valueType)
    {
        var type = valueType.Type;
        if (type is null)
        {
            return false;
        }

        foreach (var field in type.Fields)
        {
            if (!field.IsObjectReference)
            {
                continue;
            }

            try
            {
                var value = field.ReadObject(valueType.Address, interior: true);
                if (!value.IsNull && value.IsValid)
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static RawContinuationInfo ReadContinuation(ClrObject continuationSource)
    {
        if (!continuationSource.IsValid || continuationSource.Type is null)
        {
            return EmptyContinuation;
        }

        var continuationField = FindFieldByName(continuationSource.Type, "m_continuationObject");
        if (continuationField is null || !continuationField.IsObjectReference)
        {
            return EmptyContinuation;
        }

        try
        {
            var continuationObject = continuationField.ReadObject(continuationSource.Address, interior: false);
            if (continuationObject.IsNull || !continuationObject.IsValid)
            {
                return EmptyContinuation;
            }

            var references = continuationObject
                .EnumerateReferences(carefully: true, considerDependantHandles: false)
                .Select(reference => reference.Address)
                .Where(address => address != 0)
                .Distinct()
                .Take(MaxContinuationReferences)
                .ToArray();

            return new RawContinuationInfo(
                continuationObject.Address,
                continuationObject.Type?.Name,
                references);
        }
        catch
        {
            return EmptyContinuation;
        }
    }

    private static int? TryReadTaskId(ClrObject taskObject)
    {
        if (!taskObject.IsValid || taskObject.Type is null)
        {
            return null;
        }

        var taskIdField = FindFieldByName(taskObject.Type, "m_taskId");
        if (taskIdField is null)
        {
            return null;
        }

        try
        {
            return taskIdField.Read<int>(taskObject.Address, interior: false);
        }
        catch
        {
            return null;
        }
    }

    private static ClrInstanceField? FindEmbeddedAsyncStateMachineField(ClrType type, Dictionary<ulong, bool> typeCache)
    {
        foreach (var field in type.Fields)
        {
            var fieldType = field.Type;
            if (fieldType is null)
            {
                continue;
            }

            if (IsAsyncStateMachineType(fieldType, typeCache))
            {
                return field;
            }
        }

        return null;
    }

    private static ClrInstanceField? FindFieldByName(ClrType type, string fieldName)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var field = current.GetFieldByName(fieldName);
            if (field is not null)
            {
                return field;
            }
        }

        return null;
    }

    private static bool IsAsyncStateMachineType(ClrType type, Dictionary<ulong, bool> typeCache)
    {
        if (type.MethodTable != 0 && typeCache.TryGetValue(type.MethodTable, out var cached))
        {
            return cached;
        }

        var isAsync = IsAsyncStateMachineTypeUncached(type);
        if (type.MethodTable != 0)
        {
            typeCache[type.MethodTable] = isAsync;
        }

        return isAsync;
    }

    private static bool IsAsyncStateMachineTypeUncached(ClrType type)
    {
        var name = type.Name;
        if (!string.IsNullOrEmpty(name) && name.Contains('<') && name.Contains(">d__", StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            return type.EnumerateInterfaces().Any(i => string.Equals(i.Name, AsyncStateMachineInterfaceName, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTaskLike(ClrType type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, TaskTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static readonly RawContinuationInfo EmptyContinuation = new(null, null, Array.Empty<ulong>());

    private readonly record struct RawContinuationInfo(
        ulong? Address,
        string? TypeFullName,
        IReadOnlyList<ulong> References);

    private sealed record RawAsyncOperation(
        string StateMachineTypeFullName,
        ulong StateMachineAddress,
        int State,
        string? AwaiterTypeFullName,
        ulong? TaskAddress,
        string? TaskTypeFullName,
        int? TaskId,
        ulong? ContinuationObjectAddress,
        string? ContinuationObjectTypeFullName,
        IReadOnlyList<ulong> ContinuationReferences,
        long ObservedOrder,
        long DirectSizeBytes)
    {
        public AsyncOperationStat ToStat(IReadOnlyList<AsyncChainFrame> stack)
            => new(StateMachineTypeFullName, State, AwaiterTypeFullName, DirectSizeBytes)
            {
                StateMachineAddress = StateMachineAddress,
                TaskAddress = TaskAddress,
                TaskId = TaskId,
                TaskTypeFullName = TaskTypeFullName,
                ContinuationObjectTypeFullName = ContinuationObjectTypeFullName,
                Stack = stack,
                ObservedOrder = ObservedOrder,
            };

        public AsyncChainFrame ToFrame()
            => new(StateMachineTypeFullName, State, AwaiterTypeFullName, StateMachineAddress)
            {
                TaskAddress = TaskAddress,
                TaskId = TaskId,
                ContinuationObjectTypeFullName = ContinuationObjectTypeFullName,
            };
    }
}
