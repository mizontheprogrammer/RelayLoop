using RelayLoop.App.Models;
using RelayLoop.App.Services;
using RelayLoop.Core;
using Xunit;

namespace RelayLoop.IntegrationTests;

public sealed class ProfileServiceTests
{
    [Fact]
    public async Task SaveLoadListAndDelete_RoundTripsNamedProfile()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            ProfileService service = new(directory);
            MacroProfile profile = new()
            {
                Name = "Strafe farm",
                Document = CreateDocument(321, -45),
                PlaybackSpeed = 1,
                RepeatCount = 1,
                ContinuousPlayback = true,
                LockMouseDuringDirectionalHold = true,
            };
            profile.Document.Steps = MacroStepCompiler.CreateDefault(321, -45);

            await service.SaveAsync(profile);

            Assert.Equal(["Strafe farm"], await service.ListNamesAsync());
            Assert.True(await service.ExistsAsync("STRAFE FARM"));
            var loaded = await service.LoadAsync("strafe farm");
            Assert.Equal("Strafe farm", loaded.Name);
            Assert.True(loaded.ContinuousPlayback);
            Assert.True(loaded.LockMouseDuringDirectionalHold);
            Assert.True(DirectionalHoldPreset.IsMatch(loaded.Document.Events));
            Assert.Equal(321, loaded.Document.Events[1].X);
            Assert.Equal(-45, loaded.Document.Events[1].Y);
            Assert.Equal(2, loaded.Document.Steps!.Count);
            Assert.Equal(0x44, loaded.Document.Steps[0].Inputs[0].VirtualKey);

            await service.DeleteAsync("Strafe farm");

            Assert.Empty(await service.ListNamesAsync());
            Assert.False(await service.ExistsAsync("Strafe farm"));
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task SaveAsync_ReplacesSameNameWithoutCreatingDuplicate()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            ProfileService service = new(directory);
            await service.SaveAsync(CreateProfile("Main", 10));
            await service.SaveAsync(CreateProfile("main", 20));

            Assert.Equal(["main"], await service.ListNamesAsync());
            Assert.Equal(20, (await service.LoadAsync("MAIN")).Document.Events[1].X);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    public void ValidateName_RejectsEmptyOrControlCharacters(string name)
    {
        Assert.Throws<ArgumentException>(() => ProfileService.ValidateName(name));
    }

    private static MacroProfile CreateProfile(string name, int x) => new()
    {
        Name = name,
        Document = CreateDocument(x, 30),
        ContinuousPlayback = true,
    };

    private static MacroDocument CreateDocument(int x, int y) => new()
    {
        DisplayLayout = new DisplayLayout
        {
            VirtualLeft = -1920,
            VirtualTop = -1080,
            VirtualWidth = 3840,
            VirtualHeight = 2160,
            Monitors =
            [
                new MonitorInfo
                {
                    DeviceName = "TEST-DISPLAY",
                    Left = -1920,
                    Top = -1080,
                    Width = 3840,
                    Height = 2160,
                    DpiX = 96,
                    DpiY = 96,
                    IsPrimary = true,
                },
            ],
        },
        Events = DirectionalHoldPreset.CreateEvents(x, y),
    };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "RelayLoop.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
