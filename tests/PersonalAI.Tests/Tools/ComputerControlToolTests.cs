using System.Text.Json;
using FluentAssertions;
using PersonalAI.Core.Tools;
using PersonalAI.Core.Tools.ComputerControl;
using PersonalAI.Core.Tools.ComputerControl.Services;
using Moq;

namespace PersonalAI.Tests.Tools;

/// <summary>
/// ComputerControlTool.ExecuteAsync starts with a hard `!OperatingSystem.IsWindows()` guard that this
/// suite cannot flip in either direction — OperatingSystem.IsWindows() is a JIT intrinsic, not
/// something Moq/a seam can stand in for, so there is no way to unit-test the "unsupported OS" branch
/// from a test that itself must run on a real OS. These tests run on Windows (the dev/CI machine for
/// this repo), so they exercise the real dispatch logic in ExecuteAsync — but every actual OS
/// interaction (SendInput, GDI capture, UI Automation) is replaced by a Moq fake of the corresponding
/// interface. That real desktop I/O is not unit-testable in CI regardless of OS (it needs a live,
/// interactive, unlocked desktop session), so this suite deliberately limits itself to: does
/// ComputerControlTool dispatch each action to the right service call with the right arguments, and
/// does it reject bad input the way every other ITool in this codebase does (ToolCompleted
/// IsError:true, not a thrown exception)? It does not, and cannot, assert anything about whether a
/// real mouse actually moves or a real screenshot is visually correct.
/// </summary>
public class ComputerControlToolTests
{
    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    private sealed record Mocks(
        Mock<IMouseInputService> Mouse,
        Mock<IKeyboardInputService> Keyboard,
        Mock<IScreenCaptureService> ScreenCapture,
        Mock<IUiaElementScanner> UiaScanner);

    private static (ComputerControlTool Tool, Mocks Mocks) NewTool()
    {
        var mouse = new Mock<IMouseInputService>();
        var keyboard = new Mock<IKeyboardInputService>();
        var screenCapture = new Mock<IScreenCaptureService>();
        var uiaScanner = new Mock<IUiaElementScanner>();

        var tool = new ComputerControlTool(mouse.Object, keyboard.Object, screenCapture.Object, uiaScanner.Object);
        return (tool, new Mocks(mouse, keyboard, screenCapture, uiaScanner));
    }

    [Fact]
    public async Task ExecuteAsync_MissingAction_Throws()
    {
        var (tool, _) = NewTool();
        var act = async () => await tool.ExecuteAsync(Args(new { }), new ToolExecutionContext(false), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ExecuteAsync_UnknownAction_ReturnsError()
    {
        var (tool, _) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "levitate" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("Unknown action");
    }

    [Fact]
    public async Task ExecuteAsync_MoveMouse_DispatchesToMouseService()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "move_mouse", x = 100, y = 200 }), new ToolExecutionContext(false), CancellationToken.None);

        mocks.Mouse.Verify(m => m.MoveTo(100, 200), Times.Once);
        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeFalse();
        completed.Content.Should().Contain("100").And.Contain("200");
    }

    [Fact]
    public async Task ExecuteAsync_MoveMouse_MissingCoordinates_ReturnsErrorWithoutTouchingService()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "move_mouse" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("x").And.Contain("y");
        mocks.Mouse.Verify(m => m.MoveTo(It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Theory]
    [InlineData("left_click", MouseButton.Left, false)]
    [InlineData("right_click", MouseButton.Right, false)]
    [InlineData("middle_click", MouseButton.Middle, false)]
    [InlineData("double_click", MouseButton.Left, true)]
    public async Task ExecuteAsync_ClickActions_MoveThenClickWithRightButton(string action, MouseButton expectedButton, bool expectedDoubleClick)
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action, x = 10, y = 20 }), new ToolExecutionContext(false), CancellationToken.None);

        mocks.Mouse.Verify(m => m.MoveTo(10, 20), Times.Once);
        mocks.Mouse.Verify(m => m.Click(expectedButton, expectedDoubleClick), Times.Once);
        result.Should().BeOfType<ToolCompleted>().Subject.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Scroll_DispatchesAmount()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "scroll", amount = -3 }), new ToolExecutionContext(false), CancellationToken.None);

        mocks.Mouse.Verify(m => m.Scroll(-3), Times.Once);
        result.Should().BeOfType<ToolCompleted>().Subject.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Scroll_MissingAmount_ReturnsError()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "scroll" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        mocks.Mouse.Verify(m => m.Scroll(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_Drag_DispatchesAllFourCoordinates()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(
            Args(new { action = "drag", fromX = 1, fromY = 2, toX = 3, toY = 4 }), new ToolExecutionContext(false), CancellationToken.None);

        mocks.Mouse.Verify(m => m.Drag(1, 2, 3, 4), Times.Once);
        result.Should().BeOfType<ToolCompleted>().Subject.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Drag_MissingCoordinate_ReturnsError()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "drag", fromX = 1, fromY = 2, toX = 3 }), new ToolExecutionContext(false), CancellationToken.None);

        result.Should().BeOfType<ToolCompleted>().Subject.IsError.Should().BeTrue();
        mocks.Mouse.Verify(m => m.Drag(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_TypeText_DispatchesTextAndDefaultDelay()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "type_text", text = "hello" }), new ToolExecutionContext(false), CancellationToken.None);

        mocks.Keyboard.Verify(k => k.SendText("hello", 15), Times.Once);
        result.Should().BeOfType<ToolCompleted>().Subject.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_TypeText_CustomDelay_IsPassedThrough()
    {
        var (tool, mocks) = NewTool();
        await tool.ExecuteAsync(Args(new { action = "type_text", text = "hi", delayMs = 50 }), new ToolExecutionContext(false), CancellationToken.None);

        mocks.Keyboard.Verify(k => k.SendText("hi", 50), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TypeText_MissingText_ReturnsError()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "type_text" }), new ToolExecutionContext(false), CancellationToken.None);

        result.Should().BeOfType<ToolCompleted>().Subject.IsError.Should().BeTrue();
        mocks.Keyboard.Verify(k => k.SendText(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SendKeys_DispatchesKeySequence()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "send_keys", keys = new[] { "CTRL+ALT+DELETE", "ENTER" } }), new ToolExecutionContext(false), CancellationToken.None);

        mocks.Keyboard.Verify(k => k.SendKeySequence(
            It.Is<IEnumerable<string>>(keys => keys.SequenceEqual(new[] { "CTRL+ALT+DELETE", "ENTER" })), 15), Times.Once);
        result.Should().BeOfType<ToolCompleted>().Subject.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_SendKeys_EmptyArray_ReturnsError()
    {
        var (tool, mocks) = NewTool();
        var result = await tool.ExecuteAsync(Args(new { action = "send_keys", keys = Array.Empty<string>() }), new ToolExecutionContext(false), CancellationToken.None);

        result.Should().BeOfType<ToolCompleted>().Subject.IsError.Should().BeTrue();
        mocks.Keyboard.Verify(k => k.SendKeySequence(It.IsAny<IEnumerable<string>>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SendKeys_UnrecognizedKeyName_ReturnsErrorInsteadOfThrowing()
    {
        var (tool, mocks) = NewTool();
        mocks.Keyboard
            .Setup(k => k.SendKeySequence(It.IsAny<IEnumerable<string>>(), It.IsAny<int>()))
            .Throws(new ArgumentException("Unrecognized key name: 'NOTAKEY'"));

        var result = await tool.ExecuteAsync(Args(new { action = "send_keys", keys = new[] { "NOTAKEY" } }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeTrue();
        completed.Content.Should().Contain("NOTAKEY");
    }

    [Fact]
    public async Task ExecuteAsync_GetCursorPosition_ReturnsCoordinatesFromMouseService()
    {
        var (tool, mocks) = NewTool();
        mocks.Mouse.Setup(m => m.GetCursorPosition()).Returns((42, 99));

        var result = await tool.ExecuteAsync(Args(new { action = "get_cursor_position" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeFalse();
        completed.Content.Should().Contain("42").And.Contain("99");
    }

    [Fact]
    public async Task ExecuteAsync_Screenshot_CombinesCaptureAndUiaScanIntoOneMessage()
    {
        var (tool, mocks) = NewTool();
        mocks.ScreenCapture.Setup(s => s.CaptureFullScreen()).Returns(("C:\\data\\tool-output\\screenshots\\shot.png", 1920, 1080));
        mocks.UiaScanner.Setup(s => s.Scan()).Returns(("[0] Button \"OK\" @ (10,10) 50x20", 1));

        var result = await tool.ExecuteAsync(Args(new { action = "screenshot" }), new ToolExecutionContext(false), CancellationToken.None);

        var completed = result.Should().BeOfType<ToolCompleted>().Subject;
        completed.IsError.Should().BeFalse();
        completed.Content.Should().Contain("shot.png").And.Contain("1920x1080").And.Contain("Button \"OK\"");
    }

    [Fact]
    public async Task ExecuteAsync_Screenshot_NoElementsFound_SaysSo()
    {
        var (tool, mocks) = NewTool();
        mocks.ScreenCapture.Setup(s => s.CaptureFullScreen()).Returns(("C:\\shot.png", 800, 600));
        mocks.UiaScanner.Setup(s => s.Scan()).Returns(("No interactive elements detected.", 0));

        var result = await tool.ExecuteAsync(Args(new { action = "screenshot" }), new ToolExecutionContext(false), CancellationToken.None);

        result.Should().BeOfType<ToolCompleted>().Subject.Content.Should().Contain("No interactive elements detected.");
    }
}
