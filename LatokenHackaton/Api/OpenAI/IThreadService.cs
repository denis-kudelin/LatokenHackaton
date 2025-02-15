namespace LatokenHackaton.Api.OpenAI
{
    internal interface IThreadService : IAsyncDisposable
    {
        Task<IThreadMessage> CreateMessageAsync(params string[] content);
        Task<IThreadMessage[]> RunAsync();
    }
}

