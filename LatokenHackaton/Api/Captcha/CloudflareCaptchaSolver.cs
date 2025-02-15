using System;
using TwoCaptcha.Captcha;

namespace LatokenHackaton.Api.Captcha
{
    public sealed class CloudflareCaptchaSolver
    {
        private readonly TwoCaptcha.TwoCaptcha captchaSolver;

        public CloudflareCaptchaSolver(string apiKey)
        {
            this.captchaSolver = new TwoCaptcha.TwoCaptcha(apiKey);
        }

        public async Task<string?> SolveTurnstile(string url, string siteKey)
        {
            if (string.IsNullOrEmpty(siteKey)) return null;

            var cap = new Turnstile();
            cap.SetUrl(url);
            cap.SetSiteKey(siteKey);
            try
            {
                await this.captchaSolver.Solve(cap);
                return cap.Code;
            }
            catch
            {
                return null;
            }
        }
    }
}

