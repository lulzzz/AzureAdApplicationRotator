using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.KeyVault;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.Graph.RBAC.Fluent;
using Microsoft.Azure.Management.Graph.RBAC.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using System;
using System.Net;
using System.Threading.Tasks;

namespace SpRotator
{
    public static class ApplicationKeysRotator
    {
        private static TraceWriter _log;

        [FunctionName("AllApplicationIds")]
        public static async Task<IActionResult> RunAllApplicationIds([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req, TraceWriter log)
        {
            _log = log;

            string keyVaultUrl = GetKeyVaultUrl();
            var keyVaultClient = GetKeyVaultClient();

            var worker = new RotatorWorker(_log, keyVaultClient, keyVaultUrl);
            await worker.RotateAll();

            return new OkResult();
        }

        [FunctionName("ByApplicationObjectId")]
        public static async Task<IActionResult> RunByApplicationObjectId([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req, TraceWriter log)
        {
            _log = log;
            string id = req.Query["id"];

            if (string.IsNullOrWhiteSpace(id))
            {
                const string message = "No id parameter found in querystring";
                _log.Verbose(message);
                return new BadRequestObjectResult(message);
            }
            else
            {
                _log.Info($"Found id '{id}' in querystring to rotate");
            }

            var azure = GetAzureConnection();
            var application = await GetApplication(azure, id);

            if (application == null)
            {
                return new NotFoundResult();
            }

            string keyVaultUrl = GetKeyVaultUrl();
            var keyVaultClient = GetKeyVaultClient();

            const string keyName = "RotatedKey";
            var worker = new RotatorWorker(_log, keyVaultClient, keyVaultUrl);
            await worker.Rotate(application, keyName);

            return new OkResult();
        }

        private static async Task<IActiveDirectoryApplication> GetApplication(IAzure azure, string id)
        {
            _log.Verbose($"Searching for active directory application resource id '{id}'");
            
            try
            {
                ////string name = "test";
                ////var application = azure.AccessManagement.ActiveDirectoryApplications
                ////.Define(name)
                ////    .WithSignOnUrl("https://github.com/Azure/azure-sdk-for-java/" + name)
                ////    // password credentials definition
                ////    .DefinePasswordCredential("password")
                ////        .WithPasswordValue("P@ssw0rd")
                ////        .WithDuration(TimeSpan.FromDays(700))
                ////        .Attach()
                ////    .Create();

                var application = await azure.AccessManagement.ActiveDirectoryApplications.GetByIdAsync(id);

                if (application == null)
                {
                    _log.Info($"No active directory application found by id '{id}'");
                }
                else
                {
                    _log.Info($"Found active directory application '{application.Name}' by id '{application.Id}' and applicationId '{application.ApplicationId}'");
                }

                return application;
            }
            catch (GraphErrorException ex)
            {
                if (ex.Response.StatusCode == HttpStatusCode.Forbidden)
                {
                    _log.Error($"Forbidden to get active directory application with id '{id}'");
                }
                else if (ex.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    _log.Error($"Can't find active directory application with id '{id}'");
                }
                else
                {
                    _log.Error(ex.Response.Content);
                }

                return null;
            }
        }

        private static IAzure GetAzureConnection()
        {
            //Use own service principal for local login
            var p = new Principal();
            p.UserPrincipalName = "LocalLogin";
            p.AppId = Environment.GetEnvironmentVariable("ClientId", EnvironmentVariableTarget.Process);
            p.TenantId = Environment.GetEnvironmentVariable("TenantId", EnvironmentVariableTarget.Process);
            string clientSecret = Environment.GetEnvironmentVariable("ClientSecret", EnvironmentVariableTarget.Process);

            _log.Verbose($"Get the local sp credentials");
            AzureCredentials credentials = SdkContext
                .AzureCredentialsFactory
                .FromServicePrincipal(p.AppId, clientSecret, p.TenantId, AzureEnvironment.AzureGlobalCloud);

            ////_log.Verbose($"Get the MSI credentials");
            ////AzureCredentials credentials = SdkContext
            ////     .AzureCredentialsFactory
            ////     .FromMSI(new MSILoginInformation(MSIResourceType.AppService), AzureEnvironment.AzureGlobalCloud);

            _log.Verbose($"Construct the Azure object");
            var azure = Azure
                .Configure()
                .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                .Authenticate(credentials)
                .WithDefaultSubscription();

            return azure;
        }

        private static string GetKeyVaultUrl()
        {
            const string KeyVaultEnvironmentVariableName = "KeyVaultUrl";
            string keyVaultUrl = Environment.GetEnvironmentVariable(KeyVaultEnvironmentVariableName, EnvironmentVariableTarget.Process);

            if (string.IsNullOrWhiteSpace(keyVaultUrl))
            {
                throw new ApplicationException($"Missing environment variable '{KeyVaultEnvironmentVariableName}' with as value the url of the KeyVault");
            }

            return keyVaultUrl;
        }

        private static KeyVaultClient GetKeyVaultClient()
        {
            var azureServiceTokenProvider = new AzureServiceTokenProvider();
            var keyVaultClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(azureServiceTokenProvider.KeyVaultTokenCallback));

            return keyVaultClient;
        }
    }
}
