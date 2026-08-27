using RelayLoop.Core;

namespace RelayLoop.Core.Tests;

public sealed class MacroValidatorTests
{
    [Fact]
    public void ValidDocument_AllowsNegativeVirtualDesktopCoordinates()
    {
        var document = TestMacros.Create();

        var issues = MacroValidator.GetIssues(document);

        Assert.Empty(issues);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(604800000001)]
    public void Validate_RejectsDelayOutsideBounds(long delay)
    {
        var document = TestMacros.WithDelays(delay);

        var exception = Assert.Throws<MacroValidationException>(() => MacroValidator.Validate(document));

        Assert.Contains(exception.Issues, issue => issue.Path.EndsWith(".delayMicroseconds", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsTooManyEventsWithoutInspectingEveryItem()
    {
        var document = TestMacros.WithDelays();
        var validEvent = new MacroEvent
        {
            Kind = MacroEventKind.KeyDown,
            VirtualKey = 65,
        };
        document.Events = Enumerable.Repeat(validEvent, MacroValidator.MaxEventCount + 1).ToList();

        var issues = MacroValidator.GetIssues(document);

        Assert.Single(issues);
        Assert.Equal("$.events", issues[0].Path);
    }

    [Fact]
    public void Validate_BoundsNumberOfReportedMalformedEvents()
    {
        var document = TestMacros.WithDelays();
        document.Events = Enumerable.Repeat<MacroEvent>(null!, 1_000).ToList();

        var issues = MacroValidator.GetIssues(document);

        Assert.Equal(MacroValidator.MaxReportedIssues, issues.Count);
    }

    [Theory]
    [InlineData(MacroEventKind.MouseButtonDown)]
    [InlineData(MacroEventKind.MouseButtonUp)]
    public void Validate_ButtonEventsRequireAButton(MacroEventKind kind)
    {
        var document = TestMacros.WithDelays();
        document.Events.Add(new MacroEvent { Kind = kind });

        var exception = Assert.Throws<MacroValidationException>(() => MacroValidator.Validate(document));

        Assert.Contains(exception.Issues, issue => issue.Path.EndsWith(".button", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WheelRequiresSensibleNonZeroDelta()
    {
        var document = TestMacros.WithDelays();
        document.Events.Add(new MacroEvent
        {
            Kind = MacroEventKind.MouseWheel,
            WheelDelta = int.MinValue,
        });

        var exception = Assert.Throws<MacroValidationException>(() => MacroValidator.Validate(document));

        Assert.Contains(exception.Issues, issue => issue.Path.EndsWith(".wheelDelta", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(256)]
    public void Validate_RejectsInvalidVirtualKeys(int virtualKey)
    {
        var document = TestMacros.WithDelays();
        document.Events.Add(new MacroEvent
        {
            Kind = MacroEventKind.KeyDown,
            VirtualKey = virtualKey,
        });

        Assert.Throws<MacroValidationException>(() => MacroValidator.Validate(document));
    }

    [Fact]
    public void Validate_RejectsUnknownEventKind()
    {
        var document = TestMacros.WithDelays();
        document.Events.Add(new MacroEvent { Kind = (MacroEventKind)999 });

        Assert.Throws<MacroValidationException>(() => MacroValidator.Validate(document));
    }

    [Fact]
    public void Validate_RequiresExactlyOnePrimaryMonitor()
    {
        var document = TestMacros.Create();
        foreach (var monitor in document.DisplayLayout!.Monitors)
        {
            monitor.IsPrimary = false;
        }

        var exception = Assert.Throws<MacroValidationException>(() => MacroValidator.Validate(document));

        Assert.Contains(exception.Issues, issue => issue.Message.Contains("Exactly one", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsInvalidDisplayDpiAndDimensions()
    {
        var document = TestMacros.Create();
        document.DisplayLayout!.VirtualWidth = 0;
        document.DisplayLayout.Monitors[0].DpiX = 2_000;

        var exception = Assert.Throws<MacroValidationException>(() => MacroValidator.Validate(document));

        Assert.Contains(exception.Issues, issue => issue.Path == "$.displayLayout.virtualWidth");
        Assert.Contains(exception.Issues, issue => issue.Message.Contains("DPI", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsNullCollectionsFromUntrustedJson()
    {
        var document = TestMacros.Create();
        document.Events = null!;
        document.DisplayLayout!.Monitors = null!;

        var exception = Assert.Throws<MacroValidationException>(() => MacroValidator.Validate(document));

        Assert.Contains(exception.Issues, issue => issue.Path == "$.events");
        Assert.Contains(exception.Issues, issue => issue.Path == "$.displayLayout.monitors");
    }

    [Fact]
    public void Validate_RequiresDisplayLayoutMetadata()
    {
        var document = TestMacros.WithDelays(1);
        document.DisplayLayout = null;

        var exception = Assert.Throws<MacroValidationException>(() => MacroValidator.Validate(document));

        Assert.Contains(exception.Issues, issue => issue.Path == "$.displayLayout");
    }
}
