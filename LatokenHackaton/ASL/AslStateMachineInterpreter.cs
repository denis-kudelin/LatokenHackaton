using System.Collections;
using System.Globalization;

namespace LatokenHackaton.ASL
{
    internal sealed class AslStateMachineInterpreter
    {
        public delegate Task<object?> ExternalFunctionCallDelegate(string resourceName, object?[]? parameters);

        private readonly Dictionary<string, Func<string, AslState, object?, Task<StateResult>>> handlers;
        private readonly ExternalFunctionCallDelegate externalFunction;
        private object? globalData;

        public AslStateMachineInterpreter(ExternalFunctionCallDelegate externalFunction)
        {
            this.externalFunction = externalFunction;
            this.handlers = new Dictionary<string, Func<string, AslState, object?, Task<StateResult>>>(StringComparer.OrdinalIgnoreCase)
            {
                { "Pass", HandlePassAsync },
                { "Task", HandleTaskAsync },
                { "Choice", HandleChoiceAsync },
                { "Wait", HandleWaitAsync },
                { "Succeed", HandleSucceedAsync },
                { "Fail", HandleFailAsync },
                { "Map", HandleMapAsync },
                { "Parallel", HandleParallelAsync }
            };
        }

        public async Task<object?> InterpretAsync(AslDefinition definition, object? inputData = null)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrWhiteSpace(definition.StartAt)) throw new ArgumentException("StartAt is required.");
            if (definition.States == null || definition.States.Count == 0) throw new ArgumentException("States must be non-empty.");
            if (!definition.States.ContainsKey(definition.StartAt)) throw new InvalidOperationException($"Start state '{definition.StartAt}' not found.");

            this.globalData = DeepClone(inputData);
            var currentData = this.globalData;
            var currentStateName = definition.StartAt;

            while (true)
            {
                if (!definition.States.TryGetValue(currentStateName, out var currentState))
                    throw new InvalidOperationException($"State '{currentStateName}' not found.");

                var inputExtract = ApplyInputPath(currentData, currentState.InputPath);
                var parametersObj = BuildParameters(currentData, currentState.Parameters);
                var effectiveInput = parametersObj ?? inputExtract;

                if (!this.handlers.TryGetValue(currentState.Type, out var handler))
                    throw new NotSupportedException($"Unsupported state type '{currentState.Type}'.");

                var result = await handler(currentStateName, currentState, effectiveInput);
                this.globalData = MergeObjects(this.globalData, result.Output);
                if (result.Ended) return this.globalData;
                currentData = result.Output;
                currentStateName = result.NextState;
            }
        }

        private async Task<StateResult> HandlePassAsync(string currentStateName, AslState state, object? effectiveInput)
        {
            var passResult = state.Result ?? effectiveInput;
            var rp = string.IsNullOrWhiteSpace(state.ResultPath) ? $"$.{currentStateName}" : state.ResultPath;
            var passMerged = PlaceValueByResultPath(effectiveInput, passResult, rp);
            var passOutput = ApplyOutputPath(passMerged, state.OutputPath);
            if (IsEnd(state)) return new StateResult(passOutput, null, true);
            return new StateResult(passOutput, state.Next, false);
        }

        private async Task<StateResult> HandleTaskAsync(string currentStateName, AslState state, object? effectiveInput)
        {
            if (string.IsNullOrEmpty(state.Resource))
                throw new InvalidOperationException("Task state has no Resource.");

            var taskInput = state.Result == null
                ? effectiveInput
                : MergeObjects(effectiveInput, state.Result);

            var (fn, arr) = ResolveLambdaCall(currentStateName, state.Resource, BuildParameters(taskInput, state.Parameters), taskInput);
            var taskResult = await this.externalFunction(fn, arr);
            var rp = string.IsNullOrWhiteSpace(state.ResultPath) ? $"$.{currentStateName}" : state.ResultPath;
            var taskMerged = PlaceValueByResultPath(effectiveInput, taskResult, rp);
            var taskOutput = ApplyOutputPath(taskMerged, state.OutputPath);

            if (IsEnd(state)) return new StateResult(taskOutput, null, true);
            return new StateResult(taskOutput, state.Next, false);
        }

        private (string, object?[]?) ResolveLambdaCall(string currentStateName, string resource, object? builtParams, object? taskInput)
        {
            var functionName = resource;
            object?[]? args;
            if (!resource.Equals("arn:aws:states:::lambda:invoke", StringComparison.OrdinalIgnoreCase))
            {
                args = BuildParameterArray(taskInput, builtParams);
                return (functionName, args);
            }

            if (builtParams is Dictionary<string, object?> dict)
            {
                if (dict.TryGetValue("FunctionName", out var fnObj))
                {
                    functionName = fnObj?.ToString() ?? currentStateName;
                    dict.Remove("FunctionName");
                }

                if (dict.TryGetValue("Payload", out var payloadObj))
                {
                    args = BuildParameterArray(taskInput, payloadObj);
                    dict.Remove("Payload");
                }
                else
                {
                    args = BuildParameterArray(taskInput, dict);
                }
            }
            else
            {
                args = BuildParameterArray(taskInput, builtParams);
            }

            return (functionName, args);
        }

        private async Task<StateResult> HandleChoiceAsync(string currentStateName, AslState state, object? effectiveInput)
        {
            if (state.Choices == null || state.Choices.Count == 0) throw new InvalidOperationException("Choice state missing Choices.");
            var matched = false;
            var nextState = state.Default;
            var output = effectiveInput;
            foreach (var choice in state.Choices)
            {
                if (EvaluateChoice(choice, effectiveInput))
                {
                    if (!string.IsNullOrEmpty(choice.Next))
                    {
                        nextState = choice.Next;
                        matched = true;
                        break;
                    }
                }
            }
            if (!matched && string.IsNullOrEmpty(nextState)) return new StateResult(output, null, true);
            return new StateResult(output, nextState, false);
        }

        private async Task<StateResult> HandleWaitAsync(string currentStateName, AslState state, object? effectiveInput)
        {
            var waitTime = 0;
            if (state.Seconds.HasValue) waitTime = state.Seconds.Value;
            else if (!string.IsNullOrEmpty(state.SecondsPath))
            {
                var spVal = GetValueByPath(effectiveInput, state.SecondsPath) ?? GetValueByPath(this.globalData, state.SecondsPath);
                if (spVal != null && int.TryParse(spVal.ToString(), out var spInt)) waitTime = spInt;
            }
            if (!string.IsNullOrEmpty(state.Timestamp))
            {
                var targetTime = DateTime.Parse(state.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                var now = DateTime.UtcNow;
                if (targetTime > now)
                {
                    var delta = targetTime - now;
                    if (delta > TimeSpan.Zero) await Task.Delay(delta);
                }
            }
            else if (!string.IsNullOrEmpty(state.TimestampPath))
            {
                var tsVal = GetValueByPath(effectiveInput, state.TimestampPath) ?? GetValueByPath(this.globalData, state.TimestampPath);
                if (tsVal != null)
                {
                    var targetTime = DateTime.Parse(tsVal.ToString() ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
                    var now = DateTime.UtcNow;
                    if (targetTime > now)
                    {
                        var delta = targetTime - now;
                        if (delta > TimeSpan.Zero) await Task.Delay(delta);
                    }
                }
            }
            if (waitTime > 0) await Task.Delay(TimeSpan.FromSeconds(waitTime));
            if (IsEnd(state)) return new StateResult(effectiveInput, null, true);
            return new StateResult(effectiveInput, state.Next, false);
        }

        private async Task<StateResult> HandleSucceedAsync(string currentStateName, AslState state, object? effectiveInput)
        {
            return new StateResult(effectiveInput, null, true);
        }

        private async Task<StateResult> HandleFailAsync(string currentStateName, AslState state, object? effectiveInput)
        {
            throw new Exception($"{(string.IsNullOrEmpty(state.Error) ? "FailState" : state.Error)}: {(string.IsNullOrEmpty(state.Cause) ? "Failure" : state.Cause)}");
        }

        private async Task<StateResult> HandleMapAsync(string currentStateName, AslState state, object? effectiveInput)
        {
            if (string.IsNullOrEmpty(state.ItemsPath) || state.Iterator == null)
                throw new InvalidOperationException("Map state missing ItemsPath or Iterator.");

            var listObj = GetValueByPath(effectiveInput, state.ItemsPath) ?? GetValueByPath(this.globalData, state.ItemsPath);
            if (!(listObj is IEnumerable listEnumerable))
                throw new InvalidOperationException("Map ItemsPath must point to an array or list.");

            var itemsCollection = listEnumerable.Cast<object?>().ToList();
            var concurrency = state.MaxConcurrency.HasValue && state.MaxConcurrency.Value > 0 ? state.MaxConcurrency.Value : itemsCollection.Count;
            var branchList = new List<Task<object?>>();
            var mapResults = new List<object?>();

            for (int i = 0; i < itemsCollection.Count; i++)
            {
                var item = itemsCollection[i];
                if (branchList.Count >= concurrency)
                {
                    var finished = await Task.WhenAny(branchList);
                    branchList.Remove(finished);
                    mapResults.Add(await finished);
                }
                branchList.Add(InterpretAsync(state.Iterator, BuildParameters(item, state.Parameters)));
            }

            mapResults.AddRange(await Task.WhenAll(branchList));
            var rp = string.IsNullOrWhiteSpace(state.ResultPath) ? $"$.{currentStateName}" : state.ResultPath;
            var mappedData = PlaceValueByResultPath(effectiveInput, mapResults, rp);
            if (IsEnd(state)) return new StateResult(mappedData, null, true);
            var output = ApplyOutputPath(mappedData, state.OutputPath);
            return new StateResult(output, state.Next, false);
        }

        private async Task<StateResult> HandleParallelAsync(string currentStateName, AslState state, object? effectiveInput)
        {
            if (state.Branches == null || state.Branches.Count == 0)
                throw new InvalidOperationException("Parallel state missing Branches.");
            var tasks = new List<Task<object?>>();
            foreach (var branchDef in state.Branches)
            {
                tasks.Add(InterpretAsync(branchDef, effectiveInput));
            }
            var parallelResults = await Task.WhenAll(tasks);
            var parallelMerged = MergeObjects(effectiveInput, parallelResults);
            if (IsEnd(state)) return new StateResult(parallelMerged, null, true);
            var output = ApplyOutputPath(parallelMerged, state.OutputPath);
            return new StateResult(output, state.Next, false);
        }

        private static bool IsEnd(AslState s) => s.End == true;

        private record StateResult(object? Output, string? NextState, bool Ended);

        private static bool EvaluateChoice(AslChoice choice, object? data)
        {
            var val = GetValueByPath(data, choice.Variable);
            if (choice.NumericGreaterThan.HasValue) { if (TryDouble(val, out var dv) && dv > choice.NumericGreaterThan.Value) return true; }
            if (choice.NumericGreaterThanEquals.HasValue) { if (TryDouble(val, out var dv) && dv >= choice.NumericGreaterThanEquals.Value) return true; }
            if (choice.NumericLessThan.HasValue) { if (TryDouble(val, out var dv) && dv < choice.NumericLessThan.Value) return true; }
            if (choice.NumericLessThanEquals.HasValue) { if (TryDouble(val, out var dv) && dv <= choice.NumericLessThanEquals.Value) return true; }
            if (!string.IsNullOrEmpty(choice.StringEquals)) { if (val != null && val.ToString() == choice.StringEquals) return true; }
            if (!string.IsNullOrEmpty(choice.StringGreaterThan)) { if (val != null && string.Compare(val.ToString(), choice.StringGreaterThan, StringComparison.Ordinal) > 0) return true; }
            if (!string.IsNullOrEmpty(choice.StringGreaterThanEquals)) { if (val != null && string.Compare(val.ToString(), choice.StringGreaterThanEquals, StringComparison.Ordinal) >= 0) return true; }
            if (!string.IsNullOrEmpty(choice.StringLessThan)) { if (val != null && string.Compare(val.ToString(), choice.StringLessThan, StringComparison.Ordinal) < 0) return true; }
            if (!string.IsNullOrEmpty(choice.StringLessThanEquals)) { if (val != null && string.Compare(val.ToString(), choice.StringLessThanEquals, StringComparison.Ordinal) <= 0) return true; }
            if (!string.IsNullOrEmpty(choice.TimestampEquals)) { if (TryTimestamp(val, out var tv) && tv == DateTime.Parse(choice.TimestampEquals, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)) return true; }
            if (!string.IsNullOrEmpty(choice.TimestampGreaterThan)) { if (TryTimestamp(val, out var tv) && tv > DateTime.Parse(choice.TimestampGreaterThan, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)) return true; }
            if (!string.IsNullOrEmpty(choice.TimestampGreaterThanEquals)) { if (TryTimestamp(val, out var tv) && tv >= DateTime.Parse(choice.TimestampGreaterThanEquals, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)) return true; }
            if (!string.IsNullOrEmpty(choice.TimestampLessThan)) { if (TryTimestamp(val, out var tv) && tv < DateTime.Parse(choice.TimestampLessThan, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)) return true; }
            if (!string.IsNullOrEmpty(choice.TimestampLessThanEquals)) { if (TryTimestamp(val, out var tv) && tv <= DateTime.Parse(choice.TimestampLessThanEquals, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)) return true; }
            if (choice.BooleanEquals.HasValue) { if (bool.TryParse(val?.ToString(), out var boolVal) && boolVal == choice.BooleanEquals.Value) return true; }
            if (choice.IsNull == true) { if (val == null) return true; }
            if (choice.IsNull == false) { if (val != null) return true; }
            if (choice.IsNumeric == true) { if (TryDouble(val, out _)) return true; }
            if (choice.IsString == true) { if (val is string) return true; }
            if (choice.IsBoolean == true) { if (val is bool) return true; }
            if (choice.IsTimestamp == true) { if (TryTimestamp(val, out _)) return true; }
            return false;
        }

        private static object? ApplyInputPath(object? data, string? path)
        {
            if (string.IsNullOrEmpty(path) || path == "$") return data;
            return GetValueByPath(data, path);
        }

        private static object? ApplyOutputPath(object? data, string? path)
        {
            if (string.IsNullOrEmpty(path) || path == "$") return data;
            return GetValueByPath(data, path);
        }

        private static object? PlaceValueByResultPath(object? original, object? result, string? resultPath)
        {
            if (string.IsNullOrEmpty(resultPath) || resultPath == "$") return result;
            var clone = DeepClone(original);
            return PlaceValueByPath(clone, resultPath, result);
        }

        private object? BuildParameters(object? data, object? parameters)
        {
            if (parameters == null) return data;
            return ResolveParameterValue(data, parameters);
        }

        private object? ResolveParameterValue(object? data, object? param)
        {
            if (param == null) return null;
            if (param is string s) return s;
            if (param is Dictionary<string, object?> dict)
            {
                var result = new Dictionary<string, object?>();
                foreach (var kvp in dict)
                {
                    if (kvp.Key.EndsWith(".$"))
                    {
                        var newKey = kvp.Key[..^2];
                        var path = kvp.Value?.ToString();
                        var val = GetValueByPath(data, path) ?? GetValueByPath(this.globalData, path);
                        result[newKey] = val;
                    }
                    else
                    {
                        result[kvp.Key] = ResolveParameterValue(data, kvp.Value);
                    }
                }
                return result;
            }
            if (param is IEnumerable arr && param is not string)
            {
                var list = new List<object?>();
                foreach (var item in arr)
                {
                    list.Add(ResolveParameterValue(data, item));
                }
                return list;
            }
            return param;
        }

        private object?[]? BuildParameterArray(object? data, object? parameters)
        {
            if (parameters == null) return null;
            var built = BuildParameters(data, parameters);
            if (built is Dictionary<string, object?> dict)
            {
                return dict.Values.Select(x => ResolveParameterValue(data, x)).ToArray();
            }
            if (built is IEnumerable arr && built is not string)
            {
                var list = new List<object?>();
                foreach (var item in arr) list.Add(item);
                return list.Select(x => ResolveParameterValue(data, x)).ToArray();
            }
            return new[] { built };
        }

        private static object? DeepClone(object? source)
        {
            if (source == null) return null;
            if (source is Dictionary<string, object?> dict)
            {
                var clone = new Dictionary<string, object?>();
                foreach (var kvp in dict) clone[kvp.Key] = DeepClone(kvp.Value);
                return clone;
            }
            if (source is IEnumerable list && !(source is string))
            {
                var clonedList = new List<object?>();
                foreach (var item in list) clonedList.Add(DeepClone(item));
                return clonedList;
            }
            return source;
        }

        private static object? PlaceValueByPath(object? root, string path, object? newValue)
        {
            if (string.IsNullOrEmpty(path) || path == "$") return newValue;
            if (!path.StartsWith("$.") || path.Length <= 2) return root;
            var segs = path.Substring(2).Split('.', StringSplitOptions.RemoveEmptyEntries);
            return PlaceRecursive(root, segs, 0, newValue);
        }

        private static object? PlaceRecursive(object? current, string[] segs, int index, object? newValue)
        {
            if (index >= segs.Length) return newValue;
            var part = segs[index];
            if (current is Dictionary<string, object?> dict)
            {
                if (!dict.TryGetValue(part, out var exist)) exist = null;
                dict[part] = PlaceRecursive(exist, segs, index + 1, newValue);
                return current;
            }
            if (current is List<object?> list && int.TryParse(part, out var idx))
            {
                if (idx < 0) return current;
                while (idx >= list.Count) list.Add(null);
                list[idx] = PlaceRecursive(list[idx], segs, index + 1, newValue);
                return current;
            }
            if (int.TryParse(part, out var i2))
            {
                var newList = new List<object?>();
                if (current != null) newList = DeepClone(current) as List<object?> ?? new List<object?>();
                while (i2 >= newList.Count) newList.Add(null);
                newList[i2] = PlaceRecursive(newList[i2], segs, index + 1, newValue);
                return newList;
            }
            var newDict = new Dictionary<string, object?>();
            newDict[part] = PlaceRecursive(null, segs, index + 1, newValue);
            return newDict;
        }

        private static object? GetValueByPath(object? data, string? path)
        {
            if (data == null) return null;
            if (string.IsNullOrEmpty(path) || path == "$") return data;
            if (!path.StartsWith("$.") || path.Length <= 2) return null;
            var parts = path.Substring(2).Split('.', StringSplitOptions.RemoveEmptyEntries);
            var current = data;
            foreach (var p in parts)
            {
                if (current is Dictionary<string, object?> dict)
                {
                    if (!dict.TryGetValue(p, out var nxt)) return null;
                    current = nxt;
                }
                else if (current is List<object?> lst && int.TryParse(p, out var idx))
                {
                    if (idx < 0 || idx >= lst.Count) return null;
                    current = lst[idx];
                }
                else return null;
            }
            return current;
        }

        private static bool TryDouble(object? val, out double result)
        {
            result = 0;
            if (val == null) return false;
            return double.TryParse(val.ToString(), out result);
        }

        private static bool TryTimestamp(object? val, out DateTime result)
        {
            result = DateTime.MinValue;
            if (val == null) return false;
            return DateTime.TryParse(val.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out result);
        }

        private static object? MergeObjects(object? left, object? right)
        {
            if (left == null) return right;
            if (right == null) return left;
            if (left is Dictionary<string, object?> lDict && right is Dictionary<string, object?> rDict)
            {
                var merged = new Dictionary<string, object?>(lDict);
                foreach (var kvp in rDict)
                {
                    merged[kvp.Key] = MergeObjects(merged.ContainsKey(kvp.Key) ? merged[kvp.Key] : null, kvp.Value);
                }
                return merged;
            }
            if (left is IEnumerable lEnum && !(left is string) && right is IEnumerable rEnum && !(right is string))
            {
                var lList = lEnum.Cast<object?>().ToList();
                var rList = rEnum.Cast<object?>().ToList();
                var mergedList = new List<object?>(lList);
                mergedList.AddRange(rList);
                return mergedList;
            }
            return right;
        }

        public sealed class AslDefinition
        {
            public string StartAt { get; set; } = "";
            public Dictionary<string, AslState> States { get; set; } = new Dictionary<string, AslState>();
        }

        public sealed class AslState
        {
            public string Type { get; set; } = "";
            public string? Next { get; set; }
            public bool? End { get; set; }
            public string? InputPath { get; set; }
            public string? OutputPath { get; set; }
            public string? ResultPath { get; set; }
            public object? Result { get; set; }
            public Dictionary<string, object?>? Parameters { get; set; }
            public string? Resource { get; set; }
            public List<AslRetry>? Retry { get; set; }
            public List<AslCatch>? Catch { get; set; }
            public List<AslChoice>? Choices { get; set; }
            public string? Default { get; set; }
            public int? Seconds { get; set; }
            public string? SecondsPath { get; set; }
            public string? Timestamp { get; set; }
            public string? TimestampPath { get; set; }
            public AslDefinition? Iterator { get; set; }
            public string? ItemsPath { get; set; }
            public int? MaxConcurrency { get; set; }
            public List<AslDefinition>? Branches { get; set; }
            public string? Error { get; set; }
            public string? Cause { get; set; }
        }

        public sealed class AslChoice
        {
            public string Variable { get; set; } = "";
            public double? NumericGreaterThan { get; set; }
            public double? NumericGreaterThanEquals { get; set; }
            public double? NumericLessThan { get; set; }
            public double? NumericLessThanEquals { get; set; }
            public string? StringEquals { get; set; }
            public string? StringGreaterThan { get; set; }
            public string? StringGreaterThanEquals { get; set; }
            public string? StringLessThan { get; set; }
            public string? StringLessThanEquals { get; set; }
            public string? TimestampEquals { get; set; }
            public string? TimestampGreaterThan { get; set; }
            public string? TimestampGreaterThanEquals { get; set; }
            public string? TimestampLessThan { get; set; }
            public string? TimestampLessThanEquals { get; set; }
            public bool? BooleanEquals { get; set; }
            public bool? IsNumeric { get; set; }
            public bool? IsString { get; set; }
            public bool? IsBoolean { get; set; }
            public bool? IsTimestamp { get; set; }
            public bool? IsNull { get; set; }
            public string? Next { get; set; }
        }

        public sealed class AslRetry
        {
            public string? ErrorEquals { get; set; }
            public int? IntervalSeconds { get; set; }
            public int? MaxAttempts { get; set; }
            public double? BackoffRate { get; set; }
        }

        public sealed class AslCatch
        {
            public List<string>? ErrorEquals { get; set; }
            public string? Next { get; set; }
            public string? ResultPath { get; set; }
        }
    }
}