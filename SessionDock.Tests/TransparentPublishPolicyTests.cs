using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace SessionDock.Tests;

public sealed class TransparentPublishPolicyTests
{
    [Fact]
    public void ApplicationProject_PublishesInspectableSelfContainedFiles()
    {
        var project = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "SessionDock",
            "SessionDock.csproj"));

        AssertProperty(project, "PublishSingleFile", "false");
        AssertProperty(project, "SelfContained", "true");
        AssertProperty(project, "RuntimeFrameworkVersion", "10.0.10");
        AssertProperty(project, "IncludeNativeLibrariesForSelfExtract", "false");
        AssertProperty(project, "EnableCompressionInSingleFile", "false");
        AssertProperty(project, "PublishTrimmed", "false");
        AssertProperty(project, "PublishReadyToRun", "false");
    }

    [Fact]
    public void BuildAndVerifier_EnforceTransparentPinnedInventory()
    {
        var root = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(root, "scripts", "Build.ps1"));
        var verifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-Publish.ps1"));
        var securityPatchVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Test-DotNetSecurityPatch.ps1"));

        Assert.Contains("-p:PublishSingleFile=false", build, StringComparison.Ordinal);
        Assert.Contains(
            "-p:IncludeNativeLibrariesForSelfExtract=false",
            build,
            StringComparison.Ordinal);
        Assert.Contains(
            "-p:EnableCompressionInSingleFile=false",
            build,
            StringComparison.Ordinal);
        Assert.Contains("SessionDock.ExactWheel.dll", build, StringComparison.Ordinal);
        Assert.Contains("SessionDock.HandleScope.dll", build, StringComparison.Ordinal);

        Assert.Contains("SessionDock.deps.json", verifier, StringComparison.Ordinal);
        Assert.Contains("SessionDock.runtimeconfig.json", verifier, StringComparison.Ordinal);
        Assert.Contains("SessionDock.ExactWheel.dll", verifier, StringComparison.Ordinal);
        Assert.Contains("SessionDock.HandleScope.dll", verifier, StringComparison.Ordinal);
        Assert.Contains("SessionDock.ReleaseTrust.dll", verifier, StringComparison.Ordinal);
        Assert.Contains("unexpected executable or script payload", verifier,
            StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", verifier, StringComparison.Ordinal);
        Assert.Contains("O=Microsoft Corporation", verifier, StringComparison.Ordinal);
        Assert.Contains(
            "$hash.AppendData([IO.File]::ReadAllBytes($AspNetCoreNotice))",
            verifier,
            StringComparison.Ordinal);
        Assert.Contains("Publish output must not contain symbolic links", verifier,
            StringComparison.Ordinal);
        Assert.Contains("Production SessionDock.dll contains the test-only", verifier,
            StringComparison.Ordinal);
        Assert.Contains("PublishTrimmed' 'PublishTrimmed value'", securityPatchVerifier,
            StringComparison.Ordinal);
        Assert.Contains("PublishReadyToRun' 'PublishReadyToRun value'", securityPatchVerifier,
            StringComparison.Ordinal);
        Assert.Contains("must not contain Microsoft.NET.ILLink.Tasks", securityPatchVerifier,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflow_PublishesOnlyTheTransparentUnsignedPortableArchive()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(
            root,
            ".github",
            "workflows",
            "release.yml"));
        var unsignedVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-UnsignedRelease.ps1"));
        var publishVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-Publish.ps1"));
        var finalizer = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Finalize-SessionDockReleaseAssets.ps1"));
        var assetVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-Assets.ps1"));
        var defenderScanner = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Invoke-DefenderReleaseScan.ps1"));
        var repositoryVerifier = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Verify-Repository.ps1"));

        string[] exactUnsignedPeNames =
        [
            "SessionDock.exe",
            "SessionDock.dll",
            "SessionDock.ExactWheel.dll",
            "SessionDock.HandleScope.dll",
            "SessionDock.ReleaseTrust.dll",
            "Velopack.dll"
        ];
        foreach (var name in exactUnsignedPeNames)
        {
            Assert.Contains($"'{name}'", unsignedVerifier, StringComparison.Ordinal);
            Assert.Contains($"'{name}'", publishVerifier, StringComparison.Ordinal);
        }

        Assert.Contains("SignatureStatus]::NotSigned", unsignedVerifier,
            StringComparison.Ordinal);
        Assert.Contains("lacks a valid Microsoft signature",
            unsignedVerifier, StringComparison.Ordinal);
        Assert.Contains("O=Microsoft Corporation", unsignedVerifier,
            StringComparison.Ordinal);
        Assert.Contains("$_ -cnotin $unsignedSigningTargets", publishVerifier,
            StringComparison.Ordinal);
        Assert.Contains("O=Microsoft Corporation", publishVerifier,
            StringComparison.Ordinal);

        Assert.Contains("Verify-UnsignedRelease.ps1", workflow,
            StringComparison.Ordinal);
        Assert.Contains("Finalize-SessionDockReleaseAssets.ps1", workflow,
            StringComparison.Ordinal);
        Assert.Contains("-ApplicationDirectory ./release-input/app", workflow,
            StringComparison.Ordinal);
        Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(
                    workflow,
                    @"(?m)^\s*--noDefaultExclude true\s+`\s*$")
                .Cast<System.Text.RegularExpressions.Match>());
        Assert.Contains("--noInst true", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--noPortable true", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/attest@", workflow, StringComparison.Ordinal);
        Assert.Contains("Invoke-DefenderReleaseScan.ps1", workflow,
            StringComparison.Ordinal);
        Assert.Contains("-DisableRemediation", defenderScanner,
            StringComparison.Ordinal);
        Assert.Contains("Get-MpComputerStatus", defenderScanner,
            StringComparison.Ordinal);
        Assert.Contains("$LASTEXITCODE -ne 0", defenderScanner,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SessionDock-win-x64-Setup.exe", workflow,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authenticode", workflow,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("artifact-signing-action", workflow,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("azure/login", workflow,
            StringComparison.OrdinalIgnoreCase);
        string[] forbiddenExecutableSigningRoutes =
        [
            "--signTemplate",
            "--signExclude",
            "--signParallel",
            "--signParams",
            "--azureTrustedSignFile",
            "VPK_SIGN_TEMPLATE",
            "VPK_SIGN_EXCLUDE",
            "VPK_SIGN_PARALLEL",
            "VPK_SIGN_PARAMS",
            "VPK_AZURE_TRUSTED_SIGN_FILE",
            "signtool"
        ];
        foreach (var route in forbiddenExecutableSigningRoutes)
        {
            Assert.DoesNotContain(route, workflow, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(route, repositoryVerifier, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains(
            "$velopackBuildStep -match '(?m)(?:^|\\s)-n(?:\\s|$)'",
            repositoryVerifier,
            StringComparison.Ordinal);
        Assert.Contains("--dir ./final-asset-verification", workflow,
            StringComparison.Ordinal);
        Assert.Contains("@($approvedAssets.Name)", workflow,
            StringComparison.Ordinal);
        Assert.Contains("@($finalAssets.Name)", workflow,
            StringComparison.Ordinal);
        Assert.Contains("Final pre-publication asset hash mismatch", workflow,
            StringComparison.Ordinal);
        Assert.Contains("Approved release descriptor cryptographic verification failed",
            workflow, StringComparison.Ordinal);
        Assert.Contains("Final release descriptor cryptographic verification failed",
            workflow, StringComparison.Ordinal);
        Assert.Contains("--manifest $finalDescriptors[0].FullName", workflow,
            StringComparison.Ordinal);
        Assert.Contains("--package $finalPackages[0].FullName", workflow,
            StringComparison.Ordinal);

        Assert.Contains("Add-Type -AssemblyName System.IO.Compression", finalizer,
            StringComparison.Ordinal);
        Assert.Contains("ZipArchiveMode]::Create", finalizer,
            StringComparison.Ordinal);
        Assert.DoesNotContain("generatedSetup", finalizer,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remove-Item -LiteralPath $generatedPortablePath", finalizer,
            StringComparison.Ordinal);
        Assert.Contains("$publicAssets = @($portableAsset, $fullAssets[0])",
            finalizer, StringComparison.Ordinal);
        Assert.Contains("$expectedPortableEntries = $sourceComparableApplicationFiles",
            assetVerifier, StringComparison.Ordinal);
        Assert.Contains("Transparent portable ZIP contains a prohibited Velopack wrapper",
            assetVerifier, StringComparison.Ordinal);
        Assert.Contains(
            "6849325F8FB57FF5D13497C984B9DE82E6B5D46DDFBC857145012D104886287F",
            assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("Get-PortableExecutableIdentity", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("pinned Velopack 1.2.0 vendor code", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("function Invoke-ReleaseDescriptorVerification(", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("& $SignerPath verify", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("--manifest $DescriptorPath", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("--package $PackagePath", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("--public-key $KeyPath", assetVerifier,
            StringComparison.Ordinal);
        Assert.Contains("Release descriptor cryptographic verification failed", assetVerifier,
            StringComparison.Ordinal);
        var packagePathIndex = assetVerifier.IndexOf(
            "$packagePath = Join-Path", StringComparison.Ordinal);
        var descriptorVerificationIndex = assetVerifier.IndexOf(
            "Invoke-ReleaseDescriptorVerification `", StringComparison.Ordinal);
        Assert.True(packagePathIndex >= 0);
        Assert.True(descriptorVerificationIndex > packagePathIndex);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(19, false)]
    public void ReleaseAssetDescriptorGate_InvokesVerifierAndFailsClosed(
        int signerExitCode,
        bool shouldSucceed)
    {
        var root = FindRepositoryRoot();
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"sessiondock-descriptor-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var fakeSignerPath = Path.Combine(temporaryDirectory, "fake-signer.cmd");
            var argumentsPath = Path.Combine(temporaryDirectory, "arguments.txt");
            var runnerPath = Path.Combine(temporaryDirectory, "run-gate.ps1");
            var manifestPath = Path.Combine(temporaryDirectory, "sessiondock-release.json");
            var packagePath = Path.Combine(temporaryDirectory, "release.nupkg");
            var publicKeyPath = Path.Combine(temporaryDirectory, "public-key.pem");
            File.WriteAllText(fakeSignerPath,
                "@echo off\r\n" +
                "> \"%FAKE_SIGNER_ARGUMENTS%\" echo %*\r\n" +
                "exit /b %FAKE_SIGNER_EXIT_CODE%\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(manifestPath, "descriptor");
            File.WriteAllText(packagePath, "package");
            File.WriteAllText(publicKeyPath, "key");
            File.WriteAllText(runnerPath,
                "param($Source, $Signer, $Manifest, $Package, $PublicKey)\r\n" +
                "$tokens = $null\r\n" +
                "$errors = $null\r\n" +
                "$ast = [System.Management.Automation.Language.Parser]::ParseFile(" +
                "$Source, [ref] $tokens, [ref] $errors)\r\n" +
                "if ($errors.Count -ne 0) { throw 'Source parse failed.' }\r\n" +
                "$functions = @($ast.FindAll({ param($node) " +
                "$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and " +
                "$node.Name -ceq 'Invoke-ReleaseDescriptorVerification' }, $true))\r\n" +
                "if ($functions.Count -ne 1) { throw 'Descriptor gate function is not exact.' }\r\n" +
                "Invoke-Expression $functions[0].Extent.Text\r\n" +
                "Invoke-ReleaseDescriptorVerification -SignerPath $Signer " +
                "-DescriptorPath $Manifest -PackagePath $Package -KeyPath $PublicKey\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = temporaryDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(runnerPath);
            startInfo.ArgumentList.Add("-Source");
            startInfo.ArgumentList.Add(Path.Combine(root, "scripts", "Verify-Assets.ps1"));
            startInfo.ArgumentList.Add("-Signer");
            startInfo.ArgumentList.Add(fakeSignerPath);
            startInfo.ArgumentList.Add("-Manifest");
            startInfo.ArgumentList.Add(manifestPath);
            startInfo.ArgumentList.Add("-Package");
            startInfo.ArgumentList.Add(packagePath);
            startInfo.ArgumentList.Add("-PublicKey");
            startInfo.ArgumentList.Add(publicKeyPath);
            startInfo.Environment["FAKE_SIGNER_ARGUMENTS"] = argumentsPath;
            startInfo.Environment["FAKE_SIGNER_EXIT_CODE"] = signerExitCode.ToString();

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(shouldSucceed, process.ExitCode == 0);
            var signerArguments = File.ReadAllText(argumentsPath).Trim();
            Assert.StartsWith("verify ", signerArguments, StringComparison.Ordinal);
            Assert.Contains("--manifest", signerArguments, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(manifestPath), signerArguments,
                StringComparison.Ordinal);
            Assert.Contains("--package", signerArguments, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(packagePath), signerArguments,
                StringComparison.Ordinal);
            Assert.Contains("--public-key", signerArguments, StringComparison.Ordinal);
            Assert.Contains(Path.GetFileName(publicKeyPath), signerArguments,
                StringComparison.Ordinal);
            if (!shouldSucceed)
            {
                Assert.Contains("Release descriptor cryptographic verification failed",
                    standardOutput + standardError, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void GitHubSecurityAudit_FailsClosedOnReleaseProtectionGaps()
    {
        var root = FindRepositoryRoot();
        var securityAudit = File.ReadAllText(Path.Combine(
            root,
            "scripts",
            "Configure-GitHubSecurity.ps1"));
        var start = securityAudit.IndexOf("$rulesets =", StringComparison.Ordinal);
        var end = securityAudit.IndexOf(
            "$announcementEnvironmentName =",
            StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var protectedReleaseAudit = securityAudit[start..end];
        Assert.DoesNotContain("Write-Warning", protectedReleaseAudit,
            StringComparison.Ordinal);
        Assert.True(
            protectedReleaseAudit.Split(
                "Add-AnnouncementAuditFailure",
                StringSplitOptions.None).Length - 1 >= 6);
        Assert.Contains("$mainRuleset[0].enforcement -cne 'active'",
            protectedReleaseAudit, StringComparison.Ordinal);
        Assert.Contains("$tagRuleset[0].enforcement -cne 'active'",
            protectedReleaseAudit, StringComparison.Ordinal);
        Assert.Contains("$reviewerRules.Count -ne 1",
            protectedReleaseAudit, StringComparison.Ordinal);
        Assert.Contains("@($reviewerRules[0].reviewers).Count -lt 1",
            protectedReleaseAudit, StringComparison.Ordinal);
    }

    private static void AssertProperty(
        XDocument project,
        string propertyName,
        string expectedValue)
    {
        var values = project.Descendants(propertyName)
            .Select(element => element.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([expectedValue], values);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SessionDock.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
