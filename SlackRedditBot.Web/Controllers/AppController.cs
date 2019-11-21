﻿namespace SlackRedditBot.Web.Controllers
{
    using System;
    using System.Collections.Generic;
    using System.IdentityModel.Tokens.Jwt;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.Options;
    using Microsoft.IdentityModel.Tokens;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using SlackRedditBot.Web.Models;

    public class AppController : Controller
    {
        private readonly AppSettings settings;
        private readonly AppDbContext db;
        private readonly HttpClient httpClient;

        public AppController(AppDbContext db, IOptions<AppSettings> options, HttpClient httpClient)
        {
            this.db = db;
            this.settings = options.Value;
            this.httpClient = httpClient;
        }

        [HttpGet("")]
        public IActionResult Home()
        {
            return this.View(this.settings);
        }

        [HttpGet("install")]
        public async Task<IActionResult> Install()
        {
            try
            {
                return this.Redirect("https://slack.com/oauth/authorize?" +
                                     $"client_id={this.settings.ClientId}" +
                                     $"&scope={this.settings.Scopes}" +
                                     $"&state={this.GetAuthState()}");
            }
            catch (Exception e)
            {
                return await this.GetErrorView(e);
            }
        }

        [HttpGet("authorize")]
        public async Task<IActionResult> Authorize(string code, string state, string error, CancellationToken cancellationToken)
        {
            try
            {
                this.ValidateAuthState(state);

                if (error == "access_denied")
                {
                    throw new Exception("Permissions not accepted.");
                }

                var instance = await this.GetAppInstance(code, cancellationToken);

                await this.SaveAppInstance(instance, cancellationToken);

                return this.Redirect($"https://slack.com/app_redirect?app={this.settings.AppId}");
            }
            catch (Exception e)
            {
                return await this.GetErrorView(e);
            }
        }

        private async Task<ViewResult> GetErrorView(Exception e)
        {
            await Console.Error.WriteLineAsync(e.ToString());

            return this.View("Error", e);
        }

        private string GetAuthState()
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(this.settings.ClientSecret));

            return tokenHandler.CreateEncodedJwt(new SecurityTokenDescriptor
            {
                Expires = DateTime.UtcNow + TimeSpan.FromMinutes(10),
                SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256Signature),
            });
        }

        private void ValidateAuthState(string state)
        {
            var tokenHandler = new JwtSecurityTokenHandler();

            if (!tokenHandler.CanReadToken(state))
            {
                throw new Exception("Invalid request.");
            }

            var validationParams = new TokenValidationParameters
            {
                ValidateAudience = false,
                ValidateIssuer = false,
                ClockSkew = TimeSpan.Zero,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(this.settings.ClientSecret)),
            };

            try
            {
                tokenHandler.ValidateToken(state, validationParams, out _);
            }
            catch (SecurityTokenInvalidSignatureException)
            {
                throw new Exception("Invalid signature.");
            }
            catch (SecurityTokenExpiredException)
            {
                throw new Exception("Expired signature.");
            }
        }

        private async Task<Instance> GetAppInstance(string code, CancellationToken cancellationToken)
        {
            var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{this.settings.ClientId}:{this.settings.ClientSecret}"));
            var formValues = new Dictionary<string, string> { { "code", code } };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/oauth.access")
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", basicAuth) },
                Content = new FormUrlEncodedContent(formValues),
            };
            using var response = await this.httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync();
            var responseObj = (JObject)JsonConvert.DeserializeObject(responseBody);

            if (!(bool)responseObj["ok"])
            {
                throw new Exception($"Error retrieving access token from slack: {responseBody}");
            }

            return new Instance
            {
                TeamId = (string)responseObj["team_id"],
                AccessToken = (string)responseObj["access_token"],
            };
        }

        private async Task SaveAppInstance(Instance instance, CancellationToken cancellationToken)
        {
            var dbInstance = await this.db.Instances.SingleOrDefaultAsync(i => i.TeamId == instance.TeamId, cancellationToken);

            if (dbInstance == null)
            {
                this.db.Instances.Add(instance);
            }
            else
            {
                dbInstance.AccessToken = instance.AccessToken;
            }

            await this.db.SaveChangesAsync(cancellationToken);
        }
    }
}
