namespace LatokenHackaton.Api.OpenAI
{
    internal interface IAssistantService : IAsyncDisposable
    {
        Task<IThreadService> CreateThreadAsync(params string[] initialMessages);
        Task SetFileStorage(IFileStorageService fileStorage);
    }
}

