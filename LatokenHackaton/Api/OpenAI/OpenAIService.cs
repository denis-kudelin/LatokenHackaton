using LatokenHackaton.Api.OpenAI;
using LatokenHackaton.Common;
using Nito.AsyncEx;
using OpenAI;
using OpenAI.Assistants;
using OpenAI.Batch;
using OpenAI.Chat;
using OpenAI.Files;
using OpenAI.VectorStores;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Text;

internal sealed class OpenAIService : IAsyncDisposable
{
    private readonly OpenAIClient openAiApiClient;
    private readonly HttpClient httpClient;
    private readonly AssistantClient assistantClient;
    private readonly OpenAIFileClient fileClient;
    private readonly VectorStoreClient vectorStoreClient;
    private readonly BatchClient batchClient;
    private long totalCompletionTokens;
    private long totalPromptTokens;

    public OpenAIService(string apiKey, string orgId = null, string projectId = null)
    {
        if (string.IsNullOrEmpty(apiKey))
            throw new ArgumentNullException(nameof(apiKey));

        this.httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        this.openAiApiClient = this.CreateOpenAIClient(orgId, projectId, apiKey, this.httpClient);
        this.assistantClient = this.openAiApiClient.GetAssistantClient();
        this.fileClient = this.openAiApiClient.GetOpenAIFileClient();
        this.vectorStoreClient = this.openAiApiClient.GetVectorStoreClient();
        this.batchClient = this.openAiApiClient.GetBatchClient();
    }

    ValueTask IAsyncDisposable.DisposeAsync()
    {
        this.httpClient.Dispose();
        return default;
    }

    private long IncrementPromptTokens(long count)
    {
        lock (this)
        {
            this.totalPromptTokens += count;
            return this.totalPromptTokens;
        }
    }

    private long IncrementCompletionTokens(long count)
    {
        lock (this)
        {
            this.totalCompletionTokens += count;
            return this.totalCompletionTokens;
        }
    }

    private sealed record PrimitiveValue<T>(T Value);
    public async Task<T> PerformChatCompletion<T>(string model, string prompt)
    {
        var isSimpleValue = typeof(T).IsPrimitive || typeof(T) == typeof(string);
        var type = isSimpleValue ? typeof(PrimitiveValue<T>) : typeof(T);
        await using var assistant = await this.CreateAssistantAsync(model, type);
        await using var thread = await assistant.CreateThreadAsync(prompt);
        var messages = await thread.RunAsync();
        var resultText = string.Join("", messages.Select(x => x.TextContent));
        var result = isSimpleValue ? JsonUtils.DeserializeDefault<PrimitiveValue<T>>(resultText).Value : JsonUtils.DeserializeDefault<T>(resultText);
        return result;
    }

    public async Task<string> PerformChatCompletion(string model, string prompt)
    {
        var chatClient = this.openAiApiClient.GetChatClient(model);
        var messages = new[] { new UserChatMessage(prompt) };
        var result = await chatClient.CompleteChatAsync(messages);
        var textContent = result.Value.Content.FirstOrDefault()?.Text ?? "";
        return textContent;
    }

    private ChatSession CreateChatSession(string model)
    {
        return new ChatSession(this, model);
    }

    private T RetryNoTokensUsedSync<T>(Func<T> operation)
    {
        return this.RetryNoTokensUsedAsync(() => Task.FromResult(operation())).GetAwaiter().GetResult();
    }

    private async Task<T> RetryNoTokensUsedAsync<T>(Func<Task<T>> operation)
    {
        var oldPromptTokenCount = this.totalPromptTokens;
        var oldCompletionTokenCount = this.totalCompletionTokens;
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException exception)
            {
                if (this.totalPromptTokens == oldPromptTokenCount && this.totalCompletionTokens == oldCompletionTokenCount)
                    await this.WaitBeforeRetry();
                else
                    throw new Exception("Network error after tokens were consumed", exception);
            }
            catch (TaskCanceledException exception)
            {
                if (this.totalPromptTokens == oldPromptTokenCount && this.totalCompletionTokens == oldCompletionTokenCount)
                    await this.WaitBeforeRetry();
                else
                    throw new Exception("Operation timed out after tokens were consumed", exception);
            }
        }
    }

    private Task WaitBeforeRetry()
    {
        return Task.Delay(500);
    }

    private async Task DeleteAllFilesAsync()
    {
        await this.RetryNoTokensUsedAsync(async () =>
        {
            var allFiles = (await this.fileClient.GetFilesAsync()).Value;
            await this.DeleteFilesAsync(allFiles.Select(x => x.Id));
            return 0;
        });
    }

    private async Task DeleteFilesAsync(IEnumerable<string> fileIds)
    {
        await Parallel.ForEachAsync(fileIds, async (fileId, cancellationToken) =>
        {
            await this.fileClient.DeleteFileAsync(fileId, cancellationToken);
        });
    }

    private async Task<IFileStorageService> CreateFileStorageAsync()
    {
        var service = new OpenAIAssistantService.OpenAIFileStorageService(this);
        return await Task.FromResult<IFileStorageService>(service);
    }

    private async Task DeleteFileStorageAsync(IFileStorageService fileStorage)
    {
        await this.vectorStoreClient.DeleteVectorStoreAsync(
            ((OpenAIAssistantService.OpenAIFileStorageService)fileStorage).VectorStore.Id
        );
    }

    private async Task<IAssistantService> CreateAssistantAsync(string model, Type schema = null, string instructions = "")
    {
        return await this.RetryNoTokensUsedAsync(async () =>
        {
            var assistantCreationOptions = this.CreateAssistantOptions(Guid.NewGuid().ToString(), schema, instructions, null);
            var createdAssistant = await this.assistantClient.CreateAssistantAsync(model, assistantCreationOptions);
            return (IAssistantService)new OpenAIAssistantService(this, createdAssistant);
        });
    }

    private async Task<IAssistantService> CreateAssistantAsync(string model, IFileStorageService fileStorage, string instructions = "")
    {
        return await this.RetryNoTokensUsedAsync(async () =>
        {
            var assistantCreationOptions = this.CreateAssistantOptions(Guid.NewGuid().ToString(), null, instructions, fileStorage);
            var createdAssistant = await this.assistantClient.CreateAssistantAsync(model, assistantCreationOptions);
            return (IAssistantService)new OpenAIAssistantService(this, createdAssistant);
        });
    }

    private AssistantCreationOptions CreateAssistantOptions(
        string name,
        Type schema,
        string instructions,
        IFileStorageService fileStorage
    )
    {
        var assistantCreationOptions = new AssistantCreationOptions
        {
            Name = name,
            Instructions = instructions ?? "",
            Temperature = 0,
            NucleusSamplingFactor = 0
        };

        if (fileStorage != null)
        {
            assistantCreationOptions.Tools.Add(ToolDefinition.CreateFileSearch());
        }

        if (schema != null)
        {
            assistantCreationOptions.ResponseFormat = AssistantResponseFormat.CreateJsonSchemaFormat(
                name,
                new BinaryData(
                    JsonUtils.SerializeDefault(JsonSchemaGenerator.GenerateSchema(schema))
                )
            );
        }

        if (fileStorage != null)
        {
            assistantCreationOptions.ToolResources = new()
            {
                FileSearch = new()
                {
                    VectorStoreIds =
                    {
                        ((OpenAIAssistantService.OpenAIFileStorageService)fileStorage).VectorStore.Id
                    }
                }
            };
        }

        return assistantCreationOptions;
    }

    private OpenAIClient CreateOpenAIClient(string org, string proj, string key, HttpClient http)
    {
        var clientOptions = new OpenAIClientOptions
        {
            OrganizationId = org,
            ProjectId = proj,
            Transport = new HttpClientPipelineTransport(http)
        };

        return new OpenAIClient(new ApiKeyCredential(key), clientOptions);
    }

    private sealed class OpenAIAssistantService : IAssistantService, IAsyncDisposable
    {
        private readonly OpenAIService parentService;
        private Assistant assistant;

        public OpenAIAssistantService(OpenAIService parentService, Assistant assistant)
        {
            this.parentService = parentService;
            this.assistant = assistant;
        }

        public async Task<IThreadService> CreateThreadAsync(params string[] initialMessages)
        {
            var creationOptions = new ThreadCreationOptions();
            foreach (var message in initialMessages)
            {
                creationOptions.InitialMessages.Add(
                    new ThreadInitializationMessage(
                        MessageRole.User,
                        new[] { MessageContent.FromText(message) }
                    )
                );
            }

            var threadResult = await this.parentService.assistantClient.CreateThreadAsync(creationOptions);
            return new OpenAIThreadService(this.parentService, this.assistant, threadResult.Value);
        }

        public async Task SetFileStorage(IFileStorageService fileStorage)
        {
            var modificationOptions = new AssistantModificationOptions
            {
                ToolResources = new()
                {
                    FileSearch = new()
                    {
                        VectorStoreIds =
                        {
                            ((OpenAIAssistantService.OpenAIFileStorageService)fileStorage).VectorStore.Id
                        }
                    }
                }
            };

            this.assistant = await this.parentService.assistantClient.ModifyAssistantAsync(
                this.assistant.Id,
                modificationOptions
            );
        }

        public async ValueTask DisposeAsync()
        {
            if (this.assistant == null)
                return;

            var check = await this.parentService.assistantClient.GetAssistantAsync(this.assistant.Id);
            if (check.Value != null)
                await this.parentService.assistantClient.DeleteAssistantAsync(this.assistant.Id);
        }

        private sealed class OpenAIThreadService : IThreadService, IAsyncDisposable
        {
            private readonly OpenAIService parentService;
            private readonly Assistant assistant;
            private readonly AssistantThread thread;

            public OpenAIThreadService(OpenAIService parentService, Assistant assistant, AssistantThread thread)
            {
                this.parentService = parentService;
                this.assistant = assistant;
                this.thread = thread;
            }

            public async Task<IThreadMessage> CreateMessageAsync(params string[] content)
            {
                var creationOptions = new MessageCreationOptions();
                var response = await this.parentService.assistantClient.CreateMessageAsync(
                    this.thread.Id,
                    MessageRole.User,
                    content.Select(MessageContent.FromText).ToList(),
                    creationOptions
                );

                return new OpenAIThreadMessage(this.parentService, response.Value);
            }

            public async Task<IThreadMessage[]> RunAsync()
            {
            begin:
                var runCreationOptions = new RunCreationOptions
                {
                    MaxInputTokenCount = int.MaxValue,
                    MaxOutputTokenCount = int.MaxValue,
                    Temperature = 0,
                    NucleusSamplingFactor = 0
                };

                var runResult = await this.parentService.assistantClient.CreateRunAsync(
                    this.thread.Id,
                    this.assistant.Id,
                    runCreationOptions
                );

                var resultMessages = new List<IThreadMessage>();
                while (true)
                {
                    var currentRunInfo = (await this.parentService.assistantClient.GetRunAsync(
                        this.thread.Id,
                        runResult.Value.Id
                    )).Value;

                    if (currentRunInfo.Status == RunStatus.Completed)
                    {
                        var promptTokens = currentRunInfo.Usage.InputTokenCount;
                        var completionTokens = currentRunInfo.Usage.OutputTokenCount;
                        this.parentService.IncrementPromptTokens(promptTokens);
                        this.parentService.IncrementCompletionTokens(completionTokens);

                        var runSteps = this.parentService.assistantClient.GetRunStepsAsync(
                            currentRunInfo.ThreadId,
                            currentRunInfo.Id
                        );

                        await foreach (var runStep in runSteps)
                        {
                            if (runStep.Kind == RunStepKind.CreatedMessage)
                            {
                                var threadMessage = await this.GetThreadMessageAsync(runStep.Details.CreatedMessageId);
                                resultMessages.Add(threadMessage);
                            }
                        }

                        if (resultMessages.Any())
                            return resultMessages.ToArray();
                    }
                    else if (currentRunInfo.Status == RunStatus.Incomplete)
                    {
                        throw new Exception($"Run incomplete: {currentRunInfo.IncompleteDetails?.Reason}");
                    }
                    else if (
                        currentRunInfo.Status == RunStatus.Failed
                        || currentRunInfo.Status == RunStatus.Cancelled
                        || currentRunInfo.Status == RunStatus.Expired
                    )
                    {
                        await Task.Delay(500);
                        goto begin;
                    }
                    else
                    {
                        await Task.Delay(500);
                    }
                }
            }

            private async Task<IThreadMessage> GetThreadMessageAsync(string messageId)
            {
                while (true)
                {
                    var messageResult = await this.parentService.assistantClient.GetMessageAsync(
                        this.thread.Id,
                        messageId
                    );

                    var threadMessage = messageResult.Value;
                    if (threadMessage.Content?.Count > 0)
                        return new OpenAIThreadMessage(this.parentService, threadMessage);

                    await Task.Delay(500);
                }
            }

            public async ValueTask DisposeAsync()
            {
                if (this.thread != null)
                    await this.parentService.assistantClient.DeleteThreadAsync(this.thread.Id);
            }

            private sealed class OpenAIThreadMessage : IThreadMessage
            {
                private readonly OpenAIService parentService;
                private readonly ThreadMessage message;

                public OpenAIThreadMessage(OpenAIService parentService, ThreadMessage message)
                {
                    this.parentService = parentService;
                    this.message = message;
                }

                public string TextContent
                {
                    get
                    {
                        var messageContents = this.message.Content
                            .Where(x => !string.IsNullOrEmpty(x.Text))
                            .Select(x => x.Text);

                        return JsonUtils.RemoveCodeBlocks(string.Join("", messageContents));
                    }
                }

                public async Task<bool> DeleteAsync()
                {
                    var result = await this.parentService.assistantClient.DeleteMessageAsync(
                        this.message.ThreadId,
                        this.message.Id
                    );
                    return result.Value.Deleted;
                }
            }
        }

        internal sealed class OpenAIFileStorageService : IFileStorageService
        {
            private readonly OpenAIService parentService;
            private readonly ConcurrentBag<OpenAIFile> files = new();
            private readonly AsyncLock initializationLock = new();
            private VectorStore vectorStore;
            private static readonly SemaphoreSlim uploadSemaphore = new(1, 1);

            public VectorStore VectorStore => this.vectorStore;

            public OpenAIFileStorageService(OpenAIService parentService)
            {
                this.parentService = parentService;
            }

            public async ValueTask DisposeAsync()
            {
                if (this.vectorStore != null)
                    await this.parentService.vectorStoreClient.DeleteVectorStoreAsync(this.vectorStore.Id);

                await this.parentService.DeleteFilesAsync(this.files.Select(x => x.Id));
            }

            public async Task UploadFile(Guid id, Stream file)
            {
                await this.EnsureVectorStoreAsync();
                var uploadedFileInfo = await this.UploadInternalAsync(id, file, CancellationToken.None);
                await this.AddFilesAsync(new[] { uploadedFileInfo });
            }

            public async Task UploadFile(Guid id, string file)
            {
                await this.EnsureVectorStoreAsync();
                using var memoryStream = new MemoryStream(Encoding.UTF8.GetBytes(file));
                var uploadedFileInfo = await this.UploadInternalAsync(id, memoryStream, CancellationToken.None);
                await this.AddFilesAsync(new[] { uploadedFileInfo });
            }

            public async Task UploadFiles(Dictionary<Guid, string> filesToUpload)
            {
                var fileStreams = filesToUpload.ToDictionary(
                    entry => entry.Key,
                    entry => (Stream)new MemoryStream(Encoding.UTF8.GetBytes(entry.Value))
                );

                await this.UploadFiles(fileStreams);

                foreach (var stream in fileStreams.Values)
                    stream.Dispose();
            }

            public async Task UploadFiles(Dictionary<Guid, Stream> fileStreams)
            {
                await this.EnsureVectorStoreAsync();
                var uploadedFiles = new ConcurrentBag<OpenAIFile>();

                await Parallel.ForEachAsync(fileStreams, async (fileEntry, cancellationToken) =>
                {
                    var uploadedFileInfo = await this.UploadInternalAsync(fileEntry.Key, fileEntry.Value, cancellationToken);
                    uploadedFiles.Add(uploadedFileInfo);
                });

                await this.AddFilesAsync(uploadedFiles);
            }

            private async Task EnsureVectorStoreAsync()
            {
                if (this.vectorStore != null)
                    return;

                using (this.initializationLock.Lock())
                {
                    if (this.vectorStore != null)
                        return;

                    var options = new VectorStoreCreationOptions
                    {
                        Name = $"{Guid.NewGuid()}VectorStore",
                        ExpirationPolicy = new VectorStoreExpirationPolicy(
                            VectorStoreExpirationAnchor.LastActiveAt,
                            1
                        )
                    };

                    var vectorStoreCreationResult = this.parentService.vectorStoreClient.CreateVectorStoreAsync(true, options);
                    vectorStoreCreationResult.Wait();
                    this.vectorStore = vectorStoreCreationResult.Result.Value;
                }
            }

            private async Task<OpenAIFile> UploadInternalAsync(Guid fileId, Stream data, CancellationToken cancellationToken)
            {
                Exception lastException = null;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        await uploadSemaphore.WaitAsync(cancellationToken);
                        var uploadResult = await this.parentService.fileClient.UploadFileAsync(
                            data,
                            $"{fileId}.txt",
                            FileUploadPurpose.Assistants,
                            cancellationToken
                        );
                        return uploadResult.Value;
                    }
                    catch (Exception exception)
                    {
                        lastException = exception;
                    }
                    finally
                    {
                        uploadSemaphore.Release();
                    }
                }
                throw lastException ?? new Exception("Failed to upload file after 10 attempts.");
            }

            private async Task AddFilesAsync(IEnumerable<OpenAIFile> newFiles)
            {
            repeat:
                var batchFileJob = await this.parentService.vectorStoreClient.CreateBatchFileJobAsync(
                    this.vectorStore.Id,
                    newFiles.Select(x => x.Id),
                    true
                );

                while (true)
                {
                    var batchFileJobStatus = await this.parentService.vectorStoreClient.GetBatchFileJobAsync(
                        batchFileJob.Value.VectorStoreId,
                        batchFileJob.Value.BatchId
                    );

                    if (batchFileJobStatus.Value.Status == VectorStoreBatchFileJobStatus.Completed)
                    {
                        foreach (var file in newFiles)
                            this.files.Add(file);

                        return;
                    }
                    else if (
                        batchFileJobStatus.Value.Status == VectorStoreBatchFileJobStatus.Failed
                        || batchFileJobStatus.Value.Status == VectorStoreBatchFileJobStatus.Cancelled
                    )
                    {
                        goto repeat;
                    }
                    await Task.Delay(500);
                }
            }
        }
    }

    private sealed class ChatSession
    {
        private readonly OpenAIService parent;
        private readonly string model;
        private readonly List<ChatMessage> messages = new();

        public ChatSession(OpenAIService openAIService, string model)
        {
            this.parent = openAIService;
            this.model = model;
        }

        public void AddUserMessage(string text)
        {
            this.messages.Add(new UserChatMessage(text));
        }

        public void AddSystemMessage(string text)
        {
            this.messages.Add(new SystemChatMessage(text));
        }

        public void AddAssistantMessage(string text)
        {
            this.messages.Add(new AssistantChatMessage(text));
        }

        public string SendAndGetReply()
        {
            return this.parent.RetryNoTokensUsedSync(() =>
            {
                var chatClient = this.parent.openAiApiClient.GetChatClient(this.model);
                var result = chatClient.CompleteChat(this.messages);
                var answer = result.Value.Content.FirstOrDefault()?.Text ?? "";
                return answer;
            });
        }
    }
}