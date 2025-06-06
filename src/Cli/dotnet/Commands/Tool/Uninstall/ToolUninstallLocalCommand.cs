// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Microsoft.DotNet.Cli.Commands.Tool.Common;
using Microsoft.DotNet.Cli.ToolManifest;
using Microsoft.DotNet.Cli.ToolPackage;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.Cli.Utils.Extensions;
using Microsoft.Extensions.EnvironmentAbstractions;

namespace Microsoft.DotNet.Cli.Commands.Tool.Uninstall;

internal class ToolUninstallLocalCommand : CommandBase
{
    private readonly IToolManifestFinder _toolManifestFinder;
    private readonly IToolManifestEditor _toolManifestEditor;
    private readonly IReporter _reporter;

    private readonly PackageId _packageId;
    private readonly string _explicitManifestFile;

    public ToolUninstallLocalCommand(
        ParseResult parseResult,
        IToolManifestFinder toolManifestFinder = null,
        IToolManifestEditor toolManifestEditor = null,
        IReporter reporter = null)
        : base(parseResult)
    {
        _packageId = new PackageId(parseResult.GetValue(ToolUninstallCommandParser.PackageIdArgument));
        _explicitManifestFile = parseResult.GetValue(ToolUninstallCommandParser.ToolManifestOption);

        _reporter = reporter ?? Reporter.Output;

        _toolManifestFinder = toolManifestFinder ??
                              new ToolManifestFinder(new DirectoryPath(Directory.GetCurrentDirectory()));
        _toolManifestEditor = toolManifestEditor ?? new ToolManifestEditor();
    }

    public override int Execute()
    {
        (FilePath? manifestFileOptional, string warningMessage) =
            _toolManifestFinder.ExplicitManifestOrFindManifestContainPackageId(_explicitManifestFile, _packageId);

        if (!manifestFileOptional.HasValue)
        {
            throw new GracefulException([string.Format(CliCommandStrings.NoManifestFileContainPackageId, _packageId)], isUserError: false);
        }

        var manifestFile = manifestFileOptional.Value;

        _toolManifestEditor.Remove(manifestFile, _packageId);

        if (warningMessage != null)
        {
            _reporter.WriteLine(warningMessage.Yellow());
        }

        _reporter.WriteLine(
            string.Format(
                CliCommandStrings.UninstallLocalToolSucceeded,
                _packageId,
                manifestFile.Value).Green());
        return 0;
    }
}
