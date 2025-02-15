using System.Reflection;
using System.Text.Json;
using LatokenHackaton.ASL;
using LatokenHackaton.Common;

namespace LatokenHackaton.Analysis
{
    internal abstract class BaseAnalysisService
    {
        private readonly AslStateMachineInterpreter aslInterpreter;
        private readonly CryptoAnalysisMethodsBase analysisMethodsProvider;
        private readonly OpenAIService openAiService;
        private readonly string metadataJson;

        protected BaseAnalysisService(CryptoAnalysisMethodsBase analysisMethodsProvider, OpenAIService llmService)
        {
            this.analysisMethodsProvider = analysisMethodsProvider ?? throw new ArgumentNullException(nameof(analysisMethodsProvider));
            this.openAiService = llmService ?? throw new ArgumentNullException(nameof(llmService));
            this.aslInterpreter = new(this.ExternalFunctionCall);

            var metadata = AslMetadataReflector.GenerateAslMetadata(this.analysisMethodsProvider.GetType());
            this.metadataJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions());
        }

        public abstract Task<string> ExecuteAnalysis(string userQuery);

        protected async Task<string> ExecuteAnalysis(string userQuery, string language)
        {
            var analysisData = await this.GatherAnalysisData(userQuery);
            var analysisPrompt = await this.ConstructAnalysisPrompt(userQuery, analysisData, language);
            var analysisPromptString = analysisPrompt.ToString();
            var result = await this.openAiService.PerformChatCompletion("o1-preview-2024-09-12", analysisPromptString);
            return result;
        }

        private async Task<string> GatherAnalysisData(string userQuery)
        {
            var userQueryPrompt = await this.ConstructUserQueryPrompt(userQuery);
            var finalPrompt = new AIPrompt($@"
We must produce BPMN as JSON that strictly follows the Amazon States Language (ASL) format.
Interpret the entire user query as directives for constructing an algorithm and determining its parameters.
Where any part of the query is ambiguous, apply only the minimal free interpretation necessary.
Process the query in the context provided after 'Task'.
Ensure that the final result is the output data containing all necessary information for further external processing.
Resource must always be set to the function name only, never use 'arn:aws:states:::lambda:invoke'.
Methods/Types/Enums: {this.metadataJson}
Current DateTime: {DateTime.UtcNow:yyyy-MM-dd HH:mm}
Task:{{0}}", userQueryPrompt);
            var finalPromptString = finalPrompt.ToString();

            try
            {
                var aslDefinition = await this.openAiService.PerformChatCompletion<AslStateMachineInterpreter.AslDefinition>(
                    "gpt-4o",
                    finalPromptString
                );

                var _ = await aslInterpreter.InterpretAsync(aslDefinition);
                var collectedData = this.analysisMethodsProvider.GetSerializedOutput();
                return collectedData;
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }
        }

        protected abstract Task<AIPrompt> ConstructAnalysisPrompt(string userQuery, string analysisData, string language);

        protected abstract Task<AIPrompt> ConstructUserQueryPrompt(AIPrompt userQuery);

        private async Task<object?> ExternalFunctionCall(string resourceName, object?[]? parameters)
        {
            if (string.IsNullOrWhiteSpace(resourceName))
                throw new ArgumentException("Invalid resource name.", nameof(resourceName));

            parameters ??= Array.Empty<object?>();
            var methodInfos = this.analysisMethodsProvider
                .GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var targetMethod = methodInfos
                .Where(m => m.Name.Equals(resourceName, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(m => m.GetParameters().Length == parameters.Length);

            if (targetMethod == null)
                throw new MissingMethodException($"Method '{resourceName}' not found or parameter count mismatch.");

            var paramInfos = targetMethod.GetParameters();
            var finalParams = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var paramType = paramInfos[i].ParameterType;
                finalParams[i] = await AslMetadataReflector.ConvertFromAslTypeValueAsync(paramType, parameters[i]);
            }

            var invocationResult = targetMethod.Invoke(this.analysisMethodsProvider, finalParams);

            var finalOutput = await AslMetadataReflector.ConvertFromAslTypeValueAsync(
                targetMethod.ReturnType,
                invocationResult
            );

            return finalOutput;
        }

        protected internal abstract class CryptoAnalysisMethodsBase : AslMethodsBase
        {
            private readonly List<OutputEntry> output = new();
            private readonly object outputLock = new();

            [AslDescription("Captures any intermediate or final output during the ASL workflow. All captured output is gathered for subsequent analysis or as part of the final result.")]
            public void RecordOutput(
                [AslDescription("A label or category that identifies the type or purpose of this output.")] string category,
                [AslDescription("Content to be recorded under the specified category.")] object content
            )
            {
                lock (outputLock)
                {
                    this.output.Add(new OutputEntry(category, content));
                }
            }

            [AslIgnore]
            public string GetSerializedOutput()
            {
                lock (this.outputLock)
                {
                    return ReadableSerializer.Serialize(this.output);
                }
            }

            private sealed class OutputEntry
            {
                public string Category { get; }
                public object Content { get; }

                public OutputEntry(string category, object content)
                {
                    Category = category;
                    Content = content;
                }
            }
        }
    }
}