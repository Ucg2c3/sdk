// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.CommandLine;
using System.IO.Pipes;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Tools.Build;
using Microsoft.DotNet.Tools.Test;
using Microsoft.TemplateEngine.Cli.Commands;

namespace Microsoft.DotNet.Cli
{
    internal partial class TestingPlatformCommand : CliCommand, ICustomHelp
    {
        private readonly List<NamedPipeServer> _namedPipeServers = new();
        private readonly List<Task> _taskModuleName = [];
        private readonly ConcurrentBag<Task> _testsRun = [];
        private readonly ConcurrentDictionary<string, (CommandLineOptionMessage, string[])> _commandLineOptionNameToModuleNames = [];
        private readonly ConcurrentDictionary<string, TestApplication> _testApplications = [];
        private readonly PipeNameDescription _pipeNameDescription = NamedPipeServer.GetPipeName(Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _cancellationToken = new();

        private Task _namedPipeConnectionLoop;
        private string[] _args;

        private const string MSBuildExeName = "MSBuild.dll";

        public TestingPlatformCommand(string name, string description = null) : base(name, description)
        {
            TreatUnmatchedTokensAsErrors = false;
        }

        public int Run(ParseResult parseResult)
        {
            _args = parseResult.GetArguments();

            VSTestTrace.SafeWriteTrace(() => $"Wait for connection(s) on pipe = {_pipeNameDescription.Name}");
            _namedPipeConnectionLoop = Task.Run(async () => await WaitConnectionAsync(_cancellationToken.Token));

            bool containsNoBuild = parseResult.UnmatchedTokens.Any(x => x == "--no-build");

            if (containsNoBuild)
            {
                ForwardingAppImplementation mSBuildForwardingApp = new(GetMSBuildExePath(), ["-t:_GetTestsProject", $"-p:GetTestsProjectPipeName={_pipeNameDescription.Name}", "-verbosity:q"]);
                int getTestsProjectResult = mSBuildForwardingApp.Execute();
            }
            else
            {
                BuildCommand buildCommand = BuildCommand.FromArgs(["-t:_BuildTestsProject;_GetTestsProject", "-bl", $"-p:GetTestsProjectPipeName={_pipeNameDescription.Name}", "-verbosity:q"]);
                int buildResult = buildCommand.Execute();
            }

            // Above line will block till we have all connections and all GetTestsProject msbuild task complete.
            Task.WaitAll([.. _taskModuleName]);
            Task.WaitAll([.. _testsRun]);
            _cancellationToken.Cancel();
            _namedPipeConnectionLoop.Wait();

            return 0;
        }

        private async Task WaitConnectionAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    NamedPipeServer namedPipeServer = new(_pipeNameDescription, OnRequest, NamedPipeServerStream.MaxAllowedServerInstances, token);
                    namedPipeServer.RegisterAllSerializers();

                    await namedPipeServer.WaitConnectionAsync(token);

                    _namedPipeServers.Add(namedPipeServer);
                }
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == token)
            {
                // We are exiting
            }
            catch (Exception ex)
            {
                VSTestTrace.SafeWriteTrace(() => ex.ToString());
                throw;
            }
        }

        private Task<IResponse> OnRequest(IRequest request)
        {
            if (request is Module module)
            {
                var testApplication = new TestApplication(module.Name, _pipeNameDescription.Name, _args);
                _testApplications[Path.GetFileName(module.Name)] = testApplication;

                if (_args.Contains("--help") || _args.Contains("-h"))
                {
                    testApplication.HelpRequested += OnHelpRequested;
                }
                testApplication.ErrorReceived += OnErrorReceived;

                _testsRun.Add(Task.Run(async () => await testApplication.RunAsync()));
            }
            else if (request is CommandLineOptionMessages commandLineOptionMessages)
            {
                var testApplication = _testApplications[commandLineOptionMessages.ModuleName];
                testApplication?.OnCommandLineOptionMessages(commandLineOptionMessages);
            }
            else
            {
                throw new NotSupportedException($"Request '{request.GetType()}' is unsupported.");
            }

            return Task.FromResult((IResponse)VoidResponse.CachedInstance);
        }

        private void OnErrorReceived(object sender, ErrorEventArgs args)
        {
            VSTestTrace.SafeWriteTrace(() => args.ErrorMessage);
        }

        private static string GetMSBuildExePath()
        {
            return Path.Combine(
                AppContext.BaseDirectory,
                MSBuildExeName);
        }
    }
}