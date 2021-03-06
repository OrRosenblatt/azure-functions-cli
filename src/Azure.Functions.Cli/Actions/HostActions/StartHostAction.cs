﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.ServiceModel;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Cors;
using System.Web.Http.SelfHost;
using Colors.Net;
using Fclp;
using Microsoft.Azure.WebJobs.Script.WebHost;
using Newtonsoft.Json.Linq;
using Azure.Functions.Cli.Common;
using Azure.Functions.Cli.Extensions;
using Azure.Functions.Cli.Helpers;
using static Azure.Functions.Cli.Common.OutputTheme;
using System.Collections.Generic;

namespace Azure.Functions.Cli.Actions.HostActions
{
    [Action(Name = "start", Context = Context.Host, HelpText = "Launches the functions runtime host")]
    class StartHostAction : BaseAction, IDisposable
    {
        private FileSystemWatcher fsWatcher;
        const int DefaultPort = 7071;
        const int DefaultNodeDebugPort = 5858;
        const TraceLevel DefaultDebugLevel = TraceLevel.Info;
        const int DefaultTimeout = 20;

        public int Port { get; set; }

        public int NodeDebugPort { get; set; }

        public TraceLevel ConsoleTraceLevel { get; set; }

        public string CorsOrigins { get; set; }

        public int Timeout { get; set; }

        public bool UseHttps { get; set; }

        public override ICommandLineParserResult ParseArgs(string[] args)
        {
            Parser
                .Setup<int>('p', "port")
                .WithDescription($"Local port to listen on. Default: {DefaultPort}")
                .SetDefault(DefaultPort)
                .Callback(p => Port = p);

            Parser
                .Setup<int>('n', "nodeDebugPort")
                .WithDescription($"Port for node debugger to use. Default: {DefaultNodeDebugPort}")
                .SetDefault(DefaultNodeDebugPort)
                .Callback(p => NodeDebugPort = p);

            Parser
                .Setup<TraceLevel>('d', "debugLevel")
                .WithDescription($"Console trace level (off, verbose, info, warning or error). Default: {DefaultDebugLevel}")
                .SetDefault(DefaultDebugLevel)
                .Callback(p => ConsoleTraceLevel = p);

            Parser
                .Setup<string>("cors")
                .WithDescription($"A comma separated list of CORS origins with no spaces. Example: https://functions.azure.com,https://functions-staging.azure.com")
                .SetDefault(LocalhostConstants.AzureFunctionsCors)
                .Callback(c => CorsOrigins = c);

            Parser
                .Setup<int>('t', "timeout")
                .WithDescription($"Timeout for on the functions host to start in seconds. Default: {DefaultTimeout} seconds.")
                .SetDefault(DefaultTimeout)
                .Callback(t => Timeout = t);

            Parser
                .Setup<bool>("useHttps")
                .WithDescription("Bind to https://localhost:{port} rather than http://localhost:{port}. By default it creates and trusts a certificate.")
                .SetDefault(false)
                .Callback(s => UseHttps = s);

            return Parser.Parse(args);
        }

        public override async Task RunAsync()
        {
            Utilities.PrintLogo();
            ReadSecrets();
            var baseAddress = Setup();

            var config = new HttpSelfHostConfiguration(baseAddress)
            {
                IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Always,
                TransferMode = TransferMode.Streamed
            };

            var cors = new EnableCorsAttribute(CorsOrigins, "*", "*");
            config.EnableCors(cors);
            config.Formatters.Clear();
            config.Formatters.Add(new JsonMediaTypeFormatter());

            var settings = SelfHostWebHostSettingsFactory.Create(ConsoleTraceLevel);

            Environment.SetEnvironmentVariable("EDGE_NODE_PARAMS", $"--debug={NodeDebugPort}", EnvironmentVariableTarget.Process);

            WebApiConfig.Initialize(config, settings: settings);
            using (var httpServer = new HttpSelfHostServer(config))
            {
                await httpServer.OpenAsync();
                ColoredConsole.WriteLine($"Listening on {baseAddress}");
                ColoredConsole.WriteLine("Hit CTRL-C to exit...");
                await PostHostStartActions(config);
                await Task.Delay(-1);
                await httpServer.CloseAsync();
            }
        }

        private static void DisableCoreLogging(HttpSelfHostConfiguration config)
        {
            WebScriptHostManager hostManager = config.DependencyResolver.GetService<WebScriptHostManager>();

            if (hostManager != null)
            {
                hostManager.Instance.ScriptConfig.HostConfig.Tracing.ConsoleLevel = TraceLevel.Off;
            }
        }

        private void DisplayHttpFunctionsInfo(HttpSelfHostConfiguration config)
        {

            WebScriptHostManager hostManager = config.DependencyResolver.GetService<WebScriptHostManager>();

            if (hostManager != null)
            {
                foreach (var function in hostManager.Instance.Functions)
                {
                    var httpRoute = function.Metadata.Bindings.FirstOrDefault(b => b.Type == "httpTrigger")?.Raw["route"]?.ToString();
                    httpRoute = httpRoute ?? function.Name;
                    var hostRoutePrefix = hostManager.Instance.ScriptConfig.HttpRoutePrefix ?? "api/";
                    hostRoutePrefix = string.IsNullOrEmpty(hostRoutePrefix) || hostRoutePrefix.EndsWith("/")
                        ? hostRoutePrefix
                        : $"{hostRoutePrefix}/";
                    ColoredConsole.WriteLine($"{TitleColor($"Http Function {function.Name}:")} {config.BaseAddress.ToString()}{hostRoutePrefix}{httpRoute}");
                }
            }
        }

        private async Task PostHostStartActions(HttpSelfHostConfiguration config)
        {
            try
            {
                var totalRetryTimes = Timeout * 2;
                var retries = 0;
                while (!await config.BaseAddress.IsServerRunningAsync())
                {
                    retries++;
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    if (retries % 10 == 0)
                    {
                        ColoredConsole.WriteLine(WarningColor("The host is taking longer than expected to start."));
                    }
                    else if (retries == totalRetryTimes)
                    {
                        throw new TimeoutException("Host was unable to start in specified time.");
                    }
                }
                DisableCoreLogging(config);
                DisplayHttpFunctionsInfo(config);
            }
            catch (Exception ex)
            {
                ColoredConsole.Error.WriteLine(ErrorColor($"Unable to retrieve functions list: {ex.Message}"));
            }
        }

        private void ReadSecrets()
        {
            try
            {
                var secretsManager = new SecretsManager();

                foreach (var pair in secretsManager.GetSecrets())
                {
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value, EnvironmentVariableTarget.Process);
                }
            }
            catch { }

            fsWatcher = new FileSystemWatcher(Environment.CurrentDirectory, SecretsManager.AppSettingsFileName);
            fsWatcher.Changed += (s, e) =>
            {
                Environment.Exit(ExitCodes.Success);
            };
            fsWatcher.EnableRaisingEvents = true;
        }

        private Uri Setup()
        {
            var protocol = UseHttps ? "https" : "http";
            var actions = new List<InternalAction>();
            if (!SecurityHelpers.IsUrlAclConfigured(protocol, Port))
            {
                actions.Add(InternalAction.SetupUrlAcl);
            }

            if (UseHttps && !SecurityHelpers.IsSSLConfigured(Port))
            {
                actions.Add(InternalAction.SetupSslCert);
            }

            if (actions.Any())
            {
                string errors;
                if (!ConsoleApp.RelaunchSelfElevated(new InternalUseAction { Port = Port, Actions = actions, Protocol = protocol}, out errors))
                {
                    ColoredConsole.WriteLine("Error: " + errors);
                    Environment.Exit(ExitCodes.GeneralError);
                }
            }
            return new Uri($"{protocol}://localhost:{Port}");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2202:Do not dispose objects multiple times")]
        public void Dispose()
        {
            Dispose(true);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                fsWatcher.Dispose();
            }
        }
    }
}
