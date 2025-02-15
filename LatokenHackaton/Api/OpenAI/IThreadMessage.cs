using System;
namespace LatokenHackaton.Api.OpenAI
{
    internal interface IThreadMessage
    {
        Task<bool> DeleteAsync();
        public string TextContent { get; }
    }
}

