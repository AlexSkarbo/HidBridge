using HidBridge.ControlPlane.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies operational artifact discovery for Ops Status.
/// </summary>
public sealed class OperationalArtifactServiceTests
{
    [Fact]
    public void GetSummary_ResolvesCiLocalAndSmokeGroups()
    {
        using var fixture = new ArtifactFixture();
        fixture.Write(".logs/doctor/20260312-010904/doctor.log", "doctor");
        fixture.Write(".logs/ci-local/20260312-010904/Doctor.log", "ci doctor");
        fixture.Write(".logs/ci-local/20260312-010904/Checks-Sql.log", "ci checks");
        fixture.Write(".smoke-data/Sql/20260312-010904/smoke.summary.txt", "smoke summary");
        fixture.Write(".smoke-data/Sql/20260312-010904/controlplane.stderr.log", "stderr");

        var service = new OperationalArtifactService(fixture.Environment);
        var summary = service.GetSummary();

        Assert.NotNull(summary.Doctor);
        Assert.NotNull(summary.CiLocal);
        Assert.NotNull(summary.Smoke);
        Assert.Equal("Doctor", summary.Doctor!.DisplayName);
        Assert.Equal("CI Local", summary.CiLocal!.DisplayName);
        Assert.Equal("Smoke (Sql)", summary.Smoke!.DisplayName);
        Assert.Equal("Checks-Sql.log", summary.CiLocal.PreferredLink!.Name);
        Assert.Equal("smoke.summary.txt", summary.Smoke.PreferredLink!.Name);
    }

    [Fact]
    public void ResolveSafePath_AllowsKnownRoots()
    {
        using var fixture = new ArtifactFixture();
        var relative = ".smoke-data/Sql/20260312-010904/smoke.summary.txt";
        fixture.Write(relative, "ok");

        var service = new OperationalArtifactService(fixture.Environment);
        var fullPath = service.ResolveSafePath(relative);

        Assert.EndsWith("smoke.summary.txt", fullPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(fullPath));
    }

    private sealed class ArtifactFixture : IDisposable
    {
        private readonly string _root;

        public ArtifactFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"hidbridge-artifacts-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            var contentRoot = Path.Combine(_root, "Platform", "Clients", "HidBridge.ControlPlane.Web");
            Directory.CreateDirectory(contentRoot);
            Environment = new TestWebHostEnvironment(contentRoot);
        }

        public IWebHostEnvironment Environment { get; }

        public void Write(string relativePath, string content)
        {
            var fullPath = Path.Combine(_root, "Platform", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // best-effort test cleanup
            }
        }
    }

    private sealed class TestWebHostEnvironment : IWebHostEnvironment
    {
        public TestWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ApplicationName = "HidBridge.ControlPlane.Web.Tests";
            EnvironmentName = "Development";
            WebRootPath = Path.Combine(contentRootPath, "wwwroot");
            ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
            WebRootFileProvider = new NullFileProvider();
        }

        public string ApplicationName { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
        public string EnvironmentName { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
