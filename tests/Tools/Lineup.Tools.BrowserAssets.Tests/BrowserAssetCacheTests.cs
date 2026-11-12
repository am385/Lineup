using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Lineup.Tools.BrowserAssets.Tests;

/// <summary>Verifies dependency restoration, offline cache reuse, integrity failures, and actual SDK static-asset publishing.</summary>
public sealed class BrowserAssetCacheTests
{
    /// <summary>Verifies command-line verification reports a missing cache as an explicit failure instead of an unhandled exception.</summary>
    [Fact]
    public async Task Main_VerifyMissingCache_ReturnsFailure()
    {
        // Arrange
        using var fixture = new Fixture();
        var manifest = await fixture.WriteManifestAsync(new { path = "LICENSE", url = "https://assets.invalid/LICENSE", sha256 = Hash("license") });

        // Act
        var result = await Program.Main([manifest, fixture.Cache, "--verify"]);

        // Assert
        Assert.Equal(1, result);
    }

    /// <summary>Verifies original and transformed bytes are pinned, warm caches work offline, and only corrupt files are downloaded again.</summary>
    [Fact]
    public async Task PrepareAsync_ReusesVerifiedCacheAndRepairsCorruption()
    {
        // Arrange
        using var fixture = new Fixture();
        const string source = "/* original license */ url(https://fonts.invalid/font.woff2)";
        const string prepared = "/* original license */ url(fonts/local.woff2)";
        var manifest = await fixture.WriteManifestAsync(new
        {
            path = "controls.css",
            url = "https://assets.invalid/controls.css",
            sha256 = Hash(prepared),
            sourceSha256 = Hash(source),
            replacements = new[] { new { from = "https://fonts.invalid/font.woff2", to = "fonts/local.woff2" } }
        });
        using var downloads = new ResponseHandler(_ => source);
        using var online = new HttpClient(downloads);

        // Act
        var directory = await BrowserAssetCache.PrepareAsync(manifest, fixture.Cache, online, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var path = Path.Combine(directory, "controls.css");
        Assert.Equal(prepared, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(1, downloads.Requests);
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch);

        // Arrange
        using var offlineHandler = new ResponseHandler(_ => throw new HttpRequestException("Offline regression fixture."));
        using var offline = new HttpClient(offlineHandler);

        // Act
        Assert.Equal(directory, await BrowserAssetCache.PrepareAsync(manifest, fixture.Cache, offline, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(0, offlineHandler.Requests);
        Assert.Equal(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(path));

        // Arrange
        await File.WriteAllTextAsync(path, "corrupt cached stylesheet", TestContext.Current.CancellationToken);

        // Act
        await BrowserAssetCache.PrepareAsync(manifest, fixture.Cache, online, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, downloads.Requests);
        Assert.Equal(prepared, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>Verifies both source and final checksum mismatches preserve the previous file and fail explicitly.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PrepareAsync_ChecksumMismatch_DoesNotInstallUnverifiedBytes(bool sourceMismatch)
    {
        // Arrange
        using var fixture = new Fixture();
        var manifest = await fixture.WriteManifestAsync(new
        {
            path = "player.js",
            url = "https://assets.invalid/player.js",
            sha256 = Hash("expected output"),
            sourceSha256 = Hash(sourceMismatch ? "expected download" : "actual download")
        });
        var directory = Path.Combine(fixture.Cache, Hash(await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken)));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "player.js");
        await File.WriteAllTextAsync(path, "previous file", TestContext.Current.CancellationToken);
        using var handler = new ResponseHandler(_ => "actual download");
        using var client = new HttpClient(handler);

        // Act
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BrowserAssetCache.PrepareAsync(manifest, fixture.Cache, client, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("checksum mismatch", failure.Message, StringComparison.Ordinal);
        Assert.Equal("previous file", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>Verifies missing assets fail without network requests in verification mode and unavailable downloads are not treated as success.</summary>
    [Fact]
    public async Task PrepareAsync_MissingAssets_FailExplicitlyWithoutFallback()
    {
        // Arrange
        using var fixture = new Fixture();
        var manifest = await fixture.WriteManifestAsync(new { path = "LICENSE", url = "https://assets.invalid/LICENSE", sha256 = Hash("license") });
        using var handler = new ResponseHandler(_ => throw new HttpRequestException("Offline regression fixture."));
        using var client = new HttpClient(handler);

        // Act
        var missing = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BrowserAssetCache.PrepareAsync(manifest, fixture.Cache, client, verifyOnly: true, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("missing or corrupt", missing.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Requests);

        // Act
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            BrowserAssetCache.PrepareAsync(manifest, fixture.Cache, client, cancellationToken: TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(1, handler.Requests);
        Assert.Empty(Directory.GetFiles(fixture.Cache, "LICENSE", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(fixture.Cache, "*.tmp", SearchOption.AllDirectories));
    }

    /// <summary>Verifies dependency-lock changes cannot reuse an obsolete cache namespace.</summary>
    [Fact]
    public async Task PrepareAsync_ChangedManifest_UsesIndependentCache()
    {
        // Arrange
        using var fixture = new Fixture();
        var first = await fixture.WriteManifestAsync(new { path = "player.js", url = "https://assets.invalid/player.js", sha256 = Hash("first") });
        using var firstHandler = new ResponseHandler(_ => "first");
        using var firstClient = new HttpClient(firstHandler);
        var previous = await BrowserAssetCache.PrepareAsync(first, fixture.Cache, firstClient, cancellationToken: TestContext.Current.CancellationToken);
        var second = await fixture.WriteManifestAsync(new { path = "player.js", url = "https://assets.invalid/player-v2.js", sha256 = Hash("second") });
        using var secondHandler = new ResponseHandler(_ => "second");
        using var secondClient = new HttpClient(secondHandler);

        // Act
        var current = await BrowserAssetCache.PrepareAsync(second, fixture.Cache, secondClient, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(previous, current);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(previous, "player.js"), TestContext.Current.CancellationToken));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(current, "player.js"), TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies project-load evaluation resolves default and overridden paths with both SDK and Visual Studio MSBuild runtimes.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Targets_ProjectEvaluation_SupportsBothMsBuildRuntimes(bool visualStudio, bool relativeOverrides)
    {
        // Arrange
        using var fixture = new Fixture();
        var executable = "dotnet";
        if (visualStudio)
        {
            Assert.SkipWhen(!OperatingSystem.IsWindows(), "Visual Studio's .NET Framework MSBuild is Windows-only.");
            var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
            Assert.SkipWhen(!File.Exists(vswhere), "The Visual Studio installer is not available.");
            var locations = await RunProcessAsync(vswhere, fixture.Root,
                ["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"]);
            var msbuild = locations.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(File.Exists);
            Assert.SkipWhen(msbuild is null, "Visual Studio's MSBuild is not installed.");
            executable = Assert.IsType<string>(msbuild);
        }
        var repository = RepositoryRoot();
        var projectDirectory = Directory.CreateDirectory(Path.Combine(fixture.Root, "project"));
        var properties = new XElement("PropertyGroup");
        if (relativeOverrides)
        {
            properties.Add(new XElement("LineupBrowserAssetsManifest", "../manifest.json"), new XElement("LineupBrowserAssetsCacheRoot", "../custom-cache"));
        }
        var project = Path.Combine(projectDirectory.FullName, "Fixture.proj");
        new XDocument(new XElement("Project", properties,
            new XElement("Import", new XAttribute("Project", Path.Combine(repository, "build", "Lineup.Web.BrowserAssets.targets"))))).Save(project);
        string[] arguments = [project, "-nologo", "-p:DesignTimeBuild=true", "-getProperty:LineupBrowserAssetsManifest,LineupBrowserAssetsCacheRoot"];

        // Act
        var result = await RunProcessAsync(executable, fixture.Root, visualStudio ? arguments : ["msbuild", .. arguments]);
        using var evaluation = JsonDocument.Parse(result);
        var evaluated = evaluation.RootElement.GetProperty("Properties");

        // Assert
        var expectedManifest = relativeOverrides ? Path.Combine(fixture.Root, "manifest.json") : Path.Combine(repository, "build", "browser-assets.json");
        var expectedCache = relativeOverrides ? Path.Combine(fixture.Root, "custom-cache") : Path.Combine(repository, "out", "browser-assets");
        Assert.Equal(expectedManifest, evaluated.GetProperty("LineupBrowserAssetsManifest").GetString());
        Assert.Equal(expectedCache, evaluated.GetProperty("LineupBrowserAssetsCacheRoot").GetString());
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "custom-cache")));
    }

    /// <summary>Verifies development HTTP serving and no-build publishing expose cached assets without generating source files.</summary>
    /// <param name="configuration">The SDK build configuration to exercise.</param>
    [Theory]
    [InlineData("Debug")]
    [InlineData("Release")]
    public async Task WebSdk_BuildAndNoBuildPublish_IncludeCachedAssetsAndNotices(string configuration)
    {
        // Arrange
        using var fixture = new Fixture();
        var files = new Dictionary<string, string>
        {
            ["player.js"] = "window.fixturePlayer = true;",
            ["controls.css"] = "@font-face{font-family:Fixture;src:url(fonts/fixture.woff2)}",
            ["fonts/fixture.woff2"] = "owned font packaging fixture",
            ["LICENSE"] = "owned license fixture",
            ["NOTICE"] = "owned notice fixture"
        };
        var manifest = await fixture.WriteManifestAsync(files.Select(file => new
        {
            path = file.Key,
            url = "https://assets.invalid/" + file.Key,
            sha256 = Hash(file.Value)
        }).ToArray());
        using var handler = new ResponseHandler(request => files[request.RequestUri!.AbsolutePath.TrimStart('/')]);
        using var client = new HttpClient(handler);
        var cache = await BrowserAssetCache.PrepareAsync(manifest, fixture.Cache, client, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(cache, "not-in-the-manifest.txt"), "must not be published", TestContext.Current.CancellationToken);
        var repository = RepositoryRoot();
        var build = Directory.CreateDirectory(Path.Combine(fixture.Root, "build"));
        File.Copy(Path.Combine(repository, "build", "Lineup.Web.BrowserAssets.targets"), Path.Combine(build.FullName, "Lineup.Web.BrowserAssets.targets"));
        var tool = Directory.CreateDirectory(Path.Combine(fixture.Root, "scripts", "Lineup.Tools.BrowserAssets"));
        foreach (var name in new[] { "Program.cs", "BrowserAssetCache.cs", "Lineup.Tools.BrowserAssets.csproj" })
        {
            File.Copy(Path.Combine(repository, "scripts", "Lineup.Tools.BrowserAssets", name), Path.Combine(tool.FullName, name));
        }
        var web = Directory.CreateDirectory(Path.Combine(fixture.Root, "web"));
        await File.WriteAllTextAsync(Path.Combine(web.FullName, "Fixture.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <LineupBrowserAssetsManifest>../manifest.json</LineupBrowserAssetsManifest>
                <LineupBrowserAssetsCacheRoot>../cache</LineupBrowserAssetsCacheRoot>
              </PropertyGroup>
              <Import Project="../build/Lineup.Web.BrowserAssets.targets" />
            </Project>
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(web.FullName, "Program.cs"),
            "var app = WebApplication.CreateBuilder(args).Build(); app.MapStaticAssets(); app.Run();", TestContext.Current.CancellationToken);
        var output = Path.Combine(fixture.Root, "published");

        // Act
        await RunDotnetAsync(web.FullName, ["build", "Fixture.csproj", "-c", configuration, "--verbosity", "quiet"]);
        await AssertDevelopmentAssetsAsync(web.FullName, configuration, files);
        await RunDotnetAsync(web.FullName, ["publish", "Fixture.csproj", "-c", configuration, "--no-build", "--no-restore", "-o", output, "--verbosity", "quiet"]);

        // Assert
        Assert.False(Directory.Exists(Path.Combine(web.FullName, "wwwroot")));
        foreach (var file in files)
        {
            Assert.Equal(file.Value, await File.ReadAllTextAsync(Path.Combine(output, "wwwroot", "lib", file.Key), TestContext.Current.CancellationToken));
        }
        Assert.False(File.Exists(Path.Combine(output, "wwwroot", "lib", "not-in-the-manifest.txt")));
        Assert.False(File.Exists(Path.Combine(output, "wwwroot", "lib", ".restore.lock")));
        Assert.False(File.Exists(Path.Combine(output, "wwwroot", "lib", "assets.files")));
        using var endpoints = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "Fixture.staticwebassets.endpoints.json"), TestContext.Current.CancellationToken));
        foreach (var file in files)
        {
            Assert.Contains(endpoints.RootElement.GetProperty("Endpoints").EnumerateArray(), endpoint => endpoint.GetProperty("Route").GetString() == "lib/" + file.Key);
        }
    }

    private static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Lineup.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static async Task AssertDevelopmentAssetsAsync(string workingDirectory, string configuration, Dictionary<string, string> files)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var binary = Path.Combine(workingDirectory, "bin", configuration, "net10.0", "Fixture.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { binary, "--urls", "http://127.0.0.1:0" },
            Environment = { ["ASPNETCORE_ENVIRONMENT"] = "Development", ["DOTNET_ENVIRONMENT"] = "Development" }
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned development asset server.");
        var error = process.StandardError.ReadToEndAsync();
        Task<string>? remainingOutput = null;
        try
        {
            Uri? address = null;
            var startup = new StringBuilder();
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                startup.AppendLine(line);
                const string prefix = "Now listening on: ";
                var index = line.IndexOf(prefix, StringComparison.Ordinal);
                if (index >= 0)
                {
                    address = new Uri(line[(index + prefix.Length)..].Trim());
                    break;
                }
            }
            if (address is null)
            {
                Assert.Fail($"The owned development asset server did not start.{Environment.NewLine}{startup}{await error}");
            }
            remainingOutput = process.StandardOutput.ReadToEndAsync();
            using var client = new HttpClient { BaseAddress = address };
            using var endpoints = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(binary, ".staticwebassets.endpoints.json"), deadline.Token));
            foreach (var file in files)
            {
                var original = "lib/" + file.Key;
                var routes = endpoints.RootElement.GetProperty("Endpoints").EnumerateArray()
                    .Where(endpoint => endpoint.GetProperty("AssetFile").GetString() == original && endpoint.GetProperty("Selectors").GetArrayLength() == 0)
                    .Select(endpoint => Assert.IsType<string>(endpoint.GetProperty("Route").GetString()))
                    .Distinct().ToArray();
                Assert.Contains(original, routes);
                Assert.True(routes.Length > 1, $"Missing fingerprinted route for {original}.");
                foreach (var route in routes)
                {
                    using var response = await client.GetAsync(route, deadline.Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(file.Value, await response.Content.ReadAsStringAsync(deadline.Token));
                    Assert.NotNull(response.Content.Headers.ContentType);
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await error;
            if (remainingOutput is not null)
            {
                await remainingOutput;
            }
        }
    }

    private static Task<string> RunDotnetAsync(string workingDirectory, string[] arguments) => RunProcessAsync("dotnet", workingDirectory, arguments);

    private static async Task<string> RunProcessAsync(string executable, string workingDirectory, string[] arguments)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned browser-asset build regression.");
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var stdout = await output;
            var stderr = await error;
            Assert.True(process.ExitCode == 0, $"{Path.GetFileName(executable)} {string.Join(' ', arguments)} failed.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
            return stdout;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        /// <summary>Gets the owned temporary directory.</summary>
        public string Root { get; } = Directory.CreateTempSubdirectory("lineup-browser-assets-").FullName;

        /// <summary>Gets the isolated asset cache.</summary>
        public string Cache => Path.Combine(Root, "cache");

        /// <summary>Writes a dependency lock for an isolated test.</summary>
        /// <param name="assets">The fixture asset declarations.</param>
        /// <returns>The manifest path.</returns>
        public async Task<string> WriteManifestAsync(params object[] assets)
        {
            var path = Path.Combine(Root, "manifest.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { assets }), TestContext.Current.CancellationToken);
            return path;
        }

        /// <summary>Removes only this fixture's owned temporary directory.</summary>
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
    {
        /// <summary>Gets the number of attempted downloads.</summary>
        public int Requests { get; private set; }

        /// <summary>Returns controlled asset bytes or an explicit network failure.</summary>
        /// <param name="request">The download request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The controlled HTTP response.</returns>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request)) });
        }
    }
}
