using System;
namespace LatokenHackaton.Api.OpenAI
{
    internal interface IFileStorageService : IAsyncDisposable
    {
        Task UploadFile(Guid id, Stream file);
        Task UploadFile(Guid id, string file);
        Task UploadFiles(Dictionary<Guid, string> files);
        Task UploadFiles(Dictionary<Guid, Stream> files);
    }
}

