using System.Text.Json;
using PersonalAI.Core.Tools.ComputerControl.Services;

namespace PersonalAI.Core.Tools.ComputerControl;

/// <summary>Mouse, keyboard and screen control of the whole host machine; off by default and switched on per chat.
/// The chat is text only, so `screenshot` saves a PNG for the person and gives the model a text tree of UI elements,
/// whose x/y coordinates it passes back on later clicks. Nothing is kept between calls.</summary>
public sealed class ComputerControlTool(
    IMouseInputService mouse,
    IKeyboardInputService keyboard,
    IScreenCaptureService screenCapture,
    IUiaElementScanner uiaScanner) : ITool
{
    private const int DefaultKeyDelayMs = 15;

    public string Name => "computer_control";

    public string Description => """
        Control the entire Windows desktop: move/click/drag the mouse, send keyboard input, and "see"
        the screen. Off by default — must be explicitly enabled per session, like network_scan,
        because it can drive the whole machine, not just your workspace.

        This tool cannot show you a picture — call `screenshot` to get back a text list of visible,
        interactive UI elements (button/edit/checkbox/etc.) with their names and (x, y) positions.
        Read coordinates off that list and pass them explicitly to move_mouse/left_click/drag/etc. on
        follow-up calls; nothing from a previous screenshot is remembered automatically.

        All mouse actions use virtual-screen coordinates (0,0 is the primary monitor's top-left; a
        monitor positioned to the left or above the primary may have negative coordinates).
        """;

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["screenshot", "move_mouse", "left_click", "right_click", "middle_click", "double_click", "scroll", "drag", "type_text", "send_keys", "get_cursor_position"],
              "description": "Which computer-control action to perform."
            },
            "x": { "type": "integer", "description": "Target x coordinate. Required for move_mouse and the *_click actions." },
            "y": { "type": "integer", "description": "Target y coordinate. Required for move_mouse and the *_click actions." },
            "amount": { "type": "integer", "description": "Scroll amount in wheel notches for the scroll action. Positive scrolls up/away, negative scrolls down/toward (standard wheel-delta sign convention)." },
            "fromX": { "type": "integer", "description": "Drag start x coordinate. Required for drag." },
            "fromY": { "type": "integer", "description": "Drag start y coordinate. Required for drag." },
            "toX": { "type": "integer", "description": "Drag end x coordinate. Required for drag." },
            "toY": { "type": "integer", "description": "Drag end y coordinate. Required for drag." },
            "text": { "type": "string", "description": "Literal text to type. Required for type_text." },
            "keys": {
              "type": "array",
              "items": { "type": "string" },
              "description": "Key tokens to send in order for send_keys. Each token is a single named key or single character (\"ENTER\", \"A\", \"F5\") or a '+'-joined chord (\"CTRL+ALT+DELETE\")."
            },
            "delayMs": { "type": "integer", "description": "Delay in milliseconds between keystrokes for type_text/send_keys. Defaults to 15ms." }
          },
          "required": ["action"]
        }
        """;

    public Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<ToolExecutionResult>(new ToolCompleted("Computer control is only supported on Windows.", IsError: true));

        var action = arguments.GetStringArg("action") ?? throw new ArgumentException("'action' is required.");
        var delayMs = arguments.GetIntArg("delayMs") ?? DefaultKeyDelayMs;

        try
        {
            return Task.FromResult<ToolExecutionResult>(action.ToLowerInvariant() switch
            {
                "screenshot" => RunScreenshot(),
                "move_mouse" => WithXy(arguments, (x, y) =>
                {
                    mouse.MoveTo(x, y);
                    return new ToolCompleted($"Mouse moved to ({x}, {y}).");
                }),
                "left_click" => WithXy(arguments, (x, y) => ClickAt(x, y, MouseButton.Left, doubleClick: false, "left")),
                "right_click" => WithXy(arguments, (x, y) => ClickAt(x, y, MouseButton.Right, doubleClick: false, "right")),
                "middle_click" => WithXy(arguments, (x, y) => ClickAt(x, y, MouseButton.Middle, doubleClick: false, "middle")),
                "double_click" => WithXy(arguments, (x, y) => ClickAt(x, y, MouseButton.Left, doubleClick: true, "double")),
                "scroll" => RunScroll(arguments),
                "drag" => RunDrag(arguments),
                "type_text" => RunTypeText(arguments, delayMs),
                "send_keys" => RunSendKeys(arguments, delayMs),
                "get_cursor_position" => RunGetCursorPosition(),
                _ => new ToolCompleted($"Unknown action '{action}'. Must be one of screenshot, move_mouse, left_click, right_click, middle_click, double_click, scroll, drag, type_text, send_keys, get_cursor_position.", IsError: true)
            });
        }
        catch (ArgumentException ex)
        {
            // Named-key resolution failures (e.g. an unrecognized token in `keys`) surface as
            // ArgumentException from KeyboardInputService — treat as a tool error, not a thrown fault,
            // to match this tool's own validation-failure convention.
            return Task.FromResult<ToolExecutionResult>(new ToolCompleted(ex.Message, IsError: true));
        }
    }

    private ToolCompleted ClickAt(int x, int y, MouseButton button, bool doubleClick, string label)
    {
        mouse.MoveTo(x, y);
        mouse.Click(button, doubleClick);
        return new ToolCompleted($"Performed {label} click at ({x}, {y}).");
    }

    private ToolCompleted RunScreenshot()
    {
        var (filePath, width, height) = screenCapture.CaptureFullScreen();
        var (elementsText, count) = uiaScanner.Scan();

        var message = $"Screenshot saved to {filePath} ({width}x{height}).\n" +
            (count == 0 && string.IsNullOrWhiteSpace(elementsText) ? "No interactive elements detected." : elementsText);
        return new ToolCompleted(message);
    }

    private ToolCompleted RunScroll(JsonElement arguments)
    {
        var amount = arguments.GetIntArg("amount");
        if (amount is null)
            return new ToolCompleted("'amount' is required for the scroll action.", IsError: true);

        mouse.Scroll(amount.Value);
        return new ToolCompleted($"Scrolled by {amount.Value} notch(es).");
    }

    private ToolCompleted RunDrag(JsonElement arguments)
    {
        var fromX = arguments.GetIntArg("fromX");
        var fromY = arguments.GetIntArg("fromY");
        var toX = arguments.GetIntArg("toX");
        var toY = arguments.GetIntArg("toY");
        if (fromX is null || fromY is null || toX is null || toY is null)
            return new ToolCompleted("'fromX', 'fromY', 'toX', and 'toY' are all required for the drag action.", IsError: true);

        mouse.Drag(fromX.Value, fromY.Value, toX.Value, toY.Value);
        return new ToolCompleted($"Dragged from ({fromX}, {fromY}) to ({toX}, {toY}).");
    }

    private ToolCompleted RunTypeText(JsonElement arguments, int delayMs)
    {
        var text = arguments.GetStringArg("text");
        if (text is null)
            return new ToolCompleted("'text' is required for the type_text action.", IsError: true);

        keyboard.SendText(text, delayMs);
        return new ToolCompleted($"Typed {text.Length} character(s).");
    }

    private ToolCompleted RunSendKeys(JsonElement arguments, int delayMs)
    {
        var keys = arguments.GetStringArrayArg("keys");
        if (keys is null || keys.Count == 0)
            return new ToolCompleted("'keys' (a non-empty array of key tokens) is required for the send_keys action.", IsError: true);

        keyboard.SendKeySequence(keys, delayMs);
        return new ToolCompleted($"Sent key sequence: {string.Join(", ", keys)}.");
    }

    private ToolCompleted RunGetCursorPosition()
    {
        var (x, y) = mouse.GetCursorPosition();
        return new ToolCompleted($"Cursor is at ({x}, {y}).");
    }

    private static ToolCompleted WithXy(JsonElement arguments, Func<int, int, ToolCompleted> action)
    {
        var x = arguments.GetIntArg("x");
        var y = arguments.GetIntArg("y");
        if (x is null || y is null)
            return new ToolCompleted("'x' and 'y' are required for this action.", IsError: true);

        return action(x.Value, y.Value);
    }
}
