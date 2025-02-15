using System.Security.Cryptography;
using System.Text;
using AngleSharp;
using Esprima;
using Esprima.Ast;
using Ionic.Zlib;
using Jint;

namespace LatokenHackaton.Api.News.CryptoPanic
{
    internal sealed class CryptoPanicNewsDecryptor
    {
        private static readonly string NewsString = "news";
        private static readonly string AesFunctionName = "dk";
        private static readonly string ScriptIndicator = "cryptopanic.min.";
        private static readonly string JsExtension = ".js";
        private static readonly string HttpPrefix = "http";
        private static readonly string StaticPrefix = "https://static.cryptopanic.com";

        private readonly CryptopanicWebClient webClient;
        private string? aesEncryptionKey;

        public CryptoPanicNewsDecryptor(CryptopanicWebClient webClient)
        {
            this.webClient = webClient;
        }

        public async Task<string?> DecryptTextAsync(string cipherBase64, string csrfToken)
        {
            if (string.IsNullOrEmpty(csrfToken))
            {
                throw new ArgumentException(null, nameof(csrfToken));
            }

            var combinedKey = NewsString + csrfToken;
            var initializationVector = combinedKey.Length > 16
                ? combinedKey.Substring(0, 16)
                : combinedKey.PadRight(16, '0');

            try
            {
                this.aesEncryptionKey ??= await this.ExtractAesKeyAsync();
                if (this.aesEncryptionKey == null)
                {
                    return null;
                }

                var keyBytes = Encoding.UTF8.GetBytes(this.aesEncryptionKey);
                var ivBytes = Encoding.UTF8.GetBytes(initializationVector);
                var cipherData = Convert.FromBase64String(cipherBase64);

                using var aes = Aes.Create();
                aes.Key = keyBytes;
                aes.IV = ivBytes;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.Zeros;

                using var decryptor = aes.CreateDecryptor();
                var decryptedBytes = decryptor.TransformFinalBlock(cipherData, 0, cipherData.Length);

                byte[] resultBytes = DecompressZlib(decryptedBytes);

                var textResult = Encoding.UTF8.GetString(resultBytes).TrimEnd('\0');
                return textResult;
            }
            catch (Exception ex)
            {
                throw new Exception("Error during decryption: {ex.Message}", ex);
            }
        }

        private static byte[] DecompressZlib(byte[] zlibData)
        {
            if (zlibData == null || zlibData.Length < 6)
            {
                throw new ArgumentException("Invalid zlib stream: too short.");
            }

            using var inputStream = new MemoryStream(zlibData);
            using var zlibStream = new ZlibStream(inputStream, CompressionMode.Decompress, true);
            using var outputStream = new MemoryStream();
            zlibStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }

        private async Task<string?> ExtractAesKeyAsync()
        {
            var mainHtml = await this.webClient.Get("news/all");
            if (string.IsNullOrEmpty(mainHtml))
            {
                return null;
            }

            var scriptUrl = this.FindScriptUrl(mainHtml);
            if (string.IsNullOrEmpty(scriptUrl))
            {
                return null;
            }

            var scriptContent = await this.webClient.Get(scriptUrl);
            if (string.IsNullOrEmpty(scriptContent))
            {
                return null;
            }

            var dkFunction = ExtractFunction(scriptContent);
            if (string.IsNullOrEmpty(dkFunction))
            {
                return null;
            }

            try
            {
                var engine = new Engine(cfg => cfg.Strict(false));
                var result = engine.Evaluate($"({dkFunction})()").AsString();
                return result;
            }
            catch
            {
                return null;
            }
        }

        private static string? ExtractFunction(string scriptContent)
        {
            var parser = new JavaScriptParser(new ParserOptions { Tolerant = true });
            Script parsedScript;
            try
            {
                parsedScript = parser.ParseScript(scriptContent);
            }
            catch
            {
                return null;
            }

            var functionDeclaration = FindFunctionDeclaration(parsedScript, AesFunctionName);
            if (functionDeclaration == null)
            {
                return null;
            }

            var startIndex = functionDeclaration.Range.Start;
            var endIndex = functionDeclaration.Range.Start + functionDeclaration.Range.Length;
            return scriptContent.Substring(startIndex, endIndex - startIndex);
        }

        private static FunctionDeclaration? FindFunctionDeclaration(Node node, string functionName)
        {
            if (node is FunctionDeclaration declaration && declaration.Id?.Name == functionName)
            {
                return declaration;
            }

            foreach (var child in node.ChildNodes)
            {
                var result = FindFunctionDeclaration(child, functionName);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        private string? FindScriptUrl(string html)
        {
            using var context = BrowsingContext.New(AngleSharp.Configuration.Default);
            var documentTask = context.OpenAsync(config => config.Content(html));
            documentTask.Wait();

            var document = documentTask.Result;
            if (document == null)
            {
                return null;
            }

            var scripts = document.QuerySelectorAll("script");
            foreach (var scriptElement in scripts)
            {
                var srcAttribute = scriptElement.GetAttribute("src");
                if (!string.IsNullOrEmpty(srcAttribute)
                    && srcAttribute.Contains(ScriptIndicator, StringComparison.OrdinalIgnoreCase)
                    && srcAttribute.EndsWith(JsExtension, StringComparison.OrdinalIgnoreCase))
                {
                    if (!srcAttribute.StartsWith(HttpPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        srcAttribute = StaticPrefix + srcAttribute;
                    }
                    return srcAttribute;
                }
            }
            return null;
        }
    }
}